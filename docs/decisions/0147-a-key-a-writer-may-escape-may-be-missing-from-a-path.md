# A key a writer may escape may be missing from a path

**Date:** 28 Nov 2026.

**Decision.** A path finds a key only as it is spelled, so whether it finds a key depends on who wrote the JSON. Of JSON an
application wrote (declared for a column, a parameter or a REST endpoint's response, or a declared result), a key is read as
the schema says only when every character of it is an ASCII letter or digit, the blank, or one of `! # $ % ( ) * , - . : ; ? @
[ ] ^ _ { | } ~`: no common writer, run or documented, escapes those. A key with any other character may be missing, and the value read is
nullable, with a reason that names the key and the characters (for `café`, its `é`). Of JSON `FOR JSON` or
`json_object` built here, SQL Server's own spelling decides: it writes a key's `/`, `\` and control characters as escapes,
which a path can't spell, so such a key is never found; every other key is. A key written in a literal or assembled text holds no escape, so it is found. One rule (`KeySpelling`), in the
walk every path read shares (`JsonPathWalk`), decides it for `JSON_VALUE`, `json_query`, `OPENJSON` at a row path and
`OPENJSON … WITH`. A plain read, which a parser decodes, keeps every key.

**Context.** A declared schema says a key is required, and the column rule read that key non-null where the column can't be
NULL. The schema describes the decoded document; the stored text may spell the key with escapes and still satisfy it. ANN3's
reviewer observed that `JSON_VALUE(N'{"\u0041":"x"}', '$.A')` is NULL, in `nvarchar` too. So the claim was unsound for any
writer that escapes a key's characters, and Python's `json.dumps` escapes every character past ASCII by default.

Observed (`EscapedKeyObservationTests`, SQL Server 2025 RTM-CU10, Node 24, .NET 10), under `nvarchar`, `varchar` of code page
1252 and of UTF-8, and `json`: a path finds a key only when the two are spelled the same, character for character. Stored
`\u0041`, `$.A` finds nothing and `$."\u0041"` finds it; stored `A`, `$."\u0041"` finds nothing; `\u002b` and `\u002B`
differ, as do `\/` and `/`, and `\u0022` and `\"`. `OPENJSON`'s `key` is always decoded. The writers, in keys:

| Writer | Escapes | Known by |
|---|---|---|
| `System.Text.Json`, default encoder | control characters, `` " & ' + < > \ ` ``, DEL, every character past ASCII | run in the test |
| `JSON.stringify` | control characters, `"`, `\`, a lone surrogate | run in the test |
| `FOR JSON` (`PATH` and `AUTO`), `json_object`, `json_objectagg` | control characters, `"` (as `\"`), `\`, `/` | run in the test |
| `json_modify` | none: it writes the key as the path spells it | run in the test |
| Python's `json.dumps`, default `ensure_ascii` | control characters, `"`, `\`, DEL, every character past ASCII | run by hand, Python 3.14 |
| Newtonsoft.Json, by default | control characters, `"`, `\` | its documentation |
| Gson, by default (HTML-safe) | control characters, `"`, `\`, `< > & = '` | its documentation |
| Go's `encoding/json` | control characters, `"`, `\`, `< > &`, U+2028, U+2029 | its documentation |
| PHP's `json_encode`, by default | control characters, `"`, `\`, `/`, every character past ASCII | its documentation |

Python isn't promised by the test environment, so its row was run by hand; the last four aren't in the toolchain, and adding
one would be a new dependency, so they are read from their documentation.

**The assumption.** Any writer may escape any character: JSON allows `\u0061` for `a`. So the rule is a claim about writers,
not about the server: that every common writer, run or documented, writes a letter, a digit, the blank and the punctuation above
as themselves. That is why every other character is left out, even where only one writer escapes it (`=`, for Gson). A writer
that escapes a letter would make a key of letters missing where the types say not; none is known.

**Alternatives.**
- *ASCII only.* It keeps `a+b` and `<id>` non-null, which `System.Text.Json` escapes by default, and that is a writer in the
  toolchain of nearly every team that writes SQL Server JSON from .NET; and `a=b`, which Gson escapes.
- *Letters, digits and `_` only.* Sound, but it loses `unit price`, `a-b` and `a.b`, which no writer escapes.
- *Every key nullable under a path.* Sound, and it would throw away the claim for every key, the common case included.
- *A declaration that its writer keeps keys literal*, restoring the claim for such keys. Not built: it is a claim about every
  writer the column ever had, which nobody can check, and the keys it would restore are rare. Richard's to decide.
- *Treat SQL Server's spelling as an application's.* Sound, but it would make `FOR JSON`'s `café` nullable, which SQL Server
  writes as itself, and say a path may find `a/b`, which it never does.

**Reason.** A declaration describes the decoded document; a path reads the text as written. What a read claims must hold
however a writer spelled what the schema allows.

**What it costs.** A required key outside the line, read by a path under lax, is nullable even where every writer the team
has writes it as itself; under strict it stays non-null, since a key not found is an error there. An object's writers aren't
in the IR, so JSON built here that passes through the IR (a declared result) is read as an application's.

**In the code:** `KeySpelling` and `KeyWriters` in the inference, used by `JsonPathWalk`; `ObjectShape.Writers`, set by `FOR
JSON`, `json_object`, `JsonLiterals`, `JsonAssembly` and `RestCalls`, and joined by `JsonShapes`; `KeySpellingTests`,
`EscapedKeyTests` and `EscapedKeyObservationTests`.
