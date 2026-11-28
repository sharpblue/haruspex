namespace Haruspex.SqlServer.Inference;

/// <summary>Who may have written an object's keys, and so how each is spelled in the text a path reads (<see cref="KeySpelling"/>).</summary>
[Flags]
internal enum KeyWriters
{
    /// <summary>None: no object.</summary>
    None = 0,

    /// <summary>The SQL itself, in a literal or text it assembles: a key holds no escape (one that does is not modelled), so it is spelled as itself.</summary>
    Literal = 1,

    /// <summary>
    /// SQL Server's own writers, <c>FOR JSON</c> and <c>json_object</c>: a key's <c>"</c> as <c>\"</c>, its <c>\</c> as <c>\\</c>, its
    /// <c>/</c> as <c>\/</c> and a control character as <c>\u00XX</c>; anything else as itself.
    /// </summary>
    SqlServer = 2,

    /// <summary>An application, not known: JSON declared for a column, a parameter, a REST response or a declared result.</summary>
    Application = 4,
}
