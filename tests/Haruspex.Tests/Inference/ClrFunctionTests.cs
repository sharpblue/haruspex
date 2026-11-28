using Haruspex.Cli;
using Haruspex.Ir;
using Haruspex.SqlServer.Model;
using Haruspex.Tests.Sources;
using static Haruspex.TestSupport.IrNavigation;

namespace Haruspex.Tests.Inference;

/// <summary>
/// CLR functions, typed from their declarations: SQL Server converts what a .NET method returns to the declared type or fails
/// the call, and any value may be NULL. The engine rules are observed in the differential tests' <c>ClrFunctionObservationTests</c>,
/// named where they matter.
/// </summary>
public class ClrFunctionTests
{
    private const string Members = """
        CREATE FUNCTION dir.group_members (@group nvarchar(256)) RETURNS TABLE (member_id int, display_name nvarchar(200), initials nchar(3), score decimal(5, 2), joined datetime2(0))
            AS EXTERNAL NAME DirTools.[Dir.Groups].Members;
        """;

    private static IrDocument Procedure(string body, params string[] schema) =>
        InferProcedure.Infer("dbo.p", [.. schema.Select((sql, i) => ($"s{i}.sql", sql)), ("p.sql", $"CREATE PROCEDURE dbo.p AS\n{body}")]);

    private static IrDocument Returning(string type, string options = "") =>
        Procedure("SELECT dbo.f(1) AS v;", $"CREATE FUNCTION dbo.f(@x int) RETURNS {type} {options} AS EXTERNAL NAME Lib.[Lib.Functions].F;");

    private static IrDocument Column(string declaration, params string[] schema) =>
        Procedure("SELECT t.v FROM dbo.t(1) AS t;", [.. schema, $"CREATE FUNCTION dbo.t(@x int) RETURNS TABLE (v {declaration}) AS EXTERNAL NAME Lib.[Lib.Functions].T;"]);

    private static IrScalar Scalar(IrDocument doc, string name) => Assert.IsType<IrScalar>(Field(doc, name).Type);

    // ---- table-valued functions -------------------------------------------------------------------------------------

    [Fact]
    public void A_clr_table_functions_columns_are_its_declaration_each_nullable()
    {
        // Observed: A_clr_table_functions_column_may_be_null_and_takes_no_not_null.
        var doc = Procedure("SELECT m.member_id, m.display_name, m.initials, m.score, m.joined FROM dir.group_members(N'staff') AS m;", Members);

        AllTyped(doc);
        Assert.All(
            [("member_id", "int"), ("display_name", "nvarchar(200)"), ("initials", "nchar(3)"), ("score", "decimal(5,2)"), ("joined", "datetime2(0)")],
            c =>
            {
                Assert.Equal(c.Item2, Scalar(doc, c.Item1).Sql);
                Assert.Equal([IrNullRule.NullableColumn], Rules(doc, c.Item1));
            });
        Assert.Equal("dir.group_members.member_id", Assert.Single(Field(doc, "member_id").NullableBecause).At);
    }

    [Fact]
    public void A_clr_table_functions_column_keeps_its_declared_types_facts()
    {
        // Observed: A_clr_functions_value_is_converted_to_its_declared_type_or_fails_the_call.
        var doc = Procedure("SELECT m.display_name, m.score FROM dir.group_members(N'staff') AS m;", Members);

        Assert.Equal(200, Scalar(doc, "display_name").Facets?.Length?.Value);
        var digits = Scalar(doc, "score").Facets?.Digits;
        Assert.Equal((5, 2), (digits?.Precision, digits?.Scale));
    }

    [Fact]
    public void A_clr_table_function_reconciles_with_a_table()
    {
        var doc = Procedure(
            "SELECT m.member_id FROM dir.group_members(N'staff') AS m EXCEPT SELECT r.member_id FROM shop.desired_members AS r;",
            Members,
            "CREATE TABLE shop.desired_members (member_id int NOT NULL PRIMARY KEY);");

        AllTyped(doc);
        Assert.Equal("int", Scalar(doc, "member_id").Sql);
        Assert.Equal([IrNullRule.NullableColumn], Rules(doc, "member_id"));
    }

