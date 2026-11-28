using System.Text;
using Haruspex.Cli;
using Haruspex.Ir;
using static Haruspex.TestSupport.IrNavigation;

namespace Haruspex.Tests.Inference;

/// <summary>
/// A path finds a key only as it is spelled, and a writer may spell a key with escapes: a required key of declared JSON is read
/// non-null by a path only when every character of it is one no writer observed escapes; JSON built here is spelled as SQL Server
/// spells it. The same cases run for every reader of declared JSON, which share the rule (<c>JsonPathWalk</c>). Why:
/// docs/decisions/0147-a-key-a-writer-may-escape-may-be-missing-from-a-path.md; observed in <c>EscapedKeyObservationTests</c>.
/// </summary>
public class EscapedKeyTests
{
    // size: letters. café: past ASCII, which System.Text.Json and Python's json.dumps escape. a+b: + is ASCII System.Text.Json
    // escapes. a/b: / is what FOR JSON and JSON_OBJECT escape. a-b and "a b": characters no writer observed escapes.
    private const string Extra = """
        { "type": "object", "required": ["size", "café", "a+b", "a/b", "a-b", "a b"], "properties": {
            "size": { "type": "string", "maxLength": 20 }, "café": { "type": "string", "maxLength": 20 },
            "a+b": { "type": "string", "maxLength": 20 }, "a/b": { "type": "string", "maxLength": 20 },
            "a-b": { "type": "string", "maxLength": 20 }, "a b": { "type": "string", "maxLength": 20 } } }
        """;

    /// <summary>The result's columns, each a key of <see cref="Extra"/>, and the path to it.</summary>
    private static readonly (string Column, string Path)[] Keys =
        [("size", "$.size"), ("cafe", "$.\"café\""), ("plus", "$.\"a+b\""), ("slash", "$.\"a/b\""), ("minus", "$.\"a-b\""), ("blank", "$.\"a b\"")];

    /// <summary>The readers of declared JSON: <c>JSON_VALUE</c> and <c>OPENJSON … WITH</c>, of a column, a parameter and a REST response.</summary>
    public static TheoryData<string> Readers => ["column", "parameter", "openjson-column", "openjson-parameter", "rest"];

    private static string JsonValues(string json) => string.Join(", ", Keys.Select(k => $"JSON_VALUE({json}, '{k.Path}') AS {k.Column}"));

    private static string WithColumns => string.Join(", ", Keys.Select(k => $"{k.Column} nvarchar(20) '{k.Path}'"));

    private const string Column = """ "columns": { "shop.orders.extra": { "json": "schemas/extra.schema.json" } } """;

    /// <summary>The procedure <c>shop.p</c> that reads every key of <see cref="Extra"/> by <paramref name="reader"/>, inferred.</summary>
    private static IrDocument Read(string reader) => reader switch
    {
        "column" => Infer($"CREATE PROCEDURE shop.p AS SELECT {JsonValues("o.extra")} FROM shop.orders AS o;", Column),
        "parameter" => Infer($"CREATE PROCEDURE shop.p @extra nvarchar(max) AS IF @extra IS NULL RETURN; SELECT {JsonValues("@extra")};", Parameter),
        "openjson-column" => Infer($"CREATE PROCEDURE shop.p AS SELECT j.* FROM shop.orders AS o CROSS APPLY OPENJSON(o.extra) WITH ({WithColumns}) AS j;", Column),
        "openjson-parameter" => Infer($"CREATE PROCEDURE shop.p @extra nvarchar(max) AS SELECT j.* FROM OPENJSON(@extra) WITH ({WithColumns}) AS j;", Parameter),
        _ => Infer(
            $"""
            CREATE PROCEDURE shop.p AS
            DECLARE @response nvarchar(max);
            EXEC sys.sp_invoke_external_rest_endpoint @url = N'https://sizes.example/extra', @method = N'GET', @response = @response OUTPUT;
            SELECT j.* FROM OPENJSON(@response, '$.result') WITH ({WithColumns}) AS j;
            """,
            """ "endpoints": [ { "url": "https://sizes.example/extra", "method": "GET", "response": "schemas/extra.schema.json" } ] """),
    };

