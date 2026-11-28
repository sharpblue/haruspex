using Haruspex.SqlServer.Inference;
using static Haruspex.SqlServer.Inference.KeySpelling;

namespace Haruspex.Tests.Inference;

/// <summary>
/// The rule that says whether a path finds a key, by who may have written it (<c>KeySpelling</c>): every character of the printable
/// ASCII range on each side of the line, and each kind of writer. Why: docs/decisions/0147-a-key-a-writer-may-escape-may-be-missing-from-a-path.md;
/// the writers' escaping is observed in <c>EscapedKeyObservationTests</c>.
/// </summary>
public class KeySpellingTests
{
    [Fact]
    public void No_writer_observed_escapes_letters_digits_the_blank_and_some_punctuation_only()
    {
        var unescaped = string.Concat(Enumerable.Range(0, 0x10000).Select(c => (char)c).Where(NoWriterEscapes));

        Assert.Equal(" !#$%()*,-.0123456789:;?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_abcdefghijklmnopqrstuvwxyz{|}~", unescaped);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("unit price")]
    [InlineData("a-b_c.d:e")]
    [InlineData("")]
    public void A_key_of_characters_no_writer_escapes_is_found_whoever_wrote_it(string key)
    {
        foreach (var writers in Enumerable.Range(1, 7).Select(w => (KeyWriters)w))
            Assert.Equal((Verdict.Found, null), Finds(writers, key));
    }

    [Theory]
    [InlineData("café", "`é`")]
    [InlineData("ä-ö", "`ä` and `ö`")]
    [InlineData("a+b", "`+`")]
    [InlineData("a=b", "`=`")]
    [InlineData("<&'`>", "`<`, `&`, `'`, `` ` `` and `>`")]
    [InlineData("a/b", "`/`")]
    [InlineData("a\"b", "`\"`")]
    [InlineData("a\u007fb", "U+007F")]
    [InlineData("x😀", "`😀`")]
    public void A_key_an_application_wrote_may_be_missing_where_a_writer_may_escape_a_character_of_it(string key, string named)
    {
        var (verdict, why) = Finds(KeyWriters.Application, key);

        Assert.Equal(Verdict.MayBeMissing, verdict);
        Assert.Contains($"`{key}`'s {named} as an escape", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lone_surrogate_is_named_by_its_code()
    {
        // Built here: a theory's data would carry it as U+FFFD.
        var key = "a" + (char)0xd800;

        Assert.Contains("'s U+D800 as an escape", Finds(KeyWriters.Application, key).Why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("café")]
    [InlineData("a+b")]
    [InlineData("a\"b")]
    [InlineData("a\u007fb")]
    public void A_key_sql_server_writes_as_itself_is_found(string key) =>
        Assert.Equal(Verdict.Found, Finds(KeyWriters.SqlServer, key).Verdict);

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a\tb")]
    public void A_key_sql_server_escapes_is_never_found_but_may_be_where_another_writer_may_have_written_it(string key)
    {
        Assert.Equal(Verdict.Never, Finds(KeyWriters.SqlServer, key).Verdict);
        Assert.Equal(Verdict.MayBeMissing, Finds(KeyWriters.SqlServer | KeyWriters.Literal, key).Verdict);
        Assert.Equal(Verdict.MayBeMissing, Finds(KeyWriters.SqlServer | KeyWriters.Application, key).Verdict);
    }

    [Fact]
    public void A_key_written_in_a_literal_is_found_as_it_holds_no_escape() =>
        Assert.Equal(Verdict.Found, Finds(KeyWriters.Literal, "a/b café").Verdict);
}
