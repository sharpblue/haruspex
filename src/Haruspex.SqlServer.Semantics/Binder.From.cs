using Haruspex.Collections;
using Haruspex.Diagnostics;
using Haruspex.SqlServer.Model;
using Haruspex.SqlServer.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Haruspex.SqlServer.Semantics;

// What a FROM clause's table references read: joins and APPLY, names, derived tables and rowsets, each a source of the query's scope.
public sealed partial class Binder
{
    /// <summary>
    /// The sources being added to one query's scope, the scope beyond it, and the names in force. A builder, not a value: each
    /// copy made for a part of the FROM adds to the one list of sources, and nothing compares two.
    /// </summary>
    private sealed class Adding(List<BoundSource> sources, QueryScope? parent, NameEnvironment names, QueryScope? lateral = null, OptionalSideKind side = OptionalSideKind.OuterJoin)
    {
        /// <summary>The query's sources so far, shared by every copy.</summary>
        public List<BoundSource> Sources { get; } = sources;

        /// <summary>The scope beyond the query: the enclosing query's, or none.</summary>
        public QueryScope? Parent { get; } = parent;

        /// <summary>The names in force.</summary>
        public NameEnvironment Names { get; } = names;

        /// <summary>
        /// Inside an APPLY's right side, the scope it reads outer columns from (its left input's sources, then the enclosing
        /// query's); null elsewhere, where a derived table reads only the enclosing query's.
        /// </summary>
        public QueryScope? Lateral { get; } = lateral;

        /// <summary>How the optional side being added to came about: an outer join's, or an OUTER APPLY's right side, whole or inside.</summary>
        public OptionalSideKind Side { get; } = side;

        /// <summary>The scope a derived table, <c>VALUES</c> list or call here reads beyond itself.</summary>
        public QueryScope? Outer => Lateral ?? Parent;

        /// <summary>The same sources, for an optional side that came about as <paramref name="kind"/> says.</summary>
        public Adding On(OptionalSideKind kind) => new(Sources, Parent, Names, Lateral, kind);

        /// <summary>The same sources, inside an APPLY's right side that reads <paramref name="left"/>'s columns.</summary>
        public Adding Reading(QueryScope left) => new(Sources, Parent, Names, left, Side);
    }

    /// <param name="adding">The scope being built, and where in its FROM the reference is.</param>
    /// <param name="reference">The table reference.</param>
    /// <param name="optionalSideOf">The outermost optional side it is inside, if any.</param>
    private void AddSources(Adding adding, TableReference reference, SourceLocation? optionalSideOf)
    {
        var optional = Optional(optionalSideOf, adding.Side);
        switch (reference)
        {
            case JoinTableReference or JoinParenthesisTableReference:
                AddJoined(adding, reference, optionalSideOf);
                break;
            case NamedTableReference named:
                // FOR SYSTEM_TIME other than AS OF reads history too, on a table or on a view (observed:
                // For_system_time_other_than_as_of_repeats_a_key_through_a_table_or_a_view).
                // NOLOCK and READUNCOMMITTED read uncommitted rows, a scan of which can repeat a row
                // (docs/decisions/0016-visible-read-uncommitted-drops-key-claims.md).
                adding.Sources.Add(new BoundSource(named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value, Named(named, adding.Names), optional, named)
                {
                    History = named.TemporalClause is { TemporalClauseType: not TemporalClauseType.AsOf },
                    Uncommitted = Uncommitted(named),
                    Exposed = named.Alias is null ? ExposedNameOf(named.SchemaObject, adding.Names) : null,
                    NamesSynonym = named.Alias is null && IsSynonym(named.SchemaObject, adding.Names),
                });
                break;
            case VariableTableReference variable:
                // A table variable is the body's own; without a declaration of it in hand it is not modelled.
                adding.Sources.Add(new BoundSource(
                    variable.Alias?.Value ?? variable.Variable.Name,
                    adding.Names.Declarations.Variable(new LocalName(variable.Variable.Name)) is CaseVariantVariable variants
                        ? new UnresolvedTarget(Problem(BindingProblemKind.UnsupportedSource, variants.Why, At(reference)))
                        : adding.Names.Find(variable.Variable.Name) is { } relation
                            ? new RelationTarget(relation)
                            : new UnresolvedTarget(Problem(BindingProblemKind.UnsupportedSource, $"The table variable {variable.Variable.Name} is not modelled here.", At(reference))),
                    optional,
                    variable));
                break;
            case SemanticTableReference semantic:
                AddUnresolved(adding.Sources, reference, Problem(BindingProblemKind.UnsupportedSource, SemanticWhy(semantic), At(reference)));
                break;
            default:
                if (Relation(adding, reference) is { } bound)
                {
                    // A function called without an alias is known by its own full name (observed:
                    // A_view_a_synonym_and_a_function_bind_by_their_own_full_names).
                    adding.Sources.Add(new BoundSource(bound.Alias, new RelationTarget(bound.Relation), optional, reference)
                    {
                        Exposed = bound.Relation is TableFunctionCall { Syntax.Alias: null, Callee: KnownCallee { Function.Name: var function } }
                            ? new ExposedName(function.Schema, function.Name)
                            : null,
                    });
                }
                else
                {
                    AddUnresolved(adding.Sources, reference, Problem(BindingProblemKind.UnsupportedSource, $"The table source {FragmentText.OneLine(reference)} is not modelled yet.", At(reference)));
                }

                break;
        }
    }

