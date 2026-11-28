using System.Globalization;
using Haruspex.DifferentialTests.Server;
using Haruspex.DifferentialTests.Validation;

namespace Haruspex.DifferentialTests;

/// <summary>
/// What SQL Server does with the values of CLR functions, observed and kept as tests: the rules haruspex's typing of them from
/// their declarations rests on (Haruspex.SqlServer.Inference's README.md, "CLR functions"). Each test has a database of its own
/// holding an assembly compiled from source (<see cref="ClrDatabase"/>). Observed on SQL Server 2025 RTM-CU10.
/// </summary>
public class ClrFunctionObservationTests(SqlServerFixture server)
{
    private const string Failed = "error";
    private const string Null = "NULL";

    /// <summary>
    /// A .NET type (by its name in <c>ClrSources</c>), a declared type, and texts each with what a value made of it is under that
    /// type: the value and its length in bytes, NULL, or an error. A text of null is the .NET type's null.
    /// </summary>
    public static TheoryData<string, string, string[]> Conversions => new()
    {
        // A string or bytes too long is an error, trailing blanks and zeros included; nchar and binary are padded.
        { "String", "nvarchar(3)", ["ab", "ab 4", "abc", "abc 6", "abcd", Failed, "abc ", Failed, "😀😀", Failed, "", " 0", Null, Null] },
        { "SqlString", "nvarchar(3)", ["ab", "ab 4", "abcd", Failed, Null, Null] },
        { "SqlChars", "nvarchar(3)", ["ab", "ab 4", "abcd", Failed, Null, Null] },
        { "Chars", "nvarchar(3)", ["ab", "ab 4", "abcd", Failed, Null, Null] },
        { "String", "nchar(3)", ["ab", "ab  6", "abc", "abc 6", "abcd", Failed, "😀", "😀  6", "😀😀", Failed, Null, Null] },
        { "SqlString", "nchar(3)", ["ab", "ab  6", "abcd", Failed, Null, Null] },
        { "SqlString", "nvarchar(max)", ["abcdef", "abcdef 12", Null, Null] },
        { "Bytes", "varbinary(3)", ["0102", "0x0102 2", "01020304", Failed, "01020300", Failed, Null, Null] },
        { "SqlBinary", "binary(3)", ["0102", "0x010200 3", "01020304", Failed, Null, Null] },
        { "SqlBytes", "binary(3)", ["0102", "0x010200 3", "01020304", Failed, Null, Null] },

        // SqlBytes of no bytes is NULL under varbinary(max), though not null to .NET.
        { "SqlBytes", "varbinary(max)", ["0102", "0x0102 2", "", Null, Null, Null] },
        { "Bytes", "varbinary(max)", ["", "0x 0", Null, Null] },

        // A .NET value type has no null, so a null text is a .NET error; its Sql type and its nullable form give NULL.
        { "Int32", "int", ["-1", "-1 4", Null, Failed] },
        { "NullableInt32", "int", ["-1", "-1 4", Null, Null] },
        { "SqlInt32", "int", ["-1", "-1 4", Null, Null] },
        { "Byte", "tinyint", ["255", "255 1"] },
        { "SqlByte", "tinyint", ["255", "255 1", Null, Null] },
        { "Int16", "smallint", ["-1", "-1 2"] },
        { "SqlInt16", "smallint", ["-1", "-1 2", Null, Null] },
        { "Int64", "bigint", ["-1", "-1 8"] },
        { "SqlInt64", "bigint", ["-1", "-1 8", Null, Null] },
        { "Boolean", "bit", ["true", "1 1", "false", "0 1"] },
        { "SqlBoolean", "bit", ["true", "1 1", Null, Null] },

        // No NaN and no infinity.
        { "Double", "float", ["1.5", "1.5000000000000000e+000 8", "NaN", Failed, "Infinity", Failed, "-Infinity", Failed] },
        { "SqlDouble", "float", ["1.5", "1.5000000000000000e+000 8", "NaN", Failed, "Infinity", Failed, Null, Null] },
        { "Single", "real", ["1.5", "1.5000000e+000 4", "NaN", Failed, "Infinity", Failed] },
        { "SqlSingle", "real", ["1.5", "1.5000000e+000 4", "NaN", Failed, Null, Null] },

        // A decimal is rounded, half away from zero, to the declared scale; one whose integer digits don't fit is an error.
        { "Decimal", "decimal(5,2)", ["1.5", "1.50 5", "1.234", "1.23 5", "1.245", "1.25 5", "-1.235", "-1.24 5", "999.994", "999.99 5", "999.995", Failed, "1000", Failed] },
        { "SqlDecimal", "decimal(5,2)", ["1.5", "1.50 5", "1.245", "1.25 5", "999.995", Failed, "1000", Failed, Null, Null] },
        { "SqlDecimal", "numeric(5,0)", ["1.5", "2 5", "99999.5", Failed, Null, Null] },
        { "SqlMoney", "money", ["1.23456", "1.2346 8", Null, Null] },

        // A date or time is rounded to the declared fraction, down where rounding up would leave the type's range.
        { "DateTime", "datetime", ["2020-02-29T13:45:29.9999999", "2020-02-29 13:45:30.000 8", "1752-12-31", Failed, "9999-12-31T23:59:59.9999999", "9999-12-31 23:59:59.997 8"] },
        { "SqlDateTime", "datetime", ["2020-02-29T13:45:30", "2020-02-29 13:45:30.000 8", Null, Null] },
        { "DateTime", "date", ["2020-02-29T13:45:30", "2020-02-29 3", "0001-01-01", "0001-01-01 3"] },
        { "NullableDateTime", "date", ["2020-02-29T13:45:30", "2020-02-29 3", Null, Null] },
        { "DateTime", "datetime2(0)", ["2020-02-29T13:45:29.9999999", "2020-02-29 13:45:30 6", "9999-12-31T23:59:59.9999999", "9999-12-31 23:59:59 6"] },
        { "NullableDateTime", "datetime2(3)", ["2020-02-29T13:45:29.9999999", "2020-02-29 13:45:30.000 7", Null, Null] },
        { "TimeSpan", "time(0)", ["13:45:29.9999999", "13:45:30 3", "23:59:59.9999999", "23:59:59 3", "-01:00:00", Failed, "1.00:00:00", Failed] },
        { "DateTimeOffset", "datetimeoffset(0)", ["2020-02-29T13:45:29.9999999+05:30", "2020-02-29 13:45:30 +05:30 8"] },
        { "Guid", "uniqueidentifier", ["6F9619FF-8B86-D011-B42D-00C04FC964FF", "6F9619FF-8B86-D011-B42D-00C04FC964FF 16"] },
        { "SqlGuid", "uniqueidentifier", ["6F9619FF-8B86-D011-B42D-00C04FC964FF", "6F9619FF-8B86-D011-B42D-00C04FC964FF 16", Null, Null] },
        { "SqlXml", "xml", ["<a/>", "<a/> 16", "<a/><b/>", "<a/><b/> 27", "<a>", Failed, Null, Null] },
        { "SqlXml", "xml(dbo.count)", ["<n>1</n>", "<n>1</n> 55", "<other/>", Failed, Null, Null] },
    };

