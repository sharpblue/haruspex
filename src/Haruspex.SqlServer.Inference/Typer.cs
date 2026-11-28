using Haruspex.Collections;
using Haruspex.Diagnostics;
using Haruspex.Ir;
using Haruspex.SqlServer.Inference.Lattice;
using Haruspex.SqlServer.Model;
using Haruspex.SqlServer.Semantics;
using Haruspex.SqlServer.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Haruspex.SqlServer.Inference;

/// <summary>
/// The expression semantics of a body: what an expression, a query or a relation gives, as a <see cref="Value"/> or a row of
/// them, from the expression and the <see cref="ReadScope"/> it is read in, and nothing else. It holds no state of its own:
/// its fields are what the body is read against (the model, its binding, the summaries of what it calls, a function's
/// arguments), the same for every read, so reading an expression twice in one scope gives one value. The statement walk is
/// the <see cref="Walker"/>'s. The guide's chapter <c>docs/internals/04-abstract-interpretation.md</c> explains the split, and
/// <c>docs/decisions/0035-the-typer-apart-from-the-walker.md</c> argues it.
/// </summary>
/// <remarks>
/// Which table and column a reference reads is the <see cref="Binder"/>'s job (Haruspex.SqlServer.Semantics);
/// what that means for a value's shape and nullability is decided here. They are apart so
/// that lint, lineage and diff can bind without inferring.
/// </remarks>
/// <param name="model">The model the body's references resolve in.</param>
/// <param name="source">Where the object whose body it is was read; positions in the body are in its file.</param>
/// <param name="search">Where a name the body writes without a schema is looked for: the module's schema, or a query file's caller's.</param>
/// <param name="summaries">What each function the body calls returns and what each view it reads holds.</param>
/// <param name="parameters">Inside a function, its parameters' values.</param>
/// <param name="ansiNullsOff">Whether the body was created with ANSI_NULLS off.</param>
/// <param name="uncommittedReads">
/// Whether the body (a view's, a function's) is read uncommitted where it is used, so that no key of what it reads holds.
/// </param>
/// <param name="inComputedColumn">Whether it reads a computed column's expression, which may read no other computed column.</param>
/// <param name="uses">
/// Where a query file's walk with its parameters undeclared keeps what their uses say of their types (<see cref="ParameterUses"/>):
/// each query read is handed to it in its scope. Null for every other read, which records nothing.
/// </param>
internal sealed partial class Typer(
    DatabaseModel model,
    SourceSpan source,
    SchemaSearch search,
    Summaries summaries,
    EquatableMap<LocalName, Value>? parameters = null,
    bool ansiNullsOff = false,
    bool uncommittedReads = false,
    ParameterUses? uses = null,
    bool inComputedColumn = false)
{
    /// <summary>
    /// Whether this typer reads a computed column's expression (<see cref="ComputedColumn"/>), in which a computed column is one
    /// SQL Server refuses to read.
    /// </summary>
    private readonly bool inComputedColumn = inComputedColumn;

    /// <summary>Where a query file's parameters' uses are kept, while its parameters are being typed; null otherwise.</summary>
    private readonly ParameterUses? uses = uses;

    private readonly DatabaseModel model = model;

    /// <summary>Where a name the body writes without a schema is looked for (<see cref="SchemaSearch"/>).</summary>
    private readonly SchemaSearch search = search;
    private readonly Binder binder = summaries.Bindings.For(source, search);

    /// <summary>How many rows a query returns (<see cref="Cardinality"/>), asked with the scope it is read in.</summary>
    private readonly Cardinality cardinality = new(model, summaries.Bindings.For(source, search));
    private readonly SourceSpan source = source;

    /// <summary>What each scalar function returns, and what each view and table-valued function holds (<c>Typer.Views.cs</c>).</summary>
    private readonly Summaries summaries = summaries;

    /// <summary>Inside a function's body, its parameters by name; empty in a procedure, whose parameters are not followed yet.</summary>
    private readonly EquatableMap<LocalName, Value> parameters = parameters ?? [];

    /// <summary>Whether the body was created with ANSI_NULLS off, under which <c>c = @x</c> is TRUE when both are null.</summary>
    private readonly bool ansiNullsOff = ansiNullsOff;

    /// <summary>Whether the whole body is read uncommitted where it is used (see <see cref="ReadUncommitted"/>).</summary>
    private bool BodyReadUncommitted { get; } = uncommittedReads;

    /// <summary>
    /// Whether the statement being read may read uncommitted rows: the whole body is read so where it is used, or the
    /// isolation level was set to READ UNCOMMITTED on the path to it.
    /// </summary>
    private bool ReadUncommitted(ReadScope scope) => BodyReadUncommitted || scope.State.ReadUncommitted;

    /// <summary>
    /// The value a function's <c>RETURN</c> expression gives, read with no FROM in scope; its subqueries may read views and
    /// table-valued functions.
    /// </summary>
    /// <param name="expression">The returned expression.</param>
    /// <param name="parameters">
    /// The function's parameters, which are the same for every row its queries read, so a key compared with one is fixed, as
    /// in an inline function.
    /// </param>
    public Value Return(ScalarExpression expression, IReadOnlyList<Parameter> parameters)
    {
        var names = NameEnvironment.Empty.WithDeclarations(binder.Declarations(parameters));
        return Expression(expression, ReadScope.Statement(names, FlowState.Start([])));
    }

    /// <summary>A procedure's declared parameters and their types.</summary>
    public static IReadOnlyList<ParameterShape> Parameters(DatabaseModel model, Procedure procedure) =>
    [
        .. procedure.Parameters.Select(p => TableTypes.Parameter(model, p) ?? new ParameterShape(
            p.Name,
            SqlTypes.ToParameterShape(model, p.Type, p.At),
            // A table-valued parameter left out is an empty table (ParameterObservationTests), so READONLY is a default.
            HasDefault: p.Default is not null || p.IsReadOnly,
            IsOutput: p.IsOutput)),
    ];

    /// <summary>The result set a SELECT returns, or null if it returns none.</summary>
    /// <param name="select">The statement.</param>
    /// <param name="statement">The scope of the statement: the names in force and the state it starts from.</param>
    internal ResultShape? Result(SelectStatement select, ReadScope statement)
    {
        // SELECT ... INTO writes a table and SELECT @v = ... assigns variables; neither returns a result set.
        if (select.Into is not null)
            return null;
        if (select.QueryExpression is QuerySpecification { SelectElements: var elements } && elements.All(e => e is SelectSetVariable))
            return null;

        var at = At(select);
        var encoding = ForOf(select.QueryExpression) is JsonForClause ? IrEncoding.Json : IrEncoding.Rowset;

        // A top-level FOR JSON query that matches no rows returns no row at all, rather than [] or {}. A
        // rowset that matches no rows is still a result set, with no rows. A query that always returns exactly one row
        // always returns its JSON (observed: For_json_over_an_aggregate_without_group_by_always_returns_its_one_object).
        EquatableArray<IrNullReason> NoRows(Row row) => encoding == IrEncoding.Json && row.Rows != IrCardinality.One ? [new IrNullReason(IrNullRule.NoRows, at.ToString())] : [];
        EquatableArray<IrNullReason> absent = encoding == IrEncoding.Json ? [new IrNullReason(IrNullRule.NoRows, at.ToString())] : [];

        return WithCtes(
            select.WithCtesAndXmlNamespaces,
            statement,
            scope =>
            {
                // A set operation's FOR, ORDER BY and OFFSET belong to the whole (observed:
                // Order_by_and_offset_apply_to_the_whole_set_operation_and_top_to_its_first_query).
                var query = select.QueryExpression;
                var row = Outermost(query, scope);
                return ForOf(query) switch
                {
                    JsonForClause json => new ResultShape(IrEncoding.Json, ForJson(row, json, subquery: false), NoRows(row), row.Rows) { ResultsUnknown = MaskedJson(row, at) },
                    null => new ResultShape(IrEncoding.Rowset, Rowset(row), [], row.Rows),
                    var other => new ResultShape(IrEncoding.Rowset, Unsupported(IrUnknownReason.UnsupportedQuery, $"{Text(other)} is not modelled yet.", at), []),
                };
            },
            why => new ResultShape(encoding, why, absent));
    }

    // ---- queries ------------------------------------------------------------------------------------

    /// <summary>A query specification's row, read in its bound scope, with what its relations hold attached first.</summary>
    /// <param name="bound">The query, bound.</param>
    /// <param name="enclosing">The scope it is read inside: the enclosing query's, or a statement's.</param>
    private Row Query(BoundSpecification bound, ReadScope enclosing)
    {
        var query = bound.Query;
        if (GroupedByAll(query) is { } byAll)
            return new Row([], byAll);
        var scope = Attach(enclosing.In(bound.Scope));
        uses?.Specification(query, scope, this);

        // WHERE, the joins' ON and HAVING prove columns and aggregates non-null in the rows they keep (Typer.Refinement.cs), for
        // the select list and every subquery in it; GROUP BY is not read beyond what cardinality needs.
        var columns = new List<RowColumn>();
        UnknownShape? unlisted = null;
        var found = Filters(query, scope);

        // A cursor's row is read again when it is fetched, so its query's filters prove nothing of it (FetchRow).
        if (scope.Fetched)
            found.Forget();

        var row = scope.Selecting(query, found);
        foreach (var element in query.SelectElements)
        {
            switch (element)
            {
                case SelectScalarExpression scalar:
                    var value = Expression(scalar.Expression, row);
                    columns.Add(new RowColumn(Binder.ColumnName(scalar, row.Bound), value, At(scalar), Origin(scalar.Expression, scope), scalar.Expression)
                    {
                        From = Reads(scalar.Expression, row),
                        Description = Described(scalar.Expression, row),
                        JsonNeverFolded = value.IsLiveJson && NeverFoldedAsJson(scalar.Expression, row),
                        Bare = Bare(scalar.Expression, scope),
                    });
                    break;
                case SelectStarExpression all:
                    unlisted ??= Expand(all, row, columns);
                    break;
            }
        }

        var rows = cardinality.Of(query, scope, ReadUncommitted(scope));
        return new Row([.. columns], unlisted, rows, [.. scope.Bound.Sources])
        {
            Raced = rows != IrCardinality.One && cardinality.Guard(query, scope) is { Held: false, Found.At: var guard } ? guard : null,
        };
    }

    /// <summary>How many rows a statement's own query returns, read in its scope as <see cref="Enter"/> gave it.</summary>
    internal IrCardinality Rows(QuerySpecification query, ReadScope entered) => cardinality.Of(query, entered, ReadUncommitted(entered));

    /// <summary>The table column a select-list expression is, unchanged: a bare column reference without COLLATE.</summary>
    private TableColumn? Origin(ScalarExpression expression, ReadScope scope) =>
        expression is ColumnReferenceExpression { Collation: null, ColumnType: ColumnType.Regular } column && binder.Resolve(column, scope.Bound) is TableColumn bound
            ? bound
            : null;

    /// <summary>
    /// What a select-list expression reads when it is a bare column reference, in parentheses or not and without <c>COLLATE</c>,
    /// as binding resolves it; null for any other expression (see <see cref="RowColumn.Bare"/>).
    /// </summary>
    private Resolution? Bare(ScalarExpression expression, ReadScope scope) => expression switch
    {
        ParenthesisExpression { Collation: null } parenthesis => Bare(parenthesis.Expression, scope),
        ColumnReferenceExpression { Collation: null } column => binder.Resolve(column, scope.Bound),
        _ => null,
    };

    /// <summary>The name SQL Server gives the one column of top-level FOR JSON output.</summary>
    private const string JsonOutputColumn = "JSON_F52E2B61-18A1-11d1-B105-00805F49916B";

    /// <summary>The name SQL Server gives the one column of top-level FOR XML output.</summary>
    private const string XmlOutputColumn = "XML_F52E2B61-18A1-11d1-B105-00805F49916B";

    /// <summary>
    /// Why a column has no name haruspex knows: none at all, or, for <c>$IDENTITY</c> and <c>$ROWGUID</c>, the name of a column
    /// of the table, which only the table gives (<see cref="Binder.ColumnName(SelectScalarExpression, QueryScope)"/>).
    /// </summary>
    private static string Unnamed(RowColumn column) => column.Expression switch
    {
        ColumnReferenceExpression { ColumnType: ColumnType.PseudoColumnRowGuid } => $"The column {Text(column.At)} is named after its table's ROWGUIDCOL column, which the model of the project doesn't record.",
        ColumnReferenceExpression { ColumnType: ColumnType.PseudoColumnIdentity } => $"The column {Text(column.At)} is named after the identity column of what it reads, which is known only for a table it names for certain.",
        _ => $"The column {Text(column.At)} has no name.",
    };

    internal static Shape Rowset(Row row)
    {
        if (row.Unlisted is { } unlisted)
            return unlisted;
        if (row.Columns.FirstOrDefault(c => string.IsNullOrEmpty(c.Name)) is { } unnamed)
            return Unsupported(IrUnknownReason.UnnamedColumn, Unnamed(unnamed), unnamed.At);
        if (row.Columns.Select(ReplacedKey).FirstOrDefault(r => r is not null) is { } replaced)
            return replaced;

        // Drivers key a row by column name, and the mssql driver turns a repeated name into an array, so a
        // repeated name changes the row's shape.
        var duplicate = row.Columns.GroupBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return Unsupported(IrUnknownReason.DuplicateColumn, $"The column name '{duplicate.Key}' is used more than once.", duplicate.Skip(1).First().At);

        // The mssql driver takes a result set whose one column has the name SQL Server gives FOR JSON or FOR XML output for
        // that output, by the name alone and matching case: it joins the rows into one, and with parseJSON parses a JSON one
        // (MssqlDriverObservationTests). So a rowset under that name doesn't arrive as rows, in either mode.
        if (row.Columns is [{ Name: JsonOutputColumn or XmlOutputColumn } only])
        {
            return Unsupported(
                IrUnknownReason.UnsupportedQuery,
                $"The one column is named {only.Name}, as SQL Server names FOR JSON or FOR XML output, so the mssql driver joins its rows into one.",
                only.At);
        }

        var fields = row.Columns.Select(c => new FieldShape(
            // Never null: a column without a name was reported above.
            c.Name!,
            // A rowset column delivers JSON as text: the driver sees an nvarchar. Its content is what it parses to. JSON computed from
            // a masked column is its mask, which is no JSON (Value.Masked), so it is the unknown it is, and claims no text of JSON.
            c.Value is { IsLiveJson: true, IsMasked: true, Shape: UnknownShape masked } ? masked
                : c.Value.IsLiveJson ? new JsonTextShape(c.Value.Shape) : c.Value.Shape,
            IrPresence.Always,
            c.Value.NullableBecause)
        { From = c.From, Description = c.Description });
        return new ArrayShape(new ObjectShape([.. fields]), []);
    }

    // ---- FOR JSON -----------------------------------------------------------------------------------

    private sealed class JsonNode
    {
        public List<(string Key, JsonNode? Child, RowColumn? Leaf, SourceLocation At)> Entries { get; } = [];
    }

    /// <summary>
    /// The shape a <c>FOR JSON</c> query builds from a row: an object per row, by <c>PATH</c>'s rules (<see cref="PathObject"/>) or
    /// <c>AUTO</c>'s (<see cref="AutoObject"/>), in an array unless <c>WITHOUT_ARRAY_WRAPPER</c> leaves it out, and under one key
    /// with <c>ROOT</c>.
    /// </summary>
    /// <param name="subquery">
    /// Whether it is a subquery's value, which is NULL over no rows (observed), so that an array it builds has at least one
    /// item, and at most one when the query returns at most one row. A result's bounds are its cardinality instead, which
    /// emitters apply: a result with no row reads as null (the IR's reader convention), so an array that arrives has at
    /// least one item there too.
    /// </param>
    private Shape ForJson(Row row, JsonForClause clause, bool subquery)
    {
        // ROOT and WITHOUT_ARRAY_WRAPPER together are a syntax error to the parser, as to SQL Server (13620, observed:
        // Root_with_without_array_wrapper_is_refused), so a clause here has at most one of them.
        var options = clause.Options.ToDictionary(o => o.OptionKind);
        var unwrapped = options.ContainsKey(JsonForClauseOptions.WithoutArrayWrapper);
        var root = options.GetValueOrDefault(JsonForClauseOptions.Root);
        if (row.Unlisted is { } unlisted)
            return unlisted;

        var includeNulls = options.ContainsKey(JsonForClauseOptions.IncludeNullValues);
        var shape = options.ContainsKey(JsonForClauseOptions.Auto) ? AutoObject(row, includeNulls, At(clause)) : PathObject(row, includeNulls);
        if (shape is UnknownShape)
            return shape;

        // WITHOUT_ARRAY_WRAPPER is claimed as one object even where the query is not proven to return at most one row
        // (Row.Rows is Many). The claim is accepted because it cannot be silently wrong: several rows produce
        // "{...},{...}", which is not valid JSON, so the caller's parse fails loudly before any schema sees it. Where the
        // query is proven to return at most one row, the result's cardinality says so and the caveat does not apply. No
        // rows are covered by AbsentBecause.
        if (unwrapped)
            return shape;

        // ROOT puts the array under one key of an object, named as written, and taken as it is: a dot nests nothing (observed:
        // Root_wraps_the_array_in_an_object_whose_one_key_is_root_or_the_name_given). Without a name the key is "root" under
        // PATH, but under AUTO a source's name: the FROM's first, or an APPLY's (Root_without_a_name_under_auto_is_keyed_by_a_source),
        // which is not modelled. The object is written only over a row, as the array is: over none a result has no row and a
        // subquery is NULL (observed: A_top_level_for_json_auto_query_with_no_rows_returns_a_result_set_with_no_row,
        // A_root_subquery_nests_as_json_and_is_null_over_no_rows). So the array inside has a row's item at least, here, where
        // a result's cardinality can't bound it.
        if (root is not null)
        {
            var key = root.Value switch
            {
                StringLiteral named => named.Value,
                null when !options.ContainsKey(JsonForClauseOptions.Auto) => "root",
                _ => null,
            };
            return key is null
                ? Unsupported(IrUnknownReason.UnsupportedQuery, "FOR JSON AUTO, ROOT without a name keys the document by one of the FROM's sources, which is not modelled; name it, ROOT('name').", At(clause))
                : new ObjectShape([new FieldShape(key, Rows(shape, row), IrPresence.Always, [])]) { Writers = KeyWriters.SqlServer };
        }

        return subquery ? Rows(shape, row) : new ArrayShape(shape, []);
    }

    /// <summary>
    /// An array of an object for each row of a query that returns some: at least one item, and exactly one when the query returns
    /// at most one row.
    /// </summary>
    private static ArrayShape Rows(Shape item, Row row) => new(item, [], MinItems: 1, MaxItems: row.Rows == IrCardinality.Many ? null : 1);

    /// <summary>
    /// The object <c>FOR JSON PATH</c> builds from a row, with a dotted alias such as <c>customer.name</c> nesting a key inside
    /// an object.
    /// </summary>
    private static Shape PathObject(Row row, bool includeNulls)
    {
        var root = new JsonNode();
        foreach (var column in row.Columns)
        {
            // SQL Server rejects an unnamed column in FOR JSON, so the procedure fails rather than returning
            // anything; it is reported rather than modelled.
            if (string.IsNullOrEmpty(column.Name))
                return Unsupported(IrUnknownReason.UnnamedColumn, $"The column {Text(column.At)} has no name, which FOR JSON rejects.", column.At);
            if (ReplacedKey(column) is { } replaced)
                return replaced;

            var path = column.Name.Split('.');
            if (path.Any(p => p.Length == 0))
                return Unsupported(IrUnknownReason.UnsupportedQuery, $"The alias '{column.Name}' is not a valid FOR JSON path.", column.At);

            var node = root;
            foreach (var key in path[..^1])
            {
                // Keys of one nested object must come from adjacent columns. Anything else (a key reused after
                // another column, or a key that is both a value and an object) fails in SQL Server (13601), so it is
                // not modelled. Keys are compared exactly, whatever the collation: `id` and `ID`, or `a.x` and `A.y`,
                // are two keys, two objects (observed: For_json_keys_that_differ_only_in_case_are_two_keys).
                if (node.Entries.Count > 0 && node.Entries[^1] is { Child: { } child } last && last.Key.Equals(key, StringComparison.Ordinal))
                {
                    node = child;
                    continue;
                }

                if (node.Entries.Any(e => e.Key.Equals(key, StringComparison.Ordinal)))
                    return KeyConflict(column);
                var nested = new JsonNode();
                node.Entries.Add((key, nested, null, column.At));
                node = nested;
            }

            if (node.Entries.Any(e => e.Key.Equals(path[^1], StringComparison.Ordinal)))
                return KeyConflict(column);
            node.Entries.Add((path[^1], null, column, column.At));
        }

        return JsonObject(root, includeNulls);
    }

    /// <summary>
    /// Why a column's name is not its key: it holds a SQLCMD variable, <c>[$(Label)]</c>, which a deployment replaces with its
    /// value as it does in a literal, so the result's keys are not known (decision 0046; observed in SqlcmdVariableObservationTests).
    /// Null for any other name.
    /// </summary>
    private static UnknownShape? ReplacedKey(RowColumn column) =>
        column.Name is { } name && SqlcmdVariableReference.FirstIn(name) is { } variable
            ? Unsupported(IrUnknownReason.SqlcmdVariable, $"The column name '{name}' holds the SQLCMD variable {variable}, which a deployment replaces with its value, so the result's keys are not known.", column.At)
            : null;

    private static UnknownShape KeyConflict(RowColumn column) =>
        new(IrUnknownReason.DuplicateColumn, $"The alias '{column.Name}' clashes with another key in the same JSON object.", column.At);

    private static ObjectShape JsonObject(JsonNode node, bool includeNulls)
    {
        var fields = new List<FieldShape>();
        foreach (var (key, child, leaf, at) in node.Entries)
        {
            if (leaf is not null)
            {
                fields.Add(JsonLeaf(key, leaf, includeNulls));
                continue;
            }

            // Never null: an entry holds a leaf, read above, or a nested object.
            var nested = JsonObject(child!, includeNulls);

            // A nested object whose every value is null is left out of the output. With INCLUDE_NULL_VALUES it is
            // always written, with each key null, however deep it is nested: {"a":{"b":{"y":null}}}. Both were
            // observed on SQL Server 2025 and are kept as tests (ForJsonObservationTests, differential tests).
            var allNull = !includeNulls && nested.Fields.All(f => f.NullableBecause.Count > 0);
            fields.Add(allNull
                ? new FieldShape(key, nested, IrPresence.OmittedWhenNull, [new IrNullReason(IrNullRule.AllFieldsNull, at.ToString())])
                : new FieldShape(key, nested, IrPresence.Always, []));
        }

        return new ObjectShape([.. fields]) { Writers = KeyWriters.SqlServer };
    }

    /// <summary>
    /// The key a column's value is written under in <c>FOR JSON</c>, in either mode. A key whose value is null is left out, unless
    /// <c>INCLUDE_NULL_VALUES</c> keeps it as null. Live JSON keeps its shape; JSON text (<see cref="JsonTextShape"/>) is escaped
    /// into a string.
    /// </summary>
    private static FieldShape JsonLeaf(string key, RowColumn leaf, bool includeNulls)
    {
        var value = leaf.Value;
        var presence = value.NullableBecause.Count > 0 && !includeNulls ? IrPresence.OmittedWhenNull : IrPresence.Always;
        return new FieldShape(key, value.Shape, presence, value.NullableBecause) { From = leaf.From, Description = leaf.Description };
    }

    /// <summary>
    /// What an expression gives. A node whose type <see cref="AcceptedNodes"/> doesn't list is not modelled, whatever arm below
    /// might match it, since nothing says how its properties are read (decision 0113).
    /// </summary>
    /// <remarks>
    /// An expression computed from a masked value is masked as a whole, whatever rule typed it (<see cref="MaskedIfFrom"/>).
    /// </remarks>
    internal Value Expression(ScalarExpression expression, ReadScope scope)
    {
        var before = masked.Count;
        var value = expression switch
        {
            _ when !AcceptedNodes.Properties.ContainsKey(expression.GetType()) =>
                Value.Unknown(Unsupported(IrUnknownReason.UnsupportedExpression, $"The expression {Text(expression)} is not modelled yet.", At(expression))),
            PrimaryExpression { Collation: not null } collated => Collated(collated, scope),
            _ => Uncollated(expression, scope),
        };
        return MaskedIfFrom(value, before, expression);
    }

    /// <summary>
    /// An expression under <c>COLLATE</c>. A Unicode string keeps its characters under any collation, so its value is the
    /// expression's. Any other string keeps its type and when it is NULL, but its characters are converted to the collation's
    /// code page, where one may become another or be cut short: <c>'é'</c> is <c>'e'</c> under <c>Greek_CI_AS</c>, and as
    /// <c>varchar(1)</c> is <c>''</c> under a UTF-8 collation, which needs two bytes for it. So nothing more than its type is
    /// known of it (observed: Collate_keeps_a_unicode_strings_characters_and_may_change_or_cut_a_non_unicode_ones). The server
    /// refuses <c>COLLATE</c> of anything else (447), so that is not modelled.
    /// </summary>
    private Value Collated(PrimaryExpression expression, ReadScope scope)
    {
        var value = Uncollated(expression, scope);
        return value.Shape is UnknownShape ? value : TypeOf(value) switch
        {
            { IsString: true, IsNational: true } => value,
            { IsString: true } type => Typed(type, value.NullableBecause, expression),
            _ => NotModelled(expression, "COLLATE of a value that is not a string is not modelled."),
        };
    }

    /// <summary>An expression, its <c>COLLATE</c> read by <see cref="Collated"/>.</summary>
    private Value Uncollated(ScalarExpression expression, ReadScope scope) => expression switch
    {
        ColumnReferenceExpression { ColumnType: ColumnType.PseudoColumnAction } action => MergeAction(action, scope),
        ColumnReferenceExpression column => Column(column, scope),
        ParenthesisExpression parenthesis => Expression(parenthesis.Expression, scope),
        ScalarSubquery subquery => Subquery(subquery, scope),

        // Every built-in function, by the function table (Typer.FunctionTable.cs): the one path a call is typed by.
        _ when Function(expression, scope) is { } called => called,
        FunctionCall { CallTarget: not null } call when XmlMethodCall(call, scope) is { } method => method,
        FunctionCall call when FunctionSummaries.Callee(call) is { } callee => CallFunction(call, callee, scope),
        VariableReference variable => Branched(variable, Variable(variable, scope), scope),
        IntegerLiteral literal => Integer(literal),
        StringLiteral literal when SqlcmdVariableReference.FirstIn(literal.Value) is { } variable => Replaced(literal, variable),
        StringLiteral literal => FromString(literal),
        NumericLiteral literal => FromNumeric(literal),
        BinaryLiteral literal => FromBinary(literal),
        RealLiteral literal => Typed(ScalarType.Of(BuiltInType.Float), [], literal),
        MoneyLiteral literal => Typed(ScalarType.Of(BuiltInType.Money), [], literal),
        NullLiteral literal => Typed(ScalarType.Of(BuiltInType.Int), [new IrNullReason(IrNullRule.NullLiteral, At(literal).ToString())], literal),
        SearchedCaseExpression searched => Case(searched, scope),
        SimpleCaseExpression simple => Case(simple, scope),
        BinaryExpression binary => Binary(binary, scope),
        UnaryExpression unary => Unary(unary, scope),
        _ => Value.Unknown(Unsupported(IrUnknownReason.UnsupportedExpression, $"The expression {Text(expression)} is not modelled yet.", At(expression))),
    };

    private Value Column(ColumnReferenceExpression column, ReadScope scope) => binder.Resolve(column, scope.Bound) switch
    {
        var target when ReadsOutputTarget(target, scope) => Value.Unknown(Unsupported(
            IrUnknownReason.UnsupportedExpression,
            $"{Text(column)} names the table the write changes, which OUTPUT may read before or after the change; read inserted or deleted instead.",
            At(column))),
        TableColumn bound => FromColumn(bound, scope),
        RelationColumn { Relation: XmlNodesCall } => NodeColumn(Text(column), At(column)),
        RelationColumn local => FromLocal(local.Source, local.Ordinal, scope, At(column)),
        UnlistedColumn unlisted => FromLocal(unlisted.Source, -1, scope, At(column)),
        Unresolved unbound => Value.Unknown(Unknown(unbound.Problem)),
    };

    /// <remarks>
    /// The column's facts (length, digits, bounds, value sets) travel with the value only while it passes through
    /// unchanged: a column reference, through aliases, joins and FOR JSON nesting. Any expression makes the value
    /// something else, and none is modelled yet, so it becomes unknown and the facts go with it.
    /// </remarks>
    internal Value FromColumn(TableColumn bound, ReadScope scope) =>
        MaskOf(bound) is { } by ? Unmasked(bound, scope).Masked(by, bound.Column.At) : Unmasked(bound, scope);

    /// <summary>A column's value as a caller with <c>UNMASK</c> reads it; a computed column's masked as its expression is.</summary>
    private Value Unmasked(TableColumn bound, ReadScope scope)
    {
        var (source, table, column) = bound;
        var computed = column.ComputedExpression is { } expression ? ComputedColumn(bound, expression, scope) : null;
        if (computed is { Shape: UnknownShape })
            return computed;

        // A computed column may be null as its expression may, unless declared PERSISTED NOT NULL (or in a key), whose rows are
        // refused when they are written with NULL there.
        var because = new List<IrNullReason>();
        if (!SchemaRules.IsNeverNull(model, table, column) && scope.Facts.Proved(source, ColumnKey.Of(bound)) == Facts.Proof.None)
            because.AddRange(computed?.NullableBecause ?? [new IrNullReason(IrNullRule.NullableColumn, $"{table.Name}.{column.Name}")]);

        // The optional side of an outer join, or of an OUTER APPLY, has no row when nothing matches, so every column read
        // from it can be null, whatever its declaration says; unless a filter proves the row matched.
        if (source.Optional is { } optional && !scope.Facts.Matched(source))
            because.Add(new IrNullReason(optional.IsApplied ? IrNullRule.UnmatchedLateralRow : IrNullRule.UnmatchedRow, optional.At.ToString()));
        if (Rolled(source, scope) is { } rolled)
            because.Add(rolled);
        if (Unmatched(source, scope) is { } unmatched)
            because.Add(unmatched);

        // A CASE branch that tested this very value proves it, whatever made it nullable (Typer.Case.cs).
        if (scope.Facts.Proved(source, ColumnKey.Of(bound)) == Facts.Proof.OfTheValue)
            because.Clear();

        // A column declared to hold JSON of a schema holds it whoever wrote it: its type, completed by the config file. A json
        // column's is live JSON, as any json value is; any other's is a string of the column's type, holding JSON
        // (docs/decisions/0093-a-column-s-json-is-part-of-its-type-completed-by-the-config.md). Only a table's column can be
        // declared, so a view's gets it from the table's, read through its query, and a computed column is never one.
        var json = !column.IsComputed && SchemaRules.ResolveType(model, column.Type)?.Type == BuiltInType.Json;
        if (computed is null && summaries.DeclaredColumns.Content(column, this.source) is { } content)
        {
            return json
                ? new Value(content, [.. because], IsLiveJson: true)
                : Value.Computed(new JsonTextShape(content) { Text = ColumnShape(table, table.Name.ToString(), column) as ScalarShape }, [.. because]);
        }

        // A json column nests as JSON inside FOR JSON and the constructors (observed), but what it holds is data.
        // Through a driver it is text, which Rowset makes of any live value.
        if (json)
        {
            return new Value(
                Unsupported(IrUnknownReason.JsonShapeUnknown, $"{table.Name}.{column.Name} is a json column: its shape is in the data, not the schema.", column.At),
                [.. because],
                IsLiveJson: true);
        }

        return Value.Computed(computed?.Shape ?? ColumnShape(table, table.Name.ToString(), column), [.. because]) with { MaskedBy = computed?.MaskedBy, Digits = IntDigits.OfType };
    }

    /// <summary>
    /// A computed column's value where it is read: its expression, read over the same row as any expression is, PERSISTED or
    /// not (observed: A_computed_column_is_of_its_expressions_type_and_null_as_its_expression_is_persisted_or_not), in the
    /// table's own file, as its source wrote it. The row is read without the outer join or APPLY it may be on, whose reasons
    /// <see cref="FromColumn"/> adds to the column's. Unknown when its expression is, and when it holds JSON, which FOR JSON
    /// nests as JSON (A_computed_column_of_json_nests_as_json_in_for_json), not modelled for a column; and when it reads another
    /// computed column, which SQL Server refuses (1759). A non-persisted column is computed under the reader's options, and a
    /// persisted one was written under ANSI_WARNINGS on (1934 otherwise), so typing it under the callers' options claims no less
    /// than its value can be (A_persisted_computed_column_is_written_only_with_ansi_warnings_on_and_a_computed_column_reads_no_other).
    /// It compares with NULL by the table's ANSI_NULLS when it was created, whatever the reader's
    /// (A_computed_column_compares_with_null_by_its_tables_ansi_nulls_whatever_the_readers).
    /// </summary>
    private Value ComputedColumn(TableColumn bound, ScalarExpression expression, ReadScope scope)
    {
        var (source, table, column) = bound;
        if (inComputedColumn)
            return Value.Unknown(Unsupported(IrUnknownReason.UnsupportedExpression, $"{table.Name}.{column.Name} is a computed column, which another computed column can't read (1759).", column.At));

        var row = QueryScope.OfRow(source with { Optional = null }, scope.Bound.Names);
        // A computed column's expression names no table or type, and a function in it has its schema, so no search is needed.
        var value = new Typer(model, table.Span, SchemaSearch.Caller, summaries, ansiNullsOff: table is Table { AnsiNullsOff: true }, inComputedColumn: true).Expression(expression, scope.In(row));
        return value.IsLiveJson
            ? Value.Unknown(Unsupported(IrUnknownReason.UnsupportedExpression, $"{table.Name}.{column.Name} is a computed column holding JSON, which is not modelled for a column.", column.At))
            : value;
    }

    /// <summary>A column's shape when it is not null.</summary>
    /// <param name="table">The column's table, of the schema or the body's own.</param>
    /// <param name="named">The table's name, as a message gives it: <c>sales.orders</c>, <c>#totals</c>.</param>
    /// <param name="column">The column.</param>
    private Shape ColumnShape(ITable table, string named, Column column) => column.IsComputed
        ? new UnknownShape(IrUnknownReason.UnsupportedSqlType, $"{named}.{column.Name} is a computed column, which is not modelled yet.", column.At)
        : SqlTypes.ToShape(SchemaRules.ColumnFacts(model, table, column), column.Type, column.At);

    /// <summary>
    /// The IR's reason for a binding problem. A table changed after its definition was reported as an unsupported
    /// source before binding had a layer of its own, and still is; a reference form the binder does not model is
    /// an unsupported expression.
    /// </summary>
    private static UnknownShape Unknown(BindingProblem problem) => new(
        problem.Kind switch
        {
            BindingProblemKind.UnsupportedSource or BindingProblemKind.TableRedefined => IrUnknownReason.UnsupportedSource,
            BindingProblemKind.UnresolvedTable => IrUnknownReason.UnresolvedTable,
            BindingProblemKind.UnresolvedColumn => IrUnknownReason.UnresolvedColumn,
            BindingProblemKind.UnsupportedReference => IrUnknownReason.UnsupportedExpression,
            BindingProblemKind.UnsupportedStar => IrUnknownReason.UnsupportedQuery,
        },
        problem.Detail,
        problem.At);

    private Value Subquery(ScalarSubquery subquery, ReadScope scope)
    {
        var at = At(subquery);

        // A scalar subquery's value is its one column's, with its type and facts, and null when it returns no row; more
        // than one row is an error, not a value. A query that returns exactly one row (an aggregate without GROUP BY, or no
        // FROM) is never empty (observed: A_scalar_subquery_of_an_aggregate_without_group_by_always_has_a_value_for_count).
        // JSON stays live through it: observed for a constructor, an aggregate, json_query, a json value and a FOR JSON
        // subquery (with its wrapper) inside it.
        if (ForOf(subquery.QueryExpression) is null)
        {
            var plain = subquery.QueryExpression;
            var row = Nested(plain, scope);

            // A row unknown as a whole (GROUP BY ALL) gives its own reason, as it does for a derived table (Holds).
            if (row is { Unlisted: { } whole, Columns: [] })
                return Value.Unknown(whole);
            if (row is not { Unlisted: null, Columns: [var only] })
                return Value.Unknown(Unsupported(IrUnknownReason.UnsupportedExpression, $"A subquery of other than one listed column is not modelled: {Text(subquery)}", at));
            if (only.Value.Shape is UnknownShape)
                return only.Value;
            // A scalar subquery is not folded: (SELECT 1) counts an int's 10 digits beside a decimal (observed:
            // An_int_constant_counts_its_own_digits_beside_a_decimal).
            return row.Rows == IrCardinality.One
                ? only.Value with { Digits = IntDigits.OfType, Pieces = only.Value.Pieces }
                : only.Value with { Digits = IntDigits.OfType, NullableBecause = [.. only.Value.NullableBecause, Empty(row, at)], Pieces = only.Value.Pieces };
        }

        var query = subquery.QueryExpression;
        if (ForOf(query) is XmlForClause xml)
            return ForXmlValue(subquery, xml, scope);
        if (ForOf(query) is not JsonForClause json)
            return Value.Unknown(Unsupported(IrUnknownReason.UnsupportedExpression, $"A subquery with this FOR clause is not modelled: {Text(subquery)}", at));

        // A FOR JSON subquery is JSON that an enclosing FOR JSON or constructor embeds without escaping, but only with
        // its array wrapper: WITHOUT_ARRAY_WRAPPER makes it text, escaped as a string wherever it nests, until json_query
        // makes it JSON again (both observed). Either is null when the subquery returns no rows, unless its query always
        // returns exactly one.
        var forJson = Nested(query, scope);
        var shape = ForJson(forJson, json, subquery: true);
        if (shape is UnknownShape unknown)
            return Value.Unknown(unknown);
        EquatableArray<IrNullReason> empty = forJson.Rows == IrCardinality.One ? [] : [Empty(forJson, at)];
        // Without its wrapper, the text of more than one row is several values, which brackets around it make an array of
        // (observed: A_for_json_fragment_without_its_array_wrapper_holds_every_row_so_in_brackets_it_is_an_array_of_them), so
        // only text of one row at most is one JSON value as text.
        return json.Options.Any(o => o.OptionKind == JsonForClauseOptions.WithoutArrayWrapper)
            ? new Value(new JsonTextShape(shape), empty) { Pieces = forJson.Rows is IrCardinality.One or IrCardinality.AtMostOne ? [new JsonPiece(shape)] : null }
            : new Value(shape, empty, IsLiveJson: true);
    }

    /// <summary>
    /// A <c>FOR XML</c> subquery as a value, in any mode (<c>RAW</c>, <c>AUTO</c>, <c>PATH</c>, <c>EXPLICIT</c>) and with any other
    /// option: <c>xml</c> with <c>TYPE</c>, <c>nvarchar(max)</c> without; NULL exactly when its query returns no rows, a
    /// <c>ROOT</c> included, and never NULL over a row, even one whose every column is NULL (observed:
    /// A_for_xml_subquery_is_xml_with_type_and_nvarchar_max_without_and_null_only_over_no_rows). Without <c>TYPE</c> the
    /// text escapes <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c>, so <c>STUFF((SELECT ',' + name … FOR XML PATH('')), 1, 1, '')</c>
    /// holds <c>&amp;amp;</c> for <c>&amp;</c>: a matter of its value, not its type. A column not modelled may be one the server
    /// refuses there, so it leaves the value unknown with its own reason.
    /// </summary>
    private Value ForXmlValue(ScalarSubquery subquery, XmlForClause clause, ReadScope scope)
    {
        var row = Nested(subquery.QueryExpression, scope);
        return ForXmlText(row, clause, subquery);
    }

    /// <summary>
    /// What a <c>FOR XML</c> query whose rows are <paramref name="row"/> builds, a subquery's value or the one column of a
    /// derived table or CTE (<see cref="ForXmlRelation"/>): see <see cref="ForXmlValue"/>.
    /// </summary>
    private Value ForXmlText(Row row, XmlForClause clause, TSqlFragment query)
    {
        if (row.Unlisted is { } whole)
            return Value.Unknown(whole);
        if (row.Columns.Select(c => c.Value).FirstOrDefault(v => v.Shape is UnknownShape) is { } unknown)
            return Value.Unknown((UnknownShape)unknown.Shape);

        var type = clause.Options.Any(o => o.OptionKind == XmlForClauseOptions.Type)
            ? ScalarType.Of(BuiltInType.Xml)
            : new ScalarType(BuiltInType.NVarChar, -1);
        return Typed(type, row.Rows == IrCardinality.One ? [] : [Empty(row, At(query))], query);
    }

    /// <summary>
    /// A derived table or a CTE whose query is <c>FOR XML</c>, <c>(SELECT … FOR XML PATH('')) AS d (x)</c>: exactly one row, over no
    /// rows too, whose one column, named by the column list the server requires (8155), is the text the query builds, as a
    /// subquery's value is: <c>xml</c> with <c>TYPE</c>, <c>nvarchar(max)</c> without, NULL when the query returns no rows (observed:
    /// A_for_xml_derived_table_or_cte_is_one_row_of_what_the_query_builds). Null when the query has no FOR clause; <c>FOR JSON</c>
    /// there is unknown.
    /// </summary>
    /// <param name="symbol">The derived table or CTE.</param>
    /// <param name="query">Its query, as written.</param>
    /// <param name="columns">Its column list, as written.</param>
    /// <param name="row">Its query's rows.</param>
    private LocalRelationValues? ForXmlRelation(RelationSymbol symbol, QueryExpression query, IList<Identifier> columns, Row row)
    {
        var at = At(query);
        switch (ForOf(query))
        {
            case null:
                return null;
            case XmlForClause clause when columns.Count == 1:
                var value = ForXmlText(row, clause, query);
                return value.Shape is UnknownShape unknown
                    ? LocalRelationValues.Unknown(unknown)
                    : new LocalRelationValues([columns[0].Value], [value], null) { Rows = IrCardinality.One, AtLeastOne = true };
            case XmlForClause:
                return LocalRelationValues.Unknown(Unsupported(IrUnknownReason.UnsupportedQuery, $"{symbol.Name}: a FOR XML query gives one column, which a column list must name (8155).", at));
            default:
                return LocalRelationValues.Unknown(Unsupported(IrUnknownReason.UnsupportedQuery, $"{symbol.Name}: a derived table or CTE whose query has this FOR clause is not modelled.", at));
        }
    }

    /// <summary>
    /// Why a scalar subquery that may return no row is NULL: <c>empty-subquery</c>; or, for a read of a row an existence guard
    /// found and nothing holds, <c>read-twice</c> at the guard, since only another session can have deleted it between the two
    /// reads (observed: A_row_an_existence_guard_saw_may_be_gone_when_a_later_statement_reads_it), as COALESCE's second read.
    /// </summary>
    private static IrNullReason Empty(Row row, SourceLocation at) => row.Raced is { } guard
        ? new IrNullReason(IrNullRule.ReadTwice, guard.ToString())
        : new IrNullReason(IrNullRule.EmptySubquery, at.ToString());

    /// <summary>
    /// <c>json_query</c>, with a path (<see cref="JsonQueryAt"/>) or without (<see cref="JsonQuery"/>). Its row reads no clause, so
    /// <c>WITH ARRAY WRAPPER</c>, which gives an array of one element instead, the object or array found, or at a path a scalar
    /// too, which without it is NULL (observed: Json_query_with_array_wrapper_wraps_what_it_finds_in_an_array_a_scalar_at_a_path_too),
    /// has made the call unknown before it reaches here (decision 0113).
    /// </summary>
    private Value JsonQueryCall(FunctionCall call, ReadScope scope) =>
        call.Parameters.Count == 2 ? JsonQueryAt(call, scope) : JsonQuery(call, scope);

    private Value JsonQuery(FunctionCall call, ReadScope scope)
    {
        // A literal, or CASE, IIF, CHOOSE or COALESCE over JSON, is read for what it holds (Typer.Json.cs and Walker.Json.cs).
        var argument = JsonQueryArgument(call.Parameters[0], scope);
        if (argument.IsLiveJson || argument.Shape is UnknownShape)
            return argument;
        if (argument.Shape is JsonTextShape { Content: { } shape })
        {
            var queried = ObjectOrArray(shape, call);
            return queried is UnknownShape unknown ? Value.Unknown(unknown) : new Value(queried, argument.NullableBecause, IsLiveJson: true);
        }
        if (Assembled(argument, call.Parameters[0]) is { } assembled)
            return assembled is UnknownShape notKnown ? Value.Unknown(notKnown) : new Value(assembled, argument.NullableBecause, IsLiveJson: true);
        if (argument.JsonWhy is { } why)
            return Value.Unknown(why);

        // json_query over text (a column, a variable) makes it live JSON, but its shape is in data haruspex cannot see.
        return Value.Unknown(Unsupported(IrUnknownReason.JsonShapeUnknown, $"{Text(call)} reads JSON that is not built here, so its shape is unknown.", At(call)));
    }

    /// <summary>
    /// What <c>json_query</c> gives of text holding JSON of <paramref name="shape"/>: an object or an array, which it returns as
    /// JSON, unchanged as a value (observed: Json_query_of_stored_text_returns_an_object_or_array_as_the_same_json). Text that is
    /// a JSON scalar, <c>null</c> included, is an error (13609) under a lax path too (observed:
    /// Json_query_of_stored_text_that_is_not_an_object_or_array_is_an_error), so a variant of the content that is one never
    /// arrives, and content that is only ever one makes every call fail. JSON built here is an object or an array already.
    /// </summary>
    private Shape ObjectOrArray(Shape shape, FunctionCall call) => shape switch
    {
        AnyJsonShape => new AnyJsonShape(ObjectOrArray: true),
        UnionShape union when union.Variants.Where(v => ObjectOrArray(v, call) is not UnknownShape).ToList() is { Count: > 0 } kept =>
            kept.Count == 1 ? ObjectOrArray(kept[0], call) : new UnionShape([.. kept.Select(v => ObjectOrArray(v, call))]),
        ScalarShape or UnionShape => Failing(call),
        _ => shape,
    };

    /// <summary>Why a call of <c>json_query</c> over JSON that is only ever a scalar never returns.</summary>
    private UnknownShape Failing(FunctionCall call) =>
        Unsupported(IrUnknownReason.JsonShapeUnknown, $"{Text(call)} reads JSON declared to be a scalar, which json_query refuses with an error (13609), so it never returns a value.", At(call));

    // ---- scalar functions ---------------------------------------------------------------------------------
    //
    // Observed on SQL Server 2025 RTM-CU10 and kept as tests (JsonFunctionObservationTests): a call of a function
    // returning nvarchar(max) nests as an escaped string in FOR JSON and the constructors, even when it holds JSON;
    // json_query over it, or a return type of json, nests it as JSON. FOR JSON over no rows returns NULL from the function,
    // and a key whose value is NULL is left out of FOR JSON PATH. RETURNS NULL ON NULL INPUT makes f(NULL) NULL without
    // running the body. FunctionSummaries says which bodies are read and how recursion is solved.

    private Value CallFunction(FunctionCall call, ObjectName callee, ReadScope scope)
    {
        // No clause is read of a user's function (decision 0113), and the server takes none but OVER and WITHIN GROUP, which it
        // refuses for a scalar function when the procedure is created.
        var at = At(call);
        if (CallClauses.Of(call) != Clause.None)
            return Value.Unknown(Unsupported(IrUnknownReason.UnsupportedExpression, $"This form of call is not modelled: {Text(call)}", at));

        // A function's body read in a statement that reads uncommitted reads its tables so too.
        var (result, why) = summaries.Function(callee, at, ReadUncommitted(scope), source);
        if (result is null)
            return Value.Unknown(why!); // Never null: Find gives a return or why there is none.

        var because = new List<IrNullReason>(result.Content is null ? [new IrNullReason(IrNullRule.FunctionResult, at.ToString())] : result.NullableBecause);

        // With RETURNS NULL ON NULL INPUT a null argument makes the call NULL. The arguments are read only for that.
        if (result.NullOnNullInput && MayBeNullInput(call, callee, scope))
            because.Add(new IrNullReason(IrNullRule.NullInput, at.ToString()));

        var value = result switch
        {
            { AsText: true, Content: { } assembled } => new Value(result.Declared, [.. because]) { Digits = IntDigits.OfType, Pieces = [new JsonPiece(assembled)] },
            { Content: null, ReturnsJson: true } => new Value(result.Declared, [.. because], IsLiveJson: true),
            { Content: null } => Value.Computed(result.Declared, [.. because]),
            { ReturnsJson: true } => result.Content is UnknownShape unknown ? Value.Unknown(unknown) : new Value(result.Content, [.. because], IsLiveJson: true),
            _ => new Value(new JsonTextShape(result.Content), [.. because]),
        };
        return model.FindFunction(callee) is { } function ? MaskedCall(value, function, call) : value;
    }

    /// <summary>
    /// Whether an argument of a call may be NULL as its function receives it: null itself, or rounded to its parameter's fewer
    /// places under NUMERIC_ROUNDABORT on, which makes a function declared RETURNS NULL ON NULL INPUT return NULL without
    /// running (observed: A_loss_of_precision_is_null_when_numeric_roundabort_is_on_and_ansi_warnings_and_arithabort_are_off).
    /// </summary>
    private bool MayBeNullInput(FunctionCall call, ObjectName callee, ReadScope scope)
    {
        var parameters = model.FindFunction(callee)?.Parameters ?? [];
        return call.Parameters.Select((argument, i) =>
        {
            var value = Expression(argument, scope);
            var type = i < parameters.Count ? SqlTypes.Modelled(SchemaRules.ResolveType(model, parameters[i].Type)?.Scalar) : null;
            return value.NullableBecause.Count > 0 || (type is not null && Rounds(value, argument, type) && summaries.Session.RoundingMayBeNull(attempt: false));
        }).Any(mayBeNull => mayBeNull);
    }

    // ---- JSON constructors ----------------------------------------------------------------------------
    //
    // Every rule here was observed on SQL Server 2025 RTM-CU10 before it was relied on, and is kept as a test
    // (JsonConstructorObservationTests in the differential tests):
    // - json_object and json_objectagg keep a null value's key by default (NULL ON NULL); json_array and
    //   json_arrayagg drop a null by default (ABSENT ON NULL). Each takes the other option when it is written.
    // - A constructor, json_query, a FOR JSON subquery with its array wrapper and a json value nest as JSON in a
    //   constructor and in FOR JSON; any other string is escaped text, a FOR JSON subquery WITHOUT_ARRAY_WRAPPER
    //   included. So a constructor is live JSON, and a value keeps its shape (and its
    //   facets, while it passes through unchanged) and its nullability.
    // - Scalars are written as FOR JSON writes them (dates, times, GUIDs, bit, decimals, bigint, padded char), so
    //   they keep the kinds they have in FOR JSON.
    // - The aggregates over no rows are NULL, not [] or {}; under GROUP BY there is no group without a row, even
    //   with ROLLUP or GROUP BY (), so there they are never null.
    // - json_object writes a repeated literal key twice, and json_objectagg every repeated key: JSON.parse keeps the
    //   last. A key can be any expression; a null key is an error, not a value.
    // json_object and json_array exist from SQL Server 2022, the aggregates from 2025 (and Azure SQL). The target's
    // version is not known yet, so a constructor it lacks fails when the procedure is created, not here.

    /// <summary>A call of a JSON constructor: <c>json_object</c>, <c>json_array</c> and their aggregates.</summary>
    private static bool IsConstructor(FunctionCall call) =>
        call.CallTarget is null
        && ((BuiltInFunctions.Named(call.FunctionName.Value, FunctionForm.Scalar) is { } scalar && (scalar == ScalarFunctions.JsonObject || scalar == ScalarFunctions.JsonArray))
            || (BuiltInFunctions.Named(call.FunctionName.Value, FunctionForm.Aggregate) is { } aggregate && (aggregate == AggregateFunctions.JsonArrayAgg || aggregate == AggregateFunctions.JsonObjectAgg)));

    private Value Constructor(FunctionCall call, ReadScope scope, BuiltInFunction function)
    {
        var at = At(call);
        var name = function.Name;

        // Any clause but those its row reads (the pairs, the null option, RETURNING, and json_arrayagg's ORDER BY) has made the
        // call unknown already, in Function (decision 0113).

        // RETURNING json changes the value's SQL type, not what it writes or how it nests (observed).
        if (call.ReturnType is { Count: > 0 } returns && returns is not [SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Json }])
            return Value.Unknown(Unsupported(IrUnknownReason.UnsupportedExpression, $"{name} returning anything but json is not modelled: {Text(call)}", at));

        bool? nullOnNull = call.AbsentOrNullOnNull switch
        {
            null or [] => null,
            [{ Value: var option }] when option.Equals("NULL", StringComparison.OrdinalIgnoreCase) => true,
            [{ Value: var option }] when option.Equals("ABSENT", StringComparison.OrdinalIgnoreCase) => false,
            _ => throw new InvalidOperationException($"Unexpected null option in {Text(call)}."),
        };

        if (function == ScalarFunctions.JsonObject)
            return JsonObjectConstructor(call, scope, nullOnNull ?? true);
        if (function == ScalarFunctions.JsonArray)
            return JsonArrayConstructor(call, scope, nullOnNull ?? false);
        if (function == AggregateFunctions.JsonArrayAgg)
            return ArrayAggregate(call, scope, nullOnNull ?? false);
        return ObjectAggregate(call, scope, nullOnNull ?? true);
    }

    /// <summary><c>json_object('k': v, …)</c>: an object of literal keys, never null.</summary>
    private Value JsonObjectConstructor(FunctionCall call, ReadScope scope, bool nullOnNull)
    {
        var fields = new List<FieldShape>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in call.JsonParameters)
        {
            // Keys are compared by case, as JSON compares them: 'a' and 'A' are two keys (observed).
            if (pair.JsonKeyName is not StringLiteral key)
                return Value.Unknown(Unsupported(IrUnknownReason.UnsupportedExpression, $"The key {Text(pair.JsonKeyName)} of json_object is not a literal, so the object's keys are data.", At(pair.JsonKeyName)));
            if (SqlcmdVariableReference.FirstIn(key.Value) is { } variable)
                return Value.Unknown(Unsupported(IrUnknownReason.SqlcmdVariable, $"The key {Text(key)} of json_object holds the SQLCMD variable {variable}, which a deployment replaces with its value, so the object's keys are not known.", At(key)));
            if (!keys.Add(key.Value))
                return Value.Unknown(Unsupported(IrUnknownReason.DuplicateColumn, $"The key '{key.Value}' is used more than once in json_object, which writes it twice.", At(pair.JsonKeyName)));

            var value = Expression(pair.JsonValue, scope);
            var presence = value.NullableBecause.Count > 0 && !nullOnNull ? IrPresence.OmittedWhenNull : IrPresence.Always;
            fields.Add(new FieldShape(key.Value, value.Shape, presence, value.NullableBecause));
        }

        return new Value(new ObjectShape([.. fields]) { Writers = KeyWriters.SqlServer }, [], IsLiveJson: true);
    }

    /// <summary>
    /// <c>json_array(v, …)</c>, never null. Under NULL ON NULL every position stays, so it is a tuple. Under ABSENT ON
    /// NULL a null is dropped and the rest close up: a tuple only when no element can be null, otherwise an array of the
    /// elements' types, with as many items as the elements never null and at most one per element.
    /// </summary>
    private Value JsonArrayConstructor(FunctionCall call, ReadScope scope, bool nullOnNull)
    {
        var elements = call.Parameters.Select(p => Expression(p, scope)).ToList();
        if (nullOnNull || elements.All(e => e.NullableBecause.Count == 0))
            return new Value(new TupleShape([.. elements.Select(e => new ElementShape(e.Shape, e.NullableBecause))]), [], IsLiveJson: true);

        var variants = Distinct(elements.Select(e => e.Shape));
        var present = elements.Count(e => e.NullableBecause.Count == 0);
        return new Value(
            new ArrayShape(variants.Count == 1 ? variants[0] : new UnionShape([.. variants]), [], MinItems: present > 0 ? present : null, MaxItems: elements.Count),
            [],
            IsLiveJson: true);
    }

    /// <summary>
    /// The different shapes among <paramref name="shapes"/>, in order. Scalars are the same when they are equal (the same
    /// kind, format, SQL type and facets); any other shape is kept as it is, since telling two objects apart is not worth the
    /// risk of merging them.
    /// </summary>
    private static List<Shape> Distinct(IEnumerable<Shape> shapes)
    {
        var result = new List<Shape>();
        var seen = new HashSet<IrScalar>();
        foreach (var shape in shapes)
        {
            if (shape is ScalarShape scalar && !seen.Add(scalar.Type))
                continue;
            result.Add(shape);
        }

        return result;
    }

    /// <summary>
    /// <c>json_arrayagg(v [ORDER BY …])</c>: an array of v. A non-null result means at least one row, so it has an item
    /// unless ABSENT ON NULL can drop them all.
    /// </summary>
    private Value ArrayAggregate(FunctionCall call, ReadScope scope, bool nullOnNull)
    {
        if (call.Parameters.Count != 1)
            return Value.Unknown(Unsupported(IrUnknownReason.UnsupportedExpression, $"json_arrayagg of {call.Parameters.Count} values is not modelled: {Text(call)}", At(call)));

        var value = Expression(call.Parameters[0], scope.InAggregate());
        var neverEmpty = nullOnNull || value.NullableBecause.Count == 0;
        return new Value(
            new ArrayShape(value.Shape, nullOnNull ? value.NullableBecause : [], MinItems: neverEmpty ? 1 : null),
            Aggregate(call, scope),
            IsLiveJson: true);
    }

    /// <summary>
    /// <c>json_objectagg(k: v)</c>: keys come from data, so a record of string to v. Every key is a string whatever the
    /// key expression's type, so the key is not typed; a repeated key is written each time, and JSON.parse keeps the last.
    /// </summary>
    private Value ObjectAggregate(FunctionCall call, ReadScope scope, bool nullOnNull)
    {
        if (call.JsonParameters is not [var pair])
            return Value.Unknown(Unsupported(IrUnknownReason.UnsupportedExpression, $"This form of json_objectagg is not modelled: {Text(call)}", At(call)));

        var value = Expression(pair.JsonValue, scope.InAggregate());
        return new Value(new RecordShape(value.Shape, nullOnNull ? value.NullableBecause : []), Aggregate(call, scope), IsLiveJson: true);
    }

    /// <summary>
    /// Why an aggregate can be null: over no rows it is NULL, which only a query without GROUP BY can give it (with
    /// GROUP BY, no rows means no groups at all).
    /// </summary>
    private EquatableArray<IrNullReason> Aggregate(FunctionCall call, ReadScope scope) =>
        scope.Query?.GroupByClause is null && !ProvedAggregate(call, scope) ? [new IrNullReason(IrNullRule.EmptyAggregate, At(call).ToString())] : [];

    // ---- helpers ------------------------------------------------------------------------------------

    internal static UnknownShape Unsupported(IrUnknownReason reason, string detail, SourceLocation at) => new(reason, detail, at);

    private SourceLocation At(TSqlFragment fragment) => source.At(fragment);

    internal static string Text(SourceLocation at) => $"at {at}";

    internal static string Text(TSqlFragment fragment) => FragmentText.OneLine(fragment);
}
