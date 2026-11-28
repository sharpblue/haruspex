using System.Globalization;
using System.Text;

namespace Haruspex.DifferentialTests.Server;

/// <summary>
/// The C# the CLR observations compile (<see cref="ClrAssembly"/>): the assembly the server runs, and declarations of the few
/// .NET Framework members it uses, to compile it against.
/// </summary>
/// <remarks>
/// The declarations stand in for .NET Framework's reference assemblies, which would be a package. They have the names, version
/// and public key of the framework's <c>mscorlib</c> and <c>System.Data</c>, and no bodies: the compiler reads only a member's
/// signature, and the server binds each reference to its own framework. They are never deployed or kept. A declaration that
/// differs from the framework's is an error when the assembly is created or run, not a wrong observation.
/// </remarks>
internal static class ClrSources
{
    private const string Invariant = "CultureInfo.InvariantCulture";

    /// <summary>
    /// Each .NET type a value is observed as: its name in the functions (<c>S_name</c> scalar, <c>T_name</c> a table of one
    /// column), the type, what makes a value of the text <c>t</c>, and its null, for a type that has one.
    /// </summary>
    private static readonly (string Name, string Type, string Value, string? Null)[] Types =
    [
        ("String", "string", "t", "null"),
        ("SqlString", "SqlString", "new SqlString(t)", "SqlString.Null"),
        ("SqlChars", "SqlChars", "new SqlChars(t.ToCharArray())", "SqlChars.Null"),
        ("Chars", "char[]", "t.ToCharArray()", "null"),
        ("Bytes", "byte[]", "Hex(t)", "null"),
        ("SqlBinary", "SqlBinary", "new SqlBinary(Hex(t))", "SqlBinary.Null"),
        ("SqlBytes", "SqlBytes", "new SqlBytes(Hex(t))", "SqlBytes.Null"),
        ("Byte", "byte", "byte.Parse(t)", null),
        ("SqlByte", "SqlByte", "new SqlByte(byte.Parse(t))", "SqlByte.Null"),
        ("Int16", "short", "short.Parse(t)", null),
        ("SqlInt16", "SqlInt16", "new SqlInt16(short.Parse(t))", "SqlInt16.Null"),
        ("Int32", "int", "int.Parse(t)", null),
        ("NullableInt32", "int?", "int.Parse(t)", "null"),
        ("SqlInt32", "SqlInt32", "new SqlInt32(int.Parse(t))", "SqlInt32.Null"),
        ("Int64", "long", "long.Parse(t)", null),
        ("SqlInt64", "SqlInt64", "new SqlInt64(long.Parse(t))", "SqlInt64.Null"),
        ("Boolean", "bool", "bool.Parse(t)", null),
        ("SqlBoolean", "SqlBoolean", "new SqlBoolean(bool.Parse(t))", "SqlBoolean.Null"),
        ("Double", "double", $"double.Parse(t, {Invariant})", null),
        ("SqlDouble", "SqlDouble", $"new SqlDouble(double.Parse(t, {Invariant}))", "SqlDouble.Null"),
        ("Single", "float", $"float.Parse(t, {Invariant})", null),
        ("SqlSingle", "SqlSingle", $"new SqlSingle(float.Parse(t, {Invariant}))", "SqlSingle.Null"),
        ("Decimal", "decimal", $"decimal.Parse(t, {Invariant})", null),
        ("SqlDecimal", "SqlDecimal", "SqlDecimal.Parse(t)", "SqlDecimal.Null"),
        ("SqlMoney", "SqlMoney", $"new SqlMoney(decimal.Parse(t, {Invariant}))", "SqlMoney.Null"),
        ("DateTime", "DateTime", $"DateTime.Parse(t, {Invariant})", null),
        ("NullableDateTime", "DateTime?", $"DateTime.Parse(t, {Invariant})", "null"),
        ("SqlDateTime", "SqlDateTime", $"new SqlDateTime(DateTime.Parse(t, {Invariant}))", "SqlDateTime.Null"),
        ("TimeSpan", "TimeSpan", $"TimeSpan.Parse(t, {Invariant})", null),
        ("DateTimeOffset", "DateTimeOffset", $"DateTimeOffset.Parse(t, {Invariant})", null),
        ("Guid", "Guid", "Guid.Parse(t)", null),
        ("SqlGuid", "SqlGuid", "new SqlGuid(Guid.Parse(t))", "SqlGuid.Null"),
        ("SqlXml", "SqlXml", "new SqlXml(new MemoryStream(Encoding.Unicode.GetBytes(t)))", "SqlXml.Null"),
    ];

