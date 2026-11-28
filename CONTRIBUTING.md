# Contributing to haruspex

Thank you for considering a contribution. This page says how the project is built and tested, and the
few conventions that keep it trustworthy.

## The principle every change must keep

**Never claim more than the engine guarantees.** A field reported as non-null must never be null, and a
reported shape must hold for every value SQL Server can produce. When a construct is not understood,
the answer is an *unknown with a reason*, never a guess. A user forgives a tool that is sometimes too
cautious, but not one that is confidently wrong, because that error surfaces at runtime in their code.
A change that types more fields by guessing will not be accepted.

## Test first

Every change starts with a failing test.

- **A new SQL construct** gets a small fixture (invented SQL, a folder in `tests/fixtures/` laid out as
  [its README](tests/fixtures/README.md) says) and a snapshot of the IR and of the generated Zod, in the fixture's `expected/` folder. Write the fixture and the test, run it, and check that the snapshot the
  test proposes (`*.received.*`) is what SQL Server would really do, before accepting it as
  `*.verified.*` (rename it, or rerun with `HARUSPEX_ACCEPT=1`). The snapshot is the claim, so review it
  as carefully as code. The helper is described in `tests/Haruspex.Tests/Snapshotting/README.md`.
- **A bug** gets a test that reproduces it before the fix.
- **A test's name is a sentence** saying what it proves, at least three words joined by underscores
  (`Separates_absent_keys_from_null_values`, never `Ir`). `TestNameTests` (in `tests/Shared`) checks every test in both projects.
- Fixtures are invented. The one exception is the README's showcase: `wide-world-importers` and `adventure-works` copy
  tables from Microsoft's MIT-licensed sample databases, with the licence beside them. Never copy schema or code from a real
  organisation's database into this repository, even in a test.

## Decision context lives with the code

When a line of code is there for a non-obvious reason (an engine rule, a soundness argument, a
compatibility constraint), say why in a short comment beside it, or in a `README.md` in the same folder
when it needs more room. A reviewer should not need the commit history or an issue thread to understand
why the code is the way it is. Comments say *why*, not *what*.

A design decision with alternatives worth recording gets a record in [the decision log](docs/decisions/README.md), and
the code links it. An idea a reader needs in order to understand a layer is explained in
[the guide](docs/internals/README.md), and a change to the code it describes updates its chapter in the same pull request.

## Building and testing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Node.js (LTS) is optional: without it,
the test that compiles the generated TypeScript is skipped, with a message saying so.

```sh
dotnet build
dotnet test
```

The build treats warnings as errors, with nullable reference types and every SDK analyser on
(`AnalysisLevel` `latest-all`). Code style is part of the build too: every style rule is a warning, so a
file that `dotnet format` would change does not build. The house style, and the few rules turned off with the
reason for each, are in `.editorconfig`. Before committing, run:

```sh
dotnet format
```

CI builds and runs the unit tests on Windows, macOS and Linux, since haruspex must write the same bytes on each; the
differential tests run on Linux alone, where SQL Server's container does. It also compiles the command line ahead of time for
each platform (Native AOT, `-p:HaruspexNativeAot=true`), so a change that breaks trimming or AOT fails there, runs each
binary once on the example application, checks that each Linux binary needs no newer glibc and no higher x86-64 level than
[packaging/npm](packaging/npm/README.md) says, and keeps it, with the licences that go with it, as an artifact of the run. From
those binaries it packs the npm tarballs ([packaging/npm](packaging/npm/README.md)), installs them into an empty project on
each platform, and runs the command there.
It runs `dotnet format --verify-no-changes` after the build, and lints the workflow files with `actionlint` and `zizmor`.
It runs on each push to `main` and each pull request, weekly on `main`, and by hand on any branch pushed to GitHub
(`gh workflow run CI --ref <branch>`), which tests a change before it reaches `main`.
A second workflow, `site.yml`, builds haruspex.tools and deploys it to GitHub Pages from `main` and from release tags
([site](site/README.md)).
Every relative link in the repository's Markdown, anchors included, must resolve; `MarkdownLinkTests` checks
them.
Package versions are central (`Directory.Packages.props`) and locked (`packages.lock.json`, committed).
To add or update a package, change `Directory.Packages.props`, run `dotnet restore --force-evaluate`,
and commit the updated lock files with the change. Dependency updates are read by hand, never merged
automatically. A known vulnerability in any package restored, a test's transitive one included, fails the restore on purpose:
NuGet reports it as NU1901 to NU1904, and the build treats those warnings as errors, as it does every other. So an advisory
published today fails the restore of every commit, old ones too. CI runs weekly, as well as on each change, so that the
maintainer's run meets it first, and the fix is a commit that updates or pins the package. To work past one until then, or to
build an old commit for a bisect, set `NuGetAudit=false` in the environment (`NuGetAudit=false dotnet test`), which turns the
audit off for every command run with it, on any commit; a later command without it fails again. CI never sets it. A package the command line is built from goes into its native binary, so its licence goes with every copy:
commit its licence text from upstream in [packaging/licenses](packaging/licenses/README.md), whose README says how, and
`LicenseTests` fails until it is there.