    private const string Parameter = """ "parameters": { "shop.p.@extra": { "json": "schemas/extra.schema.json" } } """;

    /// <summary>
    /// <paramref name="procedure"/> inferred beside <c>shop.orders</c>, with <paramref name="setting"/> in the config file and
    /// <see cref="Extra"/> as <c>schemas/extra.schema.json</c>.
    /// </summary>
    private static IrDocument Infer(string procedure, string setting = "")
    {
        using var dir = new TempDirectory();
        dir.File("db/objects.sql", $"""
            CREATE SCHEMA shop;
            GO
            CREATE TABLE shop.orders (id int NOT NULL, size int NOT NULL, extra nvarchar(max) NOT NULL);
            GO
            {procedure}
            """);
        dir.File("schemas/extra.schema.json", Extra);
        var project = SqlProject.Load(Path.Combine(dir.Path, "db"));
        Assert.DoesNotContain(project.Diagnostics, d => d.IsError);
        var read = ConfigReader.Read(Encoding.UTF8.GetBytes($"{{ {setting} }}"), Path.Combine(dir.Path, "haruspex.json"), dir.Path);
        Assert.DoesNotContain(read.Diagnostics, d => d.IsError);
        var doc = project.Infer(project.Procedures, read.Expected()).Expected();
        Assert.Empty(doc.Unknowns);
        return doc;
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void A_required_key_every_writer_writes_as_itself_is_read_non_null(string reader)
    {
        var doc = Read(reader);

        Assert.False(Field(doc, "size").Nullable);
        Assert.False(Field(doc, "minus").Nullable);
        Assert.False(Field(doc, "blank").Nullable);
    }

    /// <summary>The columns whose key a writer may escape: the key, and its character a writer escapes.</summary>
    private static readonly (string Column, string Key, string Character)[] Escapable = [("cafe", "café", "é"), ("plus", "a+b", "+"), ("slash", "a/b", "/")];

    [Theory]
    [MemberData(nameof(Readers))]
    public void A_required_key_a_writer_may_escape_may_be_missing_saying_which_key_and_why(string reader)
    {
        var doc = Read(reader);

        foreach (var (column, key, character) in Escapable)
        {
            var because = Assert.Single(Field(doc, column).NullableBecause);
            Assert.Equal(IrNullRule.JsonNullOrAbsent, because.Rule);
            Assert.Contains($"`{key}`", because.Detail, StringComparison.Ordinal);
            Assert.Contains($"`{character}`", because.Detail, StringComparison.Ordinal);
            Assert.Contains("escape", because.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_key_a_writer_may_escape_says_only_that_it_may_not_be_found()
    {
        var doc = Read("column");

        var because = Assert.Single(Field(doc, "cafe").NullableBecause);
        Assert.Equal(
            "`$.\"café\"` may not be found: a writer may write the key `café`'s `é` as an escape (System.Text.Json and Python's json.dumps do, by default), and a path finds a key only as it is spelled",
            because.Detail);
    }

    [Fact]
    public void Under_a_strict_path_a_key_a_writer_may_escape_is_an_error_where_missing_never_null()
    {
        var doc = Infer("""CREATE PROCEDURE shop.p AS SELECT JSON_VALUE(o.extra, 'strict $."café"') AS cafe FROM shop.orders AS o;""", Column);

        Assert.False(Field(doc, "cafe").Nullable);
    }

    [Fact]
    public void A_plain_read_keeps_every_required_key_since_a_parser_decodes_escapes()
    {
        var doc = Infer("CREATE PROCEDURE shop.p AS SELECT o.extra FROM shop.orders AS o;", Column);

        var content = Assert.IsType<IrRef>(Assert.IsType<IrJsonText>(Field(doc, "extra").Type).Content);
        Assert.All(doc.Types[content.Name].Fields, f => Assert.Equal(IrPresence.Always, f.Presence));
    }

    // ---- JSON built here ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_key_for_json_and_json_object_write_as_itself_is_found()
    {
        // SQL Server writes café and a+b as themselves, where an application's writer may not.
        var doc = Infer("""
            CREATE PROCEDURE shop.p AS
            DECLARE @j nvarchar(max) = (SELECT TOP (1) o.id AS [café], o.id AS [a+b] FROM shop.orders AS o FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
            DECLARE @o nvarchar(max) = JSON_OBJECT(N'café': 1, N'a+b': 2);
            IF @j IS NULL RETURN;
            SELECT JSON_VALUE(@j, '$."café"') AS cafe, JSON_VALUE(@j, '$."a+b"') AS plus, JSON_VALUE(@o, '$."café"') AS object_cafe, JSON_VALUE(@o, '$."a+b"') AS object_plus;
            """);

        Assert.All(["cafe", "plus", "object_cafe", "object_plus"], c => Assert.False(Field(doc, c).Nullable));
    }

    /// <summary>JSON built three ways with the key <c>a/b</c>, each read at <c>$."a/b"</c>.</summary>
    public static TheoryData<string> Slashed =>
    [
        "(SELECT TOP (1) o.id AS [a/b] FROM shop.orders AS o FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)",
        "(SELECT TOP (1) o.id AS [a/b] FROM shop.orders AS o FOR JSON AUTO, WITHOUT_ARRAY_WRAPPER)",
        "JSON_OBJECT(N'a/b': 1)",
    ];

    [Theory]
    [MemberData(nameof(Slashed))]
    public void A_key_sql_server_writes_with_its_slash_escaped_is_never_found_by_a_path(string built)
    {
        // FOR JSON and json_object write a/b as a\/b (observed: For_json_and_json_object_escape_a_key_s_slash_…). $."a\/b" would
        // find it, but JsonPath reads no escape but \", so the path walked here, $."a/b", is always NULL, which main claimed non-null.
        var doc = Infer($"""
            CREATE PROCEDURE shop.p AS
            DECLARE @j nvarchar(max) = {built};
            IF @j IS NULL RETURN;
            SELECT JSON_VALUE(@j, '$."a/b"') AS slash;
            """);

        var because = Assert.Single(Field(doc, "slash").NullableBecause);
        Assert.Equal(IrNullRule.JsonNullOrAbsent, because.Rule);
        Assert.Contains("never there", because.Detail, StringComparison.Ordinal);
        Assert.Contains("`a/b`'s `/`", because.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_written_in_a_literal_or_assembled_text_is_found_whatever_its_characters()
    {
        // The SQL spells these keys itself, with no escape (one that has one is unknown), so a path spelling them the same finds them:
        // the server gives 1 for json_value(N'{"a/b":1}', '$."a/b"').
        var doc = Infer("""
            CREATE PROCEDURE shop.p @id int AS
            IF @id IS NULL RETURN;
            DECLARE @assembled nvarchar(max) = N'{"a/b":' + CAST(@id AS nvarchar(11)) + N',"café":' + CAST(@id AS nvarchar(11)) + N'}';
            SELECT JSON_VALUE(N'{"a/b":1,"café":2}', '$."a/b"') AS literal_slash, JSON_VALUE(N'{"a/b":1,"café":2}', '$."café"') AS literal_cafe,
                JSON_VALUE(@assembled, '$."a/b"') AS assembled_slash, JSON_VALUE(@assembled, '$."café"') AS assembled_cafe;
            """);

        Assert.All(["literal_slash", "literal_cafe", "assembled_slash", "assembled_cafe"], c => Assert.False(Field(doc, c).Nullable));
    }
}