    /// <summary>The types a table function's column may be that a scalar function's return type may not (6551).</summary>
    public static TheoryData<string, string, string[]> ColumnConversions => new()
    {
        { "SqlMoney", "smallmoney", ["1.23456", "1.2346 4", "214748.3647", "214748.3647 4", "214748.3648", Failed, Null, Null] },
        { "SqlDateTime", "smalldatetime", ["2020-02-29T13:45:30", "2020-02-29 13:46:00.000 4", "1899-12-31", Failed, "2079-06-06T23:59:30", Failed, Null, Null] },
    };

    private const string Count = """CREATE XML SCHEMA COLLECTION dbo.count AS N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="n" type="xs:int"/></xs:schema>';""";

    [Theory]
    [MemberData(nameof(Conversions))]
    public Task A_clr_functions_value_is_converted_to_its_declared_type_or_fails_the_call(string net, string declared, string[] observed) =>
        Converts(net, declared, observed, scalar: true);

    [Theory]
    [MemberData(nameof(ColumnConversions))]
    public Task A_clr_table_functions_column_may_be_smallmoney_or_smalldatetime(string net, string declared, string[] observed) =>
        Converts(net, declared, observed, scalar: false);

    /// <summary>
    /// Each text's value as a scalar function's result and as a table function's column, under ANSI_WARNINGS and ARITHABORT on and
    /// off, in a database of SQL Server's default collation and one of a supplementary-character UTF-8 one: the same in all.
    /// </summary>
    private async Task Converts(string net, string declared, string[] observed, bool scalar)
    {
        var style = declared switch
        {
            _ when declared.Contains("binary", StringComparison.Ordinal) => 1,
            "float" or "real" => declared == "float" ? 3 : 1,
            _ when declared.Contains("date", StringComparison.Ordinal) || declared.StartsWith("time", StringComparison.Ordinal) => 121,
            _ when declared.EndsWith("money", StringComparison.Ordinal) => 2,
            _ => 0,
        };
        foreach (var collation in new[] { null, "Latin1_General_100_CI_AS_SC_UTF8" })
            await ConvertsIn(collation, net, declared, observed, scalar, style);
    }

