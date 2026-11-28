using Haruspex.Ir;
using Haruspex.SqlServer.Inference.Typing;
using Haruspex.SqlServer.Model;
using Haruspex.SqlServer.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Haruspex.SqlServer.Inference;

// Table-valued functions as table sources. README.md in this folder ("Table-valued functions") gives the rules and
// the observations they rest on (TableFunctionObservationTests in the differential tests).
internal sealed partial class Typer
{
    /// <summary>
    /// A call of a table-valued function in FROM or APPLY: its arguments read where the call is (an APPLY's left input's
    /// columns too), converted to its parameters' types, and the function read with them (<see cref="Summaries.TableFunction"/>).
    /// </summary>
    /// <param name="symbol">The call, bound: which function it names was resolved by binding.</param>
    /// <param name="scope">The scope its arguments are read in.</param>
    private LocalRelationValues TableFunction(TableFunctionCall symbol, ReadScope scope)
    {
        var call = symbol.Syntax;
        var at = At(call);
        LocalRelationValues Fails(IrUnknownReason reason, string detail) => LocalRelationValues.Unknown(Unsupported(reason, $"{Text(call)}: {detail}", at));

        // A column list at the call is refused for a function (observed: 317), and so are table hints (a syntax error); XML's
        // nodes(), which takes one, binding tells apart (XmlNodesCall).
        if (call.Columns.Count > 0)
            return Fails(IrUnknownReason.UnsupportedSource, "a table-valued function cannot have a column alias (317).");
        // Which function the call names, binding resolved; why none, it says, and the words for it are said here.
        return symbol.Callee switch
        {
            KnownCallee known => Rows(known.Function),
            ForeignCallee => Fails(IrUnknownReason.UnsupportedSource, "a function of another database is not modelled yet."),
            AmbiguousCallee ambiguous => Fails(IrUnknownReason.UnresolvedTable, $"{ambiguous.Name} is not schema-qualified, and {string.Join(" and ", ambiguous.Candidates.Select(c => c.Name))} have that name."),
            MissingCallee => Fails(IrUnknownReason.UnresolvedTable, $"there is no such function in the project.{SchemaGaps.MayBeIn(model)}"),
        };

        LocalRelationValues Rows(Function function)
        {
            if (function.Redefinition is { } redefinition)
                return LocalRelationValues.Unknown(new UnknownShape(IrUnknownReason.Redefined, Redefinitions.Why(redefinition), redefinition.At));
            if (function is TableFunction { Body: ClrBody, Returns.Columns.Count: 0 })
                return Fails(IrUnknownReason.ExternalCode, $"{function.Name} is a CLR function, and the columns it declares can't be read.");
            if (function is ScalarFunction)
                return Fails(IrUnknownReason.UnresolvedTable, $"{function.Name} is a scalar function, not a table.");

            var parameters = function.Parameters;
            if (call.Parameters.Count != parameters.Count)
                return Fails(IrUnknownReason.UnsupportedSource, $"{function.Name} takes {parameters.Count} arguments.");

            var arguments = parameters.Select((p, i) => KeyValuePair.Create(new LocalName(p.Name), Argument(function, p, call.Parameters[i], scope)));
            return summaries.TableFunction(function, [.. arguments], ReadUncommitted(scope), source);
        }
    }

