using Haruspex.Diagnostics;
using Haruspex.Ir;
using Haruspex.SqlServer.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Haruspex.SqlServer.Inference;

// FOR JSON AUTO: the object a row is written as, nested by the sources of the query's FROM. Microsoft's page describes the
// nesting loosely, so every rule here is an observation (ForJsonObservationTests.Auto.cs in the differential tests), and a form
// not observed is unknown. README.md in this folder ("FOR JSON AUTO and ROOT") gives the rules in prose, and
// docs/decisions/0144-for-json-auto-is-typed-from-observed-nesting.md why they are typed so.
internal sealed partial class Typer
{
    /// <summary>An object <c>AUTO</c> nests inside the outermost, as the select list is read.</summary>
    /// <param name="source">The table whose first column opened it, whose name keys its array.</param>
    private sealed class AutoLevel(BoundSource source)
    {
        /// <summary>The table whose first column opened it, whose name keys its array.</summary>
        public BoundSource Source { get; } = source;

        /// <summary>The columns written in it, in order, each with its key.</summary>
        public List<(string Key, RowColumn Column)> Columns { get; } = [];
    }

    /// <summary>What a source is to <c>AUTO</c>.</summary>
    private enum AutoSource
    {
        /// <summary>A table: its bare columns open an object of their own.</summary>
        Table,

        /// <summary>Not a table: its columns are written where they stand, as an expression is.</summary>
        NotTable,

        /// <summary>Not observed, so where its columns go is not known.</summary>
        NotObserved,
    }

    /// <summary>
    /// The object <c>FOR JSON AUTO</c> writes a row as. Each source in the FROM that is a table (<see cref="SourceKind"/>) and whose
    /// bare column the select list holds gets an object of its own, and they nest in a chain, each inside the one before it,
    /// in the order their first columns stand, whatever the FROM's order or its joins (observed:
    /// For_json_auto_nests_tables_in_a_chain_in_the_order_their_first_columns_are_selected). A table's columns all go in its
    /// object, wherever they stand (For_json_auto_gathers_a_tables_columns_wherever_they_stand_and_writes_the_nested_array_last).
    /// Any other column (an expression, a function or conversion of a column, a column under <c>COLLATE</c>, an enclosing
    /// query's column, a built-in rowset function's) goes in the deepest object opened before it, or the outermost
    /// (A_column_of_no_table_joins_the_deepest_object_opened_before_it). Each inner object is in an array under its source's key,
    /// written last in the object around it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rows are merged into one object while the outer object's values repeat in adjacent rows, so how many objects an array has
    /// depends on the rows' order: a parent whose rows are apart is written twice (observed:
    /// For_json_auto_starts_a_new_object_for_a_parent_whose_rows_are_not_adjacent). So nothing is claimed of how many there are,
    /// beyond that every object comes from a row, and every row writes an object at every level, even one of no keys for the
    /// unmatched side of an outer join (<c>"l":[{}]</c>,
    /// An_unmatched_child_is_one_object_with_its_keys_left_out_or_with_include_null_values_null). So an inner array has at least
    /// one item, and exactly one when the query returns at most one row; an object is never left out, as <c>PATH</c>'s all-null
    /// one is; and a key is left out when null by the rule both modes share (<see cref="JsonLeaf"/>), so the outer join's
    /// nullable columns make its keys optional. A merged object's values are a row's, whichever row, so they keep their types.
    /// </para>
    /// <para>
    /// A dotted alias is one key, never a path (For_json_auto_writes_a_dotted_alias_as_one_key). A key written twice in one
    /// object, which <c>AUTO</c> does where <c>PATH</c> refuses it (For_json_auto_writes_a_repeated_key_twice), is JSON whose
    /// value a parser picks, so it is <c>duplicate-column</c>.
    /// </para>
    /// </remarks>
    /// <param name="row">The query's row.</param>
    /// <param name="includeNulls">Whether <c>INCLUDE_NULL_VALUES</c> is given.</param>
    /// <param name="at">Where the FOR clause is.</param>
    private static Shape AutoObject(Row row, bool includeNulls, SourceLocation at)
    {
        // A set operation's row has no sources: which query's FROM AUTO nests by was not observed.
        if (row.Sources is not { } sources)
            return Unsupported(IrUnknownReason.UnsupportedQuery, "FOR JSON AUTO over a set operation is not modelled.", at);

        // With no table in its FROM, AUTO is refused (observed: For_json_auto_refuses_a_query_whose_from_has_no_table); a table
        // no column reads is enough (A_from_with_a_table_is_enough_though_no_column_reads_it).
        if (!sources.Any(s => SourceKind(s) == AutoSource.Table))
        {
            return sources.FirstOrDefault(s => SourceKind(s) == AutoSource.NotObserved) is { } unknown
                ? NotObserved(unknown, at)
                : Unsupported(IrUnknownReason.UnsupportedQuery, "FOR JSON AUTO refuses a query whose FROM has no table (13600), so the query fails.", at);
        }

        // The outermost object takes the first table's columns and every column of no table before any table's; each table after
        // it opens an object inside the deepest so far.
        BoundSource? outermost = null;
        List<(string Key, RowColumn Column)> outer = [];
        List<AutoLevel> nested = [];
        foreach (var column in row.Columns)
        {
            if (column.Name is not { Length: > 0 } key)
                return Unsupported(IrUnknownReason.UnnamedColumn, Unnamed(column), column.At);
            if (ReplacedKey(column) is { } replaced)
                return replaced;

            var (source, why) = Placed(column, sources);
            if (why is not null)
                return why;

            List<(string Key, RowColumn Column)> columns;
            if (source is null)
            {
                columns = nested.Count > 0 ? nested[^1].Columns : outer;
            }
            else if (outermost is null || Equals(source, outermost))
            {
                outermost = source;
                columns = outer;
            }
            else if (nested.Find(l => Equals(l.Source, source)) is { } level)
            {
                columns = level.Columns;
            }
            else
            {
                nested.Add(new AutoLevel(source));
                columns = nested[^1].Columns;
            }

            columns.Add((key, column));
        }

        return Level(0);

        // The object at a depth, holding the array of the one inside it as its last key.
        Shape Level(int depth)
        {
            var fields = (depth == 0 ? outer : nested[depth - 1].Columns).Select(c => JsonLeaf(c.Key, c.Column, includeNulls)).ToList();
            if (depth < nested.Count)
            {
                var source = nested[depth].Source;
                if (source.WrittenName is not { } key)
                    return Unsupported(IrUnknownReason.UnsupportedQuery, $"FOR JSON AUTO's key for {Text(source.Syntax)} was not observed.", at);
                var deeper = Level(depth + 1);
                if (deeper is not ObjectShape inner)
                    return deeper;
                fields.Add(new FieldShape(key, Rows(inner, row), IrPresence.Always, []));
            }

            if (fields.GroupBy(f => f.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } repeated)
            {
                return Unsupported(
                    IrUnknownReason.DuplicateColumn,
                    $"FOR JSON AUTO writes the key '{repeated.Key}' twice in one object, so a parser keeps one of its values.",
                    at);
            }

            return new ObjectShape([.. fields]) { Writers = KeyWriters.SqlServer };
        }
    }