    private async Task ConvertsIn(string? collation, string net, string declared, string[] observed, bool scalar, int style)
    {
        await using var clr = await ClrDatabase.CreateAsync(server, collation);
        await clr.ExecuteAsync(Count);
        if (scalar)
            await clr.ExecuteAsync($"CREATE FUNCTION dbo.s(@t nvarchar(60)) RETURNS {declared} AS EXTERNAL NAME Shelf.[Values].S_{net};");
        await clr.ExecuteAsync($"CREATE FUNCTION dbo.t(@t nvarchar(60)) RETURNS TABLE (v {declared}) AS EXTERNAL NAME Shelf.[Values].T_{net};");
        for (var i = 0; i < observed.Length; i += 2)
        {
            var text = observed[i] == Null ? "NULL" : $"N'{observed[i]}'";
            string[] sources = scalar ? [$"(SELECT dbo.s({text}) AS v)", $"dbo.t({text})"] : [$"dbo.t({text})"];
            var expected = observed[i + 1] switch
            {
                Failed => Failed,
                Null => "v,n:  ,1",
                var value => $"v,n: {value},0",
            };
            foreach (var source in sources)
            {
                foreach (var session in new[] { "ON", "OFF" })
                {
                    var outcome = await clr.CallAsync($"""
                        SET ANSI_WARNINGS {session}; SET ARITHABORT {session};
                        SELECT CONCAT(CONVERT(nvarchar(100), x.v, {style}), N' ', DATALENGTH(x.v)) AS v, IIF(x.v IS NULL, 1, 0) AS n FROM {source} AS x;
                        """);

                    // 6522 from a scalar function, 6260 from a table function's row, 6913 from a schema collection's validation.
                    Assert.Equal(expected, outcome.Error is 6522 or 6260 or 6913 ? Failed : outcome.ToString());
                }
            }
        }
    }

    [Theory]
    [InlineData("SqlString", "varchar(3)")]
    [InlineData("SqlString", "char(3)")]
    [InlineData("SqlString", "varchar(max)")]
    [InlineData("SqlString", "text")]
    [InlineData("SqlString", "ntext")]
    [InlineData("SqlString", "json")]
    [InlineData("SqlString", "xml")]
    [InlineData("SqlBinary", "image")]
    [InlineData("SqlBinary", "rowversion")]
    [InlineData("SqlInt32", "bigint")]
    [InlineData("SqlInt32", "decimal(5,2)")]
    [InlineData("SqlDouble", "real")]
    [InlineData("SqlDecimal", "money")]
    public async Task A_clr_function_takes_only_the_declared_type_its_net_type_maps_to(string net, string declared)
    {
        await using var clr = await ClrDatabase.CreateAsync(server);

        Assert.Equal(6551, await clr.ErrorAsync($"CREATE FUNCTION dbo.s(@t nvarchar(60)) RETURNS {declared} AS EXTERNAL NAME Shelf.[Values].S_{net};"));
        Assert.Contains(
            await clr.ErrorAsync($"CREATE FUNCTION dbo.t(@t nvarchar(60)) RETURNS TABLE (v {declared}) AS EXTERNAL NAME Shelf.[Values].T_{net};"),
            new int?[] { 6258, 6514 });
    }

    [Theory]
    [InlineData("SqlMoney", "smallmoney")]
    [InlineData("SqlDecimal", "smallmoney")]
    [InlineData("Decimal", "smallmoney")]
    [InlineData("SqlDateTime", "smalldatetime")]
    [InlineData("DateTime", "smalldatetime")]
    public async Task A_clr_scalar_function_is_not_created_to_return_smallmoney_or_smalldatetime(string net, string declared)
    {
        await using var clr = await ClrDatabase.CreateAsync(server);

        Assert.Equal(6551, await clr.ErrorAsync($"CREATE FUNCTION dbo.s(@t nvarchar(60)) RETURNS {declared} AS EXTERNAL NAME Shelf.[Values].S_{net};"));
    }

