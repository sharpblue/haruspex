using Haruspex.Collections;
using Haruspex.Ir;

namespace Haruspex.SqlServer.Inference.Lattice;

/// <summary>
/// The lattice of JSON built here: what a variable, a column or a <c>CASE</c> over JSON holds when it may hold the JSON of
/// any of several writes or branches. The guide's chapter <c>docs/internals/05-values-as-a-lattice.md</c>, "One lattice per
/// fact", explains the idea.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> A shape below another allows no JSON the other doesn't. An unknown is the top. A union allows what any of
/// its variants allows, so JSON content, unlike a SQL value, can always be joined exactly: two different shapes become a
/// union of both.
/// </para>
/// <para>
/// <b>Witnesses.</b> A union lists its variants in the order they were found, the left side's first, and that order reaches
/// the IR and the Zod, which tries variants in order. The order is presentation, deterministic for a given body, so the
/// join is commutative only up to variants compared as a multiset. Of two unknowns the left one is kept.
/// </para>
/// </remarks>
internal static class JsonShapes
{
    /// <summary>
    /// JSON that is either <paramref name="a"/> or <paramref name="b"/>: what a variable or a column holds after
    /// several writes of JSON built here, and what <c>COALESCE</c>, <c>CASE</c> or <c>ISNULL</c> over JSON holds. The same shape
    /// is itself; an unknown stays unknown; arrays and tuples become an array of the items of both (the empty tuple <c>[]</c>
    /// adds no item, so <c>COALESCE(@lines, '[]')</c> keeps the lines' items and only loses their least count); objects with the
    /// same keys in the same order, each present the same way, join key by key; records join value by value; scalars of one
    /// type keep the facts both prove; an object of no keys joins a record as the record, which holds it (<c>'{' +
    /// coalesce(string_agg(…), '') + '}'</c> is <c>{}</c> over no rows; docs/decisions/0087-keys-built-from-data-make-an-object-whose-keys-are-data.md);
    /// anything else is a union of both, which is exact.
    /// </summary>
    public static Shape Join(Shape a, Shape b)
    {
        if (a == b)
            return CommonDescriptions.Of(a, b);
        if (a is UnknownShape)
            return a;
        if (b is UnknownShape)
            return b;

        // Any JSON holds every JSON value, and narrowed, every object and array (docs/decisions/0094-any-json-is-a-type-of-its-own.md).
        if (a is AnyJsonShape { ObjectOrArray: false } || (a is AnyJsonShape && Below(b, a)))
            return a;
        if (b is AnyJsonShape { ObjectOrArray: false } || (b is AnyJsonShape && Below(a, b)))
            return b;

        switch (a, b)
        {
            case (TupleShape x, TupleShape y) when x.Items.Count == y.Items.Count:
                return new TupleShape([.. x.Items.Zip(y.Items, (p, q) => new ElementShape(Join(p.Type, q.Type), NullReasons.Join(p.NullableBecause, q.NullableBecause)))]);
            case (TupleShape or ArrayShape, TupleShape or ArrayShape):
                return Items(a, b);
            case (ObjectShape x, ObjectShape y) when Objects(x, y) is { } joined:
                return joined;
            case (ScalarShape x, ScalarShape y) when x.SameType(y):
                return x.Joined(y, ScalarFacets.Join(x.Type.Facets, y.Type.Facets));
            case (RecordShape x, RecordShape y):
                return new RecordShape(Join(x.Values, y.Values), NullReasons.Join(x.ValuesNullableBecause, y.ValuesNullableBecause));
            case (ObjectShape { Fields.Count: 0 }, RecordShape):
                return b;
            case (RecordShape, ObjectShape { Fields.Count: 0 }):
                return a;
        }

        // A union: each variant of either once, a variant joining the first of its own kind it can (an array with an array).
        var variants = new List<Shape>();
        foreach (var variant in Variants(a).Concat(Variants(b)))
        {
            var i = variants.FindIndex(v => Mergeable(v, variant));
            if (i < 0)
                variants.Add(variant);
            else
                variants[i] = Join(variants[i], variant);
        }

        return variants.Count == 1 ? variants[0] : new UnionShape([.. variants.Select(CommonDescriptions.None)]);

        static IEnumerable<Shape> Variants(Shape s) => s is UnionShape u ? u.Variants : [s];
    }