    /// <summary>
    /// The source whose object a column goes in: the source of the query's own FROM its bare reference reads, when that is a table;
    /// neither a source nor a reason when it goes where it stands; or why where it goes is not known.
    /// </summary>
    private static (BoundSource? In, UnknownShape? Why) Placed(RowColumn column, IReadOnlyList<BoundSource> sources)
    {
        // $IDENTITY, $ROWGUID and the rest name a table's column, but were not observed under AUTO.
        if (WithoutParentheses(column.Expression) is ColumnReferenceExpression { ColumnType: not ColumnType.Regular } pseudo)
            return (null, Unsupported(IrUnknownReason.UnsupportedQuery, $"FOR JSON AUTO's nesting of {Text(pseudo)} was not observed.", column.At));

        return column.Bare switch
        {
            null => (null, null),
            TableColumn table => Of(table.Source),
            RelationColumn relation => Of(relation.Source),
            UnlistedColumn unlisted => Of(unlisted.Source),

            // A column binding can't resolve may be a table's, which would open an object.
            Unresolved => (null, Unsupported(IrUnknownReason.UnsupportedQuery, $"FOR JSON AUTO nests by the table a column reads, and the column {Text(column.At)} can't be bound.", column.At)),
        };

        // An enclosing query's column is no table's here (observed: A_column_of_the_enclosing_query_is_a_column_of_no_table).
        (BoundSource?, UnknownShape?) Of(BoundSource source) => !sources.Contains(source) ? (null, null) : SourceKind(source) switch
        {
            AutoSource.Table => (source, null),
            AutoSource.NotTable => (null, null),
            AutoSource.NotObserved => (null, NotObserved(source, column.At)),
        };
    }

    private static ScalarExpression? WithoutParentheses(ScalarExpression? expression) =>
        expression is ParenthesisExpression parenthesis ? WithoutParentheses(parenthesis.Expression) : expression;

    private static UnknownShape NotObserved(BoundSource source, SourceLocation at) =>
        Unsupported(IrUnknownReason.UnsupportedQuery, $"FOR JSON AUTO's nesting of {Text(source.Syntax)} was not observed.", at);

    /// <summary>
    /// What a source is to <c>AUTO</c>. A table, a view (a system view too), a derived table, <c>VALUES</c>, a CTE, a function of
    /// the project, a temporary table, a table variable, <c>OPENXML</c> and <c>nodes()</c> are tables (observed:
    /// Derived_tables_and_values_are_tables_to_for_json_auto_and_built_in_rowset_functions_are_not,
    /// A_cte_and_a_table_variable_are_tables_to_for_json_auto, Openxml_and_a_system_view_are_tables_to_for_json_auto,
    /// An_xml_nodes_source_is_a_table_to_for_json_auto_whose_column_is_never_bare,
    /// A_source_without_an_alias_is_keyed_by_its_name_as_the_from_clause_writes_it); <c>OPENJSON</c>, <c>STRING_SPLIT</c> and
    /// <c>GENERATE_SERIES</c> are not. The full-text functions, <c>inserted</c> and <c>deleted</c>, and a source or function
    /// binding can't resolve were not observed.
    /// </summary>
    private static AutoSource SourceKind(BoundSource source) => source.Target switch
    {
        TableTarget => AutoSource.Table,
        RelationTarget { Relation: var relation } => relation switch
        {
            CommonTable or DerivedTable or ValuesTable or ViewRelation or TableFunctionCall { Callee: KnownCallee } or TableVariable
                or TemporaryTable or OpenXmlCall or XmlNodesCall => AutoSource.Table,

            // A call binding can't resolve (a synonym for a function among them) was not observed.
            TableFunctionCall => AutoSource.NotObserved,
            OpenJsonCall or SplitCall or SeriesCall => AutoSource.NotTable,
            FullTextCall or PseudoTable => AutoSource.NotObserved,
        },
        UnresolvedTarget => AutoSource.NotObserved,
    };
}
