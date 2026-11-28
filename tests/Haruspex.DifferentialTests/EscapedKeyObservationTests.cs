using System.Globalization;
using System.Text;
using System.Text.Json;
using Haruspex.DifferentialTests.Server;

namespace Haruspex.DifferentialTests;

/// <summary>
/// How a key is spelled in stored JSON, and what a path finds of it, observed and kept as tests: what the rule that a required key
/// may be read non-null only where every writer writes it as itself rests on (docs/decisions/0147-a-key-a-writer-may-escape-may-be-missing-from-a-path.md).
/// A path finds a key only when the two are spelled the same, character for character, neither decoded; the writers below escape
/// different characters of a key. Observed on SQL Server 2025 RTM-CU10, Node 24 and .NET 10.
/// </summary>
public class EscapedKeyObservationTests(SqlServerFixture server)
{
    private const string Backslash = "\\";

    /// <summary>A JSON escape of the UTF-16 unit <paramref name="unit"/>, in lower-case hex unless <paramref name="upper"/>.</summary>
    private static string U(int unit, bool upper = false) => Backslash + "u" + unit.ToString(upper ? "X4" : "x4", CultureInfo.InvariantCulture);

    /// <summary>
    /// What <c>JSON_VALUE</c> finds at <paramref name="path"/> in <paramref name="json"/> as the expression <paramref name="text"/> holds it, and the
    /// key <c>OPENJSON</c> gives for its one member.
    /// </summary>
    private async Task<(string? Value, string Key)> Find(string json, string path, string text)
    {
        await using var session = await server.ReadAsync(TestContext.Current.CancellationToken);
        await using var command = session.Command($"""
            SELECT JSON_VALUE({text}, @path), (SELECT o.[key] FROM OPENJSON({text}) AS o);
            """);
        command.Parameters.AddWithValue("@json", json);
        command.Parameters.AddWithValue("@path", path);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (await reader.IsDBNullAsync(0, TestContext.Current.CancellationToken) ? null : reader.GetString(0), reader.GetString(1));
    }

    public static TheoryData<string> TextTypes =>
    [
        "CAST(@json AS nvarchar(200))",
        "CAST(@json AS varchar(200)) COLLATE SQL_Latin1_General_CP1_CI_AS",
        "CAST(@json AS varchar(200)) COLLATE Latin1_General_100_CI_AS_SC_UTF8",
        "CAST(@json AS json)",
    ];

    [Theory]
    [MemberData(nameof(TextTypes))]
    public async Task A_path_finds_a_key_only_where_both_are_spelled_the_same(string type)
    {
        // Each key is stored escaped, the way one writer or another writes it: as stored, as a path spells it as itself, and decoded.
        (string Stored, string Path, string Key)[] escaped =
        [
            (U('A'), "$.A", "A"),
            ("a" + U('+', upper: true) + "b", "$.\"a+b\"", "a+b"),
            ("a" + Backslash + "/b", "$.\"a/b\"", "a/b"),
            ("a" + U('"') + "b", "$.\"a\\\"b\"", "a\"b"),
            (U(0xe9), "$.\"é\"", "é"),
            (U(0xd83d) + U(0xde00), "$.\"😀\"", "😀"),
        ];
        foreach (var (stored, path, key) in escaped)
        {
            var json = $$"""{"{{stored}}":1}""";
            var (value, read) = await Find(json, path, type);

            Assert.Null(value);
            Assert.Equal(key, read);

            // Spelled the same, the path finds it.
            Assert.Equal("1", (await Find(json, "$.\"" + stored + "\"", type)).Value);
        }

        // Neither way round: a key stored as itself isn't found by a path that escapes it, and the case of an escape's hex counts.
        Assert.Null((await Find("""{"A":1}""", "$.\"" + U('A') + "\"", type)).Value);
        Assert.Null((await Find("{\"a/b\":1}", "$.\"a" + Backslash + "/b\"", type)).Value);
        Assert.Null((await Find($$"""{"{{U(0xe9)}}":1}""", "$.\"" + U(0xe9, upper: true) + "\"", type)).Value);
        Assert.Equal("1", (await Find("""{"a\"b":1}""", "$.\"a\\\"b\"", type)).Value);
    }