    /// <summary>
    /// Whether two shapes say the same: equal but for witnesses and presentation order. Any two unknowns are the same (the
    /// top); a union's variants are matched one to one, in any order, each with this equivalence; reasons are compared as sets
    /// (<see cref="NullReasons.Same"/>) and a scalar's facets ignoring their sources (<see cref="ScalarFacets.Same"/>). Keys,
    /// presence, counts and every shape's kind are compared exactly.
    /// </summary>
    public static bool Same(Shape a, Shape b) => (a, b) switch
    {
        (UnknownShape, UnknownShape) => true,
        (ScalarShape x, ScalarShape y) => x.Type with { Facets = null } == y.Type with { Facets = null } && ScalarFacets.Same(x.Type.Facets, y.Type.Facets),
        (ArrayShape x, ArrayShape y) => Same(x.Items, y.Items) && NullReasons.Same(x.ItemsNullableBecause, y.ItemsNullableBecause) && x.MinItems == y.MinItems && x.MaxItems == y.MaxItems,
        (TupleShape x, TupleShape y) => x.Items.Count == y.Items.Count
            && x.Items.Zip(y.Items).All(p => Same(p.First.Type, p.Second.Type) && NullReasons.Same(p.First.NullableBecause, p.Second.NullableBecause)),
        (ObjectShape x, ObjectShape y) => x.Open == y.Open && x.Writers == y.Writers && x.Fields.Count == y.Fields.Count
            && x.Fields.Zip(y.Fields).All(p => p.First.Name == p.Second.Name && p.First.Presence == p.Second.Presence
                && Same(p.First.Type, p.Second.Type) && NullReasons.Same(p.First.NullableBecause, p.Second.NullableBecause)),
        (RecordShape x, RecordShape y) => Same(x.Values, y.Values) && NullReasons.Same(x.ValuesNullableBecause, y.ValuesNullableBecause),
        (JsonTextShape x, JsonTextShape y) => SameText(x.Text, y.Text) && (x.Content is null || y.Content is null ? x.Content is null && y.Content is null : Same(x.Content, y.Content)),
        (UnionShape x, UnionShape y) => SameVariants(x.Variants, y.Variants),
        _ => a == b,
    };

    /// <summary>
    /// Whether every JSON value <paramref name="a"/> allows, <paramref name="b"/> allows too, read from the shapes' structure:
    /// anything is below an unknown; a union is below what covers each of its variants, and a shape below a union when it is
    /// below one variant; scalars of one type by their facets (<see cref="ScalarFacets.Below"/>); arrays by their items, null
    /// reasons and counts, a tuple below an array whose items cover each of its own; tuples position by position; objects of the
    /// same keys key by key, a key present the same way (or never null, so present either way); records by their values; JSON
    /// text by its content and its string's type, none (unknown content) being the top; and anything is below any JSON, an object
    /// or an array below any JSON object or array. Reasons are compared as sets. Written apart from
    /// <see cref="Join"/>, so that the law tests can check the join against it.
    /// </summary>
    public static bool Below(Shape a, Shape b) => (a, b) switch
    {
        (_, UnknownShape) => true,
        (UnknownShape, _) => false,
        (UnionShape x, _) => x.Variants.All(v => Below(v, b)),
        (_, UnionShape y) => y.Variants.Any(v => Below(a, v)),
        (_, AnyJsonShape { ObjectOrArray: false }) => true,
        (ArrayShape or TupleShape or ObjectShape or RecordShape or AnyJsonShape { ObjectOrArray: true }, AnyJsonShape) => true,
        (ScalarShape x, ScalarShape y) => x.Type with { Facets = null } == y.Type with { Facets = null } && ScalarFacets.Below(x.Type.Facets, y.Type.Facets),
        (ArrayShape x, ArrayShape y) => Below(x.Items, y.Items) && NullReasons.Below(x.ItemsNullableBecause, y.ItemsNullableBecause)
            && (x.MinItems ?? 0) >= (y.MinItems ?? 0) && (y.MaxItems is null || (x.MaxItems is { } most && most <= y.MaxItems)),
        (TupleShape x, ArrayShape y) => x.Items.All(e => Below(e.Type, y.Items) && NullReasons.Below(e.NullableBecause, y.ItemsNullableBecause))
            && x.Items.Count >= (y.MinItems ?? 0) && (y.MaxItems is null || x.Items.Count <= y.MaxItems),
        (TupleShape x, TupleShape y) => x.Items.Count == y.Items.Count
            && x.Items.Zip(y.Items).All(p => Below(p.First.Type, p.Second.Type) && NullReasons.Below(p.First.NullableBecause, p.Second.NullableBecause)),
        (ObjectShape x, ObjectShape y) => (!x.Open || y.Open) && (x.Writers & ~y.Writers) == 0 && x.Fields.Count == y.Fields.Count
            && x.Fields.Zip(y.Fields).All(p => p.First.Name == p.Second.Name
                && (p.First.Presence == p.Second.Presence || p.First.NullableBecause.Count == 0)
                && Below(p.First.Type, p.Second.Type) && NullReasons.Below(p.First.NullableBecause, p.Second.NullableBecause)),
        (RecordShape x, RecordShape y) => Below(x.Values, y.Values) && NullReasons.Below(x.ValuesNullableBecause, y.ValuesNullableBecause),
        (ObjectShape { Fields.Count: 0 }, RecordShape) => true,
        (JsonTextShape x, JsonTextShape y) => SameText(x.Text, y.Text) && (y.Content is null || (x.Content is not null && Below(x.Content, y.Content))),
        _ => a == b,
    };