    [Fact]
    public void A_clr_table_function_has_any_number_of_rows_and_no_key()
    {
        // The server refuses a key in its declaration (6525), so one written there proves nothing: a filter on it is many rows.
        var doc = Procedure(
            "SELECT t.id FROM dbo.t(1) AS t WHERE t.id = 1;",
            "CREATE FUNCTION dbo.t(@x int) RETURNS TABLE (id int NOT NULL PRIMARY KEY, n int) AS EXTERNAL NAME Lib.[Lib.Functions].T;");

        Assert.Equal(IrCardinality.Many, Assert.Single(doc.Objects[0].Results!).Cardinality);
    }

    [Theory]
    [InlineData("int NOT NULL")]
    [InlineData("int PRIMARY KEY")]
    [InlineData("int IDENTITY")]
    public void A_clr_table_functions_column_is_nullable_whatever_the_declaration_adds(string declaration) =>
        Assert.Equal([IrNullRule.NullableColumn], Rules(Column(declaration), "v"));

    [Fact]
    public void A_clr_table_functions_check_proves_nothing() =>
        Assert.Null(Scalar(Column("int CHECK (v IN (1, 2))"), "v").Facets?.Values);

    [Fact]
    public void A_clr_table_functions_column_of_an_alias_type_is_its_base_type_and_nullable()
    {
        var doc = Column("dbo.code", "CREATE TYPE dbo.code FROM nvarchar(5) NOT NULL;");

        Assert.Equal(5, Scalar(doc, "v").Facets?.Length?.Value);
        Assert.Equal([IrNullRule.NullableColumn], Rules(doc, "v"));
    }

    [Theory]
    [InlineData("varchar(10)")]
    [InlineData("char(3)")]
    [InlineData("json")]
    [InlineData("rowversion")]
    [InlineData("image")]
    public void A_clr_function_declared_with_a_type_the_server_refuses_stays_unknown(string type)
    {
        // Observed: A_clr_function_takes_only_the_declared_type_its_net_type_maps_to.
        Assert.Equal(IrUnknownReason.UnsupportedSqlType, Assert.Single(Column(type).Unknowns).Reason);
        Assert.NotEmpty(Returning(type).Unknowns);
    }

    [Fact]
    public void A_clr_table_functions_computed_column_is_unknown() =>
        Assert.Equal(IrUnknownReason.ExternalCode, Assert.Single(Column("AS 1").Unknowns).Reason);

    [Fact]
    public void A_clr_table_function_called_with_the_wrong_number_of_arguments_is_unknown() =>
        Assert.Equal(IrUnknownReason.UnsupportedSource, Assert.Single(Procedure("SELECT m.member_id FROM dir.group_members() AS m;", Members).Unknowns).Reason);

    [Fact]
    public void A_clr_scalar_function_in_from_is_not_a_table() =>
        Assert.Equal(
            IrUnknownReason.UnresolvedTable,
            Assert.Single(Procedure("SELECT * FROM dbo.f(1) AS t;", "CREATE FUNCTION dbo.f(@x int) RETURNS int AS EXTERNAL NAME Lib.[Lib.Functions].F;").Unknowns).Reason);

    [Fact]
    public void A_clr_procedure_stays_unknown() =>
        Assert.Equal(
            IrUnknownReason.ExternalCode,
            Assert.Single(InferProcedure.Infer("dbo.q", ("q.sql", "CREATE PROCEDURE dbo.q AS EXTERNAL NAME Lib.[Lib.Procedures].Q;")).Unknowns).Reason);

    // ---- scalar functions -------------------------------------------------------------------------------------------

    [Fact]
    public void A_clr_scalar_functions_string_keeps_its_declared_length()
    {
        var doc = Returning("nvarchar(10)");

        Assert.Equal(10, Scalar(doc, "v").Facets?.Length?.Value);
        Assert.Equal([IrNullRule.FunctionResult], Rules(doc, "v"));
    }

    [Fact]
    public void A_clr_scalar_functions_decimal_keeps_its_declared_digits()
    {
        var digits = Scalar(Returning("decimal(5, 2)"), "v").Facets?.Digits;

        Assert.Equal((5, 2), (digits?.Precision, digits?.Scale));
    }