    /// <summary>
    /// The sources of a join, an APPLY or a parenthesised join, each on the optional side it is on: a LEFT JOIN's right side,
    /// a RIGHT JOIN's left side, either side of a FULL JOIN, an OUTER APPLY's right side; inside an outer join's optional side the enclosing one is kept,
    /// as for nested outer joins.
    /// </summary>
    private void AddJoined(Adding adding, TableReference reference, SourceLocation? optionalSideOf)
    {
        // A source joined inside an OUTER APPLY's right side is on that side, but is not the whole of it.
        var inside = adding.On(adding.Side == OptionalSideKind.WholeApplied ? OptionalSideKind.InsideApplied : adding.Side);
        var asJoin = inside.On(OptionalSideKind.OuterJoin);

        switch (reference)
        {
            case QualifiedJoin { QualifiedJoinType: QualifiedJoinType.Inner } join:
                AddSources(inside, join.FirstTableReference, optionalSideOf);
                AddSources(inside, join.SecondTableReference, optionalSideOf);
                break;
            case QualifiedJoin { QualifiedJoinType: QualifiedJoinType.LeftOuter } join:
                // The right side has no row when nothing matches. Recorded on each of its sources, so a reader
                // can tell that any column read from them may have no value, whatever its declaration says.
                AddSources(inside, join.FirstTableReference, optionalSideOf);
                AddSources(optionalSideOf is null ? asJoin : inside, join.SecondTableReference, optionalSideOf ?? At(join.SecondTableReference));
                break;
            case QualifiedJoin { QualifiedJoinType: QualifiedJoinType.RightOuter } join:
                // A left join with its sides swapped (observed: A_right_outer_join_is_a_left_outer_join_with_its_sides_swapped):
                // the left side has no row when nothing matches.
                AddSources(optionalSideOf is null ? asJoin : inside, join.FirstTableReference, optionalSideOf ?? At(join.FirstTableReference));
                AddSources(inside, join.SecondTableReference, optionalSideOf);
                break;
            case UnqualifiedJoin { UnqualifiedJoinType: UnqualifiedJoinType.CrossApply or UnqualifiedJoinType.OuterApply } apply:
            {
                // The right side is read once for each row of the left input, and may read its columns; not those of a
                // source listed before it with a comma, which is not part of its left input (observed:
                // An_applied_query_sees_its_left_input_only). OUTER APPLY null-extends an outer row the right side gives
                // no row for, as a left join does; CROSS APPLY drops it, so its right side is optional only as the APPLY is.
                var first = adding.Sources.Count;
                AddSources(inside, apply.FirstTableReference, optionalSideOf);
                var left = new QueryScope(adding.Outer, [.. adding.Sources.Skip(first)], adding.Names);
                if (apply.UnqualifiedJoinType == UnqualifiedJoinType.OuterApply && optionalSideOf is null)
                    AddSources(adding.Reading(left).On(OptionalSideKind.WholeApplied), apply.SecondTableReference, At(apply.SecondTableReference));
                else
                    AddSources(inside.Reading(left), apply.SecondTableReference, optionalSideOf);
                break;
            }

            case QualifiedJoin { QualifiedJoinType: QualifiedJoinType.FullOuter } join:
                // Each side has no row where nothing on the other matches, so both are optional, each at its own location
                // (observed: A_full_outer_join_null_extends_both_sides_where_nothing_matches).
                AddSources(optionalSideOf is null ? asJoin : inside, join.FirstTableReference, optionalSideOf ?? At(join.FirstTableReference));
                AddSources(optionalSideOf is null ? asJoin : inside, join.SecondTableReference, optionalSideOf ?? At(join.SecondTableReference));
                break;
            case UnqualifiedJoin { UnqualifiedJoinType: UnqualifiedJoinType.CrossJoin } cross:
                // Every row of one with every row of the other: both sides read as they are.
                AddSources(inside, cross.FirstTableReference, optionalSideOf);
                AddSources(inside, cross.SecondTableReference, optionalSideOf);
                break;
            case JoinParenthesisTableReference parenthesis:
                AddSources(inside, parenthesis.Join, optionalSideOf);
                break;

            // Every join ScriptDom 170 has is above. One a later ScriptDom adds is unresolved until it is modelled, which makes
            // every column read through it unknown rather than wrongly non-null.
            default:
                AddUnresolved(adding.Sources, reference, Problem(BindingProblemKind.UnsupportedSource, "This join is not modelled yet.", At(reference)));
                break;
        }
    }

