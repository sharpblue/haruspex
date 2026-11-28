using Haruspex.Collections;
using Haruspex.Diagnostics;
using Haruspex.Ir;
using Haruspex.SqlServer.Inference;
using Haruspex.SqlServer.Inference.Lattice;

namespace Haruspex.Tests.Inference;

/// <summary>
/// Elements of each fact's lattice drawn at random from small alphabets, for the law tests (<see cref="LatticeLawTests"/>).
/// Every element is in the form the inference builds: reasons each once, empty facets as none, a value of unknown shape
/// saying it may be null, and a union only as the join builds one (two shapes joined), so its variants are distinct and
/// can't be merged. The alphabets are small so that equal elements built apart, and comparable ones, turn up by chance.
/// </summary>
#pragma warning disable CA5394 // A seeded Random gives the same sample on every run, which is the point; nothing here is secret.
internal sealed class LatticeSample(int seed)
{
    private readonly Random random = new(seed);

    public static readonly SourceLocation[] Places = [new("p.sql", 1, 1), new("p.sql", 2, 5)];

    private static readonly IrNullReason[] AllReasons =
        [new(IrNullRule.NullableColumn, "dbo.t.a"), new(IrNullRule.UnmatchedRow, "p.sql:2:5"), new(IrNullRule.NoRows, "p.sql:1:1")];

    private static readonly IrFactSource[] Sources = [new(IrFactSourceKind.Type, "int"), new(IrFactSourceKind.Check, "ck_t_a")];

    public EquatableArray<IrNullReason> Reasons()
    {
        var picked = AllReasons.Where(_ => random.Next(2) == 0).Select(r => r with { }).ToList();
        if (random.Next(2) == 0)
            picked.Reverse();
        return [.. picked];
    }

    public IntDigits Digits() => random.Next(5) switch
    {
        0 => IntDigits.OfType,
        1 => IntDigits.Unknown,
        2 => IntDigits.Literal(random.Next(1, 3)),
        3 => IntDigits.Join(IntDigits.Literal(2), IntDigits.Literal(3)),
        _ => IntDigits.Literal(3),
    };

    public IrCardinality Rows() => (IrCardinality)random.Next(3);

    /// <summary>Facets of an int: bounds of 0 or 1, open or closed, from the type or a check, and a set of two of 1, 2 and 3.</summary>
    public IrFacets? NumberFacets()
    {
        IrBound? Bound() => random.Next(3) == 0 ? null : new IrBound(random.Next(2).ToString(System.Globalization.CultureInfo.InvariantCulture), random.Next(3) != 0, Source());
        var values = random.Next(3) == 0
            ? new IrValueSet(random.Next(2) == 0 ? [new IrValue("1", false), new IrValue("2", false)] : [new IrValue("3", false), new IrValue("2", false)], IrValueMatch.Exact, [Source()])
            : null;
        var facets = new IrFacets(Min: Bound(), Max: Bound(), Values: values);
        return facets.IsEmpty ? null : facets;
    }

    /// <summary>Facets of an <c>nvarchar(10)</c>: a maximum length of 5 or 10, a minimum of 1 or 3, a fixed length, a set of strings.</summary>
    public IrFacets? StringFacets()
    {
        var length = random.Next(4) switch
        {
            0 => null,
            1 => new IrLength(5, false, IrLengthUnit.Utf16, IrByteEncoding.Unknown, Source()),
            2 => new IrLength(10, false, IrLengthUnit.Utf16, IrByteEncoding.Unknown, Source()),
            _ => new IrLength(10, true, IrLengthUnit.Utf16, IrByteEncoding.Unknown, Source()),
        };
        // Minima in both units, some implying others and some (4 code units, 3 code points) neither.
        var minLength = random.Next(5) switch
        {
            0 => null,
            1 => new IrMinLength(1, Source()),
            2 => new IrMinLength(3, Source()),
            3 => new IrMinLength(4, Source()),
            _ => new IrMinLength(3, Source()) { Unit = IrLengthUnit.CodePoints },
        };
        var values = random.Next(3) == 0 ? new IrValueSet([new IrValue(random.Next(2) == 0 ? "a" : "b", true)], IrValueMatch.Exact, [Source()]) : null;
        var facets = new IrFacets(Length: length, MinLength: minLength, Values: values);
        return facets.IsEmpty ? null : facets;
    }

    public ScalarShape Scalar() => random.Next(3) switch
    {
        0 => ScalarShape.FromIr(new IrScalar(IrScalarKind.Number, IrFormat.Int32, "int", NumberFacets())),
        1 => ScalarShape.FromIr(new IrScalar(IrScalarKind.String, null, "nvarchar(10)", StringFacets())),
        _ => ScalarShape.FromIr(new IrScalar(IrScalarKind.String, null, "nvarchar(max)")),
    };

