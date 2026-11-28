# A parameter's JSON may be declared

**Date:** 27 Nov 2026. The IR's part (0.37) and the emitters' input are provisional until the maintainer settles them at the
merge.

**Decision.** A procedure's or function's parameter may be declared to hold JSON of a schema, in the config file
(`parameters["shop.place_order.@order"].json`) or in the SQL (the extended property `haruspex.jsonSchema` on the parameter, read as
[decision 0141](0141-a-column-s-json-schema-may-be-named-in-the-sql.md) reads it on a column). The declaration is a claim about
every caller, not a fact, and is recorded as declared, never as proven.

- **The body's reads take it as given.** Until it is assigned, the parameter holds JSON text of the schema's type (live JSON for a
  `json` parameter), as a declared column holds its JSON
  ([decision 0093](0093-a-column-s-json-is-part-of-its-type-completed-by-the-config.md)). So `json_value(@order, '$.mode')`,
  `json_query`, `OPENJSON` with or without `WITH`, and a variable assigned the parameter, walk the schema's type as for a column
  ([decision 0097](0097-a-path-into-json-of-known-shape-is-walked-step-by-step.md)). An assignment to the parameter replaces
  what it holds, so a read after one has no declaration. It holds in a procedure, a multi-statement function, a scalar function
  and an inline function; in an inline function the parameter is null exactly when the call's argument may be.
- **NULL stays wherever the server can give it.** A caller may send NULL, so the parameter keeps its `parameter` reason unless the
  body proves it isn't NULL (`IF @order IS NULL RETURN`). A key the schema doesn't require may be left out, so a lax path to it is
  nullable. Observed (`ParameterJsonObservationTests`): text that isn't a JSON object or array fails the call with 13609 under a
  lax path as under a strict one, not NULL, and the IR describes calls that succeed; JSON
  of another shape gives NULL under a lax path. That last case is the assumption the declaration makes, the same one a declared
  column makes about every writer: a required key under a parameter proved not NULL is claimed not null, as declared, where a
  path finds it whatever a caller's escaping ([decision 0147](0147-a-key-a-writer-may-escape-may-be-missing-from-a-path.md)).
- **A `varchar` or `char` parameter's code page.** Its text is in the database's code page, so a character the code page lacks,
  sent as itself, arrives as another (`Ω` as `O`; through the driver, as `?`), though sent as a JSON escape (`\u03a9`) it is kept
  (`ParameterJsonObservationTests`). A caller may send either, so the body reads it as the database's code page gives it back, as a
  declared `varchar` column is read ([decision 0145](0145-varchar-json-is-read-through-its-code-page.md)); what a caller sends
  keeps all the schema says.
- **A default other than NULL** is what a caller that leaves the parameter out sends, which no declaration covers, so the body
  starts from the declared JSON joined with the default's: a literal's JSON, or unknown for anything else. An inline function
  called with `DEFAULT` reads the default as it is.
- **What can be declared.** A parameter of a procedure or function the project defines, not `OUTPUT`, of a string type (`char`,
  `varchar`, `nchar`, `nvarchar`, an alias type of one) or `json`. Anything else is HAR4117. A numbered procedure's parameter
  (`shop.get_order;2.@order`) can't be declared: haruspex reads no numbered procedure (HAR1002), and SQL Server takes no extended
  property on one's parameter (15135), so the config file's key is HAR4117 and an annotation HAR4115. The schema's top can't allow
  `null` (HAR3115): SQL Server's JSON functions refuse the text `null`, and a caller with no value sends NULL.
- **Agreement.** Where the config file and the SQL both declare a parameter, they must name the same file and pointer; otherwise
  the run stops (HAR4114) and neither is taken.