    /// <summary>
    /// The relation a derived table or a rowset in FROM is, with the alias its columns are qualified by: a derived table, a
    /// table-valued function's call, XML's nodes(), a VALUES list, OPENJSON, OPENXML, GENERATE_SERIES, STRING_SPLIT, CONTAINSTABLE
    /// or FREETEXTTABLE. Each reads the enclosing query's scope, or an APPLY's left input's, never the sources beside it. Null for
    /// any other reference.
    /// </summary>
    private (string? Alias, RelationSymbol Relation)? Relation(Adding adding, TableReference reference)
    {
        switch (reference)
        {
            case QueryDerivedTable derived:
            {
                // Its alias is required, and names it in this scope only.
                var query = Bind(derived.QueryExpression, adding.Outer, adding.Names);
                return (derived.Alias?.Value, new DerivedTable(derived, query, query.Listed([.. derived.Columns.Select(c => c.Value)])));
            }

            case SchemaObjectFunctionTableReference call:
            {
                // x.nodes('path') AS t(c) is XML's, not a function: a function takes no column list (317), and nodes() needs one.
                if (call.Columns.Count > 0 && XmlMethods.Named(call.SchemaObject.BaseIdentifier.Value) == XmlMethod.Nodes && NodesTarget(call.SchemaObject) is { } target)
                    return (call.Alias?.Value, Nodes(adding, call, target, call.Parameters, call.Columns));

                // A table-valued function takes no table hints (a syntax error), so it is never read uncommitted by its own.
                var callee = Callee(call.SchemaObject);
                return (call.Alias?.Value ?? call.SchemaObject.BaseIdentifier.Value, new TableFunctionCall(call, callee, QueryScope.Empty(adding.Outer, adding.Names), FunctionColumns(call, callee)));
            }

            case VariableMethodCallTableReference method when XmlMethods.Named(method.MethodName.Value) == XmlMethod.Nodes:
                return (method.Alias?.Value, Nodes(adding, method, method.Variable, method.Parameters, method.Columns));

            case InlineDerivedTable values:
            {
                // SQL Server requires a name for every column (8155), the same number of values in every row (10709), and
                // distinct names.
                var names = values.Columns.Select(c => c.Value).ToList();
                var listed = names.Count > 0 && values.RowValues.All(r => r.ColumnValues.Count == names.Count)
                    && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count;
                return (values.Alias?.Value, new ValuesTable(values, QueryScope.Empty(adding.Outer, adding.Names), listed ? [.. names] : null));
            }

            case OpenJsonTableReference openJson:
            {
                List<string> names = openJson.SchemaDeclarationItems.Count == 0
                    ? ["key", "value", "type"]
                    : [.. openJson.SchemaDeclarationItems.Select(i => i.ColumnDefinition.ColumnIdentifier.Value)];
                return (openJson.Alias?.Value, new OpenJsonCall(openJson, QueryScope.Empty(adding.Outer, adding.Names), names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count ? [.. names] : null));
            }

            case OpenXmlTableReference openXml:
            {
                var table = OpenXmlCall.TableOf(openXml, Model);
                return (openXml.Alias?.Value, new OpenXmlCall(openXml, QueryScope.Empty(adding.Outer, adding.Names), table, OpenXmlCall.ColumnsOf(openXml, table)));
            }

            case GlobalFunctionTableReference { Name.Value: var function } series
                when BuiltInFunctions.Named(function, FunctionForm.Rowset) == RowsetFunctions.GenerateSeries:
                return (series.Alias?.Value, new SeriesCall(series, QueryScope.Empty(adding.Outer, adding.Names)));

            case GlobalFunctionTableReference { Name.Value: var function } split
                when BuiltInFunctions.Named(function, FunctionForm.Rowset) == RowsetFunctions.StringSplit:
                return (split.Alias?.Value, new SplitCall(split, QueryScope.Empty(adding.Outer, adding.Names), SplitCall.ColumnsOf(split)));

            case FullTextTableReference fullText:
            {
                // Without an alias it is known by its table's name, not the function's (observed:
                // A_full_text_search_without_an_alias_is_known_by_its_tables_name).
                var call = new FullTextCall(fullText, Searched(fullText));
                return (call.Name, call);
            }
            default:
                return null;
        }
    }