    public UnknownShape Unknown() =>
        new(random.Next(2) == 0 ? IrUnknownReason.UnsupportedQuery : IrUnknownReason.UnresolvedTable, random.Next(2) == 0 ? "a" : "b", Pick(Places));

    /// <summary>JSON content: scalars, arrays, tuples, objects of the keys <c>id</c> and <c>name</c>, records, unknowns, and unions of two.</summary>
    public Shape Content(int depth)
    {
        switch (random.Next(depth == 0 ? 2 : 8))
        {
            case 0:
                return Scalar();
            case 1:
                return random.Next(4) == 0 ? Unknown() : Scalar();
            case 2:
                int? min = random.Next(2) == 0 ? null : random.Next(1, 3);
                int? max = random.Next(2) == 0 ? null : 2;
                return new ArrayShape(Content(depth - 1), Reasons(), min, max is { } m && min is { } n && m < n ? null : max);
            case 3:
                return new TupleShape([.. Enumerable.Range(0, random.Next(3)).Select(_ => new ElementShape(Content(depth - 1), Reasons()))]);
            case 4:
            case 5:
                var keys = random.Next(4) switch { 0 => new[] { "id" }, 1 => ["name"], 2 => [], _ => ["id", "name"] };
                return new ObjectShape([.. keys.Select(k => new FieldShape(k, Content(depth - 1), random.Next(3) == 0 ? IrPresence.OmittedWhenNull : IrPresence.Always, Reasons()))])
                {
                    Writers = (KeyWriters)random.Next(1, 8),
                };
            case 6:
                return new RecordShape(Content(depth - 1), Reasons());
            default:
                return JsonShapes.Join(Content(depth - 1), Content(depth - 1));
        }
    }

    /// <summary>
    /// What a string's text is made of (<see cref="Value.Pieces"/>): none known, or one of a few assembled arrays, so that two
    /// values may have the same pieces or others.
    /// </summary>
    private EquatableArray<TextPiece>? Pieces()
    {
        var at = new SourceLocation("p.sql", 1, 1);
        return random.Next(3) switch
        {
            0 => null,
            1 => [new LiteralPiece("[", National: true, at), new ListPiece([new JsonPiece(Content(1))], [new LiteralPiece(",", National: true, at)]), new LiteralPiece("]", National: true, at)],
            _ => [new LiteralPiece("[", National: true, at), new OpaquePiece("t.name", at), new LiteralPiece("]", National: true, at)],
        };
    }

    /// <summary>A value: a scalar, JSON text with or without its content, live JSON, or an unknown; one in six masked.</summary>
    public Value Value()
    {
        var value = Unmasked();
        return random.Next(6) == 0 ? value.Masked("t.masked", new SourceLocation("t.sql", 1, 1)) : value;
    }

    private Value Unmasked()
    {
        var digits = Digits();
        switch (random.Next(6))
        {
            case 0:
            case 1:
                var scalar = Scalar();
                var why = scalar.Type.Sql == "nvarchar(max)" && random.Next(2) == 0 ? Unknown() with { Reason = IrUnknownReason.JsonShapeUnknown } : null;
                return new Value(scalar, Reasons(), JsonWhy: why) { Digits = digits, Pieces = Pieces() };
            case 2:
                return new Value(new JsonTextShape(random.Next(3) == 0 ? null : Content(2)), Reasons()) { Digits = digits };
            case 3:
            case 4:
                return new Value(Content(2), Reasons(), IsLiveJson: true) { Digits = digits };
            default:
                var unknown = Unknown();
                return new Value(unknown, NullReasons.Join(Reasons(), [new IrNullReason(IrNullRule.Unknown, unknown.At.ToString())])) { Digits = digits };
        }
    }

    public JsonSlot Slot() => random.Next(4) switch
    {
        0 => JsonSlot.Empty,
        1 => JsonSlot.Text(Unknown()) with { Pieces = Pieces() },
        2 => JsonSlot.Assembled(Content(2)) with { Pieces = Pieces() },
        _ => JsonSlot.Json(Content(2)),
    };

    public static List<T> Many<T>(int count, Func<T> next) => [.. Enumerable.Range(0, count).Select(_ => next())];

    private IrFactSource Source() => Pick(Sources) with { };

    private T Pick<T>(T[] items) => items[random.Next(items.Length)];
}
#pragma warning restore CA5394