- **The generated function takes the value** (the maintainer's choice, 27 Nov 2026): the parameters' Zod schema checks it against
  the declared schema, serialises it with `JSON.stringify`, and checks the text as the parameter's type checks any text sent to it
  (its length; for `varchar`, what its collation says of the code page), so the function sends the text. A `char` or `varchar`
  parameter's string values have every character past ASCII escaped first
  ([decision 0145](0145-varchar-json-is-read-through-its-code-page.md)). The parameters' exported
  type is `z.input` of the schema where a parameter is declared, the value a caller passes, and `z.infer` elsewhere, where the two
  are the same. The schema of the declared JSON is the module's schema of that type, as a result's JSON is read, so a closed
  object's unlisted key is removed (or refused under `"objects": "strict"`) and an open one's kept.
- **IR 0.37.** The document's `parameters` lists each declaration its objects take or rest on, with the hash of every schema file
  it read, so `check` reports a changed schema file, a changed, added or removed annotation, or a changed config entry. A
  parameter's `content` is what a non-null value of it parses to; its `type` stays its SQL type, which still bounds the text sent.
  An object that takes a declared parameter, or reaches a body that does (a function it calls, a procedure it calls), lists a
  `parameter-json` claim naming it in `via`.

**Context.** Procedures that take a document (`@order nvarchar(max)`) and read it with `OPENJSON` and `JSON_VALUE` are common in
applications that post JSON. Without a declaration, every read of it is a string of unknown content. ANN1 named a column's schema
in the SQL and left a parameter's annotation as a warning (HAR4116) until the inference, the IR and the emitters could take it.

**Alternatives.**
- *Every read of a declared parameter nullable, whatever the schema.* Stricter than the column rule, and it would throw away what
  the declaration says where the body has proved the parameter isn't NULL. A declaration is a person's claim either way, and the
  generated code checks what it can before a call is sent.
- *Infer the parameter's shape from the body's reads* (the roadmap's JSON parameters, 6n). That is what the procedure reads, not
  what callers send, and it can't say what a read it doesn't understand expects. A declaration states the contract; inference from
  reads may come later and be checked against it.
- *Under `procedures`, by parameter name.* `procedures` keys a procedure only, and a function's parameter needs declaring too. A
  top-level `parameters` keyed `schema.object.@name` serves both, and mirrors `columns`.
- *The generated function takes the text*, checked against the schema by parsing it. The caller keeps its bytes, but writes and
  reads the JSON itself, and the function's type says only `string`.
- *Record every declaration in every document*, as `columns` does. A declaration of another procedure's parameter says nothing of
  a document's objects, and would move every document to 0.37 for nothing.
- *Record only the bodies that read the parameter.* Which value a result holds is not followed back to the parameter, so a body is
  recorded as resting on the declaration when it takes the parameter, which says no less than the results rest on.

**Reason.** A parameter's declaration is the same kind of claim as a column's, made about callers rather than writers. Reading it
with the column's rules keeps one meaning for declared JSON; keeping NULL where the server gives it, and the `parameter` reason
where nothing proves it absent, keeps the claim to what the declaration says.

**What it costs.** A caller that sends JSON of another shape gets NULLs where the types say otherwise; the generated function's
check is the guard, and callers outside the application have none. A procedure that takes a declared parameter lists the claim
even when its results don't read it.

**In the code:** `ParameterLowering.Sent` and `ZodExpression.JsonSent` in the Zod emitter; `ParameterDeclaration`, `ParameterDeclarationCheck`, `JsonSchemaAnnotations` and `ColumnSchema.DeclareParameter`
in the config layer; `DeclaredParameter`, `DeclaredParameters`, `Walker.Walk`, `FunctionSummaries.Parameter` and
`Typer.Argument` in the inference; `IrParameterDeclaration`, `IrParameter.Content` and `IrOverrideSetting.ParameterJson` in the
IR; `ParameterDeclarationTests`, `ParameterJsonObservationTests` and `MssqlFunctionTests` (through the generated function); the fixture `parcels`, whose `quote_options` names its
parameter's schema in its script.
