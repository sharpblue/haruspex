using Haruspex.Collections;
using Haruspex.Ir;
using Haruspex.SqlServer.Inference.Lattice;

namespace Haruspex.SqlServer.Inference;

/// <summary>
/// What a path finds in JSON whose shape is known (built here, or declared for a column): the values there when there is one,
/// whether there may be nothing, whether it may be JSON <c>null</c>, and, step by step, why. It is a walk of the type along the
/// path, the abstract counterpart of the server's walk of a document; README.md, "Paths into JSON", explains it.
/// </summary>
internal static class JsonPathWalk
{
    /// <summary>
    /// What a path finds. Null from <see cref="Follow"/> when the path goes where the shape says nothing (a recursive node, an
    /// unknown), which is not read.
    /// </summary>
    /// <param name="Shape">What it finds when it finds a value that is not JSON <c>null</c>; null when it never does.</param>
    /// <param name="MayBeMissing">Whether it may find nothing: a key left out or not there, an index past the end.</param>
    /// <param name="MayBeNull">Whether it may find JSON <c>null</c>.</param>
    /// <param name="Notes">Why it may be missing or null, a step at a time, for <c>explain</c>: <c>$.address may be left out, …</c>.</param>
    /// <param name="Never">Whether, where it finds nothing, the shape says it can never have that step (a closed object's key, a
    /// key of a string), rather than that it may not hold it: what the lint warns of.</param>
    internal sealed record Found(Shape? Shape, bool MayBeMissing, bool MayBeNull, EquatableArray<string> Notes, bool Never = false)
    {
        /// <summary>The values it finds, a union's one by one; empty when it finds none.</summary>
        public IReadOnlyList<Shape> Variants => Shape switch
        {
            null => [],
            UnionShape union => union.Variants,
            _ => [Shape],
        };

        /// <summary>Whether it never finds a value, not even <c>null</c>, because the shape can never have it.</summary>
        public bool NeverThere => Shape is null && !MayBeNull && Never;
    }

    /// <summary>What <paramref name="path"/> finds in JSON of <paramref name="shape"/>; null when that is not known.</summary>
    public static Found? Follow(Shape shape, JsonPath path) => FollowFrom(shape, path, 0);