    /// <summary>
    /// Whether two JSON texts are strings of one type: the same column type, or both <c>nvarchar(max)</c>, the text JSON built here
    /// is (<see cref="JsonTextShape.Text"/>).
    /// </summary>
    public static bool SameText(ScalarShape? a, ScalarShape? b) => a is null ? b is null : b is not null && a.SameType(b);

    /// <summary>Whether two unions' variants match one to one, each pair the same (<see cref="Same"/>, an equivalence, so a first match will do).</summary>
    private static bool SameVariants(EquatableArray<Shape> x, EquatableArray<Shape> y)
    {
        if (x.Count != y.Count)
            return false;
        var unmatched = y.ToList();
        foreach (var variant in x)
        {
            var i = unmatched.FindIndex(v => Same(variant, v));
            if (i < 0)
                return false;
            unmatched.RemoveAt(i);
        }

        return true;
    }

    /// <summary>
    /// Whether two variants of a union join into one without a union, as the same two would alone (<see cref="Join"/>): arrays
    /// and tuples, records, objects of the same keys, scalars of one type, or the same shape.
    /// </summary>
    private static bool Mergeable(Shape a, Shape b) =>
        (a is TupleShape or ArrayShape && b is TupleShape or ArrayShape) || a == b || (a is RecordShape or ObjectShape { Fields.Count: 0 } && b is RecordShape or ObjectShape { Fields.Count: 0 })
        || (a is ObjectShape x && b is ObjectShape y && Objects(x, y) is not null)
        || (a is ScalarShape s && b is ScalarShape t && s.SameType(t));

    /// <summary>
    /// Two objects with the same keys in the same order, each present the same way, key by key; null otherwise, and a union of
    /// both objects says it exactly. A key present as null and one left out when null can't be one field. Nor is a key never
    /// null merged with either: it could go with both, so merging it with the first met would make the join depend on the
    /// order of the joins (associativity, held by <c>LatticeLawTests</c>). Presence compared whole is an equivalence, so it
    /// doesn't.
    /// </summary>
    private static ObjectShape? Objects(ObjectShape x, ObjectShape y)
    {
        if (!x.Fields.Select(f => f.Name).SequenceEqual(y.Fields.Select(f => f.Name), StringComparer.Ordinal))
            return null;
        var fields = new List<FieldShape>();
        foreach (var (f, g) in x.Fields.Zip(y.Fields))
        {
            if (f.Presence != g.Presence)
                return null;
            fields.Add(new FieldShape(f.Name, Join(f.Type, g.Type), f.Presence, NullReasons.Join(f.NullableBecause, g.NullableBecause)) { From = f.From, Description = CommonDescriptions.Same(f.Description, g.Description) });
        }

        return new ObjectShape([.. fields]) { Open = x.Open || y.Open, Writers = x.Writers | y.Writers };
    }

    /// <summary>An array of the items of two arrays or tuples: each item of either, between the fewest and most of both.</summary>
    private static ArrayShape Items(Shape a, Shape b)
    {
        static (List<Shape> Items, List<IrNullReason> Nulls, int Min, int? Max) Of(Shape s) => s switch
        {
            TupleShape t => ([.. t.Items.Select(e => e.Type)], [.. t.Items.SelectMany(e => e.NullableBecause)], t.Items.Count, t.Items.Count),
            ArrayShape r => ([r.Items], [.. r.ItemsNullableBecause], r.MinItems ?? 0, r.MaxItems),
            _ => throw new ArgumentOutOfRangeException(nameof(s)),
        };

        var (x, y) = (Of(a), Of(b));
        // Never empty: two tuples of one length, the two empty ones among them, are joined item by item before (Join), and an
        // array always has its items' shape.
        var all = x.Items.Concat(y.Items).ToList();
        var items = all.Skip(1).Aggregate(all[0], Join);
        var nulls = NullReasons.Join([.. x.Nulls.Distinct()], [.. y.Nulls.Distinct()]);
        var min = Math.Min(x.Min, y.Min);
        int? max = x.Max is { } p && y.Max is { } q ? Math.Max(p, q) : null;

        return new ArrayShape(items, nulls, min == 0 ? null : min, max);
    }
}
