using System.Text;

namespace Haruspex.SqlServer.Inference;

/// <summary>
/// Whether a path finds an object's key: the one rule for every read by a path (<see cref="JsonPathWalk"/>), so for
/// <c>JSON_VALUE</c>, <c>json_query</c> and <c>OPENJSON</c>, with or without <c>WITH</c>, over JSON declared or built here
/// (docs/decisions/0147-a-key-a-writer-may-escape-may-be-missing-from-a-path.md).
/// </summary>
/// <remarks>
/// <para>
/// A path finds a key only when the two are spelled the same, character for character, neither decoded: a key stored as
/// <c>\u0041</c> is not found by <c>$.A</c>, nor one stored as <c>A</c> by <c>$."\u0041"</c>, under <c>nvarchar</c>, <c>varchar</c> and
/// <c>json</c> alike (observed: <c>EscapedKeyObservationTests.A_path_finds_a_key_only_where_both_are_spelled_the_same</c>). A path
/// may spell a key with any escape, and then finds the key spelled the same, but haruspex walks only a path whose one escape is
/// <c>\"</c> (<see cref="JsonPath"/> reads no other), which SQL Server's writers write for a quote too.
/// A parser decodes escapes, so a plain read keeps every key; only a path loses one.
/// </para>
/// <para>
/// So a key is found where its writer wrote it as itself. Of an application's writer nothing is known, so the key is found only
/// when every character is one none of the writers observed escapes (<see cref="NoWriterEscapes"/>); otherwise it may be missing.
/// SQL Server's own spelling is known, so a key it escapes otherwise than as <c>\"</c> is never found by a path haruspex walks.
/// </para>
/// </remarks>
internal static class KeySpelling
{
    /// <summary>Whether a path finds a key.</summary>
    internal enum Verdict
    {
        /// <summary>Wherever the key is, the path finds it.</summary>
        Found,

        /// <summary>The key may be spelled otherwise than the path spells it, so the path may find nothing.</summary>
        MayBeMissing,

        /// <summary>The key is always spelled otherwise, so the path never finds it.</summary>
        Never,
    }

    /// <summary>The ASCII punctuation, and the blank, that no common writer, run or documented, escapes in a key.</summary>
    private const string Punctuation = " !#$%()*,-.:;?@[]^_{|}~";

    /// <summary>
    /// Whether no common writer escapes <paramref name="c"/> in a key: an ASCII letter or digit, the blank, or one of
    /// <see cref="Punctuation"/>. Any writer may escape any character, so this is a claim about writers, not the server: that every
    /// common one, run or documented, writes these as themselves. Run in <c>EscapedKeyObservationTests</c>: <c>System.Text.Json</c>'s
    /// default encoder escapes <c>" &amp; ' + &lt; &gt; \ `</c>, DEL and every character past ASCII; <c>JSON.stringify</c> <c>"</c>,
    /// <c>\</c>, a control character and a lone surrogate; <c>FOR JSON</c> and <c>json_object</c> <c>"</c>, <c>\</c>, <c>/</c> and a
    /// control character. Run by hand under Python 3.14: <c>json.dumps</c> escapes, by default, DEL and every character past ASCII.
    /// Read in their documentation: Newtonsoft.Json escapes <c>"</c>, <c>\</c> and a control character; Gson, by default,
    /// <c>&lt; &gt; &amp; = '</c>; Go's <c>encoding/json</c> <c>&lt; &gt; &amp;</c> and U+2028 and U+2029; PHP's <c>json_encode</c>
    /// <c>/</c> and every character past ASCII.
    /// </summary>
    public static bool NoWriterEscapes(char c) => char.IsAsciiLetterOrDigit(c) || Punctuation.Contains(c, StringComparison.Ordinal);

    /// <summary>Whether SQL Server's writers escape <paramref name="c"/> in a key otherwise than as a path spells it.</summary>
    private static bool SqlServerEscapes(char c) => c is '\\' or '/' or < ' ';

    /// <summary>
    /// Whether a path finds <paramref name="key"/> of an object whose keys <paramref name="writers"/> may have written; and, unless it
    /// is found, why, for a note: a sentence that names the key and its characters a writer escapes.
    /// </summary>
    public static (Verdict Verdict, string? Why) Finds(KeyWriters writers, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var application = writers.HasFlag(KeyWriters.Application) ? Characters(key, c => !NoWriterEscapes(c)) : [];
        var server = writers.HasFlag(KeyWriters.SqlServer) ? Characters(key, SqlServerEscapes) : [];
        if (application.Count == 0 && server.Count == 0)
            return (Verdict.Found, null);

        var why = new List<string>();
        if (application.Count > 0)
            why.Add($"a writer may write the key `{key}`'s {List(application)} as an escape ({Writers(application)})");
        if (server.Count > 0)
            why.Add($"SQL Server writes the key `{key}`'s {List(server)} as an escape");
        var never = writers == KeyWriters.SqlServer;
        return (never ? Verdict.Never : Verdict.MayBeMissing, $"{string.Join(", and ", why)}, and a path finds a key only as it is spelled");
    }

    /// <summary>
    /// The distinct code points of <paramref name="key"/> whose UTF-16 unit <paramref name="escaped"/> holds of, in order: a pair as
    /// one, a lone surrogate as itself.
    /// </summary>
    private static List<int> Characters(string key, Func<char, bool> escaped)
    {
        var found = new List<int>();
        for (var i = 0; i < key.Length; i++)
        {
            if (!escaped(key[i]))
                continue;
            var code = Rune.TryGetRuneAt(key, i, out var rune) ? rune.Value : key[i];
            if (code > char.MaxValue)
                i++;
            if (!found.Contains(code))
                found.Add(code);
        }

        return found;
    }

    /// <summary>
    /// Characters for a note, each in backticks (a backtick in two), a control character or a lone surrogate by its code:
    /// <c>`é` and `ü`</c>, <c>U+0009</c>.
    /// </summary>
    private static string List(List<int> characters)
    {
        var each = characters.Select(c => c is < 0x20 or 0x7f or (>= 0xd800 and <= 0xdfff) ? $"U+{c:X4}" : c == '`' ? "`` ` ``" : $"`{char.ConvertFromUtf32(c)}`").ToList();
        return each.Count == 1 ? each[0] : $"{string.Join(", ", each[..^1])} and {each[^1]}";
    }

    /// <summary>Which writers observed escape the characters, for a note: an example, not a list of every writer.</summary>
    private static string Writers(List<int> characters) =>
        characters.Any(c => c > 0x7f) ? "System.Text.Json and Python's json.dumps do, by default"
        : characters.Contains('/') ? "FOR JSON, json_object and PHP's json_encode do"
        : characters.Any(c => c is '"' or '\\' or < 0x20) ? "every writer escapes some of these"
        : characters.SequenceEqual(['=']) ? "Gson does, by default"
        : "System.Text.Json does, by default";
}