    /// <summary>XML's <c>nodes()</c>, read in the scope beyond it, as any call in FROM is; one column, named by its column list.</summary>
    private static XmlNodesCall Nodes(Adding adding, TableReference call, ScalarExpression target, IList<ScalarExpression> path, IList<Identifier> columns) =>
        new(call, target, [.. path], QueryScope.Empty(adding.Outer, adding.Names), columns.Count == 1 ? [columns[0].Value] : null);

    /// <summary>
    /// The column <c>nodes()</c> is called on, made of the parts of the name before it (<c>x</c>, <c>o.x</c>, <c>dbo.o.x</c>),
    /// placed where they are so that it binds and is located as written; null when there are none, or more than a column has.
    /// </summary>
    private static ColumnReferenceExpression? NodesTarget(SchemaObjectName name)
    {
        var parts = name.Identifiers.Take(name.Identifiers.Count - 1).ToList();
        if (parts.Count is < 1 or > 3)
            return null;
        var identifier = new MultiPartIdentifier
        {
            FirstTokenIndex = parts[0].FirstTokenIndex,
            LastTokenIndex = parts[^1].LastTokenIndex,
            ScriptTokenStream = name.ScriptTokenStream,
        };
        foreach (var part in parts)
            identifier.Identifiers.Add(part);
        return new ColumnReferenceExpression
        {
            ColumnType = ColumnType.Regular,
            MultiPartIdentifier = identifier,
            FirstTokenIndex = identifier.FirstTokenIndex,
            LastTokenIndex = identifier.LastTokenIndex,
            ScriptTokenStream = name.ScriptTokenStream,
        };
    }

    /// <summary>A source's optional side, from the location of the outermost one it is inside and how it came about.</summary>
    private static OptionalSide? Optional(SourceLocation? optionalSideOf, OptionalSideKind side) =>
        optionalSideOf is null ? null : new OptionalSide(optionalSideOf, side);

    /// <summary>Whether a table or view is read with a <c>NOLOCK</c> or <c>READUNCOMMITTED</c> hint.</summary>
    private static bool Uncommitted(NamedTableReference named) =>
        named.TableHints.Any(h => h.HintKind is TableHintKind.NoLock or TableHintKind.ReadUncommitted);

