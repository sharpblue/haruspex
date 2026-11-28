using Haruspex.Diagnostics;
using Haruspex.Ir;

namespace Haruspex.SqlServer.Inference;

/// <summary>
/// IR types back into inference's shapes: how a declared result (<see cref="DeclaredResults"/>) joins a caller's results, and
/// how a column's declared JSON (<see cref="DeclaredColumn"/>) is read, to be joined, unioned and written again like any shape
/// inferred here. The opposite of <see cref="IrBuilder"/>'s
/// conversion, for the nodes a declaration can hold.
/// </summary>
internal static class IrShapes
{
    /// <summary>A declared type as a shape.</summary>
    /// <param name="type">The type.</param>
    /// <param name="types">The object types it refers to.</param>
    /// <param name="declared">What declared it, for an unknown's detail: <c>The declared results of shop.search</c>.</param>
    /// <param name="at">Where it is read, for an unknown's location.</param>
    public static Shape ToShape(IrType type, IReadOnlyDictionary<string, IrObjectType> types, string declared, SourceLocation at) =>
        Convert(type, types, declared, at, []);

    /// <remarks>
    /// A conversion builds a shape per kind of node, so it matches on kinds itself rather than walking with
    /// <see cref="IrTypeTree"/>; and it expands references inline, so it guards against a cycle with the names it is
    /// expanding rather than asking a graph of a document it doesn't have. A kind of node it doesn't know becomes an unknown
    /// with a reason, where the IR's own walks throw: the declaration is the user's, and the inference reports what it can't
    /// model as an unknown, never as a failure.
    /// </remarks>
    private static Shape Convert(IrType type, IReadOnlyDictionary<string, IrObjectType> types, string declared, SourceLocation at, HashSet<string> expanding)
    {
        Shape Of(IrType inner) => Convert(inner, types, declared, at, expanding);
        switch (type)
        {
            case IrScalar scalar:
                return ScalarShape.FromIr(scalar);
            case IrArray array:
                return new ArrayShape(Of(array.Items), array.ItemsNullableBecause, array.MinItems, array.MaxItems);
            case IrTuple tuple:
                return new TupleShape([.. tuple.Items.Select(e => new ElementShape(Of(e.Type), e.NullableBecause))]);
            case IrRecord record:
                return new RecordShape(Of(record.Values), record.ValuesNullableBecause);
            case IrUnion union:
                return new UnionShape([.. union.Variants.Select(Of)]);
            case IrJsonText text:
                return new JsonTextShape(text.Content is null ? null : Of(text.Content));
            case IrAnyJson any:
                return new AnyJsonShape(any.ObjectOrArray);
            case IrRef reference when types.TryGetValue(reference.Name, out var obj):
                // A declaration's types do not refer to themselves (the config file refuses a recursive $ref), but a cycle
                // here would not end, so it is refused rather than followed.
                if (!expanding.Add(reference.Name))
                    return Unknown(IrUnknownReason.UnresolvedRecursion, $"{declared} refers to {reference.Name} inside itself, which is not modelled.");
                try
                {
                    return new ObjectShape([.. obj.Fields.Select(f => new FieldShape(f.Name, Of(f.Type), f.Presence, f.NullableBecause))]) { Open = obj.Open, Writers = KeyWriters.Application };
                }
                finally
                {
                    expanding.Remove(reference.Name);
                }

            case IrUnknownType unknown:
                return Unknown(unknown.Reason, $"{declared} leaves this unknown.");
            default:
                return Unknown(IrUnknownReason.UnsupportedExpression, $"{declared} holds a type that is not modelled here.");
        }

        UnknownShape Unknown(IrUnknownReason reason, string detail) => new(reason, detail, at);
    }
}
