using Haruspex.Diagnostics;
using Haruspex.Ir;
using Haruspex.SqlServer.Model;
using Haruspex.SqlServer.Semantics;
using Haruspex.SqlServer.Semantics.Collations;

namespace Haruspex.SqlServer.Inference;

/// <summary>Maps SQL Server types to the IR's logical types, keeping the SQL type as written.</summary>
/// <remarks>
/// The model says what a type is (<see cref="SqlType"/>), and Semantics what it and the column's constraints
/// guarantee (<see cref="ColumnFacts"/>); what they become in the IR is inference's choice, so the mapping lives here.
/// </remarks>
internal static class SqlTypes
{
    /// <summary>
    /// A CLR scalar function's value: its declared return type with that type's facts, since SQL Server converts what the .NET
    /// method returns to it or fails the call. A decimal is rounded and an <c>nchar</c> or <c>binary</c> padded, and a string or
    /// bytes too long, or a number or date out of range, is an error, whatever the caller's <c>ANSI_WARNINGS</c>: nothing is cut
    /// (observed: A_clr_functions_value_is_converted_to_its_declared_type_or_fails_the_call). A type no CLR function can be
    /// declared to return is unknown (<see cref="ClrRefuses"/>).
    /// </summary>
    public static Shape ToClrShape(DatabaseModel model, SqlType? type, SourceLocation at) =>
        ClrRefuses(model, type, column: false, at) ?? ToConvertedShape(model, type, at);

    /// <summary>
    /// The built-in types a CLR function's value was observed under, as a return type and as a table function's column
    /// (A_clr_functions_value_is_converted_to_its_declared_type_or_fails_the_call). A .NET type maps to few SQL types, and SQL
    /// Server refuses any other when the function is created: <c>varchar</c>, <c>json</c> and <c>rowversion</c> among them
    /// (6551, 6514, 6258, observed: A_clr_function_takes_only_the_declared_type_its_net_type_maps_to).
    /// </summary>
    private static readonly HashSet<BuiltInType> ClrValues =
    [
        BuiltInType.TinyInt, BuiltInType.SmallInt, BuiltInType.Int, BuiltInType.BigInt, BuiltInType.Decimal, BuiltInType.Numeric, BuiltInType.Money,
        BuiltInType.Float, BuiltInType.Real, BuiltInType.Bit, BuiltInType.NChar, BuiltInType.NVarChar, BuiltInType.Date, BuiltInType.Time,
        BuiltInType.DateTime, BuiltInType.DateTime2, BuiltInType.DateTimeOffset, BuiltInType.UniqueIdentifier, BuiltInType.Binary,
        BuiltInType.VarBinary, BuiltInType.Xml,
    ];

    /// <summary>The types a CLR table function's column was observed under that a scalar function's return type can't be (6551).</summary>
    private static readonly HashSet<BuiltInType> ClrColumnValues = [BuiltInType.SmallMoney, BuiltInType.SmallDateTime];

    /// <summary>
    /// Why a CLR function's value of <paramref name="type"/> is unknown; null when the type is one its values were observed
    /// under, or isn't a built-in type or an alias of one, which the mapping of types says itself.
    /// </summary>
    /// <param name="model">The project, whose alias types are resolved.</param>
    /// <param name="type">The declared type.</param>
    /// <param name="column">Whether it is a table function's column, rather than a scalar function's return type.</param>
    /// <param name="at">Where it is declared.</param>
    public static UnknownShape? ClrRefuses(DatabaseModel model, SqlType? type, bool column, SourceLocation at) =>
        SchemaRules.ResolveType(model, type)?.Type is { } resolved && !ClrValues.Contains(resolved) && !(column && ClrColumnValues.Contains(resolved))
            ? new UnknownShape(IrUnknownReason.UnsupportedSqlType, $"SQL Server was not observed to take {type} as {(column ? "a CLR table function's column" : "a CLR scalar function's return type")}.", at)
            : null;

    /// <summary>
    /// A value read inside a body after SQL Server converted it to <paramref name="type"/>, such as a function's parameter or a
    /// T-SQL function's value:
    /// the type's facts hold, because the conversion cuts a string short and rounds a decimal (observed; an overflow is an
    /// error, not a value). No collation is known, so a <c>varchar</c>'s byte length has no encoding.
    /// </summary>
    public static Shape ToConvertedShape(DatabaseModel model, SqlType? type, SourceLocation at) =>
        SchemaRules.ResolveType(model, type) is { } resolved
            ? Converted(ToShape(SchemaRules.TypeFacts(resolved, collation: null), resolved, at, withFacets: true))
            : Unsupported(type, at);

