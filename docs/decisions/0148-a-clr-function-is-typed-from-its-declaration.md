# A CLR function is typed from its declaration

**Date:** 28 Nov 2026.

**Decision.** A function whose body is a .NET method is typed from what its T-SQL declares, and the assembly is never read. A
table-valued function's rows are the columns of its `RETURNS TABLE (…)`, by those names, in that order and in the collations
declared; a scalar function's value is its return type. Each keeps its declared type's facts, and each is nullable: a column
for `nullable-column`, a scalar value for `function-result`. A table function has any number of rows and no key. Only the
types a CLR value was observed under are typed; any other is unknown (`unsupported-sql-type`). A table function whose columns
a package doesn't list stays `external-code`, as a CLR procedure does, since it declares no result set.

**Context.** On-premises databases use CLR functions for what T-SQL can't reach: files, REST calls, directory services. A common
shape reads the actual state through a CLR table-valued function and compares it with tables. Every such function was
refused, though its columns are in the script, the package and the database; and haruspex gave a CLR scalar function's type
without its facts, as nothing had observed what SQL Server does with a .NET method's value.

Observed (`ClrFunctionObservationTests`, SQL Server 2025 RTM-CU10 on Linux, a SAFE assembly compiled from source), for 33
.NET types, as a scalar function's result and as a table function's column, under `ANSI_WARNINGS` and `ARITHABORT` on and
off, in the default collation and a supplementary-character UTF-8 one, all alike:

| A value | Under the declared type |
|---|---|
| a string or bytes longer than declared, trailing blanks and zeros included | an error (6522 from a scalar function, 6260 from a row); never cut |
| a shorter one under `nchar(n)` or `binary(n)` | padded to n, under a supplementary-character collation too |
| a decimal of more places | rounded, half away from zero |
| a decimal, date or time out of range, a NaN, an infinity | an error |
| a date or time of a finer fraction | rounded, or cut where rounding up would leave the type's range |
| XML the declared schema collection refuses | an error (6913) |
| the .NET null, a `Sql…` type's `Null`, a .NET SqlBytes value of no bytes under `varbinary(max)` | NULL |

A T-SQL function's string is cut where a CLR function's is an error, and its overflow is NULL under the caller's options where a
CLR function's is still an error, so the declared type's facts hold here at least as firmly. The server refuses `NOT NULL`, an
alias type declared `NOT NULL`, a key, a `CHECK`, a `DEFAULT`, `IDENTITY` and a computed column in a CLR table function's
declaration (6525, 6526, 6514), and a declaration whose columns differ in number or type from the fill-row method's (6208, 6258).

**Alternatives.**
- *Read the assembly* for `[SqlFunction]`, the method's return type or its code. The declaration already says the columns, the
  server holds the two together when the function is created, and a method's code would prove nothing the server enforces.
- *Type a scalar function non-null when its .NET type has no null* (C#'s `int`, not System.Data's SqlInt32). True on the server tried, and it
  needs the assembly; `RETURNS NULL ON NULL INPUT` makes the same function NULL without running it.
- *Believe a `NOT NULL` or a key a script writes.* The server refuses the script, so it can be true of no deployed function.
- *Type every declared type, as a T-SQL function's is.* The server creates no CLR function declared `varchar` or `json`, so
  nothing could be observed of one.

**Reason.** The declaration is what the server enforces on every value, and it is in the source.

**What it costs.** Every column and value is nullable, and nothing in the SQL removes that but a filter or a `COALESCE`. JSON
text a CLR function returns is a string: `haruspex.jsonSchema` is read on a table's column and on a parameter, not on a function
(HAR4116), and declaring a function's result would be a new kind of declaration in the IR.

**A known gap.** A scalar method marked `[SqlFunction(IsDeterministic = true)]` that isn't deterministic is run again wherever
the server copies its call: for `SELECT x.v FROM (SELECT dbo.f(i.id) AS v …) AS x WHERE x.v IS NOT NULL`, once for the filter and
once for the select list, so about half the rows of a method that answers NULL at random are NULL
(`A_filter_proves_nothing_of_a_clr_scalar_function_wrongly_marked_deterministic`; the same through a CTE, a view, `APPLY`,
`TOP` and a `CASE` over the column). Unmarked, the filter held, and a table function's column held, marked or not
(`A_filter_holds_of_a_clr_table_functions_column_though_it_is_marked_deterministic`). The mark can't be seen in the SQL, so the proof is
taken, as it was before this decision. Refusing it needs a value computed by a CLR scalar call to be known as one through every
expression and derived column, which is a change of its own; until then the README lists the gap.

**The test assembly** is compiled by the C# compiler of the SDK that builds the tests, against declarations of the few .NET
Framework members it uses, written in the tests (`ClrSources`), so that no reference-assembly package is needed and no binary is
kept. Each test trusts it by its hash while its database stands (`ClrDatabase`); `clr strict security` stays on.

**In the code:** `Typer.ReadClrTable` and `SqlTypes.ToClrShape` and `SqlTypes.ClrRefuses`; `ClrFunctionTests` and
`ClrFunctionObservationTests`.