    private static string Members(string columns, string order = "") =>
        $"CREATE FUNCTION dbo.members(@group nvarchar(256)) RETURNS TABLE ({columns}) {order} AS EXTERNAL NAME Shelf.Shelf.Members;";

    [Fact]
    public async Task A_clr_table_functions_column_may_be_null_and_takes_no_not_null()
    {
        await using var clr = await ClrDatabase.CreateAsync(server);
        await clr.ExecuteAsync("CREATE TYPE dbo.code FROM nvarchar(200) NOT NULL;");

        Assert.Equal(6526, await clr.ErrorAsync(Members("member_id int NOT NULL, display_name nvarchar(200)")));
        Assert.Equal(6526, await clr.ErrorAsync(Members("member_id int, display_name dbo.code")));
        Assert.Equal(6525, await clr.ErrorAsync(Members("member_id int PRIMARY KEY, display_name nvarchar(200)")));
        Assert.Equal(6525, await clr.ErrorAsync(Members("member_id int UNIQUE, display_name nvarchar(200)")));
        Assert.Equal(6526, await clr.ErrorAsync(Members("member_id int CHECK (member_id > 0), display_name nvarchar(200)")));
        Assert.Equal(6526, await clr.ErrorAsync(Members("member_id int DEFAULT 1, display_name nvarchar(200)")));
        Assert.Equal(6514, await clr.ErrorAsync(Members("member_id int IDENTITY, display_name nvarchar(200)")));
        Assert.Equal(6526, await clr.ErrorAsync(Members("member_id AS 1, display_name nvarchar(200)")));

        await clr.ExecuteAsync(Members("member_id int NULL, display_name nvarchar(200)", "ORDER (member_id)"));
        Assert.Equal("member_id,display_name: 1,Ada;null,null", (await clr.CallAsync("SELECT * FROM dbo.members(N'staff');")).ToString());
    }

    [Fact]
    public async Task A_clr_table_functions_columns_are_named_and_ordered_by_its_declaration()
    {
        await using var clr = await ClrDatabase.CreateAsync(server);

        // The fill-row method's parameters are id and name; the declaration's names stand. It must declare as many, of the types.
        Assert.Equal(6208, await clr.ErrorAsync(Members("member_id int")));
        Assert.Equal(6208, await clr.ErrorAsync(Members("member_id int, display_name nvarchar(200), extra int")));
        Assert.Equal(6258, await clr.ErrorAsync(Members("display_name nvarchar(200), member_id int")));
        await clr.ExecuteAsync(Members("number int, called nvarchar(200) COLLATE Latin1_General_BIN2"));
        Assert.Equal("number,called: 1,Ada;null,null", (await clr.CallAsync("SELECT * FROM dbo.members(N'staff');")).ToString());

        // The column's collation is the one declared: BIN2 tells case apart.
        Assert.Equal("number: ", (await clr.CallAsync("SELECT m.number FROM dbo.members(N'staff') AS m WHERE m.called = N'ADA';")).ToString());
    }

    [Fact]
    public async Task A_clr_table_function_gives_a_row_for_each_item_and_none_for_a_null_enumeration()
    {
        await using var clr = await ClrDatabase.CreateAsync(server);
        await clr.ExecuteAsync("CREATE FUNCTION dbo.split(@line nvarchar(4000)) RETURNS TABLE (field nvarchar(3)) AS EXTERNAL NAME Shelf.Shelf.Split;");
        await clr.ExecuteAsync("CREATE FUNCTION dbo.none(@line nvarchar(4000)) RETURNS TABLE (field nvarchar(3)) AS EXTERNAL NAME Shelf.Shelf.NoRows;");

        Assert.Equal("field: a;;bcd", (await clr.CallAsync("SELECT * FROM dbo.split(N'a,,bcd');")).ToString());
        Assert.Equal("field: ", (await clr.CallAsync("SELECT * FROM dbo.split(NULL);")).ToString());
        Assert.Equal("field: ", (await clr.CallAsync("SELECT * FROM dbo.none(N'a');")).ToString());

        // A row before a field too long may have been sent, and the call fails all the same.
        Assert.Equal(6260, (await clr.CallAsync("SELECT * FROM dbo.split(N'a,abcd');")).Error);
    }