    /// <summary>
    /// A procedure's parameter: its declared type, with that type's facts, in the database's default collation, which a parameter
    /// takes as a column without <c>COLLATE</c> does.
    /// </summary>
    /// <remarks>
    /// The facts are what SQL Server keeps of a value sent to the parameter: a string longer than <c>nvarchar(20)</c> is cut
    /// short and a decimal of more places is rounded, without an error (<c>ParameterObservationTests</c>). So they are true of the
    /// value inside the body, and they are the rule a client checks a value against before sending it, which the generated
    /// database module states (<c>docs/decisions/0054-a-parameter-is-checked-against-its-declared-type.md</c>). A guard in the
    /// body proves nothing about what a caller may send, so nothing narrower is claimed here.
    /// </remarks>
    public static Shape ToParameterShape(DatabaseModel model, SqlType? type, SourceLocation at) =>
        SchemaRules.ResolveType(model, type) is { } resolved
            ? ToShape(SchemaRules.TypeFacts(resolved, model.DefaultCollation), resolved, at, withFacets: true)
            : Unsupported(type, at);

    /// <summary>A column's type, with every fact Semantics proved about its values.</summary>
    public static Shape ToShape(ColumnFacts facts, SqlType? declared, SourceLocation at) =>
        facts.Type is { } resolved ? ToShape(facts, resolved, at, withFacets: true) : Unsupported(declared, at);

    /// <summary>A value of a type the server's rules computed: the type's facts, with no collation known.</summary>
    public static Shape ToConvertedShape(ScalarType type, SourceLocation at)
    {
        var resolved = ResolvedType.Of(type);
        return Converted(ToShape(SchemaRules.TypeFacts(resolved, collation: null), resolved, at, withFacets: true));
    }

    /// <summary>
    /// A value converted to a type the server's rules computed, with what else is proved of it (<paramref name="facts"/>, whose type
    /// is that one): XML's <c>value()</c> of a node whose schema gives facets that survive the conversion.
    /// </summary>
    public static Shape ToConvertedShape(ColumnFacts facts, SourceLocation at) =>
        facts.Type is { } resolved ? Converted(ToShape(facts, resolved, at, withFacets: true)) : Unsupported(null, at);

    /// <summary>
    /// A value converted to <c>nchar(n)</c> rather than stored as one: at most n UTF-16 code units and at least n - 1, not exactly n.
    /// Under a supplementary-character (<c>_SC</c>) collation, the value's or the database's, a conversion cuts a value before a
    /// surrogate pair that doesn't fit and doesn't pad it, so <c>nchar(3)</c> of two emoji is one, and <c>nchar(1)</c> of one is
    /// empty; a stored <c>nchar(n)</c> is always n (observed:
    /// <c>A_string_s_length_is_counted_by_the_server_in_code_units_or_bytes_and_by_zod_in_code_points</c>). A procedure's
    /// parameter keeps its fixed length (<see cref="ToParameterShape"/>), which is what a client sends and binds; a read of it in
    /// the body is converted. A <c>char(n)</c> conversion is cut between characters and padded, so it stays fixed. Why:
    /// docs/decisions/0095-a-length-is-the-server-s-and-a-target-claims-the-fewest-code-points-it-can-hold.md.
    /// </summary>
    private static Shape Converted(Shape shape) =>
        shape is ScalarShape { Type.Facets: { Length: { IsFixed: true, Unit: IrLengthUnit.Utf16 } length } facets } scalar
            ? scalar.WithFacets(facets with
            {
                Length = length with { IsFixed = false },
                MinLength = length.Value > 1 && (facets.MinLength is null || !facets.MinLength.Implies(new IrMinLength(length.Value - 1, length.From)))
                    ? new IrMinLength(length.Value - 1, length.From)
                    : facets.MinLength,
            })
            : shape;