    [Fact]
    public async Task For_json_and_json_object_escape_a_key_s_slash_backslash_quote_and_controls_and_nothing_else()
    {
        await using var session = await server.ReadAsync(TestContext.Current.CancellationToken);
        await using var command = session.Command("""
            SELECT (SELECT 1 AS [a/b], 2 AS [a"b], 3 AS [a\b], 4 AS [<&'+`>], 5 AS [gr], 6 AS [a b] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
                   JSON_OBJECT(N'a/b': 1, N'x' + NCHAR(1) + N'y': 2, N'x' + NCHAR(127) + N'y': 3, N'g' + NCHAR(246) + N'r': 4),
                   (SELECT JSON_OBJECTAGG(k.k: 1) FROM (VALUES (N'a/b')) AS k(k)),
                   JSON_MODIFY(N'{}', N'$."a/b"', 1);
            """);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        var b = Backslash;

        Assert.Equal($$"""{"a{{b}}/b":1,"a{{b}}"b":2,"a{{b}}{{b}}b":3,"<&'+`>":4,"gr":5,"a b":6}""", reader.GetString(0));
        Assert.Equal($$"""{"a{{b}}/b":1,"x{{U(1)}}y":2,"x{{(char)127}}y":3,"g{{(char)246}}r":4}""", reader.GetString(1));
        Assert.Equal($$"""{"a{{b}}/b":1}""", reader.GetString(2));

        // JSON_MODIFY writes the key as the path spells it.
        Assert.Equal("""{"a/b":1}""", reader.GetString(3));
    }

    [Fact]
    public void System_text_json_s_default_encoder_escapes_a_key_s_html_characters_and_every_character_past_ascii()
    {
        static string Written(string key)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber(key, 1);
                writer.WriteEndObject();
            }

            var text = Encoding.UTF8.GetString(stream.ToArray());
            return text[2..text.LastIndexOf("\":", StringComparison.Ordinal)];
        }

        var escaped = Enumerable.Range(0x20, 0x60).Select(c => (char)c).Where(c => Written(c.ToString()) != c.ToString());
        Assert.Equal("\"&'+<>\\`" + (char)127, string.Concat(escaped));
        Assert.All(Enumerable.Range(0, 0x20), c => Assert.StartsWith(Backslash, Written(((char)c).ToString()), StringComparison.Ordinal));
        Assert.All(Enumerable.Range(0x80, 0xd800 - 0x80).Concat(Enumerable.Range(0xe000, 0x2000)), c => Assert.StartsWith(Backslash + "u", Written(((char)c).ToString()), StringComparison.Ordinal));
    }

    [Fact]
    public void Json_stringify_escapes_a_key_s_quote_backslash_controls_and_lone_surrogates_only()
    {
        // Every UTF-16 unit as a key, each written by JSON.stringify: the units it writes otherwise than as themselves.
        const string Script = """
            const out = [];
            for (let c = 0; c < 0x10000; c++) {
              const k = String.fromCharCode(c);
              if (JSON.stringify({ [k]: 1 }) !== '{"' + k + '":1}') out.push(c);
            }
            console.log(JSON.stringify(out));
            """;
        Assert.True(EmitCheckPackage.TryRun("node", ["-e", Script], AppContext.BaseDirectory, out var output, out var why), why);
        using var units = JsonDocument.Parse(output);

        int[] expected = [.. Enumerable.Range(0, 0x20), '"', '\\', .. Enumerable.Range(0xd800, 0x800)];
        Assert.Equal(expected, units.RootElement.EnumerateArray().Select(u => u.GetInt32()));
    }
}