    [Fact]
    public void A_clr_scalar_function_of_smallmoney_or_smalldatetime_stays_unknown()
    {
        // A table function's column may be either (SqlMoney, SqlDateTime); a scalar function's return type may not (6551).
        Assert.Equal(IrUnknownReason.UnsupportedSqlType, Assert.Single(Returning("smallmoney").Unknowns).Reason);
        Assert.Equal(IrUnknownReason.UnsupportedSqlType, Assert.Single(Returning("smalldatetime").Unknowns).Reason);
        Assert.Equal("smallmoney", Scalar(Column("smallmoney"), "v").Sql);
        Assert.Equal("smalldatetime", Scalar(Column("smalldatetime"), "v").Sql);
    }

    [Fact]
    public void A_clr_scalar_function_declared_returns_null_on_null_input_is_null_for_that_too()
    {
        // Observed: Returns_null_on_null_input_makes_a_clr_function_null_without_running_it.
        var doc = Procedure(
            "SELECT dbo.f(t.n) AS v FROM dbo.t AS t;",
            "CREATE TABLE dbo.t (n int NULL);",
            "CREATE FUNCTION dbo.f(@x int) RETURNS int WITH RETURNS NULL ON NULL INPUT AS EXTERNAL NAME Lib.[Lib.Functions].F;");

        Assert.Equal([IrNullRule.FunctionResult, IrNullRule.NullInput], Rules(doc, "v"));
    }

    [Fact]
    public void A_clr_scalar_function_may_be_null_whatever_its_arguments() =>
        Assert.Equal([IrNullRule.FunctionResult], Rules(Returning("int", "WITH RETURNS NULL ON NULL INPUT"), "v"));

    // ---- packages ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A CLR table function's element as SqlPackage 170.2.70 extracts it: the type of a multi-statement function, with no return
    /// variable, its columns listed with no nullability, and the method in place of a body.
    /// </summary>
    private static string TableFunctionElement(string columns) => $"""
        <Element Type="SqlMultiStatementTableValuedFunction" Name="[dir].[group_members]">
          <Property Name="IsAnsiNullsOn" /><Property Name="IsQuotedIdentifierOn" />
          <Relationship Name="Columns">{columns}</Relationship>
          <Relationship Name="FunctionBody"><Entry><Element Type="SqlClrFunctionImplementation">
            <Property Name="MethodName" Value="Members" /><Property Name="ClassName" Value="Dir.Groups" />
            <Relationship Name="Assembly"><Entry><References Name="[DirTools]" /></Entry></Relationship>
          </Element></Entry></Relationship>
        </Element>
        """;

    private static string ColumnElement(string name, string type, int? length = null) => $"""
        <Entry><Element Type="SqlSimpleColumn" Name="[dir].[group_members].[{name}]"><Relationship Name="TypeSpecifier"><Entry><Element Type="SqlTypeSpecifier">
          {(length is null ? "" : $"""<Property Name="Length" Value="{length}" />""")}<Relationship Name="Type"><Entry><References ExternalSource="BuiltIns" Name="[{type}]" /></Entry></Relationship>
        </Element></Entry></Relationship></Element></Entry>
        """;

    private static IrDocument FromPackage(string function, string select = "m.member_id, m.display_name")
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "dir.dacpac");
        TestPackage.Write(path, function + TestPackage.Procedure("dbo", "p", $"SELECT {select} FROM dir.group_members() AS m;"));
        return SqlProject.LoadPackage(path, []).Infer([new ObjectName("dbo", "p")]).Expected();
    }

    [Fact]
    public void A_clr_table_function_of_a_package_is_typed_from_the_columns_its_element_lists()
    {
        var doc = FromPackage(TableFunctionElement(ColumnElement("member_id", "int") + ColumnElement("display_name", "nvarchar", 200)));

        AllTyped(doc);
        Assert.Equal("int", Scalar(doc, "member_id").Sql);
        Assert.Equal(200, Scalar(doc, "display_name").Facets?.Length?.Value);
        Assert.All(["member_id", "display_name"], n => Assert.Equal([IrNullRule.NullableColumn], Rules(doc, n)));
    }

    [Theory]
    [InlineData("m.member_id, m.display_name")]
    [InlineData("member_id")]
    [InlineData("*")]
    [InlineData("m.*")]
    public void A_clr_table_function_whose_columns_cant_be_read_is_external_code(string select)
    {
        var unknowns = FromPackage(TableFunctionElement(""), select).Unknowns;

        Assert.NotEmpty(unknowns);
        Assert.All(unknowns, u => Assert.Equal(IrUnknownReason.ExternalCode, u.Reason));
    }
}