    [Fact]
    public async Task Returns_null_on_null_input_makes_a_clr_function_null_without_running_it()
    {
        await using var clr = await ClrDatabase.CreateAsync(server);
        const string nullInput = "WITH RETURNS NULL ON NULL INPUT";
        await clr.ExecuteAsync("CREATE FUNCTION dbo.seven(@a int, @b int) RETURNS int AS EXTERNAL NAME Shelf.Shelf.Seven;");
        await clr.ExecuteAsync($"CREATE FUNCTION dbo.seven_null(@a int, @b int) RETURNS int {nullInput} AS EXTERNAL NAME Shelf.Shelf.Seven;");
        await clr.ExecuteAsync("CREATE FUNCTION dbo.plain(@a int) RETURNS int AS EXTERNAL NAME Shelf.Shelf.Plain;");
        await clr.ExecuteAsync($"CREATE FUNCTION dbo.plain_null(@a int) RETURNS int {nullInput} AS EXTERNAL NAME Shelf.Shelf.Plain;");
        await clr.ExecuteAsync($"CREATE FUNCTION dbo.nothing(@a int) RETURNS int {nullInput} AS EXTERNAL NAME Shelf.Shelf.Nothing;");

        // Seven answers 7 whatever it is given, so a NULL shows it didn't run. Plain takes int, which holds no null (6569).
        Assert.Equal("a,b,c,d,e: 7,7,7,null,null", (await clr.CallAsync("SELECT dbo.seven(1, 2) AS a, dbo.seven(NULL, 2) AS b, dbo.seven_null(1, 2) AS c, dbo.seven_null(NULL, 2) AS d, dbo.seven_null(1, NULL) AS e;")).ToString());
        Assert.Equal(6569, (await clr.CallAsync("SELECT dbo.plain(NULL) AS a;")).Error);
        Assert.Equal("a: null", (await clr.CallAsync("SELECT dbo.plain_null(NULL) AS a;")).ToString());

        // It says nothing of an input that isn't null: the method may still return one.
        Assert.Equal("a: null", (await clr.CallAsync("SELECT dbo.nothing(1) AS a;")).ToString());

        // A table function can't be declared with it (487).
        Assert.Equal(487, await clr.ErrorAsync($"CREATE FUNCTION dbo.members(@group nvarchar(256)) RETURNS TABLE (member_id int, display_name nvarchar(200)) {nullInput} AS EXTERNAL NAME Shelf.Shelf.Members;"));
    }

    [Fact]
    public async Task A_clr_scalar_function_of_an_alias_type_not_null_returns_null_and_no_more_than_its_length()
    {
        await using var clr = await ClrDatabase.CreateAsync(server);
        await clr.ExecuteAsync("CREATE TYPE dbo.code FROM nvarchar(5) NOT NULL;");
        await clr.ExecuteAsync("CREATE FUNCTION dbo.coded(@t nvarchar(60)) RETURNS dbo.code AS EXTERNAL NAME Shelf.[Values].S_SqlString;");

        Assert.Equal("a,b: null,abcde", (await clr.CallAsync("SELECT dbo.coded(NULL) AS a, dbo.coded(N'abcde') AS b;")).ToString());
        Assert.Equal(6522, (await clr.CallAsync("SELECT dbo.coded(N'abcdef') AS a;")).Error);
    }

    [Theory]
    [InlineData("SELECT x.v FROM (SELECT {0}(i.id) AS v FROM dbo.item AS i) AS x WHERE x.v IS NOT NULL")]
    [InlineData("SELECT a.v FROM dbo.item AS i CROSS APPLY (SELECT {0}(i.id) AS v) AS a WHERE a.v IS NOT NULL")]
    [InlineData("SELECT x.v FROM (SELECT TOP (100000) {0}(i.id) AS v FROM dbo.item AS i) AS x WHERE x.v IS NOT NULL")]
    [InlineData("SELECT CASE WHEN x.v IS NOT NULL THEN x.v ELSE -1 END AS v FROM (SELECT {0}(i.id) AS v FROM dbo.item AS i) AS x")]
    public async Task A_filter_proves_nothing_of_a_clr_scalar_function_wrongly_marked_deterministic(string query)
    {
        // The server copies a call it is told is deterministic into the filter and the select list, and runs it for each; the
        // mark is the method's attribute, in the assembly. Each of 2,000 rows is NULL half the time, so no run misses it. A known
        // gap: haruspex takes the filter's proof (decision 0148).
        await using var clr = await ClrDatabase.CreateAsync(server);
        await clr.ExecuteAsync("CREATE TABLE dbo.item (id int NOT NULL PRIMARY KEY); INSERT dbo.item SELECT value FROM GENERATE_SERIES(1, 2000);");
        await clr.ExecuteAsync("CREATE FUNCTION dbo.marked(@a int) RETURNS int AS EXTERNAL NAME Shelf.Shelf.Marked;");
        await clr.ExecuteAsync("CREATE FUNCTION dbo.unmarked(@a int) RETURNS int AS EXTERNAL NAME Shelf.Shelf.Unmarked;");

        // Kept in a table first, so that counting doesn't change the plan.
        async Task<string> AnyNull(string function) => (await clr.CallAsync($"""
            CREATE TABLE #seen (v int NULL);
            INSERT #seen {string.Format(CultureInfo.InvariantCulture, query, function)};
            SELECT IIF(COUNT(*) > COUNT(v), 1, 0) AS nulls FROM #seen;
            DROP TABLE #seen;
            """)).ToString();

        Assert.Equal("nulls: 1", await AnyNull("dbo.marked"));
        Assert.Equal("nulls: 0", await AnyNull("dbo.unmarked"));
    }

