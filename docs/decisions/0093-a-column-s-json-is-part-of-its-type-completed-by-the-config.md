# A column's JSON is part of its type, completed by the config

**Date:** 21 Nov 2026

**Decision.** The config file may declare that a table's column holds JSON of a JSON Schema
(`"columns": { "shop.orders.settings": { "json": "schemas/order-settings.schema.json" } }`). The declaration is part of the column's
type: inference reads it at every read of the column, as it reads the column's SQL type and its `CHECK` constraints, so it reaches
every result the column's value reaches, through `json_query`, `FOR JSON`, variables, temporary tables, table variables, views,
functions, `INSERT … EXEC` and `OUTPUT … INTO`. Read plainly, the column is JSON text of the schema's type; through `json_query`, live JSON of it.
The IR records each declaration with the hash of every schema file it read (`columns`, IR 0.28), so that a changed schema is a
change `haruspex check` reports, and lists, under `overrides`, a `column-json` claim on each object that reads a declared column.
An object of a column's JSON may hold keys its schema doesn't list (it is `open`) unless the schema closes it, since its writer
is an application, not `FOR JSON`; no emitter refuses such a key. A key its writer may have spelled with an escape may be missing
from a path ([decision 0147](0147-a-key-a-writer-may-escape-may-be-missing-from-a-path.md)).
It completes [decision 0017](0017-stored-json-is-typed-only-by-declaration.md): stored JSON is typed only by declaration, and this is
the declaration.

**Context.** A procedure that reads a JSON column through `json_query` returns JSON whose shape is in the data, so its result is
unknown. Only the team that owns the column can say what every writer there ever was wrote, and it can say it once, for the column,
rather than at every result that shows it.

**Alternatives.**
- *Apply it after inference, as an override*, as the config file's other claims are. An override names one place in one object's
  results; the column's value reaches results through variables, temporary tables, calls and views, which only the inference
  follows. Declaring it at each place would repeat the claim and miss the places nobody thought of.
- *Declare it per result, with `paths` and `import`.* That stays, for one place; but it types the value with the application's own
  schema, which the IR can't see into, and it is repeated at every place.
- *Infer it from the writers in the project.* [Decision 0017](0017-stored-json-is-typed-only-by-declaration.md) says why not: another
  writer may exist.

**Reason.** A column holding JSON of a known schema is a column of a more precise type than its SQL type says. Treating the
declaration as part of that type, rather than as an exception to how claims are applied, lets every rule that carries a column's
type carry this one too, and keeps the claim in one place, where a person made it.

**What it costs.** The plain read of a declared column is JSON text in the IR, which has no SQL type, so its `sql` and its length
are no longer written there (the generated code checks the JSON inside instead); `from` still names the column and its type.
A temporary table's column of a type other than `nvarchar(max)` doesn't carry the declaration, as it doesn't carry JSON built
here: a shorter type may cut the text short.

**In the code:** `ColumnDeclaration`, `DeclaredSchema` and `ColumnDeclarationCheck` in the config layer; `DeclaredColumn`,
`DeclaredColumns` and `IrInference.ViewColumnSources` in the SQL Server front end; `IrColumnDeclaration`, `IrDocument.Columns`,
`IrObjectType.Open`, `IrOverrideSetting.ColumnJson`, `IrComparison`; the IR README, "Column declarations"; the Inference README,
"JSON declared for a column"; `DeclaredColumnTests`, `DeclaredColumnObservationTests`, `ColumnDeclarationTests`.