    /// <summary>
    /// The assembly: a scalar and a table function for each of <see cref="Types"/>, a table function that splits a line of
    /// comma-separated values, one that gives a row for each row of a small table, one that returns JSON text, those whose
    /// answer doesn't depend on their argument, and those that answer NULL at random, marked deterministic or not.
    /// </summary>
    /// <param name="run">A text no other run has, so that the assembly's hash, which the server trusts it by, is this run's alone.</param>
    public static string Assembly(string run)
    {
        var source = new StringBuilder("""
            using System; using System.Collections; using System.Data.SqlTypes; using System.Globalization; using System.IO; using System.Text;
            using Microsoft.SqlServer.Server;
            public static class Values {
                static byte[] Hex(string t) { var b = new byte[t.Length / 2]; for (var i = 0; i < b.Length; i++) b[i] = Convert.ToByte(t.Substring(2 * i, 2), 16); return b; }

            """);
        foreach (var (name, type, value, isNull) in Types)
        {
            source.Append(CultureInfo.InvariantCulture, $$"""
                public static {{type}} S_{{name}}(string t) { {{(isNull is null ? "" : $"if (t == null) return {isNull}; ")}}return {{value}}; }
                [SqlFunction(FillRowMethodName = "F_{{name}}")] public static IEnumerable T_{{name}}(string t) { return new object[] { new object[] { t } }; }
                public static void F_{{name}}(object row, out {{type}} v) { v = S_{{name}}((string)((object[])row)[0]); }

                """);
        }

        source.Append(CultureInfo.InvariantCulture, $$"""
            }
            public static class Shelf {
                public const string Run = "{{run}}";
                [SqlFunction(FillRowMethodName = "FillField")] public static IEnumerable Split(SqlString line) { return line.IsNull ? null : line.Value.Split(','); }
                public static void FillField(object row, out SqlString field) { field = new SqlString((string)row); }
                [SqlFunction(FillRowMethodName = "FillMember")] public static IEnumerable Members(SqlString group) { return new object[] { 1, 2 }; }
                public static void FillMember(object row, out SqlInt32 id, out SqlString name) {
                    id = (int)row == 1 ? new SqlInt32(1) : SqlInt32.Null;
                    name = (int)row == 1 ? new SqlString("Ada") : SqlString.Null;
                }
                [SqlFunction(FillRowMethodName = "FillField")] public static IEnumerable NoRows(SqlString line) { return null; }
                public static SqlInt32 Seven(SqlInt32 a, SqlInt32 b) { return new SqlInt32(7); }
                public static SqlInt32 Nothing(SqlInt32 a) { return SqlInt32.Null; }
                public static int Plain(int a) { return 8; }
                static SqlInt32 Coin() { return Guid.NewGuid().ToByteArray()[0] % 2 == 0 ? SqlInt32.Null : new SqlInt32(1); }
                [SqlFunction(IsDeterministic = true, IsPrecise = true)] public static SqlInt32 Marked(SqlInt32 a) { return Coin(); }
                public static SqlInt32 Unmarked(SqlInt32 a) { return Coin(); }
                [SqlFunction(FillRowMethodName = "FillCoin", IsDeterministic = true, IsPrecise = true)] public static IEnumerable MarkedRows(SqlInt32 a) { return new object[] { 1, 2 }; }
                public static void FillCoin(object row, out SqlInt32 v) { v = Coin(); }
                public static SqlString Json(SqlInt32 id) { return new SqlString("{\"id\":" + id.ToString() + "}"); }
            }
            """);
        return source.ToString();
    }