    /// <summary>
    /// The built-in types inference gives a value of, and what each is in the IR. A type not listed reaches the IR as unknown
    /// (<see cref="IrUnknownReason.UnsupportedSqlType"/>); <c>json</c> is text (<see cref="JsonTextShape"/>), not a scalar.
    /// </summary>
    private static readonly Dictionary<BuiltInType, (IrScalarKind Kind, IrFormat? Format)> Scalars = new()
    {
        [BuiltInType.TinyInt] = (IrScalarKind.Number, IrFormat.Uint8),
        [BuiltInType.SmallInt] = (IrScalarKind.Number, IrFormat.Int16),
        [BuiltInType.Int] = (IrScalarKind.Number, IrFormat.Int32),
        [BuiltInType.BigInt] = (IrScalarKind.Number, IrFormat.Int64),
        [BuiltInType.Decimal] = (IrScalarKind.Number, IrFormat.Decimal),
        [BuiltInType.Numeric] = (IrScalarKind.Number, IrFormat.Decimal),
        [BuiltInType.Money] = (IrScalarKind.Number, IrFormat.Decimal),
        [BuiltInType.SmallMoney] = (IrScalarKind.Number, IrFormat.Decimal),
        [BuiltInType.Float] = (IrScalarKind.Number, IrFormat.Float),
        [BuiltInType.Real] = (IrScalarKind.Number, IrFormat.Float),
        [BuiltInType.Bit] = (IrScalarKind.Boolean, null),
        [BuiltInType.Char] = (IrScalarKind.String, null),
        [BuiltInType.VarChar] = (IrScalarKind.String, null),
        [BuiltInType.NChar] = (IrScalarKind.String, null),
        [BuiltInType.NVarChar] = (IrScalarKind.String, null),
        [BuiltInType.Date] = (IrScalarKind.Date, null),
        [BuiltInType.Time] = (IrScalarKind.Time, null),
        [BuiltInType.DateTime] = (IrScalarKind.Datetime, IrFormat.Local),
        [BuiltInType.DateTime2] = (IrScalarKind.Datetime, IrFormat.Local),
        [BuiltInType.SmallDateTime] = (IrScalarKind.Datetime, IrFormat.Local),
        [BuiltInType.DateTimeOffset] = (IrScalarKind.Datetime, IrFormat.Offset),
        [BuiltInType.UniqueIdentifier] = (IrScalarKind.Uuid, null),

        // Bytes, counted in bytes: docs/decisions/0081-binary-values-are-a-kind-of-their-own-counted-in-bytes.md. An image is
        // a varbinary(max) as it arrives (observed: An_image_value_arrives_as_varbinary_max_s_does), but no expression rule
        // that makes a binary value takes one, so an expression over it stays unknown.
        [BuiltInType.Binary] = (IrScalarKind.Bytes, null),
        [BuiltInType.VarBinary] = (IrScalarKind.Bytes, null),
        [BuiltInType.Timestamp] = (IrScalarKind.Bytes, null),
        [BuiltInType.Image] = (IrScalarKind.Bytes, null),

        // XML text, typed by a schema collection or not, with no facets:
        // docs/decisions/0133-an-xml-value-is-a-kind-of-its-own-a-branded-string.md.
        [BuiltInType.Xml] = (IrScalarKind.Xml, null),
    };

    /// <summary>Whether inference gives a value of this type as a scalar.</summary>
    public static bool IsModelled(BuiltInType type) => Scalars.ContainsKey(type);

    /// <summary>The type, when inference gives a value of it as a scalar; otherwise null, which the caller makes unknown.</summary>
    public static ScalarType? Modelled(ScalarType? type) => type is not null && IsModelled(type.Type) ? type : null;

    /// <summary>Whether a scalar is <c>nvarchar(max)</c>, which holds any JSON text unchanged.</summary>
    public static bool IsNVarCharMax(ScalarShape scalar) => scalar.BuiltIn is { IsMax: true } type && type.Type == BuiltInType.NVarChar;

    /// <summary>The type an IR scalar's SQL text names, when inference models it: <c>decimal(19,2)</c>.</summary>
    public static ScalarType? Parse(string? sql) => Modelled(ScalarType.Parse(sql));

    /// <param name="facts">What is known of the values.</param>
    /// <param name="resolved">Their type, which <paramref name="facts"/> holds too.</param>
    /// <param name="at">Where the value is read.</param>
    /// <param name="withFacets">Whether the type's and the column's facts are kept.</param>
    private static Shape ToShape(ColumnFacts facts, ResolvedType resolved, SourceLocation at, bool withFacets)
    {
        var text = resolved.Spelt;
        var type = resolved.Type;

        // A json parameter is text the caller sends, and a driver returns a json column as text. A json column inside FOR
        // JSON or a constructor is live JSON instead, which the Typer decides before this.
        if (type == BuiltInType.Json)
            return new JsonTextShape();
        if (type is null || !Scalars.TryGetValue(type, out var scalar))
            return new UnknownShape(IrUnknownReason.UnsupportedSqlType, $"The type {text} is not modelled yet.", at);
        // An xml value has no facets: its type declares no length, and text that becomes xml is parsed and serialised again, so
        // what was known of the text is not known of it.
        return new ScalarShape(new IrScalar(scalar.Kind, scalar.Format, text, withFacets && scalar.Kind != IrScalarKind.Xml ? Facets(facts) : null), resolved.Scalar);
    }