    private static Found? FollowFrom(Shape shape, JsonPath path, int from)
    {
        var steps = path.Steps;
        // Any JSON may be JSON null itself; narrowed to an object or an array, it is not.
        if (from == steps.Count)
            return shape is AnyJsonShape { ObjectOrArray: false } ? new Found(shape, false, true, [$"`{path.Prefix(from)}` may hold any JSON, null among it"]) : new Found(shape, false, false, []);
        var here = path.Prefix(from + 1);
        var last = from + 1 == steps.Count;
        switch (shape, steps[from])
        {
            case (UnionShape union, _):
                Found? all = null;
                foreach (var variant in union.Variants)
                {
                    if (FollowFrom(variant, path, from) is not { } one)
                        return null;
                    all = all is null ? one : Either(all, one);
                }

                return all;
            case (AnyJsonShape, _):
                // Any JSON may be anything below too: any value, null, or nothing.
                return new Found(new AnyJsonShape(), true, true, [$"`{path.Prefix(from)}` may hold any JSON, so `{path.Prefix(steps.Count)}` may be anything or nothing"]);
            case (ObjectShape obj, JsonPath.Key(var key)):
                if (obj.Fields.FirstOrDefault(f => f.Name == key) is not { } field)
                {
                    // A key an open object doesn't list may be there, holding any JSON (a column's declared JSON), or not.
                    return obj.Open
                        ? new Found(new AnyJsonShape(), true, true, [$"`{here}` is not a key the type lists, and the object may hold others of any value"])
                        : new Found(null, true, false, [$"`{here}` is never there: the object has no key `{key}`"], Never: true);
                }

                // A path finds the key only as it is spelled, and its writer may have spelled it otherwise.
                var (verdict, misspelled) = KeySpelling.Finds(obj.Writers, key);
                if (verdict == KeySpelling.Verdict.Never)
                    return new Found(null, true, false, [$"`{here}` is never there: {misspelled}"], Never: true);

                var nullable = field.NullableBecause.Count > 0;
                var notes = new List<string>();
                if (verdict == KeySpelling.Verdict.MayBeMissing)
                    notes.Add($"`{here}` may not be found: {misspelled}");

                // A key left out when null is missing exactly when null; one left out whatever its value (optional, in a column's
                // declared JSON) may be missing, and null too where it may be null.
                var missing = verdict == KeySpelling.Verdict.MayBeMissing || field.Presence == IrPresence.Optional || (field.Presence == IrPresence.OmittedWhenNull && nullable);
                var nullHere = nullable && field.Presence != IrPresence.OmittedWhenNull;
                if (field.Presence == IrPresence.Optional)
                    notes.Add($"`{here}` may be left out, {field.LeftOutBecause ?? "as the schema doesn't require it"}");
                else if (missing)
                    notes.Add($"`{here}` is left out when it is null");
                if (nullHere)
                    notes.Add(last ? $"`{here}` may be null" : $"`{here}` may be null, which has nothing below it");
                return Below(FollowFrom(field.Type, path, from + 1), missing || (nullable && !last), nullHere && last, notes);
            case (RecordShape record, JsonPath.Key):
                // A map's keys are data: any key may be missing.
                var valueNull = record.ValuesNullableBecause.Count > 0;
                List<string> recordNotes = [$"`{here}` may not be one of the keys, which are data"];
                if (valueNull)
                    recordNotes.Add($"`{here}` may be null");
                return Below(FollowFrom(record.Values, path, from + 1), true, valueNull && last, recordNotes);
            case (TupleShape tuple, JsonPath.Index(var index)):
                if (index >= tuple.Items.Count)
                    return new Found(null, true, false, [$"`{here}` is never there: the array has {tuple.Items.Count} items"], Never: true);
                var item = tuple.Items[index];
                var itemNull = item.NullableBecause.Count > 0;
                return Below(FollowFrom(item.Type, path, from + 1), itemNull && !last, itemNull && last, itemNull ? [$"`{here}` may be null"] : []);
            case (ArrayShape array, JsonPath.Index(var position)):
                if (array.MaxItems is { } most && position >= most)
                    return new Found(null, true, false, [$"`{here}` is never there: the array has at most {most} items"], Never: true);
                var past = array.MinItems is not { } least || position >= least;
                var itemsNull = array.ItemsNullableBecause is { Count: > 0 };
                List<string> arrayNotes = past ? [$"`{here}` may be past the end of the array"] : [];
                if (itemsNull)
                    arrayNotes.Add($"`{here}` may be null");
                return Below(FollowFrom(array.Items, path, from + 1), past || (itemsNull && !last), itemsNull && last, arrayNotes);
            case (ObjectShape or RecordShape or TupleShape or ArrayShape or ScalarShape or JsonTextShape, _):
                // A key of an array or a scalar, an index of an object or a scalar: never there.
                return new Found(null, true, false, [$"`{here}` is never there: `{path.Prefix(from)}` is {Kind(shape)}"], Never: true);
            default:
                return null;
        }

        // What the steps below find, missing or null as well where this step may be.
        static Found? Below(Found? below, bool missing, bool isNull, IEnumerable<string> notes) =>
            below is null ? null : below with
            {
                MayBeMissing = below.MayBeMissing || missing,
                MayBeNull = below.MayBeNull || isNull,
                Notes = [.. notes, .. below.Notes],
            };
    }

    /// <summary>What either of two finds finds: what JSON of a union finds, a variant at a time.</summary>
    public static Found Either(Found a, Found b) => new(
        a.Shape is null ? b.Shape : b.Shape is null ? a.Shape : JsonShapes.Join(a.Shape, b.Shape),
        a.MayBeMissing || b.MayBeMissing,
        a.MayBeNull || b.MayBeNull,
        [.. a.Notes.Concat(b.Notes).Distinct(StringComparer.Ordinal)],
        a.Never && b.Never);

    /// <summary>A kind of JSON value, in words, for a note.</summary>
    private static string Kind(Shape shape) => shape switch
    {
        ObjectShape or RecordShape => "an object",
        TupleShape or ArrayShape => "an array",
        ScalarShape { Type.Kind: IrScalarKind.Number } => "a number",
        ScalarShape { Type.Kind: IrScalarKind.Boolean } => "true or false",
        _ => "a string",
    };
}
