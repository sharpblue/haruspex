# Configuration: `haruspex.json`

A project's settings live in `haruspex.json`. `haruspex generate` and `haruspex check` look for it in the working directory
and then in each folder above it, or use the files `--config` names, or every one below a folder with `--all`: each file is a
module of its own. Paths in it are relative to the file. It is JSON with comments and trailing commas, as `tsconfig.json` is.
Its JSON Schema is published at [`schemas/haruspex.schema.json`](../schemas/haruspex.schema.json); name it in `$schema` and
editors complete and check the file as you type. `haruspex init` writes a starter file, naming it
([the command line](command-line.md#init)) at the address the build you run names. A release names the schema at its own tag,
`https://haruspex.tools/schemas/v<its version>/haruspex.schema.json`, which holds exactly the settings that
release reads. Every other build, a preview's included, names it on `main`,
`https://haruspex.tools/schemas/main/haruspex.schema.json`, which moves: it may offer a setting newer than the
haruspex you run, which that haruspex refuses as an unknown key. Whichever you name, haruspex reads `$schema` only as text; the
schema file's own `$id` is its address on `main`.

**An unknown key is an error**, with its path and line. It is never ignored, because a misspelt setting would
otherwise silently do nothing.

The page is in two parts. The **essentials**, from [reading a folder of scripts](#reading-a-folder-of-scripts-source) to
[the settings you probably want](#the-settings-you-probably-want), are what every project sets: where the database is, which
procedures are typed, the application's query files, what is written and what `check` fails on. Read alone and in order, they
give a working file. The [advanced settings](#advanced-settings) are what some projects need, and each of their sections opens
with the situation it is for. A file of the essentials alone:

```jsonc
{
  "$schema": "https://haruspex.tools/schemas/main/haruspex.schema.json",
  "source": "database",                     // the folder of .sql files, a .sqlproj, or a .dacpac
  "out": "src/db",                          // where haruspex.ir.json and what "emit" names go
  "queries": "sql",                         // the application's query files; leave it out when there are none
  "emit": ["zod", "mssql"],                 // the schemas and types, and a function for each procedure and query file
  "check": { "unknowns": "report" },        // or "fail", to fail the check on any unknown
  "schemas": {                              // keyed by the schema's name as it is
    "sales": { "include": ["get_*", "list_*"], "exclude": ["*_internal"] }
  }
}
```

The command line overrides the file for one run: the source argument (a folder, a `.sqlproj` or a `.dacpac`, or several folders) replaces `source`, `--out` replaces `out`, `--baseline` replaces `baseline`, and
`--include schema.pattern` replaces the schemas' includes (each schema's `exclude` still applies, and overrides of
procedures the run leaves out are skipped). Without a file, the source argument, `--include` and `--out` are required.

Keep `out` out of every formatter: add it to `.prettierignore`, or to Biome's `files.includes` as an exclusion after `**` (`!src/db`, for an `out` of `src/db`). Otherwise a formatter (Prettier, Biome, an editor's format on save) restyles the generated files, and `haruspex check` then fails, since it compares each file with what haruspex writes
([HAR6015](diagnostics/HAR6015.md)).

## Reading a folder of scripts: `source`

`source` as a folder reads every `.sql` file below it, by the pattern a database project's build lists its default items with
(`**/*.sql`), but without the build's exclusions: the extension matched ignoring case, in every folder below, hidden folders,
`node_modules`, `bin` and `obj` included. A link
to a folder elsewhere is followed, and one back to a folder already read isn't, so each script is read once. Nothing is left
out, so name the database's own folder rather than the application's: a folder holding both would read the application's
query files, its installed packages and a copy of the repository kept inside it as the database's scripts, and an object
defined twice is an error ([HAR1002](diagnostics/HAR1002.md)). [`haruspex init`](command-line.md#init) chooses such a folder
for you. To leave scripts out of a folder, read it as a [project file](#reading-a-project-file-source-as-a-sqlproj), whose
build says which scripts it reads, or [list the folders](#several-folders-source-as-a-list) that hold the database's scripts.

## Reading a DACPAC: `source` and `references`

`source` may name a `.dacpac` instead of a folder. The package's `model.xml` is then read, without DacFx, with every package
of the same database it refers to, directly or through another, into one model
([decision 0040](decisions/0040-a-dacpac-is-read-with-the-packages-of-its-database.md)); another database's package, or
`master.dacpac`, is read into a model of its own, so that a three-part name in a body reads its tables
([decision 0042](decisions/0042-a-table-of-another-database-is-read-as-its-package-declares-it.md)). A database on another server
isn't read ([HAR1012](diagnostics/HAR1012.md)). The IR records the package and each reference, with the SHA-256 of the file read
for it (`source.package`, `source.references`).

A referenced package is looked for beside the package that refers to it, where a build leaves it; then in each folder
`references` lists, in order, relative to this file; and last at the path the package recorded when it was built, unless that
path names a share or a device (`\\server\share\…`), which is never looked in. One found
nowhere is a warning ([HAR1003](diagnostics/HAR1003.md)) that lists every place looked, and the run goes on: whatever would
have come from it is unknown, and each such unknown says the package it may be in.

A package that DacFx's build task did not make claims no key constraint, unless
[`builds`](#a-package-built-by-another-tool-builds) declares it built from a project; an extract from a database claims none.

Locations in what is read from a package name the package and the object, with a line in the object's own text:
`sales.dacpac/[sales].[get_order]:3:5` ([decision 0039](decisions/0039-a-location-in-a-dacpac-names-the-package-and-the-object.md)).

## Reading a project file: `source` as a `.sqlproj`

`source` may name a database project's file, `.sqlproj`, which is read as its build would read it, with no MSBuild, no DacFx
and no build: the scripts the build reads, each with the settings it starts with, and the database's settings. Both kinds of
project are read. A `Microsoft.Build.Sql` project's build reads every `.sql` file under its folder (but `bin` and `obj`), less
what `Build Remove`, `None`, the pre- and post-deployment scripts and the files they include with `:r` take out, plus any
`Build` item outside the folder; a `Content` item neither adds a script nor takes one out. A legacy SSDT project's build reads
the `Build` items it lists. A script starts with its item's `AnsiNulls` and `QuotedIdentifier`, or else the project's.

The pre- and post-deployment scripts define nothing, but a deployment runs them every time, so they are read for what they
change, with each file they include by `:r`, as a DACPAC's `predeploy.sql` and `postdeploy.sql` are: a check one turns off is
claimed no longer, a table one alters, drops or creates is unknown ([HAR2006](diagnostics/HAR2006.md)), and a procedure,
function or view one creates, alters or drops is `redefined` ([HAR2003](diagnostics/HAR2003.md)). One that can't be read in
full, such as a script with another sqlcmd command or an include through a variable, is a warning
([HAR1021](diagnostics/HAR1021.md)), and every object whose name it holds is unknown.

The database's settings are the project's properties (`DefaultCollation`, `CompatibilityMode` or the `DSP`'s version,
`AnsiNulls`, `QuotedIdentifier` and the other options), with the values a build gives one the project doesn't set: the
collation `SQL_Latin1_General_CP1_CI_AS`, every option on but `NUMERIC_ROUNDABORT`. So a project gives the collation a folder
lacks, and the value sets it decides ("The database's collation", below).

Each project of the same database it refers to (a `ProjectReference` naming no other database) is read too, from its own
`.sqlproj` and with its scripts' own settings, as a DACPAC's packages are; one that isn't there is a warning
([HAR1007](diagnostics/HAR1007.md)). What else it refers to is read as a DACPAC's references are, from where its build takes each
([decision 0045](decisions/0045-a-project-s-references-are-read-from-where-its-build-takes-them.md)):

- another database's project (`DatabaseSqlCmdVariable` or `DatabaseVariableLiteralValue`), from its own `.sqlproj`, into a model
  of its own under the name a body writes it by (`[$(VaultDb)]`, `almanac`);
- a DACPAC (`ArtifactReference`), at the path its `Include` gives, of this database or, when it names one, another;
- the DACPAC in a NuGet package (`PackageReference`, in a `Microsoft.Build.Sql` project), such as `master.dacpac` in
  `Microsoft.SqlServer.Dacpacs.Master`: `tools/<name>.dacpac` where a restore of the version the project names put it, in
  `NUGET_PACKAGES`, `.nuget/packages` in your home, or the project's `RestorePackagesPath`. A version that is a range or a
  floating one (`170.*`) isn't read, and is reported, since only a restore against a feed settles it.

A DACPAC not where its build takes it, such as one in a package not restored, or Visual Studio's own `master.dacpac`
(`$(DacPacRootPath)\…\SqlSchemas\master.dacpac`, which only a Visual Studio install has), is looked for by its file name in each
folder `references` lists. A `master.dacpac` found there stands for Visual Studio's: haruspex doesn't check that it is the version
the path names (the `150` in `SqlServer\150`), and the IR records the hash of the file it read. One found nowhere is a warning ([HAR1003](diagnostics/HAR1003.md)) naming where it was looked for. The
IR records each package read, with its SHA-256, in `source.references`, and no `source.package`: the project's own scripts aren't
hashed.

A project file is MSBuild, and haruspex evaluates the part of it a database project uses: properties, conditions on the
configuration, items and their patterns, the nearest `Directory.Build.props`, and the files it imports
([decision 0044](decisions/0044-a-project-file-is-evaluated-in-part-and-the-rest-reported.md)). It is read as `Debug`, as
`dotnet build` builds it, and a project that builds differently as `Release` says so. Whatever isn't evaluated (a property
function, a property defined nowhere, `Choose`, an import it can't find) is never guessed: when it touches a script or a
setting, that script or setting isn't read, and a warning ([HAR1008](diagnostics/HAR1008.md)) says what, where and why.
Locations are relative to the project's folder (`../shared/audit.sql:3:5` for a script beside it); an object read from a DACPAC
is located in it, as when the DACPAC is the source.

## Which procedures are typed: `schemas`

Settings are per schema. Each key of `schemas` is a schema's name exactly as in the database: no brackets, dots
allowed (`"Ledger."`). Its block holds:

- `include` and `exclude`: patterns matching the schema's procedures by their **own names**, ignoring case. `*` matches
  any characters, `?` one; brackets make what they hold literal (`[a*b]`). `exclude` wins over `include`.
- `prefix`: put in front of the names of the schema's objects (see Naming). Empty by default.
- `naming`: the schema's own naming rules, merged over the top-level ones: its `case` replaces the top-level case, and
  its `replace` rules run after the top-level ones.

**No procedure is selected unless its schema has a block with `include`**: typing every procedure is something you ask for
(`"include": ["*"]` per schema), never the default. Whatever matches nothing is an error: a schema key that names no
schema of the project, an include that selects nothing in its schema (a typo would otherwise drop an interface without
anyone noticing), an exclude that removes nothing its include selected, an exclude in a block without an include, and a
block whose exclude removes everything its include selects. A one-off `--include` whose every match a schema's
exclude leaves out is an error too: a run that would type nothing fails rather than writing an empty module.

A block is an object so that settings can be added to it later without breaking a file written today.

## Query files: `queries`

An application that keeps queries in `.sql` files of its own, rather than in procedures, names them in `queries`: a folder (every
`.sql` file below it), a file, or a pattern, relative to the config file (`*` is any characters but `/`, `**` any folders, `?` one
character), or an array of them. Each file is typed with the procedures, against the same project, and is one object of the
module, named from its file by the naming rules: `get_order_card.sql` is named GetOrderCard, and under `"case": "camel"`
`getOrderCard.sql` keeps its name, getOrderCard. Its id in the IR is its path relative to the config file's folder.

```jsonc
{
  "source": "database",
  "out": "src/db",
  "queries": ["sql", "reports/**/*.sql"],
  "schemas": { "sales": { "include": ["get_*"] } }   // optional: query files alone are a selection too
}
```

A query file is one batch, run through `sp_executesql` as it is written, with no `GO`
([HAR1014](diagnostics/HAR1014.md)). Its parameters are the variables it uses without declaring. Each is typed from its uses,
as SQL Server's own `sp_describe_undeclared_parameters` types it: compared with a column, `where o.order_id = @order_id`, it takes
the column's type, `int`; passed to a procedure, the procedure's parameter's type; and so on
([the rules](../src/Haruspex.SqlServer.Inference/README.md#query-files)). Where nothing in the file decides a parameter's type,
or two uses decide differently, it is unknown ([HAR2024](diagnostics/HAR2024.md)), and a comment in the file declares it, one
to a line, with the type written as `DECLARE` writes it:

```sql
-- @param @label nvarchar(40)
-- @param @since datetime2(0)
SELECT @label AS label, o.order_id
FROM sales.orders AS o
WHERE o.placed >= @since;
```

A declaration that can't be taken, one that names a variable the file doesn't use, say, is an error
([HAR1015](diagnostics/HAR1015.md)). [Decision 0059](decisions/0059-a-query-file-s-parameters-are-typed-from-their-uses.md) says
why parameters are typed this way.

**A query file belongs to the module whose config file's folder holds it.** An entry can't reach above the config file
([HAR3022](diagnostics/HAR3022.md)), and a folder below with a `haruspex.json` of its own is that module's, so its files are
passed over; an entry that names no file of this module is an error ([HAR4007](diagnostics/HAR4007.md)), as an include that
selects nothing is. A query file's name and a procedure's share the module's names: one that clashes is an error naming both
([HAR4303](diagnostics/HAR4303.md)), and neither is renamed for the other.

A comment in the file names its result sets, as `sets` names a procedure's (under "Overrides"), in order and between commas:

```sql
-- @sets order, lines
SELECT o.order_id, o.placed FROM sales.orders AS o WHERE o.order_id = @order_id;
SELECT l.sku, l.quantity FROM sales.order_lines AS l WHERE l.order_id = @order_id;
```

Its function then returns `{ order, lines }`, even for one set, and its types are named after the sets. The rules are the
same as for `sets`: every set or none, each name in camel case, given once, not a reserved word, and neither `output` nor
`returnValue`; each problem is reported at the comment ([HAR1018](diagnostics/HAR1018.md) for a comment that doesn't read,
[HAR3023](diagnostics/HAR3023.md) and [HAR4112](diagnostics/HAR4112.md) for its names). A shape a query file's type shares with
another is declared once, as a procedure's is
([decision 0071](decisions/0071-a-shared-shape-is-named-by-declaration-or-by-its-structure.md)).

## What is written: `emit`

`generate` writes the IR, `haruspex.ir.json`, to the output folder, and beside it what `emit` names, each made from the IR
alone:

- `"zod"`, the default: `haruspex.zod.ts`, a TypeScript module of Zod schemas and their types
  ([the Zod emitter](../src/Haruspex.Emit.Zod/README.md)).
- `"json-schema"`: `haruspex.jsonschema.json`, one JSON Schema document (draft 2020-12), for any language with a JSON Schema
  validator. Each procedure's results are the definition under its name in `$defs`, `#/$defs/GetOrder`, the same name the Zod
  module exports; every value is described as SQL Server's `FOR JSON` writes it, a rowset's rows as an array of objects as
  `FOR JSON PATH, INCLUDE_NULL_VALUES` writes them
  ([the JSON Schema emitter](../src/Haruspex.Emit.JsonSchema/README.md),
  [decision 0051](decisions/0051-one-json-schema-document-per-module-every-value-as-json.md)).

- `"mssql"`: `haruspex.mssql.ts`, the database module's functions, one for each procedure and query file, which check its
  parameters, run it through the mssql driver and check what comes back ([the mssql emitter](../src/Haruspex.Emit.Mssql/README.md)). They import
  the schemas and types from `haruspex.zod.ts`, so `"mssql"` needs `"zod"` beside it.
- `"mapping"`: `haruspex.mapping.ts`, TypeScript types with which the application's own mapper accounts for every key of a
  generated row, using it or ignoring it with a reason, so that once the module is regenerated, a key the row gains, at any
  depth, is a compile error in the mapper ([the layers](layers.md#a-mapper-that-accounts-for-every-key),
  [decision 0066](decisions/0066-a-mapper-accounts-for-every-key-of-a-generated-row.md)). It is types only, the same for every
  module, and works on the types `haruspex.zod.ts` declares, so `"mapping"` needs `"zod"` beside it.

```jsonc
"emit": ["zod", "json-schema"]   // both, each from the same IR
```

```jsonc
"emit": ["zod", "mssql"]   // the schemas, and the functions that use them
```

**The functions belong on the server.** They take a connection, and a connection holds the database's credentials; and the
database module is two files so that the schemas and types, which a browser may use, stay apart from the functions, which it
must not. In a Next.js application they are for route handlers, server actions and server components. `"serverOnly": true`
starts `haruspex.mssql.ts` with `import 'server-only'`, so that a build fails where client code imports it (install the
`server-only` package); it needs `"mssql"` in `emit`, and is off by default, since haruspex assumes no framework. Tests that
import the functions then need `server-only` resolved as Next.js resolves it, or replaced by an empty module: the database
module's page has [the Vitest configuration for each](database-module.md#where-the-functions-run).

`emit` lists at least one, each once. `check` compares each file it names with the one in the output folder, and fails when one is
missing. The file of an emitter taken off the list is removed by the next `generate`
([below](#the-files-generate-removes)).

## The files generate removes

`generate` writes the list of the files it wrote, `haruspex.files.json`, beside them in the output folder, and removes each file
the last run's list names that this run doesn't write: a module taken out of `split`, or one a baseline leaves with no
procedures, loses its files, and so does an emitter taken off `emit`. Taking the module `admin` out of the split removes
`admin.zod.ts`, `admin.jsonschema.json` and `admin.mssql.ts` on the next run, and nothing else; the run says which files it
removed. Commit the list with the other files: it is generated, and `check` compares it as it compares them
([decision 0091](decisions/0091-generate-removes-only-the-files-its-list-names.md)).

```json
{
  "about": "The files haruspex generate wrote to this folder, which a later run removes when it no longer writes them. Generated: do not edit.",
  "files": [
    "admin.jsonschema.json",
    "admin.mssql.ts",
    "admin.zod.ts",
    "haruspex.ir.json",
    "sales.jsonschema.json",
    "sales.mssql.ts",
    "sales.zod.ts"
  ]
}
```

- **Only what the list names is removed.** The output folder may hold files of your own: `generate` never looks for files to
  remove, so a file it didn't write is never removed, whatever its name.
- **A file edited by hand is removed** when the list names it and the run no longer writes it. Each generated file says it is
  generated: a comment at the top of the TypeScript files, the JSON Schema's `description`, the list's `about`.
- **Nothing is removed** by a run that fails before writing, by the first run that writes a list (after upgrading, a module
  removed before then leaves its files: remove them yourself), or when the list can't be read ([HAR6039](diagnostics/HAR6039.md)).
- **A run narrowed by `--include` removes nothing.** It is a look at part of the module, so the files it doesn't write stay, and
  stay on the list, for the next full run to write again or remove. `check` reports them meanwhile, since the IR it wrote is the
  narrowed one.
- **Nothing outside the folder.** An entry that is absolute, climbs out with `..`, has a character no name `generate` writes has
  (it writes ASCII letters, digits, `.`, `-` and `_`), or is reached through a symbolic link is never removed, with a warning ([HAR6040](diagnostics/HAR6040.md)); `generate` writes no such entry, so the list was edited. No
  folder is ever removed, even one a removed file leaves empty.
- **Case.** On a file system that ignores case, as macOS's and Windows' do by default, a module renamed only by case keeps the
  file the run wrote, under the module's new name: the old file is renamed as it is replaced, so the names on disk are the
  ones the imports and the list say. Git there (`core.ignoreCase`, its default on such a file system) still stages each file
  under its old name, so commit the rename with `git mv` (to another name and then to the new one), or a checkout on a file
  system that tells case apart fails `check` ([HAR6011](diagnostics/HAR6011.md)).
- **`check`** fails at each file the next `generate` would remove ([HAR6042](diagnostics/HAR6042.md)), and when the list is
  missing, as for any other file.

## What check fails on: `check`

`haruspex check` fails when what the module claims would change, when the output folder holds nothing to compare with, and when a selected
procedure's result sets are unknown and not declared (the CLI's README says each). An unknown value is a note, as in `generate`,
and doesn't fail it, since the module still checks it at runtime as a value of any shape. For a module that must be typed
throughout:

```jsonc
"check": { "unknowns": "fail" }   // any unknown fails the check (HAR6017); "report", the default, notes each
```

`unknowns` is `"report"`, the default, or `"fail"`.

## Errors

Every error and warning is written to standard error as a compiler writes one, so editors and CI can jump to it, with its
code, whose page in [the list of diagnostics](diagnostics/README.md) says what it means and what to do. (Notes, one for each
unknown, are counted rather than listed unless `--verbose` asks; [the command line](command-line.md) says what each run prints,
and how to have the same as JSON, SARIF or GitHub annotations.)

```text
haruspex.json:14:9: error HAR4104: procedures["sales.get_order"].paths["lines[].skew"]: the path results[0].lines[].skew does not exist: the object at results[0].lines[] has no key "skew". Its keys are "sku", "quantity".
haruspex.json:22:7: warning HAR4110: procedures["sales.get_order"].paths.id: "nullable": false agrees with the inference, which already proves the value at results[0].id is never null. Remove it.
haruspex.json: error HAR4303: "GetOrder" would name both the procedure audit.get_order and the procedure sales.get_order. Generated names must not clash: give one of their schemas a "prefix" in "schemas", …
```

Errors in the config file exit with code 2, as command line mistakes do, and nothing is written; [the command
line](../src/Haruspex.Cli/README.md#what-it-guarantees) lists every exit code.

## What an editor can't check

The schema an editor checks the file with refuses every setting of the wrong form, but some rules can't be written in a JSON
Schema, or depend on the files beside the config file, so an editor shows these as valid and `haruspex generate` refuses them:

- **A key written twice in one object**, which a JSON parser keeps the last of.
- **A procedure named in other than two parts**, `schema.name`, in `procedures`.
- **A pattern, a path or a regular expression that is not well formed**: a pattern or a path with a bracket left open, a
  naming rule's expression that doesn't compile, an endpoint's URL with a `{` left open.
- **A name that is a reserved word** in the generated code: a type's, or a set's.
- **Two names that differ only in case**: two schemas' blocks, or two modules, since a file system that ignores case would
  hold one file for both.
- **A schema in two modules**, compared ignoring case, as SQL Server compares schema names.
- **A warning acknowledged twice**: one code and subject with two reasons.
- **An endpoint declared twice**: one method and URL with two responses.
- **A collation SQL Server does not have**, or one no database can have.
- **A file or folder that is not there**: a folder of `references`, a JSON Schema file, or a `$ref` in one.
- **A declared JSON Schema haruspex can't type** ([what a declaration reads](#the-json-schemas-a-declaration-reads)).

Nor can an editor check what depends on the database: a pattern that matches no procedure, an override's path that its results
don't have. `generate` and `check` report each with its line.

## The settings you probably want

`haruspex init` writes `source`, `out` and `schemas`, and with those alone haruspex claims only what holds whatever the
callers and the build are. Most projects meet one or more of these situations, where a setting lets it claim more, or read what
it otherwise can't. Each is a statement about your project that haruspex can't see for itself, so it is yours to make.

- **The application calls through a driver that sets its session**: declare [`session`](#the-callers-session-session),
  whose section says what each driver sets, and haruspex claims what the declared options allow.
- **The callers hold `UNMASK`**, and the database masks columns: declare `"session": { "unmask": true }`
  ([the callers' session](#the-callers-session-session)), and masked columns claim more than their type.
- **The source is a DACPAC built by MSBuild.Sdk.SqlProj**, or by any tool that builds through DacFx's API rather than its
  build task: name it in [`builds`](#a-package-built-by-another-tool-builds), and its key
  constraints are claimed.
- **The source is a folder of `.sql` files** and a table has a `CHECK` that lists its values: declare the database's
  [`collation`](#the-databases-collation-collation). A folder doesn't record it, and without it such a set of strings is a
  comment, not an enum.
- **The database is kept as folders side by side** (`tables`, `procedures`) beside folders that aren't its own, such as the
  application's query files: list them as [`source`](#several-folders-source-as-a-list). A folder that holds them all would
  read the others as the database's scripts too.
- **The database keeps its descriptions under a name of its own**, such as `Description` rather than SQL Server's
  `MS_Description`: name it in [`descriptions`](#descriptions-descriptions), and each column's description reaches the fields
  that are that column.
- **The source refers to other DACPACs** that its build doesn't leave beside it: list their folders in
  [`references`](#reading-a-dacpac-source-and-references). One found nowhere is a warning, and what would have come from it is
  unknown.
- **The application keeps SQL in files of its own**: name them in [`queries`](#query-files-queries), and each is typed
  with the procedures, against the same project.
- **The application calls the database through the `mssql` package**: add `"mssql"` to [`emit`](#what-is-written-emit), and
  each procedure and query file gets a function that checks its parameters, runs it and checks what comes back.
- **The application and the database deploy separately**: name the database as last released in
  [`baseline`](#the-baseline-baseline), so the types work against both while one is ahead of the other, and `check` fails on
  a change the running application would fail on.

## Advanced settings

The sections from here on are for some projects, not every one. Each opens with the situation it is for, so that a reader
can tell from its first lines whether it applies. A file that uses every setting, the essentials included:

```jsonc
{
  "$schema": "https://haruspex.tools/schemas/main/haruspex.schema.json",
  "source": "database",                     // the folder of .sql files, a .sqlproj, or a .dacpac; or a list of folders
  "baseline": "release/database",           // the database before the change being made; see "The baseline"
  "references": ["../shared/bin/Release"],  // more folders to find the DACPACs a .dacpac or .sqlproj refers to in
  "collation": "Latin1_General_100_CI_AS",  // the database's default, for a folder of .sql files; see "The database's collation"
  "descriptions": ["MS_Description"],       // the extended properties that describe; see "Descriptions"
  "queries": "sql",                         // the application's query files: a folder, files or patterns; see "Query files"
  "out": "src/db",                          // where haruspex.ir.json and what "emit" names go
  "split": "schema",                        // a module per schema; see "Splitting the output into modules"

  "emit": ["zod", "mssql"],                 // and "json-schema", "mapping"; see "What is written"
  "serverOnly": false,                      // with "mssql", true: the functions import 'server-only'; see "What is written"
  "objects": "strip",                       // or "strict"
  "comments": "surprises",                  // or "none", "all"; see "Comments"

  "session": { "ansiWarnings": true },      // what the callers' connections set; see "The callers' session"

  "check": { "unknowns": "report" },        // or "fail"; see "What check fails on"

  "acknowledge": [                          // warnings read and lived with; see "Warnings acknowledged"
    { "code": "HAR1008", "subject": "StampBuildInfo", "why": "it stamps the build's number and adds no scripts" }
  ],

  "naming": {                               // for every schema
    "replace": [{ "pattern": "#$", "with": "Row" }],
    "case": "pascal"                        // or "camel", "preserve"
  },

  "schemas": {                              // keyed by the schema's name as it is
    "sales": { "include": ["get_*", "list_*"], "exclude": ["*_internal"] },
    "audit": { "include": ["*"], "prefix": "Audit" },                          // AuditGetOrder
    "Ledger.": { "include": ["GetEntryList"], "naming": { "case": "preserve" } }
  },

  "procedures": {
    "sales.get_order": {
      "name": "OrderDetail",
      "paths": {
        "lines[]": { "name": "OrderLine" },
        "customer.email": { "nullable": false, "why": "checkout requires an email" },
        "status": { "values": "exact", "why": "the application writes lower case only" },
        "settings": { "type": "OrderSettingsSchema", "import": "@app/orders/settings" }   // see "type" below
      }
    },
    "sales.get_order_with_lines": { "sets": ["order", "lines"] },   // returns { order, lines }; see "sets" below
    "sales.search_orders": {                // dynamic SQL: see "Declared results" below
      "results": { "rowset": "schemas/order-search-row.schema.json" },
      "why": "the filter is built as dynamic SQL"
    },
    "audit.write_log": { "results": "none", "why": "it only writes the log" }
  }
}
```

## Several folders: `source` as a list

**For a database kept as folders side by side**, beside folders that are not its own, such as the application's query
files. Such a database is read by listing its folders:

```jsonc
{
  "source": ["tables", "procedures"],            // read together as one database; "queries" beside them is not read
  "queries": "queries",
}
```

Every `.sql` file below each folder listed is read, as for one folder, and the folders together are one database: a procedure
in `procedures` reads a table in `tables`. Use a list when the folder that holds all the database's scripts would hold others too,
such as the application's query files; when one folder holds the database alone, name it as one path. The order of the list
doesn't matter, and a list of one folder is that folder.

A list names folders only: a `.sqlproj` or a `.dacpac` in it is an error ([HAR3005](diagnostics/HAR3005.md)), since each is a
whole database of its own. Each script is read once, so the same folder listed twice, in any spelling the file system takes for
the same (`db` and `./db`, or `Tables/` and `tables/` where the file system ignores case), one folder inside another, or two
folders that reach one folder through a link, is an error naming both ([HAR1023](diagnostics/HAR1023.md)). Case is
judged by the folders' own listings, so a Windows drive read from Linux, which ignores case, is judged as Windows would.
A drive that is another folder under a second name (`subst`, a mapped drive) isn't known to be that folder: listed under both
names, its scripts are read twice, and haruspex may not say so, so list each folder by one name.

The IR records the folders read, each by its name on disk relative to this file's folder, in `source.paths` (so writing the list
in another case, where the file system ignores case, changes nothing), and a location in it, such as an
object's `at`, is relative to this file's folder too, so that it says which folder its script is in (`procedures/weigh.sql:1:1`).
A document of a list is IR 0.32 (0.33 when it records a description too, 0.34 when it holds an XML value, 0.35 when it holds a fact an XML schema collection proves); one of one folder is written as before. `check` reports a folder added to the list or removed
from it, even where no type changes, since what the database is made of is what every claim rests on
([decision 0127](decisions/0127-a-source-may-be-several-folders.md)).

## A package built by another tool: `builds`

**For a DACPAC built by MSBuild.Sdk.SqlProj**, or by any other tool that builds through DacFx's API rather than its build
task.

A package extracted from a database (SqlPackage's `/Action:Extract`) differs from a built one in its keys too: an extract
doesn't record whether a primary key's or unique constraint's index is disabled, which lets the table hold duplicates, so no
key constraint of an extract is claimed, and a query that fixes its columns isn't claimed to return at most one row
([decision 0120](decisions/0120-an-extract-s-key-constraints-are-no-key.md)). A unique index created apart is claimed unless the
extract marks it disabled. A package is taken for a build only when its `Origin.xml` says DacFx's build task
(`Microsoft.Build.Sql`'s or SSDT's) made it, so one written by DacFx's API, as community SDKs build, claims no key constraint
either. `builds` lists the packages, by file name (`"builds": ["sales.dacpac"]`), that you declare built from a project:
their key constraints are then claimed, on your word that every one is enforced. The IR records each package read as one
(`source.builds`), so a generated file says what it assumed, and `check` fails when the declaration changes, naming the package
([decision 0122](decisions/0122-the-ir-records-the-packages-declared-builds.md)).

## The database's collation: `collation`

**For a folder of `.sql` files** whose tables have a `CHECK` constraint that lists their values.

A column without `COLLATE` takes the database's default collation. A `.dacpac` records it, and a `.sqlproj` gives it; a folder
of scripts doesn't, so `collation` declares it. A package's or a project's own wins, and one that disagrees with `collation` is
a warning ([HAR6010](diagnostics/HAR6010.md)). The name must be one SQL Server 2025 has, as `sys.fn_helpcollations()` lists
them, in any case, and not one of Unicode only (such as `Indic_General_100_CI_AS`), which no database can have; any other is
an error at the setting ([HAR3005](diagnostics/HAR3005.md)). A name haruspex can't place
is never read as some other collation's, which a misspelt `_UTF8` would otherwise be: the lengths of `char` and `varchar`
depend on the collation's encoding.

The collation decides how a `CHECK` constraint's set of strings is typed. For a `char` or `varchar` column under a collation
haruspex has proven (`SQL_Latin1_General_CP1_CI_AS`, `Latin1_General_CI_AS`, `Latin1_General_100_CI_AS`,
`Latin1_General_100_CS_AS`, `Latin1_General_100_CI_AI`), a set of plain strings (ASCII letters and digits, space, `_`, `-`,
`.`), written without `N`, is an enum: a value is first mapped to the string it equals under the collation, so `'PAID'` and `'paid '` both arrive as
`"paid"` ([decision 0043](decisions/0043-a-value-set-is-canonical-by-a-fold-observed-from-the-server.md)). Any other set, and
every set of an `nchar` or `nvarchar` column, is a comment saying the values match as the database compares strings; an
override can still declare it exact (`"values": "exact"`).

## Descriptions: `descriptions`

**For a database that keeps its descriptions under a name of its own**, rather than SQL Server's `MS_Description`, or
whose descriptions are not to be read.

A database may describe its tables, views, procedures, columns and parameters in extended properties
(`sp_addextendedproperty`, which SQL Server Management Studio and SSDT's table designer write, and which a DACPAC holds).
`descriptions` names the properties that hold descriptions: `["MS_Description"]` when it is left out, compared ignoring case.
A project that writes another name lists it, `["MS_Description", "Description"]`; an empty array reads none.

A description reaches only what is exactly the thing described: a result's field that is one column unchanged (`d.name`, or the
column through `*`, a view or a derived table, where a view's own column description replaces the table's), a procedure's
parameter, and the procedure itself. A field computed from a column, such as `UPPER(d.name)`, or read from several, has none. In
the Zod module a description is the property's `.describe("…")`, so `z.toJSONSchema` and an editor show it, and the first
lines of its comment; in JSON Schema it starts the `description`. A description whose arguments aren't literals, that names
nothing the source defines, or that is set twice or updated is passed over with a note ([HAR1024](diagnostics/HAR1024.md)).
A changed description changes no claim, but `check` still fails until the module is generated again, since its text changed
([decision 0128](decisions/0128-a-column-s-description-reaches-the-fields-that-are-that-column.md)).

## Overrides

**For what the database cannot prove and the team knows**: a value never null, a value set that holds exactly its items, the
application's own schema for a value; and for a procedure's names and the names of its result sets.

An override corrects or completes what was inferred, for one procedure. Overrides live in `procedures`, keyed by the
procedure's two-part name (`sales.get_order`, with T-SQL brackets around a part that holds a dot), which must be selected;
only `results`, below, may be given for any procedure. Overrides are applied to the IR after inference, never inside it, and every one stays **checked at
runtime**: the generated validator still checks every value, so a wrong override fails loudly on real data; it is never
a silent cast. The IR lists every claim an override made under `overrides`, with its reason.

- `name`: the procedure's generated name.
- `sets`: a name for each of its result sets, in order, such as `["order", "lines"]`. Its function then returns them by
  name, `{ order, lines }`, with `output` and `returnValue` beside them as before, **even when there is one set**; and the
  types in each set are named after it, `GetOrderLinesRow` rather than `GetOrderResult2Row` (a `name` under `paths` still
  wins). Name every set the procedure returns or none: more or fewer names than its sets is an error naming both counts
  ([HAR4112](diagnostics/HAR4112.md)), and so is naming sets that can't be listed until `results` declares them. Each name is
  in camel case (a lower-case ASCII letter, then letters and digits), given once, not a reserved word, and neither `output`
  nor `returnValue` ([HAR3023](diagnostics/HAR3023.md)). It can be given with `results`, naming the declared sets. Naming a
  procedure's sets is how a team says it will grow: a set the database adds after them is ignored until it is named
  ([decision 0063](decisions/0063-a-function-ignores-result-sets-after-those-it-knows.md)), and naming it then adds a key
  rather than changing what the function returns
  ([decision 0070](decisions/0070-a-procedure-s-result-sets-are-named-all-or-none.md)).
- `paths`: places in its results, in the IR's path syntax, relative to the result: `[]` for a list's rows, `.key` (the
  first dot may be left out) or `["a key"]`, `{}` for a record's values, `[0]` for a tuple's position, `<json>` for
  what JSON text holds. A procedure with several result sets names one: `results[1][].id`. Each path takes:
  - `name`: the name of the object type there. Types inside it build their names on it.
  - `nullable: false` with a required `why`: the value is never null, which the database doesn't prove. A key `FOR
    JSON` leaves out when null then becomes required.
  - `type` and `import`: the value is the application's own schema, `type` exported by the module `import`, with
    `[]` for each level of array (`"TagSchema[]"`). Any unknowns there are then declared, not reported. A relative
    `import` (`./`, `../`) is relative to this file, and is rewritten relative to `out`; a package or alias is kept as
    written. **The imported schema receives the value as it arrives.** At JSON text (a `json` or JSON-holding column
    returned in a rowset, a `FOR JSON` subquery `WITHOUT_ARRAY_WRAPPER`) that is the string, not what it parses to, so
    the schema must parse it itself, as `z.string().transform((t) => JSON.parse(t)).pipe(OrderSettings)` does; a plain
    `z.object(…)` there would reject every row. Inside live JSON (`FOR JSON`, `json_query`) it receives the parsed
    value.
  - `values: "exact"` with a required `why`: a value set that the database compares under its collation (where `'Open'`
    can pass a check for `'open'`) holds exactly its items, giving a `z.enum`. It needs a value set the IR already has,
    from a CHECK constraint on a column the value passes through unchanged.

**An override whose path doesn't exist is an error**, naming the keys that do. **One the inference already agrees
with is a warning**, so the file shrinks as the SQL improves. Generation goes ahead after a warning.

## Declared results: `results`

**For a procedure whose results the project cannot show**, such as one that runs dynamic SQL.

Some procedures' result sets can't be known from the project: dynamic SQL (`EXEC (@sql)`, `sp_executesql`), a procedure named
in a variable, a system procedure, a CLR procedure, one in another database or not in the project, a call without a schema
that names a procedure in more than one (which one runs depends on the caller's default schema), and a module created
`WITH ENCRYPTION`, whose text the project doesn't hold. Their results are unknown, and so are those of
every procedure that calls them, with a reason naming the statement. `generate` warns about each selected procedure whose
results are unknown for one of these reasons, naming the statement and what to declare; it still writes its output, and
exits as before (the `check` command will fail it).

`results` declares them, with a required `why`:

```jsonc
"procedures": {
  "audit.write_log": { "results": "none", "why": "it only writes the log" },
  "sales.search_orders": {
    "results": [
      { "rowset": { "type": "object", "properties": {
          "order_id": { "type": "integer", "format": "int32" },
          "note": { "type": ["string", "null"] } } } },
      { "json": "schemas/search-totals.schema.json", "mayBeAbsent": true }
    ],
    "why": "the filter is built as dynamic SQL"
  }
}
```

- `"none"`, one result, or an array of them in the order the procedure returns them. A result is `{ "rowset": schema }`, the
  JSON Schema of **one row** (an object whose `properties` are its columns, each a scalar and always there), or
  `{ "json": schema }`, the JSON Schema of the document `FOR JSON` writes, with `"mayBeAbsent": true` when it may match no
  rows (FOR JSON then returns no row at all); the generated schema then accepts `null`, and its summary names the declaration as
  the reason ("null when absent, as the config file declares"). A schema is written inline, or as the path of a file holding it, relative to
  this file; a problem in a file is reported in that file, with its line.
- **The schema keywords read** are those of [The JSON Schemas a declaration reads](#the-json-schemas-a-declaration-reads), below.
  A key of a JSON object that is not `required` may be left out but is never null, as `FOR JSON` writes it; one that may be both
  is an error.
- **Any procedure** can be declared: one that isn't selected, or isn't in the project. A call of it contributes the declared
  result sets to its caller, and the caller's IR lists that claim (an override with `via`). What it does to temporary tables
  is not known either way, so after a call of it they are unknown. A declaration that no selected procedure uses is a
  warning.
- **A declaration of results haruspex can infer** is an error when it disagrees, naming where, and a warning when it agrees,
  so it can be removed. They are compared as far as a declaration can say: how many result sets, each one's encoding and
  whether it may be absent, and in every value its kind, its keys, and whether it may be null or left out. To correct part of
  inferred results, use `paths` instead; `paths` and `results` can't be used together.
- **`INSERT … EXEC` staging.** A call whose results go into a temporary table (`INSERT INTO #t EXEC …`) and isn't followed
  (dynamic SQL, or a procedure running it) leaves every temporary table unknown, because it could change any of them. A
  declaration of the procedure called doesn't change that: it says what the procedure returns, not what it does to
  temporary tables. `generate` warns of each such call, naming it; to type the values read back from the table, override
  their paths under `paths` with `type` and `import`.
- **Declared results are checked at runtime**, like everything else: the generated validator rejects output that doesn't
  fit the declaration, so a wrong one fails loudly on real data.


## JSON stored in a column: `columns`

**For a column that stores JSON**, which the application is to receive typed.

A column that stores JSON holds whatever its writers wrote, and nothing in the project shows every writer there ever was, so
haruspex types it only when it is declared ([decision 0017](decisions/0017-stored-json-is-typed-only-by-declaration.md)).
`columns` declares it, once for the column, with a JSON Schema file:

```jsonc
"columns": {
  "shop.orders.settings": { "json": "schemas/order-settings.schema.json" },
  "shop.parties.details": { "json": "schemas/party.schema.json#/$defs/person" },
  "shop.steps.args": { "json": "schemas/anything.schema.json" }   // {}: any JSON
}
```

- **The key** is the column, `schema.table.column`, a part that holds a dot in brackets (`[sales.eu].orders.settings`). It must be
  a table's column, not computed, of a string type (`char`, `varchar`, `nchar`, `nvarchar`, directly or through an alias type) or
  `json`; anything else is HAR4113. A view's column holds what it reads, so it gets the declaration from the table column, and
  declaring it is HAR4113, naming that column.
- **`json`** is the schema file, relative to this file, with a JSON Pointer after `#` to a schema inside it when it isn't the
  whole file. A reference in the schema to another file is read relative to the file it is written in.
- **The declaration is part of the column's type**, so it goes wherever the column's value goes: through views, functions,
  variables, temporary tables, table variables, `INSERT … EXEC` and `OUTPUT … INTO`
  ([decision 0093](decisions/0093-a-column-s-json-is-part-of-its-type-completed-by-the-config.md)). Read plainly, the column is
  a string holding JSON of the schema's type, which the generated code parses and checks (`jsonText(() => …)` in Zod).
  Through `json_query` it is that JSON, live, nested as JSON in `FOR JSON`; and since `json_query` returns only an object or an
  array, and errs on anything else, only the schema's objects and arrays reach it.
- **A path into it is typed from the schema**: `JSON_VALUE(c, '$.address.city')`, `json_query(c, '$.lines')` and `OPENJSON(c)`,
  with or without a row path and `WITH`, walk the schema's type a step at a time, so a key the schema doesn't require, a nullable
  value, an index past an array's fewest items or a key of a map makes the result nullable under a lax path, and `explain` names
  the step. An `OPENJSON … WITH` column is typed by its declared SQL type, not the schema's. A path the schema can never have, such
  as a key a closed object doesn't list, is warned of (HAR2102)
  ([decision 0097](decisions/0097-a-path-into-json-of-known-shape-is-walked-step-by-step.md)). A declaration also claims that no
  key is written twice in one object: SQL Server's paths read the first of two, and `JSON.parse` the last.
- **A path finds a key only as it is spelled**, and a writer may spell one with escapes (`"caf\u00e9"` for `café`): so a
  required key is read non-null by a path only when every character of it is an ASCII letter or digit, the blank, or one of
  `! # $ % ( ) * , - . : ; ? @ [ ] ^ _ { | } ~`, which no common writer escapes. Any other key may be missing under a lax path,
  and `explain` names the key and its characters. `System.Text.Json` escapes `+`, `<`, `&` and every character past ASCII by
  default, and Python's `json.dumps` every character past ASCII; a plain read, which a parser decodes, keeps every key
  ([decision 0147](decisions/0147-a-key-a-writer-may-escape-may-be-missing-from-a-path.md)).
- **A `varchar` or `char` column's text is in its code page**, its collation's or else the database's (the `collation` setting,
  for a folder of scripts), and a character the code page lacks, written as itself, comes back as another: `Ω` as `O` under a
  Latin1 collation, an emoji as `??`. A JSON escape (`\u03a9`) is kept. So a string's value set is kept only when the code page
  holds every character of every value (where the collation isn't known, only ASCII), and a greatest length of n code points is
  read as 2n. A key the code page lacks comes back as another of its length, so it may be missing, the object may hold a key it
  doesn't list, and a key of the same length may hold either value. Under a `_UTF8` collation, or in `nchar` and `nvarchar`,
  everything declared stands
  ([decision 0145](decisions/0145-varchar-json-is-read-through-its-code-page.md)).
- **A key the schema doesn't require** may be left out, and may be null only where its schema allows `null`: an application may
  write it either way, unlike `FOR JSON`. An object its schema doesn't close (with `false` for its other keys) may hold other keys,
  which the generated code keeps (`z.looseObject` in Zod), whatever `objects` says. The top of the schema can't allow `null` (HAR3115): a column
  with no value is NULL in SQL, which its own nullability says.
- **`{}` (or `true`) declares any JSON**, a type of its own: the column holds JSON, which nests as JSON and is checked as JSON
  (`z.json()`), but its shape is the writer's ([decision 0094](decisions/0094-any-json-is-a-type-of-its-own.md)).
- **The IR records each declaration** with the SHA-256 of every schema file it read, so a change to a schema's text is a change
  `check` reports (but not a change of its line endings or byte order mark, which git may make on checkout), and each object whose results may hold a declared column's JSON lists the claim (an override with `via`).
- **A declaration is checked at runtime**, like everything else: the generated validator rejects a value that doesn't fit it, so
  a wrong schema fails loudly, on real data. It is a claim about every writer there ever was, which only the team can make.

### Naming a column's schema in the SQL

**For a team whose database should carry its columns' schemas**, so that they travel with its scripts and its DACPAC rather
than with one application's `haruspex.json`.

The extended property `haruspex.jsonSchema` on a table's column names its schema, written as `json` is above (a path relative
to the config file's folder, with a JSON Pointer after `#`), and declares exactly what that `columns` entry would
([decision 0141](decisions/0141-a-column-s-json-schema-may-be-named-in-the-sql.md)):

```sql
EXEC sp_addextendedproperty N'haruspex.jsonSchema', N'schemas/order-lines.schema.json',
    N'SCHEMA', N'shop', N'TABLE', N'orders', N'COLUMN', N'lines_json';
```

- **It is read from scripts and from a DACPAC alike**, wherever haruspex reads the database, and recorded in the IR as a
  `columns` entry is: a declaration, checked at runtime, never a proof. So `check` reports a changed schema file, and a changed,
  added or removed annotation, as it reports the same change in `haruspex.json`.
- **Where both declare a column, they must name the same schema** (the same file and pointer); naming two is HAR4114, and
  haruspex chooses neither.
- **Only a table's column takes one**, of the columns. One on a view's or a table function's column is HAR4113, as `columns` would
  be, naming the table column to declare instead. One on a procedure's or function's parameter declares the parameter's JSON, as
  `parameters` does ([JSON a parameter takes](#json-a-parameter-takes-parameters), below). One on an object is HAR4116, a warning:
  it declares nothing there. SQL Server takes none on a numbered procedure (`get_order;2`) or a scalar function's result.
- **What can't be read is an error** (HAR4115): a value that isn't literal text, a property named twice, updated or dropped
  (which stands depends on the order the scripts run), or one on something the source doesn't define. A URL is refused
  (HAR3112): haruspex never fetches a schema, so keep a copy in the repository and name that.
- **The name is compared ignoring case**, as SQL Server compares it in a database that ignores case.

## JSON a parameter takes: `parameters`

**For a procedure or function that takes a JSON document** (`@order nvarchar(max)`) and reads it with `OPENJSON` or `JSON_VALUE`.

Any caller may send a parameter any text, so what it holds is the callers' and not the database's to say, and haruspex types it
only when it is declared ([decision 0143](decisions/0143-a-parameter-s-json-may-be-declared.md)). `parameters` declares it, once
for the parameter, with a JSON Schema file:

```jsonc
"parameters": {
  "shop.place_order.@order": { "json": "schemas/order.schema.json" },
  "shop.lines_of.@doc": { "json": "schemas/order.schema.json#/$defs/lines" }   // a function's parameter
}
```

- **The key** is the parameter, `schema.object.@name`, its `@` included, a part that holds a dot in brackets. It must be a
  parameter of a procedure or function in the project, not `OUTPUT`, of a string type (`char`, `varchar`, `nchar`, `nvarchar`,
  directly or through an alias type) or `json`; anything else is HAR4117. A numbered procedure's parameter
  (`shop.get_order;2.@order`) can't be declared: haruspex reads no numbered procedure.
- **`json`** is the schema file, as for [`columns`](#json-stored-in-a-column-columns), and is read as
  [the JSON Schemas a declaration reads](#the-json-schemas-a-declaration-reads) says. Its top can't allow `null` (HAR3115): SQL
  Server's JSON functions refuse the text `null`, and a caller with no value sends NULL, which the parameter's own nullability says.
- **The body's reads take it as given** until the parameter is assigned: `JSON_VALUE(@order, '$.mode')`, `json_query`, `OPENJSON`
  with or without `WITH`, and a variable assigned the parameter, walk the schema's type as for a declared column, a key a caller
  may escape included. A read after the parameter is assigned has no declaration.
- **A default other than NULL** is what a caller that leaves the parameter out sends, so the body reads it beside the declared JSON.
  A `varchar` or `char` parameter's text is in the database's code page, so its reads are typed as that code page gives them back,
  as a declared `varchar` column's are (above).
- **NULL stays wherever the server can give it.** A caller may send NULL, so a read is nullable unless the body proves the parameter
  isn't (`IF @order IS NULL RETURN`); a key the schema doesn't require may be left out, so a lax path to it is nullable. Text that
  isn't JSON fails the call (13609), in lax mode too. JSON of another shape gives NULL where the types say otherwise: that is the
  claim the declaration makes about every caller.
- **The generated function takes the value**, an object of the schema's type rather than its text: it checks it against the
  schema, serialises it with `JSON.stringify` (for `char` and `varchar`, every character past ASCII in a string value escaped, so
  that the code page keeps it; keys as written) and checks the text against the parameter's type before it sends anything
  ([the database module](database-module.md#how-the-parameters-are-checked)).
- **The IR records each declaration** its objects take or rest on, with the SHA-256 of every schema file it read, so `check`
  reports a changed schema file or declaration, and gives the parameter the declared `content`. Each object that takes the
  parameter, or calls a function or procedure that does, lists the claim (an override with `via`).
- **In the SQL**, the extended property `haruspex.jsonSchema` on the parameter declares the same, read from scripts and from a
  DACPAC as on a column ([Naming a column's schema in the SQL](#naming-a-columns-schema-in-the-sql)), and the two must agree
  (HAR4114):

  ```sql
  EXEC sp_addextendedproperty N'haruspex.jsonSchema', N'schemas/order.schema.json',
      N'SCHEMA', N'shop', N'PROCEDURE', N'place_order', N'PARAMETER', N'@order';
  ```

## Calls to REST endpoints: `endpoints`

**For a procedure that calls a REST service** with `sys.sp_invoke_external_rest_endpoint` (SQL Server 2025) and returns what
it read.

What SQL Server writes into `@response` is its own, and haruspex types it from what the server was observed to do: an envelope
holding the status, the headers and the reply's body as `result`. The body is the service's, and nothing in the project shows
what a service sends, so `result` is unknown until the config file declares it, for each endpoint, with a JSON Schema
([decision 0140](decisions/0140-a-rest-response-is-typed-from-a-schema-declared-for-its-endpoint.md)):

```jsonc
"endpoints": [
  { "url": "https://billing.example/plans/{id}", "method": "GET", "response": "schemas/plan.schema.json" },
  { "url": "https://billing.example/plans?tier={tier}", "method": "GET", "response": "schemas/plans.schema.json" }
]
```

```sql
CREATE PROCEDURE billing.get_plan @plan_id int AS
DECLARE @response nvarchar(max), @url nvarchar(200) = N'https://billing.example/plans/' + CAST(@plan_id AS nvarchar(11));
EXEC sys.sp_invoke_external_rest_endpoint @url = @url, @method = N'GET', @response = @response OUTPUT;
SELECT JSON_VALUE(@response, '$.result.name') AS name, JSON_QUERY(@response, '$.result') AS plan_body;
```

- **`url`** is the endpoint's `https` URL. Its scheme and host compare without case, as the server reads them; everything after
  them compares exactly, the query and a fragment included, which the server sends as written. A `{name}` stands for one
  segment of the path, or one value of the query: text that is never empty, never holds `/`, `?`, `#`, `&` or `=`, and is never
  `.` or `..`, which the server removes from a path before sending it (`/plans/.` reaches `/plans/`). A URL held in a `varchar`
  is converted to its code page first, so a character outside it is sent as another (`?`, or `l` for `ł`); such text matches no
  declaration.
- **`method`** is `GET`, `POST`, `PUT`, `PATCH` or `DELETE`, compared with the call's without case. A call that gives no
  `@method` sends `POST`.
- **`response`** is the JSON Schema file of the reply's body, relative to this file, with a JSON Pointer after `#` when it isn't
  the whole file, read as [a column's JSON is](#the-json-schemas-a-declaration-reads): an object may hold keys the schema
  doesn't list, a key it doesn't require may be left out, and a key a writer may escape may be missing from a path. It describes the body of **every** status, since an error's body is
  written into `result` too: a service whose errors send another shape is declared with `anyOf`. A body that isn't an object or
  an array fails the call, so only those parts of the schema are read.
- **Which calls take it.** `@url` and `@method` are read from the text the SQL builds: literals, `+` and `CONCAT`, a number
  converted to text (which stands for a `{name}`), `CASE`, `IIF`, `COALESCE`, a variable long enough to hold every text, and a
  wrapper procedure's parameter, read at each call of the wrapper with the text the call passes. Where every URL and method a
  call may send match one declaration, `result` is its response. Anything else leaves `result` unknown, saying why: text built
  at run time (a parameter the application passes, a column), text that may match no declaration or more than one, or a
  variable that may cut the URL short. A NULL that `+` treats as empty (unless [the callers' session](#the-callers-session-session)
  sets `CONCAT_NULL_YIELDS_NULL` on) or that `CONCAT` drops is a URL too: `…/plans/` matches nothing above.
- **The envelope is typed either way**, where a JSON function reads `@response`: `JSON_VALUE(@response,
  '$.response.status.http.code')` is never NULL after a call that surely sets it, and anything under `$.result` may be, since a reply
  with no body leaves it out. Only a `@response` of `nvarchar(max)` holds it whole, and `@headers` must accept JSON: no Accept, or
  `application/json`, written as literal text. Read plainly, `@response` stays a string: no request header proves the reply is
  JSON, and a JSON function raises an error on the XML form, which is how every claim made through one stays true.
- **`@headers` that may not be JSON text** (a parameter, text built at run time) may leave `@response` as it was: such a call
  returns 1 without sending anything, and the procedure goes on.
- **The IR records each declaration** with the SHA-256 of every schema file it read, so a changed declaration or schema file is a
  change `check` reports, and each object whose results may hold a declared response lists the claim (`endpoint-json`, its
  `via` the method and URL). A declaration is a claim about someone else's service, which the generated validator checks on every
  reply.

## The JSON Schemas a declaration reads

**For whoever writes the JSON Schema** of declared results, of a column's JSON or of an endpoint's response.

Declared results, declared columns and declared endpoints read a JSON Schema (draft 2020-12), and translate it into haruspex's own types. The subset
read:

| Keyword | How it is read |
|---|---|
| `type` | One type or several, with `"null"` when the value may be null. Several types are a value of any of them, each with the keywords written for it. A schema without a `type` is any JSON, and a keyword that applies only to one type is then a warning. |
| `properties`, `required` | An object's keys. Without `properties`, an object whose keys are data, each value of `additionalProperties`, or any JSON when that is not given. In a column's JSON a key not `required` may be left out whatever its value; in declared results it is left out only when null. |
| `additionalProperties` | Alone, the type of every value of an object whose keys are data. `false` beside `properties` closes the object, unless `patternProperties` allows other keys, which aren't read, so the object stays open: in a column's JSON an object it doesn't close may hold keys it doesn't list, which the generated code keeps and never refuses; a declared result's object holds only its keys, as `FOR JSON` writes it, and the `objects` setting decides what happens to any other. A schema beside `properties` is a warning, and not checked. |
| `items`, `minItems`, `maxItems` | An array's items and how many there are. Without `items`, an array of any JSON; with `items` false, an empty array. Beside `prefixItems` (a tuple, as Zod writes one), `items` speaks only for the items after the prefix, so every item is any JSON, and `prefixItems` is a warning. |
| `enum`, `const` | A set of strings, of numbers (in plain decimal form), or both, which the generated schema checks exactly; `null` in it makes the value nullable. A set with any other kind of value is a warning, and the schema is read without it. |
| `anyOf`, `oneOf` | A value of any branch: a union. `oneOf` is read as `anyOf`, which also accepts a value two branches accept: wider, so never refusing a value the schema allows. A branch of `null` alone makes the value nullable. A keyword beside them is a warning, and not checked. |
| `minLength`, `maxLength` | A string's minimum and maximum length, counted in code points as JSON Schema counts them, as the generated checks do too (Zod's own check counts code points, observed in `ZodObservationTests`). |
| `format` | `date`, `time`, `date-time` (with an offset), `date-time-local` (without one, as SQL Server writes `datetime2`), `uuid`, and `int32` or `int64` on an `integer`. An `integer` without a format is checked as a number; one of `int64` follows the rule for large integers in JSON. In a column's JSON, `time`, `date-time` and `date-time-local` are read as strings, with HAR3150: an application may write forms RFC 3339 allows (a time's offset, a lower-case `t` or `z`, a leap second) that the checks of SQL Server's forms refuse. Any other format is a warning, HAR3150, and not checked. |
| `$ref` | A JSON Pointer in the same document (`#/$defs/person`), or another local file, relative to the file that refers to it, with a pointer after `#` or without one for the whole file. A reference to a schema on the network is an error: haruspex never fetches one. A schema that refers to itself is an error, since a recursive one isn't supported yet. |
| `{}`, `true` | Any JSON value: an object, an array, a string, a number, a boolean or `null`, of any content. It is a type of its own, not an unknown ([decision 0094](decisions/0094-any-json-is-a-type-of-its-own.md)). |

Annotations (`title`, `description`, `$schema`, `$comment`, `examples`, …) are ignored. **Any other keyword is a warning**,
HAR3151, and is not checked, so the generated type is wider than the schema, never narrower. `pattern` is one, until a row carries
it over exactly.

## Splitting the output into modules: `split`

**For a module large enough** that code should import only the part it uses.

Without `split`, `generate` writes one module: one file per emitter, which every reader imports whole. `split` writes a module
per group of schemas instead, each the files one config writes, named after the module, so that code imports only the part it
uses and a change to one schema's procedures shows in that schema's files
([decision 0074](decisions/0074-a-config-may-split-its-output-into-modules.md)):

```jsonc
"split": "schema"                                          // sales.zod.ts, shop.zod.ts, admin.zod.ts, …
```

```jsonc
"split": { "sales": ["sales", "shop"], "admin": ["admin"] }   // sales.zod.ts holds both schemas' procedures
```

With `"schema"`, each schema of the selected procedures is a module of its own, named as the schema is. A list names each module
and the schemas whose procedures it holds, compared ignoring case as SQL Server compares schema names; a schema is in one module,
and a selected procedure whose schema no module lists is an error naming it ([HAR4008](diagnostics/HAR4008.md)). A module's name
starts with a letter and has only letters, digits, `_` and `-`, since it names files, and isn't `shared`; with `"schema"`, a schema
whose name can't be a module's needs a list ([HAR3024](diagnostics/HAR3024.md)).

**Query files** are in the module their `-- @module` comment names, or else in the module `queries`:

```sql
-- @module sales
SELECT o.order_id, o.total FROM sales.orders AS o WHERE o.placed >= @since;
```

The comment names one of the modules, or `queries`; a list may hold a module of query files alone, with an empty list of
schemas. Without `split`, a well-formed comment is a warning ([HAR4009](diagnostics/HAR4009.md)), since it does nothing. A
comment that can't be taken, naming no module or more than one word, or written twice, is an error whether or not the output is
split ([HAR1019](diagnostics/HAR1019.md)): the file is read the same either way, as its `-- @sets` comment is.

**What is written.** For each module, each emitter's file, named after the module: `sales.zod.ts`, `sales.jsonschema.json`,
`sales.mssql.ts`. A definition two or more modules use is written once, in `shared.zod.ts` and `shared.jsonschema.json`, which the
modules import from or refer to; a shared file is written only when something is shared
([decision 0075](decisions/0075-what-several-modules-share-is-written-once-in-a-shared-file.md)). The IR stays one file,
`haruspex.ir.json`, and so does `haruspex.mapping.ts`. Each `sales.mssql.ts` imports its own module's `sales.zod.ts`.

- **Every name is the one the unsplit output gives**, unique across the whole config, so switching `split` on, or moving a schema
  to another module, renames nothing: an import changes the file it names, not what it imports.
- **A shape declared by name** (a `name` in `paths`) that two modules share is exported from `shared.zod.ts`, under that name.
  A shape named by its structure, `shape3f2a9c`, is exported there marked `@internal` so that the modules can import it; it is
  not a public name, and no module re-exports it. Import a type by its own name from its own module, as `ListStaffRow` from
  `shop.zod.ts`.
- **In JSON Schema**, a module's document refers to the shared one by a `$ref` relative to its own name,
  `shared.jsonschema.json#/$defs/PartNode`: add each document to the validator under its file name, as
  `ajv.addSchema(document, "sales.jsonschema.json")`.
- **There is no `index.ts`** that re-exports every module, which would make every reader import everything again.
- **Each module's functions are self-contained**, with their own error classes, as the functions of several config files are:
  tell a failure apart by its `name` and `kind` ([when a call fails](database-module.md#when-a-call-fails)).
- **A module that is gone loses its files**: taken out of the list, or left with no procedures, its files are removed by the next
  `generate`, as an emitter's are when it is taken off `emit` ([the files generate removes](#the-files-generate-removes)).

`check` compares every file as it compares one module's, and fails when one is missing. A procedure added to one schema changes
the IR and its module's files, and the shared file only when it shares a definition with another module; that module's file then
imports the shape it shares, its own types keeping their names.

## Results policy: `objects`

**For a project that deploys the application first, or with the database**, and wants a key or result set it does not
expect to fail the call.

- `"strip"`, the default: `z.object`, and JSON Schema objects that allow other keys. Types are still exact, but keys a type
  doesn't list are removed when parsing, or allowed when validating, so a column the database gains before the application
  redeploys doesn't break the running application. In the same way, a function reads the result sets its procedure lists and
  ignores any after them, so a procedure that gains a trailing result set doesn't break it either.
- `"strict"`: `z.strictObject`, and JSON Schema objects that allow no other keys, for projects that deploy the application first or together
  with the database, and want an unexpected key to fail loudly. A function then refuses a result set after those it knows,
  with a `ResultError` (`dbo.get_order, the results: Expected exactly 2 result sets, received 3`).

Either way, a result set the procedure always returns that doesn't arrive fails the call. Only the reaction to something
unexpected changes: the types, and what a function returns, are the same under both
([decision 0063](decisions/0063-a-function-ignores-result-sets-after-those-it-knows.md)).

This is the one deliberate exception to haruspex's rule that the generated code claims everything the engine guarantees
([decision 0005](decisions/0005-claim-everything-the-engine-guarantees.md)): `FOR JSON` produces exactly the known
keys, so strict is the accurate schema, and strip is the default for deployment safety. It applies to results only.

## Comments: `comments`

**For a team that wants more or fewer** of the generated code's comments.

Generated code says why a field is what it is where that would surprise its reader: a field whose nullability, presence or
type differs from what its source column declares, and every unknown. A `COUNT` is nullable, a key is left out because an outer
join made it null, a `SUM` of a `decimal(9,2)` is a `decimal(38,2)`: each is deliberate, and each looks like a bug without its
reason. A key `FOR JSON` leaves out only because its column is nullable is not a surprise: `FOR JSON`, and `JSON_OBJECT` with
`ABSENT ON NULL`, leave out every key whose value is null, and the Zod module says so once, in its header.

```ts
  /**
   * May be null: COUNT is an int, null past 2,147,483,647 rows when the caller's session has ANSI_WARNINGS and ARITHABORT off (count-overflow).
   * To change it: use COUNT_BIG, or declare the callers' ANSI_WARNINGS or ARITHABORT on in the config file's "session".
   * Why: haruspex explain shop.order_summary line_count
   */
  line_count: z.int32().nullable(),
```

Each comment gives the reasons' short texts with their codes (each has a page in [the reasons](reasons/README.md)), the way out
when there is one, and the [`haruspex explain`](command-line.md#explain) command that shows where in the SQL each reason
applied. The comments leave the places out, so that they change only when a reason does.

- `"surprises"`, the default: a comment on each surprise. A key left out when null for a reason other than its column's (or
  parameter's, or the config file's) declaration is one, and its comment says why it may be null.
- `"all"`: a comment on every field, with the columns it is read from.
- `"none"`: none of these comments. A description the database keeps ([`descriptions`](#descriptions-descriptions)) is still
  written under `"none"`: it is the database's documentation of the field, not a reason haruspex gives.

The Zod module writes them above each property, and, for a type whose shape another type shares, above its export, so that
comments never stop a shape being shared. The functions file says why a result set may be missing, or can't be listed; under
`"all"`, how many rows each returns too. JSON Schema writes the same lines at the end of the property's `description`. The notes
that state a fact a check rests on (`varchar(n)` counts bytes, a bound JavaScript reads as the nearest double) are written
whatever this says ([decision 0079](decisions/0079-a-surprise-is-explained-where-it-is-written.md)).

## Naming

**For generated names other than the default**, which is Pascal case from each object's own name.

Generated names come from each object's own name, **never its schema**, by declarative rules applied in this order:

1. `replace`: rules of the form `{ "pattern": "…", "with": "…" }`, run over the name in order, each replacing every match
   of the regular expression `pattern` with `with` (`$1` names a group, and `""` removes the match); the top-level rules
   first, then the schema's own. `#$` is the end of the name. Expressions run without backtracking, so
   backreferences and lookarounds are not available.
2. `prefix`: the schema's prefix, if any, goes in front as words of its own: `"prefix": "Audit"` makes
   `audit.get_order` `AuditGetOrder`.
3. `case` (the schema's, or else the top level's): `pascal` (the default) breaks words at every character that isn't a
   letter or digit (`$`, `#`, `_`), so `stock$level$history` gives `StockLevelHistory`. `camel` gives
   `stockLevelHistory`. `preserve` keeps the prefix and the name as they are, `$` included; anything that is still
   not a valid identifier is an error naming the object.

Nested types build on their procedure's name: `GetOrderRow` for its rows, `GetOrderLine` for the items of `lines`,
`GetOrderLinesRow` for the rows of a set named `lines` (`sets`, under "Overrides"), `GetOrderResult2Row` for a second unnamed
result set's rows, and `PartTreeNode` for the recursive type of a function `bom.part_tree` (named by its own schema's
settings).

**Names don't shift.** When two objects' names come out the same (two schemas with a `get_order` each, say), it is an
error naming both, which asks for a `prefix` on one of the schemas or a `name` override; haruspex never adds a number
between objects, because that renumbered names whenever an object was added and broke code that used them. Inside one
procedure two types can still meet (a nested key `row` beside the row type, or the variants of a union, which share a
path); there a number is added in the order the procedure's results are written (`GetOrderRow2`), because it depends
on nothing but that procedure.

## The baseline: `baseline`

**For an application and a database that deploy separately.**

An application and its database deploy separately, so for a while the application meets the database before its latest
change or after it. `baseline` names the database before: usually the one as of the application's last release. With it,
`haruspex generate` writes types that work against both, and `haruspex check` fails on any change the application already
running would fail on, besides comparing the module with the output folder (with what `generate`, given the same baseline,
writes):

```jsonc
"baseline": "release/database"            // a folder of .sql files, a .sqlproj, a .dacpac, or an haruspex.ir.json
```

It is any source `source` takes, read with this file's selection, overrides and naming, or an IR file written by this version
of haruspex. The path is relative to this file, and `--baseline` replaces it for one run. It is never fetched: CI puts it
there first, usually the database as of the application's last release (the release's `.dacpac`, or its tag checked out into a
folder). [Generating against a baseline](command-line.md#generating-against-a-baseline) shows what `generate` writes, and
[Checking against a baseline](command-line.md#checking-against-a-baseline) what the check says, with the recipes for GitHub
Actions. A combined IR (one `generate` wrote with a baseline) describes an application, not a database, so it is a baseline for
`check --fit-only` only (HAR6036).

**The types `generate` writes** take, for each value, what both databases allow, merged into one type
([decision 0073](decisions/0073-generate-with-a-baseline-writes-types-that-hold-against-both-databases.md)). For `shop.get_order`
returning `{ order_id, status }` before and `{ order_id, status, channel }` after, with `'held'` a new status:

```ts
export const GetOrderRowSchema = z.object({
  order_id: z.int32(),
  /** One of "open", "closed", "held", as the database compares strings. */
  status: z.string().max(10),
  /** varchar(10) counts bytes and its collation isn't declared, so the length is at most 10. */
  channel: z.string().max(10).nullable().optional(),    // only the newer database returns it
})
```

| | Results (to the application) widen | Parameters (to the database) narrow |
|---|---|---|
| Combined | a key only one returns may be left out; nullable if either is; a length, range, digits or value set the larger; integers of two sizes the wider; a trailing result set only one returns may be missing; a value of another kind a union of both | a parameter only one has is left out while it has a default there; the narrower type of the two (two decimals meet at the fewer places each side of the point); a default only where both have one; `OUTPUT` only where both are |
| An error, the procedure left out of the module (HAR6033) | rows that became JSON, or the reverse | a parameter added without a default; one removed that the baseline requires; types with no value in common, dates neither of which holds the other, or lengths counted in another unit; an `OUTPUT` parameter of another type, whose value comes back too |
| A warning | a key gone and another appeared in the same row: perhaps a rename (HAR6034); a function whose return shape changes (HAR6028) | |

A procedure only one database has is left out with a warning (HAR6035), and so is the rest of the module written when one
procedure can't be bridged: the run then fails. A query file is combined only when the baseline was read with its text as it
is now, which a source baseline is, and that text runs against the baseline's database: one that names a table, a column or a
procedure the baseline doesn't have can't be combined, nor one whose text differs from an IR baseline's (HAR6033). Named result
sets are read from a source baseline by position, so a procedure whose `sets` name one set more or fewer than the baseline returns
is combined, not refused.

**What `check` judges.** Values flow two ways, and each is judged in its own direction
([decision 0068](decisions/0068-a-baseline-fits-when-the-running-application-accepts-every-value.md)):

| Change | Results (to the application) | Parameters (to the database) |
|---|---|---|
| Fails the check | a key or result set removed; a value that may now be null, a key that may now be left out, a key left out when null that may now arrive as `null`, a result that may now be absent or not be sent; a type of another kind; a length, range, number of digits, number of rows or value set widened; a procedure gone | a parameter added without a default, removed, narrowed (a date or time to a narrower range or a coarser fraction too), of a type now unknown, of a CLR type or `xml` (whose definition the IR doesn't hold) even when unchanged, of a table type whose definition changed so that a row the application sends may be refused or go to another column, no longer `OUTPUT`, or that lost its default; an `OUTPUT` parameter whose value, which comes back, widened, as a result's would |
| A warning | a new key; a new trailing result set; both fail under `"objects": "strict"` | a parameter added with a default |
| Fits | a value that became non-null; a length, range or value set narrowed; keys in another order; a trailing result set the application may find missing (a statement that may not run), gone | a parameter widened, or that gained a default; a table type unchanged, or whose columns widened or became nullable, or that lost a key or CHECK |

## Warnings acknowledged: `acknowledge`

**For a warning the team has read and decided to live with.**

Some warnings say something about the source that a team has read and decided to live with: a build target that only stamps
a version, a reference to a package a build machine doesn't have. Said on every run, they teach the team to skip warnings,
and the next one, which matters, is skipped with them. `acknowledge` names each such warning by its `code` and its `subject`
together, never by its code alone, with `why`:

```jsonc
"acknowledge": [
  { "code": "HAR1008", "subject": "RunTestsBeforeDeploy", "why": "it runs the tests and adds no scripts" },
  { "code": "HAR1003", "subject": "master.dacpac", "why": "no procedure reads a system view" }
]
```

An acknowledged warning is counted rather than said (`haruspex: nothing changed since the last run: 4 procedures, no
unknowns; 2 warnings acknowledged (--verbose lists them).`); `--verbose` lists each, with its `why`, and `--format json` and
`sarif` hold them apart, SARIF as suppressed results. Every other warning of the same code is still said. The subject is
the name the warning gives:

| Code | Subject |
|---|---|
| [HAR1003](diagnostics/HAR1003.md), a referenced DACPAC isn't found | its file name, such as `master.dacpac` |
| [HAR1007](diagnostics/HAR1007.md), a referenced project isn't found | its file name, such as `Shared.sqlproj` |
| [HAR1008](diagnostics/HAR1008.md), part of a project file isn't read | the name of the build target that isn't read; the other parts can't be acknowledged |
| [HAR2101](diagnostics/HAR2101.md), a write to an object the project doesn't define | the object's name, its parts joined by dots without brackets, such as `audit.log` |

A subject is compared ignoring case, as SQL Server compares names, so `[Audit].[Log]` and `audit.log` are one subject. No other
code can be acknowledged: an error must be fixed, and a warning about the config file is fixed in it (HAR6024). An
entry that matches no warning in a run is said, as a warning (HAR6025), so that one left behind after its cause is gone doesn't
hide the next warning of that subject; a run whose `--include` narrows the selection doesn't say it, since the warning may be
about a procedure the run left out.

## The callers' session: `session`

**For callers whose connections set `ANSI_WARNINGS` or `ARITHABORT`**, as the `mssql` package's driver does, or who hold
`UNMASK`.

Whether some values can be null depends on `SET` options of the session that calls the procedure, not on the procedure:
with `ANSI_WARNINGS` and `ARITHABORT` both off, an overflow, a division by zero or a `COUNT` past `int` is NULL; with either
on, it is an error that fails the call. With `NUMERIC_ROUNDABORT` on, a decimal rounded to fewer places is NULL in the same
session, and an error in any other. The caller is out of sight, so by default haruspex assumes nothing, and each such value
is nullable (`arithmetic-overflow`, `count-overflow`, `domain-error`, `precision-loss` in the IR). Whether a masked column
gives its values or its mask depends on the session too, on whether its user holds `UNMASK`. Declare what your callers set and
hold, and haruspex claims what they allow:

```jsonc
"session": {
  "ansiWarnings": true,             // ANSI_WARNINGS
  "arithAbort": false,              // ARITHABORT
  "concatNullYieldsNull": true,     // CONCAT_NULL_YIELDS_NULL
  "numericRoundAbort": false,       // NUMERIC_ROUNDABORT
  "unmask": true                    // the callers hold the UNMASK permission
}
```

- **`ansiWarnings`** or **`arithAbort`** `true`: an overflow, a division by zero, a `COUNT` past `int`, a value outside a
  math function's domain and a loss of precision are errors, so none makes a value nullable. Either one is enough, but a
  loss of precision under `TRY_CAST` or `TRY_CONVERT` is NULL all the same: only `numericRoundAbort` `false` removes that.
- **`numericRoundAbort`** `false`: a decimal rounded to fewer places (a narrowing `CAST`, a division or a product whose scale
  is cut, `AVG` of a decimal, an assignment to a variable of fewer places) is rounded silently, so it never makes a value
  nullable. `true` changes nothing: that is what haruspex assumes when it isn't known.
- **`concatNullYieldsNull`** `false`: string `+` with one side NULL gives the other side, so it is null only when both sides
  may be. `true` changes nothing: that is what haruspex assumes when it isn't known.
- **`unmask`** `true`: the callers hold the `UNMASK` permission, so a masked column (`MASKED WITH (FUNCTION = …)`) gives them
  its values. By default haruspex assumes they don't: a caller without it reads the column's mask (`0`, `'xxxx'`, `1900-01-01`,
  what `email()`, `partial()`, `random()` or `datetime()` writes) in place of each value, and the mask of anything computed from
  one in place of the result. So of a masked column, and of every value computed from one, haruspex claims only the type,
  whether it may be null and the greatest length: not a CHECK's range or set of values, not a `char`'s, `nchar`'s or `binary`'s
  fixed length, and not a minimum length. JSON computed from a masked column is its mask, which is not JSON, so its shape is
  unknown, and a `FOR JSON` that nests it leaves the procedure's results unknown. With `unmask` `true` every claim comes back,
  unless a procedure, function or trigger of the project runs `WITH EXECUTE AS` another user, whose permissions then decide what
  it reads: haruspex keeps every mask and says so ([HAR2103](diagnostics/HAR2103.md)). `false` changes nothing. A user holds
  `UNMASK` when it is granted to them or a role of theirs, and `db_owner` does; it can also be granted on a schema, a table or a
  column alone, which this setting can't say yet, so declare it only when the callers hold it on every masked column they read
  ([decision 0118](decisions/0118-a-masked-column-claims-only-its-type-nullability-and-greatest-length.md)).
- **An option left out is not known**, and haruspex claims only what holds whatever it is. With no `session` block every
  value a caller's options could make NULL is nullable, a loss of precision included since IR 0.18, which made such values
  nullable that earlier versions claimed never null.

Declare what the **callers' connections** set, and what their users hold, not the database's defaults: those apply only to a connection that sets
nothing, and current drivers set these options at login. tedious (the `mssql` package's driver) sets the first three on and
`NUMERIC_ROUNDABORT` off; Microsoft.Data.SqlClient sets `ANSI_WARNINGS` and `CONCAT_NULL_YIELDS_NULL` on and `ARITHABORT` and
`NUMERIC_ROUNDABORT` off; the ODBC and OLE DB drivers set `ANSI_WARNINGS` on. [Decision 0038](decisions/0038-the-callers-session-is-declared.md) lists the sources, and
why each option is or isn't here. An application can `SET` its own after connecting, so when in doubt, ask the session:
`SELECT SESSIONPROPERTY('ANSI_WARNINGS')`.

A declaration is a claim, like `"nullable": false`: if a caller sets the options otherwise, a value the schema says is never
null can be NULL. The IR records it (`source.session`) and the generated module's header says it, so a generated file says what it
assumed. A procedure that sets one of these options otherwise in its body overrides the declaration until it
returns; haruspex then leaves its results unknown. Other options that change no claim haruspex makes (`DATEFIRST`,
`LANGUAGE`, `DATEFORMAT`) are not settings; writing one is an unknown setting.