    /// <remarks>
    /// A user's type is named with the schema it was written with, so that a table type given a built-in type's name reads apart
    /// from it: <c>IrFit</c> takes an unknown built-in type named alike in two documents as the same type.
    /// </remarks>
    private static UnknownShape Unsupported(SqlType? type, SourceLocation at) =>
        // CLR types, and alias types that cannot be resolved for certain.
        new(IrUnknownReason.UnsupportedSqlType, $"The type {(type is { Schema: { } schema } ? $"{schema}.{type}" : type?.ToString() ?? "(none)")} is not modelled yet.", at);

    private static IrFacets? Facets(ColumnFacts facts)
    {
        var irFacets = new IrFacets(
            Length: facts.Length is { } length
                ? new IrLength(length.Value, length.IsFixed, length.Unit == TextUnit.Utf16 ? IrLengthUnit.Utf16 : IrLengthUnit.Bytes, Encoding(length.Encoding), Source(length.From))
                : null,
            MinLength: facts.MinLength is { } minLength ? new IrMinLength(minLength.Value, Source(minLength.From)) { Unit = minLength.InCodePoints ? IrLengthUnit.CodePoints : IrLengthUnit.Utf16 } : null,
            Digits: facts.Digits is { } digits ? new IrDigits(digits.Precision, digits.Scale, Source(digits.From)) : null,
            Min: Bound(facts.Min),
            Max: Bound(facts.Max),
            Values: facts.Values is { } values
                ? new IrValueSet(
                    values.Numbers is { } numbers
                        ? [.. numbers.Select(n => new IrValue(n.ToString(), IsString: false))]
                        // Never null: a value set holds numbers or strings (ValueSetFact).
                        : [.. values.Strings!.Select(s => new IrValue(s, IsString: true))],
                    values.Comparison switch
                    {
                        ValueComparison.Exact => IrValueMatch.Exact,
                        ValueComparison.Padded => IrValueMatch.Padded,
                        ValueComparison.Canonical => IrValueMatch.Canonical,
                        _ => IrValueMatch.Collation,
                    },
                    [.. values.From.Select(Source)],
                    values.Fold?.Name)
                : null);
        return irFacets.IsEmpty ? null : irFacets;
    }

    /// <summary>A fold as the IR holds it.</summary>
    public static IrFold Fold(CollationFold fold) => new(fold.Ignored, fold.Classes);

    private static IrBound? Bound(BoundFact? bound) => bound is null ? null : new IrBound(bound.Value.ToString(), bound.Inclusive, Source(bound.From));

    private static IrByteEncoding Encoding(TextEncoding encoding) => encoding switch
    {
        TextEncoding.SingleByte => IrByteEncoding.SingleByte,
        TextEncoding.Utf8 => IrByteEncoding.Utf8,
        TextEncoding.DoubleByte => IrByteEncoding.DoubleByte,
        _ => IrByteEncoding.Unknown,
    };

    /// <summary>
    /// A type by its name; a check by its schema-qualified name, or its location when it has none; an XML schema collection by its
    /// name and the node's declaration (<c>shop.order_schema {urn:shop}order/@id</c>). A temporary table's or
    /// table variable's check is named by its table (<c>#totals.ck_amount</c>), since it belongs to no schema of the project.
    /// </summary>
    private static IrFactSource Source(FactSource from) => from switch
    {
        TypeSource type => new IrFactSource(IrFactSourceKind.Type, type.Type),
        CheckSource { Check.Name: { } name, Table: DeclaredTable table } => new IrFactSource(IrFactSourceKind.Check, $"{table.Name}.{name}"),
        CheckSource { Check.Name: { } name, Table: Table table } => new IrFactSource(IrFactSourceKind.Check, $"{table.Name.DatabasePrefix}{table.Name.Schema}.{name}"),
        CheckSource check => new IrFactSource(IrFactSourceKind.Check, check.Check.At.ToString()),
        XmlSchemaSource xml => new IrFactSource(IrFactSourceKind.XmlSchema, $"{xml.Collection} {xml.Declaration}"),
    };
}
