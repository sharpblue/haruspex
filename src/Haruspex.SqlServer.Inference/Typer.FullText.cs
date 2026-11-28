using Haruspex.Ir;
using Haruspex.SqlServer.Model;
using Haruspex.SqlServer.Semantics;

namespace Haruspex.SqlServer.Inference;

// CONTAINSTABLE and FREETEXTTABLE in FROM. README.md in this folder ("Full-text table functions") gives the rules and the
// observations they rest on (FullTextObservationTests in the differential tests); binding is Binder.FullText.cs's.
internal sealed partial class Typer
{
    /// <summary>
    /// A full-text call's two columns. <c>KEY</c> is of the type of its table's full-text key (<see cref="FullTextKey"/>), never
    /// null; <c>RANK</c> is an <c>int</c>, which SQL Server declares nullable (observed:
    /// <c>A_full_text_search_declares_key_as_its_key_column_never_null_and_rank_an_int_it_may_make_null</c>).
    /// </summary>
    private LocalRelationValues FullText(FullTextCall call)
    {
        var at = At(call.Syntax);

        // No NULL RANK has been seen, from any form of search, but the server describes the column as nullable, and a SELECT INTO
        // makes it a nullable column: what the server declares is what it guarantees, so the column is read as declared
        // (docs/decisions/0135-a-full-text-search-s-key-is-its-key-column-s-type-and-its-rank-is-nullable.md).
        var rank = Value.Computed(
            SqlTypes.ToConvertedShape(ScalarType.Of(BuiltInType.Int), at),
            [new IrNullReason(IrNullRule.NullableColumn, $"{call.Function}.RANK")]);
        return new LocalRelationValues(["KEY", "RANK"], [Key(call), rank], null);
    }

    /// <summary>
    /// <c>KEY</c>: the value of its table's full-text key column, of that column's type and collation, never null (observed:
    /// <c>A_full_text_search_declares_key_as_its_key_column_never_null_and_rank_an_int_it_may_make_null</c>). The key index is
    /// the unique index, unique constraint or primary key the full-text index names, of one column that is never null and
    /// unmasked: SQL Server refuses any other (7653), and refuses a mask on a key column (16013, 16014). Only the type's facts are
    /// claimed, not the column's CHECK constraints or its value set: a key is read from the full-text index, which may still hold
    /// a row deleted since, even one deleted by a transaction not yet committed (observed:
    /// <c>A_full_text_search_returns_the_key_of_a_row_deleted_and_not_committed</c>). Unknown when the table, its full-text index,
    /// its key index or its column isn't known.
    /// </summary>
    private Value Key(FullTextCall call)
    {
        var at = At(call.Syntax);
        Value Untyped(string why) => Value.Unknown(Unsupported(IrUnknownReason.UnsupportedSource, $"{call.Function}'s KEY: {why}", at));

        if (call.Searched is UnresolvedTarget { Problem: var problem })
            return Value.Unknown(Unknown(problem));
        if (call.Searched is not TableTarget { Table: var table })
            return Untyped($"{Text(call.Syntax.TableName)} is not a table of this database, whose full-text key could be read.");

        var named = table.FullTextKeys.Select(k => k.Index).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (named is not [var index])
        {
            return Untyped(named.Count == 0
                ? $"the project creates no full-text index on {table.Name}, so its key isn't known."
                : $"the project creates the full-text index on {table.Name} more than once, with the keys {string.Join(" and ", named)}, so which stands isn't known.");
        }

        // An index's name is compared ignoring case, as a table's column is (Table.Column); two keys of one name, which only a
        // case-sensitive database can have, leave it unknown.
        var keys = table.UniqueKeys.Where(k => index.Equals(k.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (keys is not [{ Columns: [var name], DisabledAt: null }])
        {
            return Untyped(keys.Count switch
            {
                0 => $"{table.Name} has no unique index, unique constraint or primary key named {index} in the project, which its full-text index names as its key.",
                1 when keys[0].DisabledAt is { } disabled => $"{table.Name}'s key index {index} may be dropped or rebuilt at {disabled}.",
                1 => $"{table.Name}'s key index {index} has more than one column, which a full-text key can't have.",
                _ => $"{table.Name} has more than one key named {index}.",
            });
        }

        if (table.Column(name) is not { } column || column.IsComputed || column.IsMasked)
            return Untyped($"{table.Name}.{name}, its full-text key column, is computed, masked or not in the table, which is not modelled.");
        return SchemaRules.ResolveType(model, column.Type) is { } type
            ? Value.Computed(SqlTypes.ToShape(SchemaRules.TypeFacts(type, SchemaRules.EffectiveCollation(model, table, column)), column.Type, at), [])
            : Value.Unknown((UnknownShape)SqlTypes.ToConvertedShape(model, column.Type, at));
    }
}