    /// <summary>
    /// What a parameter holds for one call: the argument, converted to the parameter's type as an assignment converts it (a
    /// string cut short, an overflow NULL under a caller's ANSI_WARNINGS and ARITHABORT off: observed,
    /// An_argument_is_converted_to_its_parameters_type_as_an_assignment_is), null exactly when the argument may be or the
    /// conversion may overflow or round to fewer places (<see cref="Converting"/>). <c>DEFAULT</c> passes the parameter's default (observed: Default_passes_the_parameters_default).
    /// An argument of the parameter's own type keeps its facts. One not modelled is any value of the type, or null
    /// (<c>parameter</c>), as a scalar function's parameter is.
    /// </summary>
    private Value Argument(Function function, Parameter parameter, ScalarExpression argument, ReadScope scope)
    {
        var declared = SqlTypes.ToConvertedShape(model, parameter.Type, parameter.At);
        var any = Value.Computed(declared, [new IrNullReason(IrNullRule.Parameter, parameter.Name)]);
        if (declared is UnknownShape unknown)
            return Value.Unknown(unknown);

        // A parameter declared to hold JSON of a schema holds it, as every caller is claimed to send it, null exactly when the
        // argument may be (docs/decisions/0143-a-parameter-s-json-may-be-declared.md). The function's body takes it, so the
        // declaration is recorded as read there. DEFAULT sends the default, which no declaration covers, so it is read as it is below.
        if (Strip(argument) is not DefaultLiteral && summaries.DeclaredParameters.Content(parameter, function.Span) is { } content)
        {
            var passed = Expression(argument, scope);
            return Value.Computed(new JsonTextShape(content) { Text = declared as ScalarShape }, passed.Shape is UnknownShape ? any.NullableBecause : passed.NullableBecause);
        }

        Value value;
        if (Strip(argument) is DefaultLiteral)
        {
            // The default is read where the function is defined; a constant reads nothing there.
            if (parameter.Default is not { } fallback)
                return any;
            value = Expression(fallback, ReadScope.Statement(NameEnvironment.Empty, scope.State));
        }
        else
        {
            value = Expression(argument, scope);
        }

        if (value.Shape is UnknownShape || value.IsLiveJson)
            return any;
        var target = (declared as ScalarShape)?.BuiltIn;
        if (TypeOf(value) is { } from && target is not null && from == target)
            return value;
        var fits = LiteralOf(argument) is ({ } literal, var negative) && target is not null
            ? Conversions.Fits(literal, target, negative)
            : null;
        return Value.Computed(declared, Converting(value, declared, At(argument), summaries.Session, argument, fits: fits == true));
    }

    /// <summary>
    /// A CLR table-valued function's rows: the columns its <c>RETURNS TABLE</c> declares, in that order and by those names
    /// (observed: A_clr_table_functions_columns_are_named_and_ordered_by_its_declaration). SQL Server converts each value the
    /// fill-row method sets to its column's type or fails the call, so the type's facts hold, in the column's collation
    /// (A_clr_functions_value_is_converted_to_its_declared_type_or_fails_the_call). Any value may be NULL: the server refuses
    /// <c>NOT NULL</c> in the declaration, with a key, a <c>CHECK</c>, a <c>DEFAULT</c> and <c>IDENTITY</c>
    /// (A_clr_table_functions_column_may_be_null_and_takes_no_not_null), so whatever a script writes of them proves nothing.
    /// How many rows it returns is the .NET code's.
    /// </summary>
    public LocalRelationValues ReadClrTable(TableFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        var name = function.Name.ToString();
        var table = function.Returns;
        var declared = table with
        {
            Columns = [.. table.Columns.Select(c => c with { Nullability = DeclaredNullability.Null, IsIdentity = false, HasDefault = false })],
            PrimaryKey = [],
            Checks = [],
            UniqueKeys = [],
        };
        var values = declared.Columns.Select(c => c.IsComputed
            ? Value.Unknown(Unsupported(IrUnknownReason.ExternalCode, $"{name}.{c.Name} is declared as a computed column, which SQL Server refuses in a CLR function (6526).", c.At))
            : SqlTypes.ClrRefuses(model, c.Type, column: true, c.At) is { } refused
                ? Value.Unknown(refused)
                : DeclaredValue(declared, c, name, temporary: false));
        return new LocalRelationValues([.. declared.Columns.Select(c => c.Name)], [.. values], null);
    }

    /// <summary>
    /// An inline function's rows: its query read as a view's is (<see cref="ReadView"/>), in its own file and ANSI_NULLS, with
    /// its parameters holding the call's values, which the query reads as constants: a key compared with one is fixed. Its
    /// keys pass through as a view's do (<see cref="ViewKeys"/>).
    /// </summary>
    public LocalRelationValues ReadInline(InlineFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        var select = function.Query;
        var names = NameEnvironment.Empty.WithDeclarations(binder.Declarations(function.Parameters));
        var at = At(select);
        LocalRelationValues Fails(string detail) => LocalRelationValues.Unknown(Unsupported(IrUnknownReason.UnsupportedQuery, $"{function.Name}: {detail}", at));

        return WithCtes(
            select.WithCtesAndXmlNamespaces,
            ReadScope.Statement(names, FlowState.Start([])),
            scope =>
            {
                if (select.QueryExpression is not { } query || ForOf(query) is not null)
                    return Fails("a function whose query is FOR XML or FOR JSON is not modelled yet.");
                var bound = binder.Bind(query, null, scope.Names);
                var row = Query(bound, scope);
                return Holds(bound.Listed([]), row) is { } relation
                    ? relation with { Rows = row.Rows, AtLeastOne = row.Rows == IrCardinality.One, Keys = Keys(query, row, scope) }
                    : Unlisted(function.Name.ToString(), row, at);
            },
            LocalRelationValues.Unknown);
    }
}