    [Fact]
    public async Task A_filter_holds_of_a_clr_table_functions_column_though_it_is_marked_deterministic()
    {
        await using var clr = await ClrDatabase.CreateAsync(server);
        await clr.ExecuteAsync("CREATE TABLE dbo.item (id int NOT NULL PRIMARY KEY); INSERT dbo.item SELECT value FROM GENERATE_SERIES(1, 2000);");
        await clr.ExecuteAsync("CREATE FUNCTION dbo.marked_rows(@a int) RETURNS TABLE (v int) AS EXTERNAL NAME Shelf.Shelf.MarkedRows;");

        Assert.Equal(
            "kept,nulls: 1,0",
            (await clr.CallAsync("""
                CREATE TABLE #seen (v int NULL);
                INSERT #seen SELECT t.v FROM dbo.item AS i CROSS APPLY dbo.marked_rows(i.id) AS t WHERE t.v IS NOT NULL;
                SELECT IIF(COUNT(*) > 0, 1, 0) AS kept, COUNT(*) - COUNT(v) AS nulls FROM #seen;
                """)).ToString());
    }

    [Fact]
    public async Task What_haruspex_infers_of_a_procedure_that_reads_clr_functions_holds_for_what_it_returns()
    {
        const string members = "CREATE FUNCTION dbo.members(@group nvarchar(256)) RETURNS TABLE (member_id int, display_name nvarchar(3)) AS EXTERNAL NAME Shelf.Shelf.Members;";
        const string json = "CREATE FUNCTION dbo.as_json(@id int) RETURNS nvarchar(20) AS EXTERNAL NAME Shelf.Shelf.Json;";
        const string nothing = "CREATE FUNCTION dbo.nothing(@a int) RETURNS int AS EXTERNAL NAME Shelf.Shelf.Nothing;";
        const string read = "CREATE PROCEDURE dbo.reconcile AS SELECT m.member_id, m.display_name, dbo.as_json(1) AS text, dbo.nothing(1) AS nothing FROM dbo.members(N'staff') AS m;";
        await using var clr = await ClrDatabase.CreateAsync(server);
        foreach (var batch in new[] { members, json, nothing, read })
            await clr.ExecuteAsync(batch);

        var observed = await clr.RunAsync("dbo.reconcile");
        var ir = InferProcedure.Infer("dbo.reconcile", ("m.sql", members), ("j.sql", json), ("n.sql", nothing), ("p.sql", read));

        Assert.Empty(ir.Unknowns);
        Assert.Equal(2, Assert.Single(observed).Rows.Count);
        Assert.Empty(IrValidator.Validate(ir, "dbo.reconcile", observed).Violations);
    }

    [Fact]
    public async Task A_test_database_withdraws_the_trust_of_its_assembly_and_strict_security_stays_on()
    {
        const string state = """
            SELECT (SELECT COUNT(*) FROM sys.trusted_assemblies WHERE hash = {0}) AS trusted,
                   (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'clr strict security') AS strict;
            """;
        var query = string.Format(CultureInfo.InvariantCulture, state, ClrAssembly.Built.Hash);

        await using (var clr = await ClrDatabase.CreateAsync(server))
            Assert.Equal("trusted,strict: 1,1", (await clr.CallAsync(query)).ToString());

        await using var after = await server.ScratchAsync();
        await using var session = await after.SessionAsync(TestContext.Current.CancellationToken);
        await using var command = session.Command(query);
        Assert.Equal("trusted,strict: 0,1", (await ObservedCall.Read(command, errorsAsMessages: false, TestContext.Current.CancellationToken)).ToString());
    }
}