The emit-check test (`tests/emit-check`) writes the Zod generated from the fixtures and compiles it with
`tsc --noEmit` (strict) against a pinned `zod`, and `tests/emit-lint` lints the same modules with ESLint's recommended rules
and typescript-eslint's strict and stylistic sets, failing on any warning
([decision 0062](docs/decisions/0062-generated-files-pass-typescript-eslint-s-strict-rules-with-no-disable-header.md)). Each
runs `npm ci` in its folder the first time, and again when its `package-lock.json` has changed since. The lint has a
package of its own because typescript-eslint reads TypeScript through the compiler's JavaScript API, which the TypeScript
the emit check compiles with no longer has. The example application, `examples/greenhouse`, is compiled the same way
against the module committed beside it (`ExampleAppTests`). The same package holds
`tests/emit-check/src/multiple-of.mjs`, the evidence that Zod checks a decimal's step without floating-point false
failures, `tests/emit-check/src/json-text-once.mjs`, the evidence that the JSON text helper builds its content's schema once,
`tests/emit-check/src/schema-verdicts.mjs`, the evidence that the generated schemas refuse the forms SQL Server and the driver
never hand over,
`tests/emit-check/src/accept.mjs`, through which the differential tests run real output into the generated schemas,
`tests/emit-check/src/accept-json-schema.mjs`, through which they validate the same output against the generated JSON Schema
with Ajv, `tests/emit-check/src/meta-schema.mjs`, which checks every generated JSON Schema, the IR's and the config file's, against the draft 2020-12
meta-schema and compiles it in Ajv's strict mode, `tests/emit-check/src/verdicts.mjs`, which gives Ajv's verdict on each IR file
and config file the tests hold a schema and its reader to, and `tests/emit-check/src/driver.mjs`, through which they call the database with the pinned mssql driver, and
`tests/emit-check/src/functions.mjs`, through which they call it through the generated functions. CI sets
`HARUSPEX_REQUIRE_NODE=1`, which turns a missing Node into a failure rather than a skip. A test gives a script the folder it
wrote its files to, never a list of the files: Windows refuses a command line past a fixed length, so the tests refuse one past
cmd.exe's 8,191 characters on every platform, and a list that grows with the fixtures fails on the machine it is written on.

### Differential tests against SQL Server

`tests/Haruspex.DifferentialTests` deploys the fixtures into a real SQL Server, seeds them, runs every
procedure and checks the output against the inferred IR; its `README.md` explains how. It needs a SQL
Server, found in this order:

1. `HARUSPEX_TEST_SQLSERVER`, a connection string to a server of your own. Each run creates a database
   with a random name (`haruspex_test_…`) and drops it afterwards, even when tests fail, so the login needs
   rights to create and drop databases. **Never point it at a shared server or one holding real data.**
2. Otherwise Testcontainers starts SQL Server in Docker, which needs Docker reachable through a local
   socket, a named pipe or TCP. It cannot use a Docker host reached over SSH.
3. Otherwise these tests are skipped, saying why. CI sets `HARUSPEX_REQUIRE_SQLSERVER=1` on Linux, which turns the
   skip into a failure.

SQL Server's images are x64 only, so on an Apple Silicon Mac, or wherever Docker is on another machine, a
long-running server of your own is the simplest route. On any x64 machine with Docker:

```sh
docker run -d --name haruspex-mssql --restart unless-stopped \
  -p 14330:1433 -e ACCEPT_EULA=Y -e MSSQL_PID=Developer \
  -e MSSQL_SA_PASSWORD="$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c 32)Aa1" \
  mcr.microsoft.com/mssql/server:2025-CU10-ubuntu-24.04
```

Read the password back with `docker exec haruspex-mssql printenv MSSQL_SA_PASSWORD`, keep it in a file
outside the repository that only you can read, and export the connection string from there before testing:

```sh
export HARUSPEX_TEST_SQLSERVER='Server=<host>,14330;User ID=sa;Password=<password>;Encrypt=true;TrustServerCertificate=true'
```

Use the same image as `SqlServerFixture.Image`, so local runs see what CI sees. `TrustServerCertificate`
is there because the container's certificate is self-signed; use it only for a server of your own.

To run the tool from source:

```sh
dotnet run --project src/Haruspex.Cli -- generate tests/fixtures/orders/database --include 'sales.*' --out out
cd tests/fixtures/shop && dotnet run --project ../../../src/Haruspex.Cli -- generate --out ../../../out/shop  # with its haruspex.json
```

### Coverage on public corpora

`tests/corpus` holds two of Microsoft's sample databases, vendored at a pinned commit, and `CorpusFiguresTests` writes what
haruspex makes of them to [docs/corpus-figures.md](docs/corpus-figures.md). Every other public corpus is fetched into a
folder outside the checkout, and `CorpusHarvestTests` writes its figures, with the harvest that ranks what every corpus shows
haruspex missing, to [docs/corpus-harvest.md](docs/corpus-harvest.md). Fetch them once, before testing (it needs git):

```sh
node tests/corpus/fetch.mjs
```

Without them, the tests that need them are skipped, saying so. A change that types more or less of a corpus fails its page's
test until the page is rewritten with `HARUSPEX_ACCEPT=1`; commit the page with the change, and say in the commit why the
figures moved. A figure in any other public text is copied from these pages, never measured elsewhere
([decision 0020](docs/decisions/0020-public-figures-come-from-public-corpora.md)). How the corpora are kept, fetched and
refreshed is in [tests/corpus/README.md](tests/corpus/README.md).

### Adding a corpus

A corpus is added by fetching it, not by copying it into the repository: one entry in `tests/corpus/fetched.json` and the
rewritten harvest are the whole change.

1. **Check its licence.** It must be public, and its licence or terms must allow downloading it for analysis. A repository
   with no licence is a pause, not a yes: all rights are reserved, and publishing counts from it is the project's act, so ask
   the maintainer first. A copyleft licence is fine, since nothing from the corpus is committed.