    /// <summary>
    /// The full name a table, view, synonym or temporary table named in FROM without an alias is known by: as written with its
    /// schema; a one-part name under the schema it resolves to (<see cref="SchemaNames.Resolve"/>), or dbo; a temporary table
    /// under any schema. Null for a CTE and the body's other relations, for a name whose schema depends on the caller (in a query
    /// file, where several schemas have the name), and for
    /// a name of another database: SQL Server binds a full name to it only when that database is the one the query runs in
    /// (observed: A_full_name_binds_a_source_named_with_this_databases_name), which the project can't say.
    /// </summary>
    private ExposedName? ExposedNameOf(SchemaObjectName name, NameEnvironment names)
    {
        if (Temporary(name) is { } temporary)
            return new ExposedName(null, temporary.Text);
        if (name.ServerIdentifier is not null || name.DatabaseIdentifier is not null)
            return null;
        var one = name.BaseIdentifier.Value;
        if (name.SchemaIdentifier is { Value: var schema })
            return new ExposedName(schema, one);
        if (names.Find(one) is { } relation)
            return relation is TemporaryTable ? new ExposedName(null, one) : null;
        return SchemaNames.Resolve(Readable(), one, search) switch
        {
            OneObject only => new ExposedName(only.Named.Name.Schema, only.Named.Name.Name),
            NoObject none => new ExposedName(none.InDbo.Schema, none.InDbo.Name),
            SeveralObjects => null,
        };
    }

    /// <summary>
    /// Whether a name in FROM names a synonym, of this database or of another one the source holds, by the rules
    /// <see cref="Named"/> reads it by; never a temporary table or a CTE.
    /// </summary>
    private bool IsSynonym(SchemaObjectName name, NameEnvironment names)
    {
        if (Temporary(name) is not null || (IsOnePart(name) && names.Find(name.BaseIdentifier.Value) is not null) || name.ServerIdentifier is not null)
            return false;
        if (name.DatabaseIdentifier is { Value: var database })
        {
            return Model.Databases.TryGetValue(database, out var other)
                && name.SchemaIdentifier is { Value: { Length: > 0 } schema }
                && other.FindReadable(new ObjectName(schema, name.BaseIdentifier.Value)) is Synonym;
        }

        ObjectName? objectName = name.SchemaIdentifier is not null
            ? SyntaxReader.Name(name)
            : SchemaNames.Resolve(Readable(), name.BaseIdentifier.Value, search) switch
            {
                OneObject one => one.Named.Name,
                NoObject none => none.InDbo,
                SeveralObjects => null,
            };
        return objectName is { } found && Model.FindReadable(found) is Synonym;
    }

    /// <summary>What a name in FROM reads.</summary>
    private SourceTarget Named(NamedTableReference named, NameEnvironment names)
    {
        var name = named.SchemaObject;
        var at = At(named);
        UnresolvedTarget Unresolved(BindingProblemKind kind, string detail) => new(Problem(kind, detail, at));

        // A temporary table, however its name is qualified (see Temporary), is the body's own.
        if (Temporary(name) is { } temporary && names.Find(temporary.Text) is { } own)
            return new RelationTarget(own);

        // A CTE hides a table of the same name, but only a one-part name can mean it.
        if (IsOnePart(name) && names.Find(name.BaseIdentifier.Value) is { } relation)
            return new RelationTarget(relation);

        // Another database is read only through a reference the source holds for it, by the very name the body writes: a
        // variable ([$(Ledger)]) never as a database of that literal name, a literal only as a reference's literal.
        if (name.ServerIdentifier is null && name.DatabaseIdentifier is { Value: var database } && Model.Databases.TryGetValue(database, out var other))
        {
            // db..t has a schema identifier with no text: the caller's default schema there decides.
            return name.SchemaIdentifier is { Value: { Length: > 0 } schema }
                ? InOtherDatabase(other, database, new ObjectName(schema, name.BaseIdentifier.Value), at)
                : Unresolved(BindingProblemKind.UnresolvedTable, $"The reference {FragmentText.OneLine(name)} names no schema, so what it reads depends on the caller's default schema in {database}.");
        }

        if (name.ServerIdentifier is not null || name.DatabaseIdentifier is not null)
            return Unresolved(BindingProblemKind.UnsupportedSource, $"The reference {FragmentText.OneLine(name)} to another database is not modelled yet.");

        if (name.SchemaIdentifier is not null)
            return InSchema(SyntaxReader.Name(name), at);

        // Views, synonyms and functions count too: a body may read one of them instead of a table.
        return SchemaNames.Resolve(Readable(), name.BaseIdentifier.Value, search) switch
        {
            OneObject one => InSchema(one.Named.Name, at),
            NoObject none => InSchema(none.InDbo, at),
            SeveralObjects several => Unresolved(
                BindingProblemKind.UnresolvedTable,
                $"{name.BaseIdentifier.Value} is not schema-qualified, and {string.Join(" and ", several.Candidates.Select(c => $"the {SchemaNames.Kind(c)} {c.Name}"))} have that name: which is read depends on the caller's default schema."),
        };
    }

