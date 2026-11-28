using System.Text;
using System.Text.Json;
using Haruspex.Cli;
using Haruspex.DifferentialTests.Server;
using Haruspex.Ir;

namespace Haruspex.DifferentialTests;

/// <summary>
/// A parameter declared to hold JSON of a schema, through the generated function: the caller passes the value, which is checked
/// against the schema, serialised with <c>JSON.stringify</c> (for <c>varchar</c>, every character past ASCII escaped), and the text
/// checked against the parameter's type before anything is sent (docs/decisions/0143-a-parameter-s-json-may-be-declared.md,
/// docs/decisions/0145-varchar-json-is-read-through-its-code-page.md).
/// </summary>
public partial class MssqlFunctionTests
{
    private const string Order = """
        { "type": "object", "required": ["mode"], "properties": {
            "mode": { "enum": ["fast", "standard"] },
            "note": { "type": "string" } } }
        """;

    private const string ReadOrder = """
        CREATE PROCEDURE outlet.read_order @order nvarchar(60), @label varchar(60) = NULL
        AS SELECT json_value(@order, '$.mode') AS mode, @order AS sent, json_value(@label, '$.note') AS label;
        """;

    /// <summary>The database module of <see cref="ReadOrder"/>, its <c>@order</c> and <c>@label</c> declared to hold JSON of <see cref="Order"/>.</summary>
    private static IrDocument Declared()
    {
        using var dir = new TempDirectory();
        dir.File("db/schema.sql", "CREATE SCHEMA outlet;");
        dir.File("db/read_order.sql", ReadOrder);
        dir.File("schemas/order.schema.json", Order);
        var project = SqlProject.Load(Path.Combine(dir.Path, "db"));
        Assert.Empty(project.Diagnostics);
        var config = ConfigReader.Read(
            Encoding.UTF8.GetBytes("""{ "parameters": { "outlet.read_order.@order": { "json": "schemas/order.schema.json" }, "outlet.read_order.@label": { "json": "schemas/order.schema.json" } } }"""),
            Path.Combine(dir.Path, "haruspex.json"),
            dir.Path).Expected();
        return project.Infer(project.Procedures, config).Expected();
    }

    [Fact]
    public async Task A_declared_parameter_takes_the_value_and_sends_its_json_text()
    {
        var response = Assert.Single(await Run(Declared(), [ReadOrder], new GeneratedFunctions.Call("call", "readOrder", Params(("order", Params(("mode", "fast"), ("note", "x")))))));

        Assert.Null(response.Error);
        Assert.Equal("""{"mode":"fast","note":"x"}""", response.Value!.Value[0].GetProperty("sent").GetString());
    }

    [Fact]
    public async Task A_declared_parameter_takes_null_and_sends_null()
    {
        var response = Assert.Single(await Run(Declared(), [ReadOrder], new GeneratedFunctions.Call("call", "readOrder", Params(("order", null)))));

        Assert.Null(response.Error);
        Assert.Equal("""[{"mode":null,"sent":null,"label":null}]""", JsonSerializer.Serialize(response.Value));
    }

    [Fact]
    public async Task A_value_its_schema_refuses_is_refused_before_the_connection_is_touched()
    {
        var response = Assert.Single(await Run(Declared(), [ReadOrder], new GeneratedFunctions.Call("wrong", "readOrder", Params(("order", Params(("mode", "slow")))), Db: "untouchable")));

        var error = response.Error!;
        Assert.Equal(("HaruspexParameterError", "outlet.read_order"), (error.Name, error.Procedure));
        Assert.Equal(["order.mode"], error.Issues.Select(i => string.Join(".", i.Path)));
    }

    [Fact]
    public async Task Json_text_longer_than_the_parameter_s_type_is_refused_since_the_server_would_cut_it_short()
    {
        // {"mode":"fast","note":"…"} with 36 characters of note is 61: one more than nvarchar(60) keeps, which would cut it into text that isn't JSON.
        var response = Assert.Single(await Run(Declared(), [ReadOrder], new GeneratedFunctions.Call("long", "readOrder", Params(("order", Params(("mode", "fast"), ("note", new string('n', 36))))), Db: "untouchable")));

        var error = response.Error!;
        Assert.True(error.Name == "HaruspexParameterError", error.Message);
        Assert.Equal(["order"], error.Issues.Select(i => string.Join(".", i.Path)));
    }

