using Haruspex.Ir;
using static Haruspex.TestSupport.IrNavigation;

namespace Haruspex.Tests.Inference;

// A scalar function's value, typed from the type it declares it returns.
public partial class ConstructTests
{
    private static IrDocument Returning(string function, string call) =>
        InferProcedure.Infer("dbo.p", ("f.sql", function), ("p.sql", $"CREATE PROCEDURE dbo.p AS SELECT {call} AS v;"));

    private static IrFacets? ReturnFacets(IrDocument doc) => ((IrScalar)Field(doc, "v").Type).Facets;

    [Fact]
    public void A_t_sql_functions_string_keeps_its_declared_length()
    {
        // Observed: A_scalar_functions_value_is_converted_to_its_declared_return_type ('abcdef' as varchar(2) is 'ab').
        var doc = Returning("CREATE FUNCTION dbo.f() RETURNS varchar(2) AS BEGIN RETURN 'abcdef'; END;", "dbo.f()");

        Assert.Equal(2, ReturnFacets(doc)?.Length?.Value);
        Assert.Equal([IrNullRule.FunctionResult], Rules(doc, "v"));
    }

    [Fact]
    public void A_t_sql_functions_decimal_keeps_its_declared_digits()
    {
        var digits = ReturnFacets(Returning("CREATE FUNCTION dbo.f() RETURNS decimal(5, 2) AS BEGIN RETURN 1.005; END;", "dbo.f()"))?.Digits;

        Assert.Equal((5, 2), (digits?.Precision, digits?.Scale));
    }

    [Fact]
    public void A_t_sql_functions_nchar_is_converted_not_stored()
    {
        // A converted nchar(4) holds 3 or 4 code units under a supplementary-character collation, as a converted variable does.
        var length = ReturnFacets(Returning("CREATE FUNCTION dbo.f() RETURNS nchar(4) AS BEGIN RETURN N'a'; END;", "dbo.f()"))?.Length;

        Assert.Equal((4, false), (length?.Value, length?.IsFixed));
    }
}