    /// <summary>What a name of the schema reads: a table, a view (read from its definition), or why neither.</summary>
    /// <param name="objectName">The name, resolved to its schema.</param>
    /// <param name="at">Where the reference is.</param>
    private SourceTarget InSchema(ObjectName objectName, SourceLocation at)
    {
        UnresolvedTarget Unresolved(BindingProblemKind kind, string detail) => new(Problem(kind, detail, at));

        if (Model.FindTableRedefinition(objectName) is { } change)
            return new UnresolvedTarget(Problem(BindingProblemKind.TableRedefined, Redefinitions.Why(change), change.At));

        // A view is read from its definition, by whoever reads the body's values (it may say why it cannot be).
        if (Model.FindReadable(objectName) is View view)
            return new RelationTarget(new ViewRelation(view, ViewColumns(view)));

        // A synonym reads what it stands for: a table or view of this database, or a table of another one the source holds.
        if (Model.FindReadable(objectName) is Synonym { For: var target } synonym)
            return Through(synonym, target, at);
        if (Model.FindReadable(objectName) is { } other)
            return Unresolved(BindingProblemKind.UnresolvedTable, $"{objectName} is a {SchemaNames.Kind(other)}, which is not modelled yet.");

        if (Model.FindTable(objectName) is { } table)
            return new TableTarget(table);

        // The system's views (sys, INFORMATION_SCHEMA) are in every database; master.dacpac lists them.
        if (SystemViews.IsSystemSchema(objectName.Schema) && Model.Databases.TryGetValue(SystemViews.Master, out var master) && master.FindReadable(objectName) is View system)
            return new RelationTarget(new ViewRelation(system, system.Columns));

        return Unresolved(BindingProblemKind.UnresolvedTable, $"There is no table {objectName} in the project.{SchemaGaps.MayBeIn(Model)}");
    }

    /// <summary>
    /// What a synonym reads: a name of this database (one without a schema is looked for in the synonym's schema and then in
    /// <c>dbo</c>), or a name of another database the source holds. A synonym for a synonym, here or there, is refused when it is read (observed:
    /// Msg 470, "Synonym chaining is not allowed", for a chain, a cycle and a synonym for itself, though each can be created),
    /// so it is unknown with that reason, and following a synonym never goes further than its one target.
    /// </summary>
    private SourceTarget Through(Synonym synonym, SynonymTarget target, SourceLocation at)
    {
        UnresolvedTarget Unresolved(string detail) => new(Problem(BindingProblemKind.UnresolvedTable, detail, at));
        if (target.Server is not null)
            return Unresolved($"The synonym {synonym.Name} stands for {target}, on another server, which is not modelled.");
        if (target.Database is null)
        {
            // A target without a schema is the synonym's schema's, or else dbo's, whoever reads it (see SchemaSearch); a script
            // that deploys has one object of a name in a schema.
            var name = target.Schema is { } schema ? new ObjectName(schema, target.Name) : ResolveWritten(synonym) is [var only] ? only : (ObjectName?)null;
            if (name is not { } found)
                return Unresolved($"The synonym {synonym.Name} stands for {target}, and its schema has more than one object of that name.");
            return Model.FindReadable(found) is Synonym
                ? Unresolved(Chained(synonym.Name, target))
                : InSchema(found, at);
        }

        if (target.Schema is not { } otherSchema)
            return Unresolved($"The synonym {synonym.Name} stands for {target}, which names no schema, so what it reads depends on the caller's default schema.");
        return Model.Databases.TryGetValue(target.Database, out var other)
            ? InOtherDatabase(other, target.Database, new ObjectName(otherSchema, target.Name), at, synonym.Name)
            : Unresolved($"The synonym {synonym.Name} stands for {target}, in another database the source doesn't hold.");
    }