    /// <summary>The members of <c>mscorlib</c> the assembly uses, and those the compiler needs.</summary>
    public const string Mscorlib = """
        [assembly: System.Reflection.AssemblyVersion("4.0.0.0")]
        namespace System {
            public class Object { public Object() { } public virtual string ToString() { throw null; } }
            public abstract class ValueType { } public abstract class Enum : ValueType { } public interface IFormatProvider { }
            public struct Void { } public struct Char { } public struct IntPtr { }
            public struct Boolean { public static bool Parse(string s) { throw null; } }
            public struct Guid { public static Guid Parse(string s) { throw null; } public static Guid NewGuid() { throw null; } public byte[] ToByteArray() { throw null; } }
            public struct Byte { public static byte Parse(string s) { throw null; } }
            public struct Int16 { public static short Parse(string s) { throw null; } }
            public struct Int32 { public static int Parse(string s) { throw null; } }
            public struct Int64 { public static long Parse(string s) { throw null; } }
            public struct Double { public static double Parse(string s, IFormatProvider p) { throw null; } }
            public struct Single { public static float Parse(string s, IFormatProvider p) { throw null; } }
            public struct Decimal { public static decimal Parse(string s, IFormatProvider p) { throw null; } }
            public struct DateTime { public static DateTime Parse(string s, IFormatProvider p) { throw null; } }
            public struct TimeSpan { public static TimeSpan Parse(string s, IFormatProvider p) { throw null; } }
            public struct DateTimeOffset { public static DateTimeOffset Parse(string s, IFormatProvider p) { throw null; } }
            public struct Nullable<T> where T : struct { public Nullable(T value) { } }
            public sealed class String {
                public char[] ToCharArray() { throw null; } public int Length { get { throw null; } } public string Substring(int start, int length) { throw null; }
                public string[] Split(params char[] separator) { throw null; }
                public static string Concat(string a, string b, string c) { throw null; }
            }
            public static class Convert { public static byte ToByte(string s, int fromBase) { throw null; } }
            public abstract class Array { public int Length { get { throw null; } } } public abstract class Delegate { } public abstract class MulticastDelegate : Delegate { }
            public class Exception { } public abstract class Attribute { } public abstract class Type { } public class ParamArrayAttribute : Attribute { }
            public enum AttributeTargets { All = 32767 }
            public class AttributeUsageAttribute : Attribute { public AttributeUsageAttribute(AttributeTargets t) { } public bool AllowMultiple { get; set; } public bool Inherited { get; set; } }
        }
        namespace System.IO { public abstract class Stream { } public class MemoryStream : Stream { public MemoryStream(byte[] b) { } } }
        namespace System.Text { public abstract class Encoding { public static Encoding Unicode { get { throw null; } } public virtual byte[] GetBytes(string s) { throw null; } } }
        namespace System.Globalization { public class CultureInfo : IFormatProvider { public static CultureInfo InvariantCulture { get { throw null; } } } }
        namespace System.Collections {
            public interface IEnumerable { IEnumerator GetEnumerator(); }
            public interface IEnumerator { object Current { get; } bool MoveNext(); void Reset(); }
        }
        namespace System.Reflection { public class AssemblyVersionAttribute : Attribute { public AssemblyVersionAttribute(string v) { } } }
        namespace System.Runtime.CompilerServices {
            public class CompilationRelaxationsAttribute : Attribute { public CompilationRelaxationsAttribute(int i) { } }
            public class RuntimeCompatibilityAttribute : Attribute { public bool WrapNonExceptionThrows { get; set; } }
        }
        namespace System.Runtime.InteropServices { public class OutAttribute : Attribute { } }
        """;

    /// <summary>The members of <c>System.Data</c> the assembly uses.</summary>
    public static string SystemData { get; } = Data();

    private static string Data()
    {
        (string Type, string Of)[] values =
        [
            ("SqlBinary", "byte[]"), ("SqlByte", "byte"), ("SqlInt16", "short"), ("SqlInt32", "int"), ("SqlInt64", "long"), ("SqlBoolean", "bool"),
            ("SqlDouble", "double"), ("SqlSingle", "float"), ("SqlMoney", "decimal"), ("SqlDateTime", "System.DateTime"), ("SqlGuid", "System.Guid"),
        ];
        (string Type, string Of)[] references = [("SqlChars", "char[]"), ("SqlBytes", "byte[]"), ("SqlXml", "System.IO.Stream")];
        return $$"""
            [assembly: System.Reflection.AssemblyVersion("4.0.0.0")]
            namespace System.Data.SqlTypes {
                public struct SqlString { public SqlString(string s) { } public static readonly SqlString Null; public bool IsNull { get { throw null; } } public string Value { get { throw null; } } }
                public struct SqlDecimal { public static SqlDecimal Parse(string s) { throw null; } public static readonly SqlDecimal Null; }
                {{string.Join("\n", values.Select(v => $$"""public struct {{v.Type}} { public {{v.Type}}({{v.Of}} v) { } public static readonly {{v.Type}} Null; public override string ToString() { throw null; } }"""))}}
                {{string.Join("\n", references.Select(v => $$"""public sealed class {{v.Type}} { public {{v.Type}}({{v.Of}} v) { } public static {{v.Type}} Null { get { throw null; } } }"""))}}
            }
            namespace Microsoft.SqlServer.Server { public sealed class SqlFunctionAttribute : System.Attribute { public string FillRowMethodName { get; set; } public bool IsDeterministic { get; set; } public bool IsPrecise { get; set; } } }
            """;
    }
}
