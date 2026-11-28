# Decisions

The decisions haruspex's design rests on, one short record each: when it was made, what was decided, the context, the
alternatives, and why they lost. The code and the guide link here rather than repeat the argument. A record describes the
decision as it stands; when one is reversed, the record says so and links its successor, and is not deleted. A number is
set aside when work that may need a record starts, so a number whose work needed none is never used: the log has 30
gaps today, from 0049 to 0136, and no record was deleted to make them.

| Record | Date | Decision |
|---|---|---|
| [0001](0001-infer-from-source-never-a-live-database.md) | 7 Nov 2026 | Infer from the source, never from a live database |
| [0002](0002-unknown-with-a-reason-never-a-guess.md) | 7 Nov 2026 | Never claim more than the engine guarantees: unknown, with a reason, never a guess |
| [0003](0003-an-ir-of-its-own.md) | 7 Nov 2026 | An intermediate representation of its own, not JSON Schema |
| [0004](0004-scriptdom-is-the-only-sql-dependency.md) | 9 Nov 2026 | ScriptDom is the only SQL dependency; no DacFx |
| [0005](0005-claim-everything-the-engine-guarantees.md) | 9 Nov 2026 | Claim everything the engine guarantees |
| [0006](0006-layers-with-one-way-dependencies.md) | 9 Nov 2026 | Layers, with dependencies one way and checked by a test |
| [0007](0007-every-engine-rule-is-observed.md) | 9 Nov 2026 | Every rule about the engine is observed on a real server |
| [0008](0008-objects-strip-unknown-keys-by-default.md) | 9 Nov 2026 | Objects strip unknown keys by default; strict is a setting |
| [0009](0009-parameters-are-strict.md) | 9 Nov 2026 | Parameter objects are strict |
| [0010](0010-procedures-are-selected-per-schema.md) | 9 Nov 2026 | Procedures are selected per schema |
| [0011](0011-generated-names-leave-the-schema-out.md) | 9 Nov 2026 | Generated names leave the schema out, and a clash is an error |
| [0012](0012-recursion-is-solved-as-a-fixed-point.md) | 9 Nov 2026 | Recursion is solved as a fixed point |
| [0013](0013-results-that-cannot-be-inferred-are-declared.md) | 9 Nov 2026 | Results that cannot be inferred are declared, and still checked |
| [0014](0014-count-is-nullable.md) | 10 Nov 2026 | `COUNT` is nullable |
| [0015](0015-fetch-without-into-is-unknown.md) | 10 Nov 2026 | `FETCH` without `INTO` is unknown |
| [0016](0016-visible-read-uncommitted-drops-key-claims.md) | 10 Nov 2026 | Visible read uncommitted drops key claims; the caller's isolation is assumed |
| [0017](0017-stored-json-is-typed-only-by-declaration.md) | 11 Nov 2026 | JSON stored in a column is typed only by declaration |
| [0018](0018-snapshots-without-a-snapshot-library.md) | 9 Nov 2026 | Snapshots without a snapshot library |
| [0019](0019-the-guide-is-held-to-the-code-by-tests.md) | 11 Nov 2026 | The guide is held to the code by tests |
| [0020](0020-public-figures-come-from-public-corpora.md) | 11 Nov 2026 | Public figures come only from public corpora, written by a test |
| [0021](0021-fixtures-are-found-never-listed.md) | 11 Nov 2026 | Fixtures are found by listing their folder, never named in a list |
| [0022](0022-values-compare-by-structure.md) | 12 Nov 2026 | Values compare by structure, never by their rendered text |
| [0023](0023-the-ir-schema-is-generated-from-the-model.md) | 12 Nov 2026 | The IR's JSON Schema is generated from the model |
| [0024](0024-an-ir-reader-reads-only-versions-whose-documents-mean-what-its-own-do.md) | 12 Nov 2026 | An IR reader reads only versions whose documents mean what its own do |
| [0025](0025-null-reasons-in-neutral-terms.md) | 12 Nov 2026 | Reasons to be null are named for the reason, not the SQL |
| [0026](0026-the-emitter-plans-lowers-and-prints.md) | 12 Nov 2026 | The emitter plans, lowers and prints, and walks over types go through one graph |
| [0027](0027-one-lattice-per-fact.md) | 12 Nov 2026 | One lattice per fact, facts dropped to their top by default, and the type rule named apart from the join |
| [0028](0028-one-fixed-point-and-one-environment-of-facts.md) | 12 Nov 2026 | One fixed-point iteration with one cap, and one immutable environment of what conditions prove |
| [0029](0029-built-in-types-are-one-catalogue-observed-row-by-row.md) | 12 Nov 2026 | Built-in types and conversions are one catalogue of data, observed row by row |
| [0030](0030-built-in-functions-are-entries-with-stable-codes-typed-through-one-table.md) | 13 Nov 2026 | Built-in functions are entries with stable codes, typed through one table |
| [0031](0031-the-model-is-closed-hierarchies-of-records.md) | 13 Nov 2026 | The model is closed hierarchies of records, read from ScriptDom in one place, in source order |
| [0032](0032-a-switch-over-a-closed-hierarchy-is-exhaustive.md) | 13 Nov 2026 | A switch over a closed hierarchy is exhaustive, and the build checks it |
| [0033](0033-binding-is-on-demand-and-kept-for-each-set-of-names.md) | 13 Nov 2026 | Binding is on demand, and kept for each set of names |
| [0034](0034-collections-that-compare-by-value-are-a-layer-of-their-own.md) | 13 Nov 2026 | Collections that compare by value are a layer of their own |
| [0035](0035-the-typer-apart-from-the-walker.md) | 13 Nov 2026 | The typer apart from the walker: expressions as a function of their scope, statements as the flow |
| [0036](0036-diagnostics-have-stable-codes.md) | 13 Nov 2026 | Diagnostics have stable codes, one type and one written form |
| [0037](0037-steps-return-outcomes.md) | 13 Nov 2026 | Steps return outcomes; exceptions are for faults |
| [0038](0038-the-callers-session-is-declared.md) | 14 Nov 2026 | The callers' session options are declared, and not known by default |
| [0039](0039-a-location-in-a-dacpac-names-the-package-and-the-object.md) | 14 Nov 2026 | A location in a DACPAC names the package and the object |
| [0040](0040-a-dacpac-is-read-with-the-packages-of-its-database.md) | 14 Nov 2026 | A DACPAC is read with the packages of its database, found beside it first |
| [0041](0041-a-type-is-spelt-with-its-defaults-written-out.md) | 14 Nov 2026 | A type is spelt with its defaults written out |
| [0042](0042-a-table-of-another-database-is-read-as-its-package-declares-it.md) | 14 Nov 2026 | A table of another database is read as its package declares it |
| [0043](0043-a-value-set-is-canonical-by-a-fold-observed-from-the-server.md) | 14 Nov 2026 | A value set is canonical by a fold observed from the server |
| [0044](0044-a-project-file-is-evaluated-in-part-and-the-rest-reported.md) | 15 Nov 2026 | A project file is evaluated in part, and the rest reported |
| [0045](0045-a-project-s-references-are-read-from-where-its-build-takes-them.md) | 15 Nov 2026 | A project's references are read from where its build takes them |
| [0046](0046-a-sqlcmd-variable-in-a-body-is-unknown.md) | 15 Nov 2026 | A SQLCMD variable in a body is unknown |
| [0047](0047-a-star-is-bound-against-the-tables-the-project-defines.md) | 15 Nov 2026 | A star is bound against the tables the project defines |
| [0048](0048-an-absent-json-result-reads-as-null.md) | 16 Nov 2026 | An absent JSON result reads as null, as the mssql driver hands it over |
| [0050](0050-check-compares-claims-not-files.md) | 16 Nov 2026 | `check` compares claims, not files, and fails when there is nothing to compare |
| [0051](0051-one-json-schema-document-per-module-every-value-as-json.md) | 16 Nov 2026 | One JSON Schema document per module, every value as JSON writes it |
| [0052](0052-a-decimal-s-places-are-a-note-not-multiple-of.md) | 16 Nov 2026 | A decimal's places are a note in JSON Schema, not `multipleOf` |
| [0053](0053-a-guard-narrows-the-state-and-a-select-that-may-assign-nothing-is-kept-for-one.md) | 16 Nov 2026 | A guard narrows the state, and a SELECT that may assign nothing is kept for one |
| [0054](0054-a-parameter-is-checked-against-its-declared-type.md) | 16 Nov 2026 | A parameter is checked against its declared type |
| [0055](0055-dates-and-times-are-sent-as-iso-8601-strings.md) | 16 Nov 2026 | Dates and times are sent as ISO 8601 strings |
| [0056](0056-a-function-throws-three-kinds-of-failure.md) | 16 Nov 2026 | A function throws three kinds of failure |
| [0057](0057-parameter-keys-are-their-declared-names.md) | 16 Nov 2026 | Parameter keys are their declared names |
| [0058](0058-the-functions-are-a-file-of-their-own-named-for-their-driver.md) | 16 Nov 2026 | The functions are a file of their own, named for their driver |
| [0059](0059-a-query-file-s-parameters-are-typed-from-their-uses.md) | 16 Nov 2026 | A query file's parameters are typed from their uses |
| [0060](0060-a-call-returns-its-one-result-itself.md) | 16 Nov 2026 | A call returns its one result itself |
| [0061](0061-the-npm-packages-are-haruspex-and-a-scope-per-platform.md) | 17 Nov 2026 | The npm packages are `haruspex` and a scope per platform |
| [0062](0062-generated-files-pass-typescript-eslint-s-strict-rules-with-no-disable-header.md) | 17 Nov 2026 | Generated files pass typescript-eslint's strict rules, with no disable header |
| [0063](0063-a-function-ignores-result-sets-after-those-it-knows.md) | 17 Nov 2026 | A function ignores result sets after those it knows, and refuses them under strict objects |
| [0064](0064-the-command-line-s-output-is-designed.md) | 17 Nov 2026 | The command line's output is designed for a command run many times a day |
| [0065](0065-a-warning-is-acknowledged-by-its-code-and-subject.md) | 17 Nov 2026 | A warning is acknowledged by its code and subject; a missing `master.dacpac` stays a warning |
| [0066](0066-a-mapper-accounts-for-every-key-of-a-generated-row.md) | 17 Nov 2026 | A mapper accounts for every key of a generated row, with a helper asked for by name |
| [0067](0067-a-function-a-compatibility-level-lacks-is-unknown-only-at-a-level-known-to-lack-it.md) | 17 Nov 2026 | A function a compatibility level lacks is unknown only at a level known to lack it |
| [0068](0068-a-baseline-fits-when-the-running-application-accepts-every-value.md) | 18 Nov 2026 | A baseline fits when the running application accepts every value the database can give it |
| [0069](0069-check-with-a-baseline-judges-the-deploy-window.md) | 18 Nov 2026 | `check` with a baseline judges the deploy window, beside the comparison with the output folder |
| [0070](0070-a-procedure-s-result-sets-are-named-all-or-none.md) | 18 Nov 2026 | A procedure's result sets are named all or none |
| [0071](0071-a-shared-shape-is-named-by-declaration-or-by-its-structure.md) | 18 Nov 2026 | A shared shape is named by declaration or by its structure |
| [0072](0072-a-document-written-with-a-baseline-is-the-combined-one-and-says-so.md) | 18 Nov 2026 | A document written with a baseline is the combined one, and says so |
| [0073](0073-generate-with-a-baseline-writes-types-that-hold-against-both-databases.md) | 18 Nov 2026 | `generate` with a baseline writes types that hold against both databases |
| [0074](0074-a-config-may-split-its-output-into-modules.md) | 18 Nov 2026 | A config may split its output into modules |
| [0075](0075-what-several-modules-share-is-written-once-in-a-shared-file.md) | 18 Nov 2026 | What several modules share is written once in a shared file |
| [0076](0076-a-table-valued-parameter-is-an-array-of-strict-rows-of-its-type.md) | 19 Nov 2026 | A table-valued parameter is an array of strict rows of its type |
| [0077](0077-the-ir-carries-a-table-type-s-definition.md) | 19 Nov 2026 | The IR carries a table type's definition |
| [0078](0078-the-ir-says-where-each-field-came-from.md) | 19 Nov 2026 | The IR says where each field came from |
| [0079](0079-a-surprise-is-explained-where-it-is-written.md) | 19 Nov 2026 | A surprise is explained where it is written |
| [0080](0080-the-last-definition-in-a-script-stands.md) | 19 Nov 2026 | The last definition of a procedure, function or view in a script stands; across scripts, a second is a doubt |
| [0081](0081-binary-values-are-a-kind-of-their-own-counted-in-bytes.md) | 19 Nov 2026 | Binary values are a kind of their own, counted in bytes |
| [0082](0082-a-binary-value-is-a-buffer-in-a-rowset-and-base64-in-json.md) | 19 Nov 2026 | A binary value is a `Buffer` in a rowset and base64 in JSON |
| [0083](0083-a-body-s-own-table-has-the-keys-sql-server-enforces-at-each-statement.md) | 20 Nov 2026 | A body's own table has the keys SQL Server enforces on it at each statement |
| [0084](0084-json-assembled-as-text-is-typed-where-each-element-is-one-json-value.md) | 20 Nov 2026 | JSON assembled as text is typed where each element is one JSON value |
| [0085](0085-json-built-as-text-is-a-note-wherever-it-is-written.md) | 20 Nov 2026 | JSON built as text is a note wherever it is written |
| [0086](0086-a-json-object-assembled-as-text-is-typed-where-each-key-is-written-once-in-a-literal.md) | 20 Nov 2026 | A JSON object assembled as text is typed where each key is written once in a literal |
| [0087](0087-keys-built-from-data-make-an-object-whose-keys-are-data.md) | 20 Nov 2026 | Keys built from data make an object whose keys are data |
| [0088](0088-a-type-that-depends-on-folding-is-typed-where-the-server-never-folds.md) | 21 Nov 2026 | A type that depends on folding is typed where the server never folds |
| [0091](0091-generate-removes-only-the-files-its-list-names.md) | 21 Nov 2026 | `generate` removes only the files its list names |
| [0092](0092-a-key-named-proto-is-refused-by-the-typescript-emitters.md) | 21 Nov 2026 | A key named `__proto__` is refused by the TypeScript emitters |
| [0093](0093-a-column-s-json-is-part-of-its-type-completed-by-the-config.md) | 21 Nov 2026 | A column's JSON is part of its type, completed by the config |
| [0094](0094-any-json-is-a-type-of-its-own.md) | 21 Nov 2026 | Any JSON is a type of its own |
| [0095](0095-a-length-is-the-server-s-and-a-target-claims-the-fewest-code-points-it-can-hold.md) | 21 Nov 2026 | A length is the server's, and a target claims the fewest code points it can hold |
| [0097](0097-a-path-into-json-of-known-shape-is-walked-step-by-step.md) | 22 Nov 2026 | A path into JSON of known shape is walked step by step |
| [0107](0107-names-that-differ-only-in-case-are-unknown.md) | 22 Nov 2026 | Names that differ only in case are unknown |
| [0112](0112-a-script-is-parsed-as-the-server-parses-each-batch.md) | 23 Nov 2026 | A script is parsed as the server parses each batch, and its synonyms are played in order |
| [0113](0113-a-clause-no-rule-reads-makes-the-call-unknown.md) | 23 Nov 2026 | A clause no rule reads makes the call unknown |
| [0117](0117-an-ir-document-names-the-build-that-made-its-claims.md) | 23 Nov 2026 | An IR document names the build that made its claims |
| [0118](0118-a-masked-column-claims-only-its-type-nullability-and-greatest-length.md) | 24 Nov 2026 | A masked column claims only its type, nullability and greatest length |
| [0119](0119-names-that-differ-only-in-case-are-an-error.md) | 24 Nov 2026 | Names that differ only in case are an error |
| [0120](0120-an-extract-s-key-constraints-are-no-key.md) | 24 Nov 2026 | An extract's key constraints are no key |
| [0121](0121-every-address-haruspex-writes-is-served-from-haruspex-tools.md) | 24 Nov 2026 | Every address haruspex writes is served from haruspex.tools |
| [0122](0122-the-ir-records-the-packages-declared-builds.md) | 25 Nov 2026 | The IR records the packages declared builds |
| [0127](0127-a-source-may-be-several-folders.md) | 26 Nov 2026 | A source may be several folders |
| [0128](0128-a-column-s-description-reaches-the-fields-that-are-that-column.md) | 26 Nov 2026 | A column's description reaches the fields that are that column |
| [0135](0135-a-full-text-search-s-key-is-its-key-column-s-type-and-its-rank-is-nullable.md) | 26 Nov 2026 | A full-text search's key is its key column's type, and its rank is nullable |
| [0137](0137-xml-s-nodes-query-for-xml-and-openxml-are-typed-as-the-server-gives-them.md) | 26 Nov 2026 | XML's `nodes()`, `query()`, `FOR XML` subqueries and `OPENXML` are typed as the server gives them |
| [0139](0139-xml-is-typed-from-its-schema-collection-where-the-schema-proves-it.md) | 26 Nov 2026 | XML is typed from its schema collection where the schema proves it |
| [0141](0141-a-column-s-json-schema-may-be-named-in-the-sql.md) | 27 Nov 2026 | A column's JSON Schema may be named in the SQL |
| [0142](0142-a-call-s-declared-result-sets-are-its-type.md) | 27 Nov 2026 | A call's declared result sets are its type |
| [0143](0143-a-parameter-s-json-may-be-declared.md) | 27 Nov 2026 | A parameter's JSON may be declared |
| [0144](0144-for-json-auto-is-typed-from-observed-nesting.md) | 27 Nov 2026 | FOR JSON AUTO is typed from observed nesting |
| [0145](0145-varchar-json-is-read-through-its-code-page.md) | 28 Nov 2026 | JSON in `varchar` text is read through its code page |
| [0146](0146-a-session-value-is-nullable-unless-it-is-never-null-in-any-state.md) | 19 Nov 2026 | A session value is nullable unless it is never NULL in any state |
| [0147](0147-a-key-a-writer-may-escape-may-be-missing-from-a-path.md) | 28 Nov 2026 | A key a writer may escape may be missing from a path |

## Writing a record

Number it after the last, and name the file after the decision. Keep it short: a date, the decision in a sentence or two,
the context, the alternatives with why each lost, and the reason. Write "we" for the project if it reads better, as
nowhere else in the documentation. A record states only what was decided about haruspex's design and why; history that
explains nothing about the code as it is belongs nowhere.

The code that rests on a decision links its record (`docs/decisions/0014-count-is-nullable.md`) rather than repeating the
argument, and the test in `CodeReferenceTests` checks that every such path resolves.