    /// <summary>Why a synonym for a synonym reads nothing: SQL Server refuses it (Msg 470).</summary>
    private static string Chained(ObjectName synonym, SynonymTarget target) =>
        $"The synonym {synonym} stands for {target}, which is a synonym too, and SQL Server refuses to read through a chain of synonyms (Msg 470).";

    /// <summary>
    /// What a name of another database reads: a table, as this database sees it (<see cref="OtherDatabases.AsSeenHere"/>); a
    /// synonym of that database, followed there; or why neither. A view, function or procedure of another database isn't read:
    /// its body binds against its own database, which inference doesn't do yet.
    /// </summary>
    /// <param name="other">The other database's model.</param>
    /// <param name="database">The database as the body names it.</param>
    /// <param name="objectName">The name in that database.</param>
    /// <param name="at">Where the reference is.</param>
    /// <param name="through">The synonym of this database the name was reached through, if any: a synonym there is then a chain.</param>
    private static SourceTarget InOtherDatabase(DatabaseModel other, string database, ObjectName objectName, SourceLocation at, ObjectName? through = null)
    {
        UnresolvedTarget Unresolved(BindingProblemKind kind, string detail) => new(new BindingProblem(kind, detail, at));
        var named = objectName with { Database = database };
        if (other.FindTable(objectName) is { } table)
        {
            return OtherDatabases.AsSeenHere(table, other, database) is { } seen
                ? new TableTarget(seen)
                : Unresolved(BindingProblemKind.UnsupportedSource, $"{named} is a table of a database whose package records no collation, so how its strings compare isn't known.");
        }

        return other.FindReadable(objectName) switch
        {
            Synonym when through is { } here => Unresolved(BindingProblemKind.UnresolvedTable, $"The synonym {here} stands for {named}, which is a synonym too, and SQL Server refuses to read through a chain of synonyms (Msg 470)."),
            Synonym { For: { Database: null, Server: null, Schema: { } schema } target } when other.FindReadable(new ObjectName(schema, target.Name)) is Synonym =>
                Unresolved(BindingProblemKind.UnresolvedTable, Chained(named, target)),
            Synonym { For: { Database: null, Server: null, Schema: { } schema } target } => other.FindTable(new ObjectName(schema, target.Name)) is { } targetTable && OtherDatabases.AsSeenHere(targetTable, other, database) is { } seenThrough
                ? new TableTarget(seenThrough)
                : Unresolved(BindingProblemKind.UnsupportedSource, $"{named} is a synonym of {database} for {target}, which is not a table read across databases yet."),
            { } readable => Unresolved(BindingProblemKind.UnsupportedSource, $"{named} is a {SchemaNames.Kind(readable)} of the database {database}, which is not read across databases yet."),
            null => Unresolved(BindingProblemKind.UnresolvedTable, $"There is no table {objectName} in the database {database}."),
        };
    }

    /// <summary>
    /// A view's columns: its column list (observed: A_view_column_list_names_every_column_and_no_more), or else its query's, with
    /// its stars expanded as SQL Server binds them when it creates the view; for a view whose text the source doesn't hold, the
    /// names the source lists; null for a view defined more than once, one of FOR XML or FOR JSON, or one whose columns can't be
    /// listed.
    /// </summary>
    private EquatableArray<string>? ViewColumns(View view) => bindings.Columns(view, () =>
    {
        if (view.Query is null)
            return view.Redefinition is null ? view.Columns : null;
        if (view.Redefinition is not null || view.Query.QueryExpression is not { } query || ForOf(query) is not null)
            return null;
        var binder = bindings.For(view.Span, SchemaSearch.Within(view));
        var names = binder.BindWith(view.Query.WithCtesAndXmlNamespaces, NameEnvironment.Empty).Names;
        return binder.Bind(query, null, names).Listed(view.Columns);
    });

