# haruspex

A tool that carries a SQL Server database's types into TypeScript by static analysis. It reads a database project's source
and infers what each stored procedure takes and returns, `FOR JSON` documents included. From that it writes
[Zod](https://zod.dev) schemas and types, and a function for each procedure that checks what goes in before calling it and
what comes back before returning it. It is pre-release: see [Status and roadmap](#status-and-roadmap).

```ts haruspex-call
import { getOrder } from './db/haruspex.mssql'

const order = await getOrder(pool, { OrderID: 42 })   // the order, typed and checked, or null
```

## The problem

Many applications reach their database through stored procedures, and nothing at that boundary is typed. A result arrives
as rows the driver can't describe, or as one string of JSON, however deep the `FOR JSON` document inside it. So developers
write the types by hand, and they drift until a field that "can't be null" is null in production. The way in is no safer:
SQL Server cuts a string too long for its parameter, and rounds a decimal with too many places, without an error.

The tool works from the source alone (a folder of `.sql` files, an SSDT `.sqlproj` or a DACPAC). It never connects to a
database, so it needs no credentials and no data. And it follows one rule: **never claim more than the engine guarantees.**

## What a type means here

A hand-written type describes the values its author expects. A type haruspex writes is a claim about every value the
database can produce: for any rows its tables can hold, and any arguments its parameters accept, under the session the
callers declare and the few assumptions [listed below](#what-it-guarantees-and-what-it-cant-see), which no source can show.
Not today's data, not the usual case, every case. Most of what makes haruspex different follows from that one choice:

- **It is looser than intuition where the engine allows more.** `COUNT(*)` is nullable, because SQL Server returns NULL
  past 2,147,483,647 rows under session options the caller controls.
- **It is tighter where the database promises more.** A `CHECK` constraint becomes a value set or a range, and a query
  that filters on a whole key returns at most one row.
- **What it can't prove, it doesn't claim.** A construct it doesn't model, or a procedure whose results only appear at run
  time, is unknown, with a reason, never a guess.
- **The engine decides, not the documentation.** A rule the types rely on is observed on SQL Server by a test, so a claim
  that SQL Server stops honouring fails in the test suite before it fails in an application. What no test can observe,
  such as the isolation level a caller sets, is assumed, and said so.
- **It is still checked when it runs.** The generated functions validate every value, so a database that has moved on
  from the one the types were made from fails loudly at the call, not deep inside the application.

A type that holds in every case can be trusted without reading the SQL behind it, which is the point.
[What it guarantees](#what-it-guarantees-and-what-it-cant-see) has the limits, what it assumes and the larger gaps known today, and [the guide](docs/internals/README.md)
how the analysis reaches its claims.

## What an application gets

With `"emit": ["zod", "mssql"]` in `haruspex.json`, `haruspex generate` writes the database module: `haruspex.zod.ts`, a schema
and a type for each procedure's parameters and results, and `haruspex.mssql.ts`, a function for each procedure over the
[mssql](https://www.npmjs.com/package/mssql) driver. A function takes a connection it doesn't own and the parameters by their
declared names, checks them against their declared types before anything is sent, so a value the database would silently
cut or round is caught first ([decision 0054](docs/decisions/0054-a-parameter-is-checked-against-its-declared-type.md)),
and returns what comes back checked and typed: the result itself, or, for a procedure that also has `OUTPUT` parameters or
return codes, or several result sets, an object naming each.

Queries the application keeps in its own `.sql` files get the same: a function each, named for its file, with its parameters'
types inferred from how the query uses them ([an example](#a-query-file-over-the-same-tables)).

The functions belong on the server, since they take the connection and its credentials; the schemas and types, in their own
file, may go to the browser. [The database module](docs/database-module.md) has the detail: dates, return shapes and
`serverOnly` for Next.js.

The generated types are the database's, not the application's: [the application's own types](docs/layers.md) shows the thin
step, written by hand on them, where an absent list becomes empty, a figure gets its unit and the database's names stop.

## Quick start

`haruspex init` finds the database's files and writes a starter `haruspex.json`; `haruspex generate` writes the module. Run
again after a change to the database, it says what changed, and nothing else (an invented kennel database, after a column
was made nullable and a procedure added):

```text
$ haruspex generate
haruspex: 3 procedures, 1 unknown. Wrote src/db/haruspex.ir.json and src/db/haruspex.zod.ts.
4 changes since the last run:
  + kennel.count_dogs: added
  ~ kennel.get_dog results[0][].name: became nullable
  ~ kennel.list_dogs results[0][].name: became nullable
  ~ kennel.list_dogs results[0][].name: presence changed from always to omittedWhenNull
1 unknown, as before (--verbose lists them).
```

When nothing changed, that is one line: `haruspex: nothing changed since the last run: 3 procedures, 1 unknown`. In CI,
`haruspex check` fails when the module in the output folder, the one committed, would change, with annotations in GitHub Actions; [the command
line](docs/command-line.md) has the rest. Keep the output folder out of your formatter: in `.prettierignore`, or as an
exclusion in Biome's `files.includes` (`"includes": ["**", "!src/db"]` for `src/db`). Otherwise a formatter (Prettier, Biome,
an editor's format on save) restyles the generated files, and `haruspex check` then fails, since it compares each file with
what haruspex writes.

With haruspex built ([Install](#install)) and run as [the command line](docs/command-line.md) says, the essentials, read in order, give a working setup:
[five minutes with AdventureWorks](docs/quick-start.md), from a fresh clone to the schemas and the function for one of the
sample's procedures; [the database module](docs/database-module.md), how an application calls them;
[the application's own types](docs/layers.md); [the example application](examples/greenhouse/README.md), which calls its
procedures through the functions haruspex wrote for it; and the essentials of [the configuration](docs/configuration.md) and
[the command line](docs/command-line.md), for your own database. [The documentation's index](docs/README.md) lists them, and
says what every other page is for.

## Examples on Microsoft's sample databases

Each example is written for this page over the real tables of a Microsoft sample kept in
[`tests/corpus`](tests/corpus/README.md). A test fails if the code shown differs from what haruspex writes, and the
differential tests call each through its generated function on SQL Server and check what comes back against the claims.

### An order with its lines, from WideWorldImporters

```sql haruspex-example WideWorldImporters
CREATE PROCEDURE Website.GetOrder
    @OrderID int
AS
SET NOCOUNT ON;

SELECT o.OrderID AS id,
       o.OrderDate AS orderDate,
       o.ExpectedDeliveryDate AS expectedDelivery,
       o.CustomerPurchaseOrderNumber AS purchaseOrderNumber,
       sp.FullName AS [salesperson.name],
       sp.EmailAddress AS [salesperson.email],
       (SELECT ol.Description AS description,
               ol.Quantity AS quantity,
               ol.UnitPrice AS unitPrice,
               si.StockItemName AS [stockItem.name],
               si.Brand AS [stockItem.brand],
               si.IsChillerStock AS [stockItem.chilled]
        FROM Sales.OrderLines AS ol
        JOIN Warehouse.StockItems AS si ON si.StockItemID = ol.StockItemID
        WHERE ol.OrderID = o.OrderID
        ORDER BY ol.OrderLineID
        FOR JSON PATH) AS lines
FROM Sales.Orders AS o
JOIN Application.People AS sp ON sp.PersonID = o.SalespersonPersonID
WHERE o.OrderID = @OrderID
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
```

What haruspex writes for it, the module's header left out:

```ts haruspex-zod
export const GetOrderSalespersonSchema = z.object({
  name: z.string().max(50),
  email: z.string().max(256).optional(),
})
export type GetOrderSalesperson = z.infer<typeof GetOrderSalespersonSchema>

export const GetOrderLineStockItemSchema = z.object({
  name: z.string().max(100),
  brand: z.string().max(50).optional(),
  chilled: z.boolean(),
})
export type GetOrderLineStockItem = z.infer<typeof GetOrderLineStockItemSchema>

export const GetOrderLineSchema = z.object({
  description: z.string().max(100),
  quantity: z.int32(),
  /** decimal(18,2) runs from -9999999999999999.99 to 9999999999999999.99, which JavaScript reads as the nearest doubles. */
  unitPrice: z.number().min(-10000000000000000).max(10000000000000000).multipleOf(1e-2).optional(),
  stockItem: GetOrderLineStockItemSchema,
})
export type GetOrderLine = z.infer<typeof GetOrderLineSchema>

export const GetOrderRowSchema = z.object({
  id: z.int32(),
  orderDate: z.iso.date(),
  expectedDelivery: z.iso.date(),
  purchaseOrderNumber: z.string().max(20).optional(),
  salesperson: GetOrderSalespersonSchema,
  /**
   * May be null: it is a subquery, which is null when it returns no rows (empty-subquery).
   * Left out when null, as FOR JSON, and JSON_OBJECT with ABSENT ON NULL, leave out a key whose value is null.
   * To change it: if the data rules it out, declare it in the config file with "nullable": false and why.
   * Why: haruspex explain Website.GetOrder lines
   */
  lines: z.array(GetOrderLineSchema).min(1).optional(),
})
export type GetOrderRow = z.infer<typeof GetOrderRowSchema>

/** Website.GetOrder: its parameters, checked before anything is sent. */
export const GetOrderParamsSchema = z.strictObject({
  OrderID: z.int32().nullable(),
})
export type GetOrderParams = z.infer<typeof GetOrderParamsSchema>

/** Website.GetOrder: returns JSON, null when no rows match. */
export const GetOrderSchema = GetOrderRowSchema.nullable()
export type GetOrder = z.infer<typeof GetOrderSchema>
```

Each line has its reason in the SQL. `email`, `brand` and `purchaseOrderNumber` are optional rather than nullable, because
their columns allow NULL and `FOR JSON` leaves a null key out rather than writing `null`. `lines` has at least one item when
it is there, and is absent for an order with none, because a `FOR JSON` subquery over no rows is NULL, not `[]`. The
salesperson is always there: the join is on a foreign key that is never NULL. The whole result is one object or null, because
the filter is on the primary key and `WITHOUT_ARRAY_WRAPPER` writes a single object.

And its function, after the helpers every module of functions begins with:

```ts haruspex-mssql
/**
 * Calls Website.GetOrder.
 * Its result may be missing: the query returns nothing when no rows match (no-rows).
 * Why: haruspex explain Website.GetOrder
 */
export async function getOrder(db: Queryable, params: types.GetOrderParams): Promise<types.GetOrder> {
  const p = await checked(GetOrderParamsSchema, params, (issues) => new ParameterError('Website.GetOrder', issues))
  const request = db.request()
  request.input('OrderID', sql.Int, p.OrderID)
  const response = await request.execute('Website.GetOrder')
  const sets = response.recordsets as unknown as readonly unknown[]
  if (sets.length < 1) throw new ResultError('Website.GetOrder', 'the results', [{ message: `Expected at least one result set, received ${String(sets.length)}` }])
  const result = await checked(GetOrderSchema, readJson(sets[0]), (issues) => new ResultError('Website.GetOrder', 'the results', issues))
  return result
}
```

### A query file over the same tables

An application's own query, kept in `sql/getOrderLines.sql` and named in `haruspex.json` by `"queries": "sql"`:

```sql haruspex-query WideWorldImporters sql/getOrderLines.sql
SELECT ol.OrderLineID AS id,
       ol.Description AS description,
       ol.Quantity AS quantity,
       si.Brand AS brand
FROM Sales.OrderLines AS ol
JOIN Warehouse.StockItems AS si ON si.StockItemID = ol.StockItemID
WHERE ol.OrderID = @OrderID
ORDER BY ol.OrderLineID;
```

```ts haruspex-zod
export const GetOrderLinesRowSchema = z.object({
  id: z.int32(),
  description: z.string().max(100),
  quantity: z.int32(),
  brand: z.string().max(50).nullable(),
})
export type GetOrderLinesRow = z.infer<typeof GetOrderLinesRowSchema>

/** sql/getOrderLines.sql: its parameters, checked before anything is sent. */
export const GetOrderLinesParamsSchema = z.strictObject({
  OrderID: z.int32().nullable(),
})
export type GetOrderLinesParams = z.infer<typeof GetOrderLinesParamsSchema>

/** sql/getOrderLines.sql: returns rows. */
export const GetOrderLinesSchema = z.array(GetOrderLinesRowSchema)
export type GetOrderLines = z.infer<typeof GetOrderLinesSchema>
```

`@OrderID` is declared nowhere: compared with `ol.OrderID`, it takes that column's type, `int`. `brand` is nullable here
rather than optional: a row holds a NULL as `null`, where `FOR JSON` leaves the key out. The function sends the file's text through
`sp_executesql`, declaring the parameter as it was typed:

```ts haruspex-mssql
/** Runs the query file sql/getOrderLines.sql. */
export async function getOrderLines(db: Queryable, params: types.GetOrderLinesParams): Promise<types.GetOrderLines> {
  const p = await checked(GetOrderLinesParamsSchema, params, (issues) => new ParameterError('sql/getOrderLines.sql', issues))
  const request = db.request()
  request.input('stmt', sql.NVarChar(sql.MAX), 'SELECT ol.OrderLineID AS id,\n       ol.Description AS description,\n       ol.Quantity AS quantity,\n       si.Brand AS brand\nFROM Sales.OrderLines AS ol\nJOIN Warehouse.StockItems AS si ON si.StockItemID = ol.StockItemID\nWHERE ol.OrderID = @OrderID\nORDER BY ol.OrderLineID;\n')
  request.input('params', sql.NVarChar(sql.MAX), '@OrderID int')
  request.input('OrderID', sql.Int, p.OrderID)
  const response = await request.execute('sys.sp_executesql')
  const sets = response.recordsets as unknown as readonly unknown[]
  if (sets.length < 1) throw new ResultError('sql/getOrderLines.sql', 'the results', [{ message: `Expected at least one result set, received ${String(sets.length)}` }])
  const result = await checked(GetOrderLinesSchema, readRows(sets[0]), (issues) => new ResultError('sql/getOrderLines.sql', 'the results', issues))
  return result
}
```

```ts haruspex-call
import { getOrderLines } from './db/haruspex.mssql'

const lines = await getOrderLines(pool, { OrderID: 42 })   // the order's lines, typed and checked
```

### A bill of materials as a tree, from AdventureWorks

A function that calls itself builds a tree of any depth, and haruspex types it as a recursive schema:

```sql haruspex-example AdventureWorks
CREATE FUNCTION Production.GetComponents(@ProductAssemblyID int)
RETURNS nvarchar(max)
AS
BEGIN
    RETURN (
        SELECT b.PerAssemblyQty AS quantity,
               b.UnitMeasureCode AS unit,
               p.ProductID AS [product.id],
               p.Name AS [product.name],
               p.ProductNumber AS [product.number],
               p.StandardCost AS [product.standardCost],
               JSON_QUERY(Production.GetComponents(p.ProductID)) AS components
        FROM Production.BillOfMaterials AS b
        JOIN Production.Product AS p ON p.ProductID = b.ComponentID
        WHERE b.ProductAssemblyID = @ProductAssemblyID
          AND b.EndDate IS NULL
        ORDER BY p.ProductID
        FOR JSON PATH);
END
GO
CREATE PROCEDURE Production.GetProductTree
    @ProductID int
AS
SET NOCOUNT ON;

SELECT p.ProductID AS id,
       p.Name AS name,
       p.ProductNumber AS number,
       JSON_QUERY(Production.GetComponents(p.ProductID)) AS components
FROM Production.Product AS p
WHERE p.ProductID = @ProductID
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
```

```ts haruspex-zod
export const GetComponentsNodeProductSchema = z.object({
  /** Primary key for Product records. */
  id: z.int32().describe("Primary key for Product records."),
  /** Name of the product. */
  name: z.string().max(50).describe("Name of the product."),
  /** Unique product identification number. */
  number: z.string().max(25).describe("Unique product identification number."),
  /**
   * Standard cost of the product.
   * money runs from 0 to 922337203685477.5807, which JavaScript reads as the nearest doubles.
   */
  standardCost: z.number().min(0).max(922337203685477.6).multipleOf(1e-4).describe("Standard cost of the product."),
})
export type GetComponentsNodeProduct = z.infer<typeof GetComponentsNodeProductSchema>

export const GetComponentsNodeSchema = z.object({
  /** Quantity of the component needed to create the assembly. */
  quantity: z.number().min(1).max(999999.99).multipleOf(1e-2).describe("Quantity of the component needed to create the assembly."),
  /**
   * Standard code identifying the unit of measure for the quantity.
   * nchar(3) is exactly 3 UTF-16 code units, which is 2 to 3 code points.
   */
  unit: z.string().min(2).max(3).describe("Standard code identifying the unit of measure for the quantity."),
  product: GetComponentsNodeProductSchema,
  /**
   * May be null: it is a subquery, which is null when it returns no rows (empty-subquery).
   * Left out when null, as FOR JSON, and JSON_OBJECT with ABSENT ON NULL, leave out a key whose value is null.
   * To change it: if the data rules it out, declare it in the config file with "nullable": false and why.
   * Why: haruspex explain GetComponentsNode components
   */
  get components() { return z.array(GetComponentsNodeSchema).min(1).optional() },
})
export type GetComponentsNode = z.infer<typeof GetComponentsNodeSchema>

export const GetProductTreeRowSchema = z.object({
  /** Primary key for Product records. */
  id: z.int32().describe("Primary key for Product records."),
  /** Name of the product. */
  name: z.string().max(50).describe("Name of the product."),
  /** Unique product identification number. */
  number: z.string().max(25).describe("Unique product identification number."),
  /**
   * May be null: it is a subquery, which is null when it returns no rows (empty-subquery).
   * Left out when null, as FOR JSON, and JSON_OBJECT with ABSENT ON NULL, leave out a key whose value is null.
   * To change it: if the data rules it out, declare it in the config file with "nullable": false and why.
   * Why: haruspex explain Production.GetProductTree components
   */
  components: z.array(GetComponentsNodeSchema).min(1).optional(),
})
export type GetProductTreeRow = z.infer<typeof GetProductTreeRowSchema>

/** Production.GetProductTree: its parameters, checked before anything is sent. */
export const GetProductTreeParamsSchema = z.strictObject({
  ProductID: z.int32().nullable(),
})
export type GetProductTreeParams = z.infer<typeof GetProductTreeParamsSchema>

/** Production.GetProductTree: returns JSON, null when no rows match. */
export const GetProductTreeSchema = GetProductTreeRowSchema.nullable()
export type GetProductTree = z.infer<typeof GetProductTreeSchema>
```

`quantity` is at least 1 and `standardCost` at least 0 from the sample's `CHECK` constraints, `name` is at most 50 characters
from its alias type `Name`, and `unit` is exactly three UTF-16 code units because `nchar(3)` arrives padded. Zod counts code
points, of which a character outside the Basic Multilingual Plane (an emoji) is one but takes two code units, so it checks two
to three.

[The harvest](docs/corpus-harvest.md) measures more databases; [what haruspex models](docs/coverage.md) lists every construct.

## Advanced features

What some projects need, each a line here; [the documentation's index](docs/README.md#advanced-features) says which situation
each is for.

- **Separate deployments:** types that work against both databases, and a check of the running application ([a baseline](docs/command-line.md#checking-against-a-baseline)).
- **The callers' session:** more claims when connections set `ANSI_WARNINGS` or hold `UNMASK` ([`session`](docs/configuration.md#the-callers-session-session)).
- **Dynamic SQL:** results declared where the project can't show them ([declared results](docs/configuration.md#declared-results-results)).
- **Names of your own:** a call's results named and typed where it is made, dynamic SQL included ([`WITH RESULT SETS`](docs/database-module.md#naming-a-calls-results-with-with-result-sets)).
- **JSON columns:** JSON stored in a column, typed by a JSON Schema ([`columns`](docs/configuration.md#json-stored-in-a-column-columns)).
- **JSON parameters:** a document a procedure takes, typed by a JSON Schema ([`parameters`](docs/configuration.md#json-a-parameter-takes-parameters)).
- **JSON Schemas in the database:** a column's or parameter's schema named in its scripts and DACPAC ([`haruspex.jsonSchema`](docs/configuration.md#naming-a-columns-schema-in-the-sql)).
- **REST services:** what a procedure reads from a service's reply, typed by a JSON Schema declared for its endpoint ([`endpoints`](docs/configuration.md#calls-to-rest-endpoints-endpoints)).
- **Overrides:** what the team knows and the database can't prove, and other names ([overrides](docs/configuration.md#overrides)).
- **Descriptions:** the database's descriptions under a name of its own ([`descriptions`](docs/configuration.md#descriptions-descriptions)).
- **A DACPAC built by another tool:** its key constraints, on your word ([`builds`](docs/configuration.md#a-package-built-by-another-tool-builds)).
- **Several source folders:** a database kept beside folders that aren't its own ([`source` as a list](docs/configuration.md#several-folders-source-as-a-list)).
- **Several modules:** a module per schema ([`split`](docs/configuration.md#splitting-the-output-into-modules-split)), or per plugin ([`check --all`](src/Haruspex.Cli/README.md)).
- **Why a type is what it is:** a field's derivation from the committed IR ([`explain`](docs/command-line.md#explain)).
- **Tools and CI:** the report as JSON, SARIF or GitHub annotations ([the formats](docs/command-line.md#formats)).
- **Other languages:** the same claims as one JSON Schema document ([the JSON Schema emitter](src/Haruspex.Emit.JsonSchema/README.md)).
- **The schemas alone:** for an application that calls the driver itself ([reading each result](docs/database-module.md#calling-the-driver-yourself)).
- **Every key accounted for:** a mapper the compiler stops when a row gains a key ([the mapping helper](docs/layers.md#a-mapper-that-accounts-for-every-key)).

## What it guarantees, and what it can't see

Every claim haruspex makes holds for every execution, given what it assumes and except in the known gaps listed below
(both after the run-time checks): a field it calls non-null is never null, and a bound is never exceeded. That makes its types an over-approximation, sometimes looser than intuition (`COUNT(*)` is nullable, because past
2,147,483,647 rows it returns NULL under session options the caller controls). When it can't prove a shape it says the shape
is **unknown, with a reason**, and never guesses. It can't see what isn't in the source: dynamic SQL, `EXEC @procedure`, CLR
and system procedures, and objects outside the project unless their DACPACs are given; a config file can declare their
results. Nor can it see the caller's session `SET` options, unless `session` in `haruspex.json` declares them
([decision 0038](docs/decisions/0038-the-callers-session-is-declared.md)).

Nor can it see what JSON stored in a column holds: another application, an import or a script run by hand may have written
it, so it is typed only when declared ([decision 0017](docs/decisions/0017-stored-json-is-typed-only-by-declaration.md)). A
JSON Schema file declares it once, for the column:

```jsonc
"columns": { "shop.orders.settings": { "json": "schemas/order-settings.schema.json" } }
```

The declaration is part of the column's type, so it goes wherever the column's value goes, through views, functions, variables
and temporary tables: read plainly, the column is a string the generated code parses and checks against the schema, and through
`json_query` it is that JSON, nested as JSON; a path into it (`JSON_VALUE(c, '$.address.city')`, `json_query` with a path,
`OPENJSON`) is typed from the schema a step at a time, nullable where a step may find nothing. It is a claim about every writer there ever was, checked at run time like any other
([the configuration](docs/configuration.md#json-stored-in-a-column-columns)).

The more the database declares, the more the types say: a `CHECK` constraint becomes a value set or a range, and a key a
query filters on in full means at most one row. A masked column is the exception: a caller without `UNMASK` reads its mask,
which a `CHECK` doesn't bind, so of it, and of what is computed from it, only the type, nullability and greatest length are
claimed, unless the config file declares that the callers hold `UNMASK`
([the configuration](docs/configuration.md#the-callers-session-session)). Overrides in `haruspex.json` are for what a database
doesn't declare.

At run time the generated functions check both ends, so a wrong parameter or a result that no longer matches its schema
fails loudly, with an error saying which, rather than reaching the application's code; database errors stay the driver's.
[When a call fails](docs/database-module.md#when-a-call-fails) says how to tell them apart.

**What it assumes.** No source can show these, so haruspex assumes them, each where it is decided:

- **The database is the one the project deploys**, changed since only by the project's own code. So a `CHECK` constraint is a
  claim only while SQL Server trusts it: a bulk load from outside the project (.NET's SqlBulkCopy, or `BULK INSERT` without
  `CHECK_CONSTRAINTS`) skips the constraint and leaves it untrusted, as `ALTER TABLE … NOCHECK` run by hand does.
  `SELECT name FROM sys.check_constraints WHERE is_not_trusted = 1` lists any such constraint.
- **Views and inline functions are refreshed when they are deployed, and columns are deployed in their declared order**, as
  SqlPackage does ([decision 0047](docs/decisions/0047-a-star-is-bound-against-the-tables-the-project-defines.md)).
- **No caller reads uncommitted data.** A `NOLOCK` hint, or a level the procedure sets itself, is seen and drops the claims it
  breaks; the level a caller sets is not ([decision 0016](docs/decisions/0016-visible-read-uncommitted-drops-key-claims.md)).
- **Names are matched without case**, as SQL Server matches them under a case-insensitive collation. Under a case-sensitive
  one the two agree for every project that deploys, unless two names in one namespace differ only in case (`[Code]` and `[code]`
  in one table): haruspex stops on that with an error rather than take one name for the other
  ([decision 0119](docs/decisions/0119-names-that-differ-only-in-case-are-an-error.md)).
- **In a folder of scripts, a definition is created with `ANSI_NULLS` on**, unless its own script sets it off before it (a
  DACPAC records the setting for each definition, and it is read)
  ([the guide](docs/internals/02-the-inputs-and-the-model.md#a-script-becomes-the-model-in-three-steps)).
- **A DACPAC named in `builds` was built from a project**, so its primary keys and unique constraints are enforced. Without it,
  only a package Microsoft.Build.Sql or SSDT built keeps them: an extract can't say whether a constraint's index is disabled
  ([decision 0120](docs/decisions/0120-an-extract-s-key-constraints-are-no-key.md)).
- **SQL Server 2025.** The rules are observed on the version the tests run, and an earlier one may differ
  ([the guide](docs/internals/08-the-sql-server-type-system-as-data.md#limits)).
- **No query reads 2^63 rows**, which is why a `SUM` of a decimal of at most 19 digits is claimed never to overflow
  ([the inference](src/Haruspex.SqlServer.Inference/README.md#sum-avg-and-the-statistical-aggregates)).

**Known gaps.** A case where a claim can be wrong because of how a database is set up, run or loaded,
and which needs a decision or a larger change before the code handles it, is listed here until it is handled. Not every bug
is: a rule of the analysis found wrong is fixed as a bug, in a change of its own, and isn't listed here.

- **A CLR scalar function marked deterministic that isn't.** When a method marked `IsDeterministic = true` can give another
  answer for the same arguments, a filter or a `CASE` proves nothing of a column computed by it: the server may run it again for
  the select list, so a column `WHERE v IS NOT NULL` kept can still be null. The mark is in the assembly, which haruspex doesn't
  read, so it takes the proof ([decision 0148](docs/decisions/0148-a-clr-function-is-typed-from-its-declaration.md)).

On the two samples above, a test writes [the figures](docs/corpus-figures.md):

- **Typed 32 of 34 fields in AdventureWorks.**
- **Typed 197 of 198 fields in WideWorldImporters; the results of 22 of its 129 procedures can't be described, so they have no fields to count.**

[The harvest](docs/corpus-harvest.md) measures more databases; [what haruspex models](docs/coverage.md) lists every construct.

## Status and roadmap

**Pre-release.** The command line, the generated code and the IR (the record of what haruspex inferred) will change without
notice. Don't depend on it yet.

Available now, as well as procedures: **query files**, queries kept in the application's own `.sql` files
([the configuration](docs/configuration.md#query-files-queries)); and **declared JSON columns**, JSON stored in a column typed by
a JSON Schema ([the configuration](docs/configuration.md#json-stored-in-a-column-columns)).

Next: **packages**, rather than building from source.

Possible later, with no date: checking hand-written schemas against a procedure, and checking each consuming application
before a database change is deployed. Not planned: connecting to a database
([decision 0001](docs/decisions/0001-infer-from-source-never-a-live-database.md)), building SQL at run time, or an API over
the database.

## Install

It is a command-line tool, not yet published as a package. Build it from source, which needs the
[.NET 10 SDK](https://dotnet.microsoft.com/download):

```sh
git clone https://github.com/haruspex-tools/haruspex.git
cd haruspex
dotnet build
```

### A preview from npm tarballs

**This is a preview.** Once published, haruspex will be installed from npm, as `npm install --save-dev haruspex`, with a native binary for
each platform and no .NET runtime needed ([how the packages work](packaging/npm/README.md)). Until it is published, a CI
run that the maintainer starts by hand on `main` builds the packages as tarballs and keeps them for 30 days as its artifact
`haruspex-npm`: the command, `haruspex-0.0.0-preview-<commit>.tgz`, and a binary for each platform, with the licences of what
it contains, `haruspex.tools-<platform>-0.0.0-preview-<commit>.tgz`, where the platform is `linux-x64`, `linux-arm64`,
`darwin-arm64`, `darwin-x64` or `win32-x64`. Only those runs' tarballs are the project's. Every other run keeps its tarballs a
day, under a name of its own: `unreviewed-pr-haruspex-npm` for a pull request, built from code no one has reviewed, and
`ci-haruspex-npm` for any other run. Find the latest successful run started by hand on `main`, then download its tarballs,
with the `gh` CLI:

```sh
gh run list --repo haruspex-tools/haruspex --workflow CI --branch main --event workflow_dispatch --status success --limit 1
```

```sh
gh run download <run id> --repo haruspex-tools/haruspex --name haruspex-npm --dir haruspex-preview
```

To try it on one machine, install the command and the binary for that machine:

```sh
npm install --save-dev ./haruspex-preview/haruspex-0.0.0-preview-<commit>.tgz ./haruspex-preview/haruspex.tools-darwin-arm64-0.0.0-preview-<commit>.tgz
npx haruspex --version
```

In an application that others install too, or that CI installs with `npm ci`, keep the tarballs in the repository (say in
`vendor/haruspex`), install the command as before, and every platform's binary as optional, so that npm installs only the one
for the machine it runs on and the lock file covers them all:

```sh
npm install --save-dev ./vendor/haruspex/haruspex-0.0.0-preview-<commit>.tgz
npm install --save-optional ./vendor/haruspex/haruspex.tools-linux-x64-0.0.0-preview-<commit>.tgz ./vendor/haruspex/haruspex.tools-linux-arm64-0.0.0-preview-<commit>.tgz ./vendor/haruspex/haruspex.tools-darwin-arm64-0.0.0-preview-<commit>.tgz ./vendor/haruspex/haruspex.tools-darwin-x64-0.0.0-preview-<commit>.tgz ./vendor/haruspex/haruspex.tools-win32-x64-0.0.0-preview-<commit>.tgz
```

Then `npx haruspex generate` and `npx haruspex check` run it, and so does a package script (`"db": "haruspex generate"`). The
package holds the JSON Schema of `haruspex.json`, so the config file can name it as
`"$schema": "./node_modules/haruspex/haruspex.schema.json"`. The Linux binaries need glibc 2.34 or later, which CI checks;
[packaging/npm](packaging/npm/README.md) says what else they need of the processor.

pnpm installs the tarballs the same way (`pnpm add`). Yarn 1 can't: it looks for every optional dependency on the registry,
where none is published yet. Yarn 4 can when `package.json` names every platform's tarball in `resolutions` before the command
is added:

```jsonc
"resolutions": {
  "@haruspex.tools/linux-x64": "file:./vendor/haruspex/haruspex.tools-linux-x64-0.0.0-preview-<commit>.tgz",
  "@haruspex.tools/linux-arm64": "file:./vendor/haruspex/haruspex.tools-linux-arm64-0.0.0-preview-<commit>.tgz",
  "@haruspex.tools/darwin-arm64": "file:./vendor/haruspex/haruspex.tools-darwin-arm64-0.0.0-preview-<commit>.tgz",
  "@haruspex.tools/darwin-x64": "file:./vendor/haruspex/haruspex.tools-darwin-x64-0.0.0-preview-<commit>.tgz",
  "@haruspex.tools/win32-x64": "file:./vendor/haruspex/haruspex.tools-win32-x64-0.0.0-preview-<commit>.tgz"
}
```

and then `yarn add -D ./vendor/haruspex/haruspex-0.0.0-preview-<commit>.tgz`, with either of its linkers.

The application installs [Zod](https://zod.dev) 4.6.7 or a later 4.x, and [mssql](https://www.npmjs.com/package/mssql),
itself; the package declares that range of zod as an optional peer dependency. 4.6.7 is the least release the tests check, and
4.4.3 is known not to be enough: it counts a string's length in UTF-16 code units, so it refuses values a length declared in
code points allows. The tests compile
every generated module in strict mode with zod 4.6.7, mssql 12.7.14 and TypeScript 7.0.2.

## How it is built

This project is built by agentic engineering. AI coding agents write each change, in small slices, and a second agent reviews
each slice adversarially, trying to break its claims. A person decides what is built and merges every change. Each rule
haruspex relies on about SQL Server is observed on a real server by a test, and the code that relies on it names that test;
what no test can observe is [stated as an assumption](#what-it-guarantees-and-what-it-cant-see).
[The guide](docs/internals/README.md) explains how it works and why a static analyser is built this way, and
[the decision log](docs/decisions/README.md) records each decision with the alternatives.

## Who it is not for

- **Applications that own their schema.** An ORM such as EF Core, Prisma or Drizzle fits better: there the application
  owns the model, and here the database does.
- **Anyone wanting an API generated over the database**: Data API Builder, PostGraphile or Hasura.
- **Databases other than SQL Server.** For PostgreSQL, pgtyped, sqlc or Kysely with `kysely-codegen`.
- **Code that builds its SQL at run time.** A typed query builder such as Kysely types it where it is built.
- **Databases with no project and no DACPAC.** Extract a DACPAC first, with SqlPackage's `/Action:Extract`. An extract can't
  say whether a primary key's or unique constraint's index is enabled, so its keys give no "at most one row"
  ([decision 0120](docs/decisions/0120-an-extract-s-key-constraints-are-no-key.md)); a package built from the project keeps them.
- **Anyone who needs a supported tool.** This is a personal open-source project with
  [no support commitment](SUPPORT.md).
- **Anyone who won't use code written by AI.** Better to know before you start.

## Licence

[Apache 2.0](LICENSE). Contributions are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md). Report a vulnerability
privately, as [SECURITY.md](SECURITY.md) says.