2. **Add its entry** to `tests/corpus/fetched.json`, in the format
   [the corpus README](tests/corpus/README.md#the-manifest) gives: an `id`, its name and kind, the repository, the licence
   and its notices, the paths to take, how to read it, and what it is. Leave `commit`, `committed` and `files` empty.
3. **Pin it**, which fills them in and fetches it:

   ```sh
   node tests/corpus/fetch.mjs pin <id> <ref>
   ```

4. **Account for every script.** `FetchedCorpusManifestTests` fails until each script fetched is read or left out in
   `leftOut` with a reason. Leave a script out only for a reason the harvest can state, such as a file that defines its
   objects a second time.
5. **Rewrite the harvest** with `HARUSPEX_ACCEPT=1 dotnet test --filter CorpusHarvestTests`, and read it. The page heads each
   corpus with its name, so if `tests/prose/check.sh` flags the name, add it to `.cspell/words.txt` and to the heading
   exceptions in `.vale/styles/haruspex/Headings.yml`, beside the other corpora's, since it is a name.
6. **Commit the entry and the page together.** Nothing else from the corpus is committed: not its code, not a name from it,
   not the IR or Zod haruspex makes of it. A gap it shows becomes a fixture of invented SQL, never a copy of its code.

## Writing

Everything a reader reads (the Markdown, XML documentation, comments and diagnostics) follows the house style in
[docs/style.md](docs/style.md): British spelling, plain words, sentence-case headings, the project's fixed terms.
Tools check the rules its last table assigns to them: cspell the spelling, Vale the style, and Mermaid that every
diagram parses. Run all three before committing a change to prose:

```sh
tests/prose/check.sh
```

Two tests hold the guide, the decision log and the READMEs to the code: every code reference in them must resolve
(`CodeReferenceTests`), and every worked example is run and its output compared (`WorkedExampleTests`). The conventions
they check are in [the guide's index](docs/internals/README.md#how-the-guide-is-kept-true).

It needs Node, and Vale 3.24.0 on the `PATH` or Docker with a local socket to run Vale's image. CI runs the same
script. [tests/prose/README.md](tests/prose/README.md) says what to do when a check is wrong rather than the text:
a real term goes in the project dictionary, never a misspelling.

## Where a construct's tests are

The tests are laid out like the code. `tests/Haruspex.Tests` has a folder per layer, named as the layer is
(`tests/Haruspex.Tests/Inference`, `tests/Haruspex.Tests/Emit.Zod` and the rest), beside folders for the tests of the
repository itself: its architecture, its documentation, the corpus, the fixtures and the snapshot helper. One project
with a folder per layer, rather than a test project per layer: the Inference tests, the most numerous, reach their layer
through `SqlProject` and the fixtures, which compose every layer, and one project builds and runs them once. The
architecture test, not the project boundary, is what keeps the layers apart. Outside Inference a test class is named for
the type it tests (`ConfigReaderTests` tests `ConfigReader`). Inside Inference it is named for a **construct family**,
the group of SQL constructs one part of the inference handles, and that one name leads to everything else about it:

| What | Where | Named |
|---|---|---|
| Unit tests | `tests/Haruspex.Tests/Inference` | `<Family>Tests`; when the class is large, one partial part per construct, named for it: `ExecTests.InsertExec.cs`, and the family's checks of the IR and Zod it writes, as a whole, in a part of their own, `ExecTests.Zod.cs` or `RefinementTests.IrAndZod.cs` |
| Observation tests, one engine rule each | `tests/Haruspex.DifferentialTests` | `<Family>ObservationTests`, each test named for the rule it observes |
| The reference | `src/Haruspex.SqlServer.Inference/README.md` | a section per construct, which names its code and its observation tests |
| The fixture | `tests/fixtures/<domain>` | for its invented domain (`calls` is a bakery); its README's first sentence names its constructs |

So a rule's unit test is found from the construct's name, and the rule's observation from the unit test's class. A new
construct joins its family's classes, or starts a family with all four. A class of either kind over about 400 lines is
split into parts along the constructs it covers. The families today:

| Family | Unit tests | Observation tests | Fixture | Reference section |
|---|---|---|---|---|
| Statement flow | `StatementFlowTests` | `StatementFlowObservationTests` | `tests/fixtures/flow` | "The state", "Result sets are positional", "CTEs" |
| Calls | `ExecTests` | `ExecObservationTests` | `tests/fixtures/calls` | "Calls" |
| Refinement | `RefinementTests` | `RefinementObservationTests` | `tests/fixtures/refine` | "Refinement" |
| Writes | `DmlTests` | `DmlObservationTests` | `tests/fixtures/dml` | "DML", "Triggers", "Cursors" |
| APPLY and views | `ApplyViewTests` | `ApplyViewObservationTests` | `tests/fixtures/apply` | "APPLY", "Views" |
| Expressions | `ExpressionTests` | `ExpressionObservationTests`, `BitwiseGridTests` | `tests/fixtures/tabs` | "Expressions" |
| Numbers | `NumericTests` | `NumericObservationTests`, `ArithmeticGridTests` | `tests/fixtures/ledger` | "Decimal arithmetic", "Numeric functions" |
| CASE and its kin | `CaseTests` | `CaseObservationTests` | `tests/fixtures/cases` | "CASE, IIF, CHOOSE and NULLIF", "COALESCE and ISNULL over JSON" |
| What the server folds | `FoldingTests` | `FoldingObservationTests` | `tests/fixtures/folds` | "What the server folds" |
| Built-in functions | `FunctionTests` | `FunctionObservationTests` | `tests/fixtures/funcs` | "Built-in functions", "Window functions" |
| A call's clauses, and `COLLATE` | `FunctionTests` (`FunctionTests.Clauses.cs`), `ClauseTableTests` | `ClauseObservationTests` | `tests/fixtures/funcs` | "Clauses" |
| Table-valued functions | `TableFunctionTests` | `TableFunctionObservationTests` | `tests/fixtures/tabs` | "Table-valued functions" |
| Full-text table functions | `FullTextTests` | `FullTextObservationTests` | `tests/fixtures/glossary` | "Full-text table functions", [decision 0135](docs/decisions/0135-a-full-text-search-s-key-is-its-key-column-s-type-and-its-rank-is-nullable.md) |
| Set operations and joins | `SetJoinTests` | `SetJoinObservationTests` | `tests/fixtures/sets` | "Set operations", "CROSS JOIN and FULL OUTER JOIN", "OPENJSON" |
| Combining types | `SetTypeTests` | `SetTypeObservationTests`, `SetTypeGridTests` | `tests/fixtures/bazaar` | "Combining types" |
| JSON traced | `JsonTracingTests` | `JsonTracingObservationTests` | `tests/fixtures/kits` | "JSON built here, traced", "OPENJSON" |
| JSON assembled as text | `JsonAssembledTests` | `JsonAssembledObservationTests` | `tests/fixtures/beads` | "JSON assembled as text" |
| `FOR JSON AUTO` and `ROOT` | `ForJsonAutoTests` | `ForJsonObservationTests` (`ForJsonObservationTests.Auto.cs`) | `tests/fixtures/nests` | "FOR JSON AUTO and ROOT", [decision 0144](docs/decisions/0144-for-json-auto-is-typed-from-observed-nesting.md) |
| JSON constructors | `JsonConstructorTests` | `JsonConstructorObservationTests` | `tests/fixtures/payloads` | "JSON constructors" |
| Functions that return JSON | `JsonFunctionTests` | `JsonFunctionObservationTests` | `tests/fixtures/trees` | the guide's [Summaries and recursion](docs/internals/07-summaries-and-recursion.md) |
| JSON declared for a column | `DeclaredColumnTests` | `DeclaredColumnObservationTests` | `tests/fixtures/parcels` | "JSON declared for a column", [decision 0093](docs/decisions/0093-a-column-s-json-is-part-of-its-type-completed-by-the-config.md) |
| Calls to REST endpoints | `RestEndpointTests` | `RestEndpointObservationTests` | `tests/fixtures/tariffs` | "Calls to REST endpoints", [decision 0140](docs/decisions/0140-a-rest-response-is-typed-from-a-schema-declared-for-its-endpoint.md) |
| JSON Schema named in the SQL | `ColumnAnnotationTests`, with `PackagePropertiesTests` in `tests/Haruspex.Tests/Sources` | `ColumnAnnotationObservationTests` | `tests/fixtures/parcels` (`route`) | [decision 0141](docs/decisions/0141-a-column-s-json-schema-may-be-named-in-the-sql.md) |
| JSON declared for a parameter | `ParameterDeclarationTests` | `ParameterJsonObservationTests` | `tests/fixtures/parcels` (`quote_options`) | "JSON declared for a parameter", [decision 0143](docs/decisions/0143-a-parameter-s-json-may-be-declared.md) |
| JSON in `varchar` text, through its code page | `CodePageReadTests`, `CodePageTextTests` | `CodePageObservationTests` | none | "JSON declared for a column", [decision 0145](docs/decisions/0145-varchar-json-is-read-through-its-code-page.md) |
| A key a writer may escape, read by a path | `EscapedKeyTests`, `KeySpellingTests` | `EscapedKeyObservationTests` | none | "Paths into JSON", [decision 0147](docs/decisions/0147-a-key-a-writer-may-escape-may-be-missing-from-a-path.md) |
| Read uncommitted | `ReadUncommittedTests` | `ReadUncommittedObservationTests` | `tests/fixtures/tabs` | [decision 0016](docs/decisions/0016-visible-read-uncommitted-drops-key-claims.md) |
| The callers' session | `SessionTests` | `SessionObservationTests` | `tests/fixtures/meters` | "The callers' session" |
| Masked columns | `MaskingTests` | `MaskingObservationTests` | `tests/fixtures/fittings` | "Masked columns", [decision 0118](docs/decisions/0118-a-masked-column-claims-only-its-type-nullability-and-greatest-length.md) |
| Precision lost | `PrecisionLossTests` | `PrecisionLossObservationTests`, `PrecisionLossGridTests` | `tests/fixtures/assays` | "Precision lost" |
| Binary values | `BinaryTests` | `BinaryObservationTests` | `tests/fixtures/gallery` | "Binary values", [decision 0081](docs/decisions/0081-binary-values-are-a-kind-of-their-own-counted-in-bytes.md) |
| XML values | `XmlTests`, `XmlExpressionTests`, `XmlSchemaTests` | `XmlObservationTests`, `XmlExpressionObservationTests`, `XmlSchemaObservationTests` | `tests/fixtures/loom` | "XML values", [decision 0133](docs/decisions/0133-an-xml-value-is-a-kind-of-its-own-a-branded-string.md), [decision 0137](docs/decisions/0137-xml-s-nodes-query-for-xml-and-openxml-are-typed-as-the-server-gives-them.md), [decision 0139](docs/decisions/0139-xml-is-typed-from-its-schema-collection-where-the-schema-proves-it.md) |
| Canonical value sets | `CanonicalValueSetTests` | `CanonicalValueSetObservationTests`, `CollationFoldGridTests` | `tests/fixtures/tannery` | [decision 0043](docs/decisions/0043-a-value-set-is-canonical-by-a-fold-observed-from-the-server.md) |
| SQLCMD variables | `SqlcmdVariableTests` | `SqlcmdVariableObservationTests` | `tests/fixtures/kiosk` | [decision 0046](docs/decisions/0046-a-sqlcmd-variable-in-a-body-is-unknown.md) |
| `SELECT *` | `SelectStarTests` | `SelectStarObservationTests` | `tests/fixtures/haberdashery` | "`SELECT *`", [decision 0047](docs/decisions/0047-a-star-is-bound-against-the-tables-the-project-defines.md) |
| Parameters | `ParameterTests` | `ParameterObservationTests` | `tests/fixtures/till` | "Parameters" |
| Descriptions | `DescriptionTests`, with `DescriptionFinderTests` and `ExtendedPropertyCallsTests` in `tests/Haruspex.Tests/Sources` | `DescriptionObservationTests` | `tests/fixtures/lectern` | "Descriptions", [decision 0128](docs/decisions/0128-a-column-s-description-reaches-the-fields-that-are-that-column.md) |
| Binding | `BinderTests`, in `tests/Haruspex.Tests/Semantics` | `BindingObservationTests` | `tests/fixtures/scopes` | the Semantics README's "How data flows through it" |
| Query files | `QueryFileTests` | `QueryParameterObservationTests`, `QueryTextObservationTests` | the invented shop in `InferQuery.Shop` (`tests/Shared`) | "Query files", [decision 0059](docs/decisions/0059-a-query-file-s-parameters-are-typed-from-their-uses.md) |

Eleven classes are named otherwise, for what they cover across families: `ConstructTests` (queries, results and sources
in general, with `FOR JSON`'s rules in `ForJsonObservationTests` and `tests/fixtures/orders`), `FactFlowTests` (facts a
value carries through expressions), `ValueEqualityTests` (the laws equality of values and shapes keeps, over a generated
sample), `LatticeLawTests` (the laws of each fact's lattice and of the join of values, over a generated sample), `FixedPointTests`
(the one iteration every solver uses), `FactsTests` (the one environment of what filters and branches prove),
`CatalogueSweepTests` (every built-in type through every operator, with `CatalogueSweepGridTests` checking each type against
the server), `ConversionsTests` (the table of conversions as
data, each row observed in `ConversionObservationTests`), `FunctionTableTests` (the table of built-in functions as data: a row
for each function of the catalogue, each observed) and, in Semantics, `BuiltInTypeObservationTests` (the
catalogue of built-in types, row by row, against the server) and `SchemaFactsTests` (what types, alias types and CHECK constraints
guarantee, observed in `CheckConstraintObservationTests` on `tests/fixtures/checks`). `OPENJSON` belongs to two families: as
a source of rows with its `WITH` columns (`SetJoinTests.OpenJson.cs`), and reading JSON a procedure built and traced
(`JsonTracingTests.OpenJsonAndJsonValue.cs`). One observation class observes a driver rather than the engine:
`MssqlDriverObservationTests`, what the mssql driver hands an application for each form of JSON, which
`MssqlDriverAcceptanceTests` parses with the generated schemas
([decision 0048](docs/decisions/0048-an-absent-json-result-reads-as-null.md)). `CodeReferenceTests` checks that every name in these tables exists, and
`ConstructFamilyTests` that every Inference test class and every observation or grid class is named here.

## Layers

The code is split into layers, one project each, and **a layer refers only to the layers and packages its row
in the table allows**: only `Sources` reads a database project, `Semantics` never sees the IR, the IR and the
emitters never see SQL, and no SQL Server layer knows the emitters. The table, the reasons for its
lines, and the types that were split to fit it are in [docs/architecture.md](docs/architecture.md).

`tests/Haruspex.Tests/Architecture/ArchitectureTests.cs` enforces it: it reads the table in `docs/architecture.md`, which is its only
copy, and each assembly's references and each project file's, failing on anything the table doesn't allow. A new layer,
or a new reference between layers, is a change to that table's row, nowhere else. Never weaken the test to let a
reference through; move the code to the layer that may know it.

**Each file holds one type and is named after it** (`Table.cs` holds `Table`), so a type's name says where it is.
Nested types stay in the type they belong to, and a partial type's parts are named after the type and the part
(`Typer.Case.cs`).
`tests/Haruspex.Tests/Architecture/SourceLayoutTests.cs` enforces it.

| Path | What it holds |
|---|---|
| `src/Haruspex.*` | The layers, one project each. What each knows and may refer to is in [the table in docs/architecture.md](docs/architecture.md#the-table); each project's `README.md` describes it. |
| `schemas` | The published JSON Schemas: of `haruspex.json`, checked against examples by `ConfigSchemaTests`; and of the IR's file, one per IR version in `schemas/ir`, the current one generated from the model by `IrJsonSchema` and never edited by hand (`IrJsonSchemaTests` fails when it is stale; rewrite it with `HARUSPEX_ACCEPT=1`), and older ones kept as they were ([its README](schemas/ir/README.md)). |
| `tests/Haruspex.Tests` | Unit, snapshot, end-to-end and architecture tests, a folder per layer ("Where a construct's tests are", above). |
| `tests/Haruspex.DifferentialTests` | Runs the fixtures in SQL Server and checks the output against the IR and the generated Zod. |
| `tests/Shared` | Test support compiled into both test projects (`Haruspex.TestSupport`): the fixtures, found by listing their folder (`Fixture`); inferring procedures from SQL a test writes (`InferProcedure`) and finding things in the IR (`IrNavigation`); temporary folders, the repository's paths, and running the emit-check npm package. |
| `tests/fixtures` | The fixtures, one folder each: schema, seed, runs and expected outputs ([its README](tests/fixtures/README.md)). |
| `tests/dacpacs` | DACPACs built from the fixtures, the corpus and two small projects, and a few built or extracted in other ways, each with how it was made ([its README](tests/dacpacs/README.md)). DacFx made them, in development only; nothing refers to it. |
| `tests/ir-versions` | One IR file from each version of the format, which the reader must read (the current one) or refuse ([its README](tests/ir-versions/README.md)). |
| `tests/emit-check` | A small npm package that type-checks the generated Zod. |
| `tests/emit-lint` | A small npm package that lints the generated Zod. |
| `examples` | Small applications that use what haruspex generates, compiled by the tests and in CI ([greenhouse](examples/greenhouse/README.md)). |
| `tests/prose` | The prose checks (`check.sh`), their npm package and the fixtures that test them. |
| `packaging/npm` | The npm packages: the launcher of the `haruspex` command, and the script that packs it and each platform's binary into tarballs ([its README](packaging/npm/README.md)). |
| `site` | The script that builds haruspex.tools, which serves every address haruspex writes, and its tests ([its README](site/README.md)). |

Only `Model`, `Syntax` and `Sources` are packable (`IsPackable`, with package metadata and a readme), with `Diagnostics`
and `Collections` because their types are in those layers' public API; every other project is not. Nothing is published
yet: that is a later decision, so don't push packages anywhere. Their public API must be documented, which the build
enforces: a public member without a comment is an error (`CS1591`) in all five and in the layers reusable enough to follow
them (`Semantics` and `Ir`), and `PackableDocumentationTests` fails on a packable project that `.editorconfig` leaves out.
A comment says what the member is for, not its name again.

## Dependencies

Every dependency is listed here with its licence and the reason it is needed. A new one needs the same, and
the maintainer's agreement.

| Package | Licence | Used by | Why |
|---|---|---|---|
| `Microsoft.SqlServer.TransactSql.ScriptDom` | MIT | Model, Sources, Semantics, Inference | Microsoft's own T-SQL parser: the only way to read T-SQL as SQL Server does. |
| `System.CommandLine` | MIT | CLI | Argument parsing, help and usage errors, maintained by the .NET team. |
| `Microsoft.CodeAnalysis.CSharp` | MIT | the analysers in `analysers/`, their tests | The compiler's API, which haruspex's own build-time check of switches over closed hierarchies is written against ([decision 0032](docs/decisions/0032-a-switch-over-a-closed-hierarchy-is-exhaustive.md)). Build time only: nothing ships it. |
| `xunit.v3` | Apache-2.0 | tests | The test framework; runs on Microsoft.Testing.Platform (opted into in `global.json`). |
| `typescript` (npm) | Apache-2.0 | emit-check, examples | Compiles the generated Zod, to prove it type-checks. |
| `zod` (npm) | MIT | emit-check, examples | The library the generated code targets, pinned so the check is repeatable. |
| `ajv`, `ajv-formats` (npm) | MIT | emit-check | Validates real output against the generated JSON Schema, and each generated document against the draft 2020-12 meta-schema, as the differential acceptance test of the second emitter; `ajv-formats` asserts `format`, which draft 2020-12 leaves an annotation. The most used JSON Schema validator, in the package the Zod acceptance test already runs in. |
| `mssql` (npm) | MIT | emit-check, examples | The SQL Server driver most Node applications call procedures through: the differential tests observe what it hands over, parse that with the generated schemas, and call every fixture's procedures through the generated functions over it, and the example application calls it. |
| `@types/mssql` (npm) | MIT | emit-check, examples | `mssql`'s type declarations, which it doesn't ship, so that the generated functions and the example application compile against the driver. |
| `eslint`, `@eslint/js` (npm) | MIT | emit-lint | Lints the generated Zod with ESLint's recommended rules, as a team that lints its own code would. |
| `typescript-eslint` (npm) | MIT | emit-lint | The strict and stylistic rules for TypeScript, and the parser ESLint needs to read it. |
| `typescript` 6 (npm) | Apache-2.0 | emit-lint | typescript-eslint's parser, which needs the compiler's JavaScript API (below 6.1). |
| `cspell` (npm) | MIT | prose check | Spelling in British English across Markdown, C# and the workflows, identifiers included. |
| `mermaid` (npm) | MIT | prose check | Mermaid's own parser, to prove every diagram in the Markdown parses. |
| `jsdom` (npm) | MIT | prose check | The DOM that Mermaid's parser needs in Node for some diagram types, instead of a browser. |
| Vale (Docker image `jdkato/vale`) | MIT | prose check | Checks the house style's rules in the Markdown and the C# comments. |
| `Microsoft.Data.SqlClient` | MIT | differential tests | Microsoft's SQL Server driver for .NET: the tests must run procedures and read their results as SQL Server returns them. |
| `Testcontainers.MsSql` | MIT | differential tests | Starts a throwaway SQL Server in Docker when no server is configured, as in CI; it can later run a matrix of SQL Server versions. |
| `System.Security.Cryptography.Pkcs` | MIT | differential tests | Not used directly: pinned to a patched version because the one SqlClient brings has two advisories (see `Directory.Packages.props`). |
| `Microsoft.SqlServer.Dacpacs.Master` | Microsoft Software License Terms | tests | Microsoft's `master.dacpac`, to show the hand-written stand-in in `tests/dacpacs/master-shape` has its shape. Its terms allow use "to develop and test your applications" and not redistribution, so it is only restored, never committed or copied. |

These three are used only by the differential tests, and nothing in them is shipped. Their transitive
packages are MIT, with one exception: `Microsoft.Data.SqlClient.SNI.runtime`, SqlClient's native network
layer for Windows, is under the Microsoft Software License Terms. They allow it to be used freely "to develop
and test your applications", which is all the tests do; like DacFx's, they ask anyone who *redistributes* it to
indemnify Microsoft. So it must never reach anything haruspex ships: the shipped projects under `src/` must
not reference SqlClient.

`mssql` and `@types/mssql` are development dependencies of the emit check and the example, and nothing in them is shipped; the
generated functions import `mssql`, which an application that uses them installs itself.
The 75 npm packages `mssql` brings (76 with its types) are MIT, BSD-3-Clause, ISC or 0BSD; most are tedious's support for
signing in with Microsoft Entra ID (`@azure/identity` and its libraries), which the tests never use.

`ajv` and `ajv-formats` are development dependencies of the emit check too, and nothing in them is shipped. They bring four
packages: `fast-deep-equal`, `json-schema-traverse` and `require-from-string`, under the MIT licence, and `fast-uri`, under
BSD-3-Clause.

The prose check's packages run only in development and CI, and nothing in them is shipped. Most of the 259 npm
packages they bring are MIT, ISC, BSD or Apache-2.0; the others are permissive too (MIT-0, CC0, Unlicense,
BlueOak, WTFPL, MPL-2.0 or Apache-2.0 for DOMPurify), except two that are not ordinary code licences: cspell's
dictionary of common misspellings is CC BY-SA 4.0, and `elkjs`, a layout engine Mermaid includes but the check
never calls, is EPL-2.0. Both would matter only to someone redistributing them, which this repository does not.

The corpora are data, not dependencies. The fetched ones are never in the repository: each is under its own licence, named
in `tests/corpus/fetched.json`, and only counts made from it are committed. Every file in the repository that isn't
haruspex's own is in this list, with its licence beside it and a record of where it came from. A new one is added to the list
with both.

| Where | What | Licence | Where it came from |
|---|---|---|---|
| `tests/corpus/sql-server-samples` | Two of Microsoft's sample databases, the vendored corpora | MIT, `license.txt` beside them | `tests/corpus/manifest.json`: the commit and each file's hash ([its README](tests/corpus/README.md)) |
| `tests/fixtures/adventure-works`, `tests/fixtures/wide-world-importers` | A few tables of those samples, which the fixtures deploy | MIT, `license.txt` in each | each table's script names the sample's file and what was left out |
| `tests/dacpacs/corpus` | Two DACPACs built from WideWorldImporters, which hold its scripts | MIT, `license.txt` beside them | `build.sh corpus` and `build.sh ssdt` ([its README](tests/dacpacs/README.md)) |
| `tests/sarif` | The SARIF 2.1.0 schema the SARIF output is tested against | MIT, `LICENSE` beside it | [its README](tests/sarif/README.md): the commit, the path and the hash |
| `packaging/licenses` | The licence texts of the libraries the binary ships | each its own | [its README](packaging/licenses/README.md): the commit of each |

Nothing builds or ships any of them, except the licence texts, which go with the binary.

ScriptDom (MIT) is the only SQL dependency. **DacFx must not be added**: its licence requires anyone who
redistributes it to indemnify Microsoft, which no maintainer of a free tool should take on. DACPACs will
be read directly from their `model.xml`. Snapshot tests use a small helper in the repository rather than a
snapshot library; `tests/Haruspex.Tests/Snapshotting/README.md` says why.

## Review of agent and CI configuration

`.github/CODEOWNERS` names the maintainer as the owner of `.claude/`, `.github/`, `AGENTS.md` and `CLAUDE.md`, so GitHub
asks for the maintainer's review on a pull request that changes them. It asks; no setting requires it yet, so the review is
the maintainer's rule rather than GitHub's. Agent instructions and hooks run on contributors' machines, and workflows run in CI with
the repository's permissions, so a change to them is a change to what runs on other people's computers.

## Licence

By contributing you agree that your contribution is licensed under the [Apache License 2.0](LICENSE).