    /// <summary>
    /// The function a call in FROM names. An unqualified name resolves as a table's does (<see cref="SchemaNames.Resolve"/>).
    /// </summary>
    private Callee Callee(SchemaObjectName name)
    {
        if (name.ServerIdentifier is not null || name.DatabaseIdentifier is not null)
            return new ForeignCallee();
        if (name.SchemaIdentifier is not null)
            return Model.FindFunction(SyntaxReader.Name(name)) is { } function ? new KnownCallee(function) : new MissingCallee();
        return SchemaNames.Resolve(Model.Functions, name.BaseIdentifier.Value, search) switch
        {
            OneObject one => new KnownCallee((Function)one.Named),
            SeveralObjects several => new AmbiguousCallee(name.BaseIdentifier.Value, [.. several.Candidates.Cast<Function>()]),
            NoObject => new MissingCallee(),
        };
    }

    /// <summary>
    /// The columns of what a call in FROM returns: an inline function's query's, a multi-statement
    /// or CLR function's declared table's; null for a call with a column list, which SQL Server refuses (317), and for a
    /// function that is not table-valued, is defined more than once, isn't the project's, or whose declared columns couldn't be read.
    /// </summary>
    private EquatableArray<string>? FunctionColumns(SchemaObjectFunctionTableReference call, Callee callee)
    {
        if (call.Columns.Count > 0 || callee is not KnownCallee { Function: { Redefinition: null } function })
            return null;
        return function switch
        {
            InlineFunction inline => bindings.Columns(inline, () =>
            {
                if (inline.Query.QueryExpression is not { } query || ForOf(query) is not null)
                    return null;
                var binder = bindings.For(inline.Span, SchemaSearch.Within(inline));
                var names = binder.BindWith(inline.Query.WithCtesAndXmlNamespaces, NameEnvironment.Empty.WithDeclarations(bindings.Declarations(inline.Parameters, null, null, inline.Span, SchemaSearch.Within(inline)))).Names;
                return binder.Bind(query, null, names).Listed([]);
            }),

            // Every table function has a column, so one whose declaration lists none is one that couldn't be read: its columns
            // aren't known, rather than none.
            TableFunction { Returns.Columns.Count: 0 } => null,
            TableFunction table => [.. table.Returns.Columns.Select(c => c.Name)],
            ScalarFunction => null,
        };
    }

    /// <summary>
    /// The FOR clause of a query expression: a set operation's is on its last query when it is not a statement's (see
    /// Inference's <c>ForOf</c>, which reads it the same way).
    /// </summary>
    private static ForClause? ForOf(QueryExpression query) => query.ForClause ?? (query is BinaryQueryExpression ? Last(query)?.ForClause : null);

    private static QuerySpecification? Last(QueryExpression query) => query switch
    {
        BinaryQueryExpression binary => Last(binary.SecondQueryExpression),
        QuerySpecification specification => specification,
        _ => null,
    };

    /// <summary>
    /// Adds an unresolvable source for every alias under <paramref name="reference"/>, so that a column
    /// qualified by any of them, or not qualified at all, becomes unbound rather than binding elsewhere.
    /// </summary>
    private static void AddUnresolved(List<BoundSource> sources, TableReference reference, BindingProblem why)
    {
        switch (reference)
        {
            case JoinTableReference join:
                AddUnresolved(sources, join.FirstTableReference, why);
                AddUnresolved(sources, join.SecondTableReference, why);
                break;
            case JoinParenthesisTableReference parenthesis:
                AddUnresolved(sources, parenthesis.Join, why);
                break;
            case NamedTableReference named:
                sources.Add(new BoundSource(named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value, new UnresolvedTarget(why), null, named));
                break;
            default:
                sources.Add(new BoundSource((reference as TableReferenceWithAlias)?.Alias?.Value, new UnresolvedTarget(why), null, reference));
                break;
        }
    }
}