    [Fact]
    public async Task A_varchar_parameter_s_json_escapes_what_its_code_page_lacks_so_it_arrives_intact()
    {
        // Sent as itself, ā through the driver into code page 1252 is ? (observed by ANN2), and an emoji ??. Escaped, each UTF-16
        // unit is ASCII, which every code page keeps, and json_value reads the escapes back as the characters
        // (docs/decisions/0145-varchar-json-is-read-through-its-code-page.md).
        var response = Assert.Single(await Run(Declared(), [ReadOrder], new GeneratedFunctions.Call("code page", "readOrder", Params(("order", null), ("label", Params(("mode", "fast"), ("note", "ā😀")))))));

        Assert.Null(response.Error);
        Assert.Equal("ā😀", response.Value!.Value[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task A_varchar_parameter_s_length_is_checked_on_the_escaped_text()
    {
        // {"mode":"fast","note":"…"} is 25 characters and each ā escaped is 6: six of them make 61, one more than varchar(60)
        // keeps, though unescaped they would make 31.
        var response = Assert.Single(await Run(Declared(), [ReadOrder], new GeneratedFunctions.Call("escaped", "readOrder", Params(("order", null), ("label", Params(("mode", "fast"), ("note", new string('ā', 6))))), Db: "untouchable")));
        var fits = Assert.Single(await Run(Declared(), [ReadOrder], new GeneratedFunctions.Call("fits", "readOrder", Params(("order", null), ("label", Params(("mode", "fast"), ("note", new string('ā', 5))))))));

        var error = response.Error!;
        Assert.True(error.Name == "HaruspexParameterError", error.Message);
        Assert.Equal(["label"], error.Issues.Select(i => string.Join(".", i.Path)));
        Assert.Null(fits.Error);
        Assert.Equal(new string('ā', 5), fits.Value!.Value[0].GetProperty("label").GetString());
    }

    private const string ReadBadge = """
        CREATE PROCEDURE outlet.read_badge @badge varchar(200) AS
        IF @badge IS NULL RETURN;
        SELECT json_value(@badge, N'$."café"') AS cafe, json_value(@badge, N'$.o."Ω"') AS omega;
        """;

    [Fact]
    public async Task A_varchar_parameter_s_keys_are_sent_as_written_so_a_path_finds_one_its_code_page_holds()
    {
        // A path never finds a key written as an escape (observed by ann3-review), so keys are sent as themselves: café, which code
        // page 1252 holds, arrives intact and is found; Ω, which it lacks, arrives as O, and the read side types it as optional. The
        // read of café is still nullable: another caller may send its é as an escape, which no path finds
        // (docs/decisions/0147-a-key-a-writer-may-escape-may-be-missing-from-a-path.md).
        using var dir = new TempDirectory();
        dir.File("db/schema.sql", "CREATE SCHEMA outlet;");
        dir.File("db/read_badge.sql", ReadBadge);
        dir.File("schemas/badge.schema.json", """
            { "type": "object", "required": ["café", "o"], "properties": {
                "café": { "type": "string", "maxLength": 10 },
                "o": { "type": "object", "required": ["Ω"], "properties": { "Ω": { "type": "string" } } } } }
            """);
        var project = SqlProject.Load(Path.Combine(dir.Path, "db"), "SQL_Latin1_General_CP1_CI_AS");
        var config = ConfigReader.Read(Encoding.UTF8.GetBytes("""{ "parameters": { "outlet.read_badge.@badge": { "json": "schemas/badge.schema.json" } } }"""), Path.Combine(dir.Path, "haruspex.json"), dir.Path).Expected();
        var doc = project.Infer(project.Procedures, config).Expected();

        var response = Assert.Single(await Run(doc, [ReadBadge], new GeneratedFunctions.Call("keys", "readBadge", Params(("badge", Params(("café", "ā"), ("o", Params(("Ω", "p")))))))));

        Assert.Null(response.Error);
        Assert.Equal("ā", response.Value!.Value[0].GetProperty("cafe").GetString());
        Assert.Equal(JsonValueKind.Null, response.Value!.Value[0].GetProperty("omega").ValueKind);
        Assert.True(IrNavigation.Field(doc, "cafe").Nullable);
        Assert.True(IrNavigation.Field(doc, "omega").Nullable);
    }
}
