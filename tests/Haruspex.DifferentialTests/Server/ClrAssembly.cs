using System.Reflection;
using System.Security.Cryptography;

namespace Haruspex.DifferentialTests.Server;

/// <summary>
/// The .NET Framework assembly the CLR observations run on the server, compiled from <see cref="ClrSources"/> once per run by
/// the C# compiler of the SDK that built the tests.
/// </summary>
/// <param name="Bytes">The assembly.</param>
internal sealed record ClrAssembly(byte[] Bytes)
{
    /// <summary>
    /// The public key of ECMA-335's standard libraries, which <c>mscorlib</c> and <c>System.Data</c> are named with. It is
    /// public, and has no private half: a reference names it, and nothing is signed.
    /// </summary>
    private static readonly byte[] StandardKey = [0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0];

    private static readonly Lazy<ClrAssembly> Compiled = new(Compile, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The assembly, compiled the first time it is asked for.</summary>
    public static ClrAssembly Built => Compiled.Value;

    /// <summary>The assembly as a T-SQL binary literal.</summary>
    public string Literal => "0x" + Convert.ToHexString(Bytes);

    /// <summary>Its SHA-512 hash as a T-SQL binary literal, which the server trusts it by.</summary>
    public string Hash => "0x" + Convert.ToHexString(SHA512.HashData(Bytes));

    private static ClrAssembly Compile()
    {
        using var folder = new TempDirectory();
        File.WriteAllBytes(Path.Combine(folder.Path, "standard.snk"), StandardKey);
        string[] reference = ["-refOnly", "-publicSign", "-keyFile:standard.snk", "-noWarn:626,824"];
        Run(folder, "mscorlib", ClrSources.Mscorlib, [.. reference, "-runtimeMetadataVersion:v4.0.30319"]);
        Run(folder, "System.Data", ClrSources.SystemData, [.. reference, "-r:mscorlib.dll"]);
        Run(folder, "Shelf", ClrSources.Assembly(Guid.NewGuid().ToString("N")), ["-r:mscorlib.dll", "-r:System.Data.dll", "-o"]);
        return new ClrAssembly(File.ReadAllBytes(Path.Combine(folder.Path, "Shelf.dll")));
    }

    private static void Run(TempDirectory folder, string name, string source, string[] options)
    {
        File.WriteAllText(Path.Combine(folder.Path, name + ".cs"), source);
        string[] arguments = [Built("CSharpCompiler"), "-nologo", "-noConfig", "-noStdLib", "-target:library", "-deterministic", $"-out:{name}.dll", .. options, name + ".cs"];
        if (!EmitCheckPackage.TryRun(Built("DotNetHost"), arguments, folder.Path, out var output, out var why))
            throw new InvalidOperationException($"{name} did not compile: {why}\n{output}");

        static string Built(string key) =>
            typeof(ClrAssembly).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value
            ?? throw new InvalidOperationException($"The build did not record {key}.");
    }
}
