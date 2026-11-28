using Haruspex.Collections;
using Haruspex.Ir;
using Haruspex.SqlServer.Model;
using Haruspex.SqlServer.Semantics;

namespace Haruspex.SqlServer.Inference;

/// <summary>
/// What each view of the project holds, read once per document from its definition (<see cref="Typer.ReadView"/>),
/// and what each call of a table-valued function returns, read once per function and set of argument values. A
/// view or function reads others; one that reads itself through them cannot be created, and is unknown here.
/// The idea, and the alternatives, are in docs/internals/07-summaries-and-recursion.md.
/// </summary>
/// <param name="model">The model whose views and functions these are.</param>
internal sealed class ViewSummaries(DatabaseModel model)
{
    private readonly Dictionary<(ObjectName, bool), LocalRelationValues> done = [];
    private readonly HashSet<ObjectName> reading = [];
    private readonly Dictionary<Reading, LocalRelationValues> calls = [];

    /// <summary>
    /// What a call of a table-valued function returns, with its parameters holding <paramref name="arguments"/>: an inline
    /// function's query read with them (<see cref="Typer.ReadInline"/>), a multi-statement one's declared return
    /// table (<see cref="Walker.ReadReturnTable"/>), a CLR one's declared columns (<see cref="Typer.ReadClrTable"/>).
    /// </summary>
    /// <param name="uncommitted">Whether it is read uncommitted (Cardinality.cs), so that no key of what it reads holds.</param>
    /// <param name="summaries">What the function's body may ask of the document's other objects.</param>
    public LocalRelationValues Function(Function function, EquatableMap<LocalName, Value> arguments, bool uncommitted, Summaries summaries)
    {
        var key = new Reading(function.Name, uncommitted, arguments);
        if (calls.TryGetValue(key, out var found))
            return found;
        if (!reading.Add(function.Name))
            return LocalRelationValues.Unknown(new UnknownShape(IrUnknownReason.UnresolvedTable, $"{function.Name} reads itself through the functions and views it reads.", function.Span.Start));

        try
        {
            found = function switch
            {
                InlineFunction inline => new Typer(model, function.Span, SchemaSearch.Within(function), summaries, arguments, function.AnsiNullsOff, uncommitted).ReadInline(inline),
                TableFunction { Body: ClrBody } external => new Typer(model, function.Span, SchemaSearch.Within(function), summaries, arguments, function.AnsiNullsOff, uncommitted).ReadClrTable(external),
                TableFunction declared => new Walker(model, function.Span, SchemaSearch.Within(function), summaries, arguments, ansiNullsOff: function.AnsiNullsOff, uncommittedReads: uncommitted).ReadReturnTable(declared),
                _ => throw new ArgumentException("Not a table-valued function.", nameof(function)),
            };
        }
        finally
        {
            reading.Remove(function.Name);
        }

        calls[key] = found;
        return found;
    }

    /// <summary>A placeholder location for an unknown about the view as a whole; each read reports its own location instead.</summary>
    private static readonly Diagnostics.SourceLocation Nowhere = new("", 0, 0);

    /// <summary>A view's rows, read once per document and per way of reading it: committed, or uncommitted inside too.</summary>
    /// <param name="uncommitted">
    /// Whether it is read uncommitted: under a NOLOCK or READUNCOMMITTED hint on it, which applies to every table under it, or
    /// in a statement that reads uncommitted. Then no key of the tables inside holds.
    /// </param>
    /// <param name="summaries">What the view's body may ask of the document's other objects.</param>
    public LocalRelationValues Read(View view, bool uncommitted, Summaries summaries)
    {
        if (done.TryGetValue((view.Name, uncommitted), out var found))
            return found;

        // A view created and altered, or defined twice, depends on the order the scripts run (Sources).
        if (view.Redefinition is not null)
            return LocalRelationValues.Unknown(new UnknownShape(IrUnknownReason.UnresolvedTable, $"{view.Name} is defined more than once, and which definition applies depends on the order the scripts run.", Nowhere));
        if (!reading.Add(view.Name))
            return LocalRelationValues.Unknown(new UnknownShape(IrUnknownReason.UnresolvedTable, $"{view.Name} reads itself through the views it reads, which SQL Server refuses.", Nowhere));

        try
        {
            found = new Typer(model, view.Span, SchemaSearch.Within(view), summaries, ansiNullsOff: view.AnsiNullsOff, uncommittedReads: uncommitted).ReadView(view);
        }
        finally
        {
            reading.Remove(view.Name);
        }

        done[(view.Name, uncommitted)] = found;
        return found;
    }

    /// <summary>
    /// One reading of a table-valued function: the function, whether it is read uncommitted, and each argument's value by its
    /// parameter. Two calls with equal arguments, compared by value, read the same rows.
    /// </summary>
    private sealed record Reading(ObjectName Function, bool Uncommitted, EquatableMap<LocalName, Value> Arguments);
}
