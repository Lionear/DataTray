# Visual Query Builder (core)

← [Plugins overview](../../docs/PLUGINS.md)

The pure core of the Visual Query Builder plugin: a query **model**, a **generator** per dialect, a **reader** that
turns SQL back into the model, and a **read-back check** that decides whether a query may be shown in the builder.

There is **no UI yet** (the pane, the plugin entry class and staging into the desktop app are not yet implemented) and
**no DML generation or reading yet**. `INSERT`/`UPDATE`/`DELETE`/`MERGE` exist in the model only; the generator answers
`dml-not-yet` and the reader refuses them with the same code. The core has no Avalonia, no host types and no I/O.

```
Model (Statement) ──SqlGenerator + IDialect──▶ SQL text
SQL text ──ReadBack (reader)──▶ Model ──SqlGenerator──▶ SQL text ──token compare──▶ shown / refused
```

## Layout

| Folder | Contents |
|---|---|
| `Model/` | Immutable records: `Query`, `Select`, `SetOp`, `With`, sources, expressions, windows, DML statements. `QueryModelJson` (de)serialises them with a root `formatVersion` (a file without one is version 1); `Deserialize` returns a `QueryModelLoadResult` whose `Error` explains bad content instead of throwing. |
| `Dialects/` | `SqlDialects.For`, `IDialect`, `DialectCapabilities` and the style enums. |
| `Generation/` | `SqlGenerator.Generate(statement, dialect)` returning `GenerationResult(Sql, Problems)`. |
| `Reading/` | `ReadBack.ReadAndVerify`, the only public read path; the readers, the script scanner and the token comparison are internal. |

## Dialects and capabilities

`SqlDialects.For(providerId, serverVersion)` takes a provider id (`sqlserver`, `postgres`, `mysql`, `sqlite`; anything
else throws `NotSupportedException`) and the server's version string. The version is parsed into a `DialectCapabilities`
record. **A null or unparseable version turns every version-gated capability off**: the builder never guesses a feature on.
A major-only version (`16`) reads as `16.0.0`; a number too large to parse makes the version unknown. **MariaDB** (a version
string containing `MariaDB` on the `mysql` provider) is treated as an unknown version: its numbering and features differ
from MySQL's, so every version-gated feature is off.

| Capability | sqlserver | postgres | mysql | sqlite |
|---|---|---|---|---|
| Identifier quote | `[ ]` | `" "` | `` ` ` `` | `" "` |
| Row limit | `TOP` / `OFFSET … FETCH` | `LIMIT` | `LIMIT` | `LIMIT` |
| Concatenation | `CONCAT()` | `\|\|` | `CONCAT()` | `\|\|` |
| RIGHT / FULL JOIN | yes / yes | yes / yes | yes / no | 3.39+ |
| LATERAL / APPLY | APPLY | LATERAL | LATERAL 8.0.14+ | no |
| Window functions | yes | yes | yes | 3.25+ |
| Named `WINDOW` | 16+ | yes | yes | 3.25+ |
| `GROUPS` frame / `EXCLUDE` | no | yes | no | 3.28+ |
| `RANGE` with offset | no | yes | yes | 3.28+ |
| INTERSECT / EXCEPT | yes | yes | 8.0.31+ | yes |
| INTERSECT / EXCEPT `ALL` | no | yes | 8.0.31+ | no |
| MERGE (`NOT MATCHED BY SOURCE`) | yes (yes) | 15+ (17+) | no | no |
| Boolean literals | no (`1` / `0`) | yes | yes | 3.23+ |
| `VALUES` as a query | no | yes | 8.0.19+ | yes |
| Quantified subquery (`ANY`/`ALL`) | yes | yes | yes | no |
| Case-insensitive `LIKE` | `LOWER()` both sides | `ILIKE` | `LOWER()` both sides | `LOWER()` both sides |
| Nested `WITH` | no | yes | yes | yes |
| `RETURNING` / `OUTPUT` | `OUTPUT` | yes | no | 3.35+ |
| Upsert | none | `ON CONFLICT` | `ON DUPLICATE KEY` | `ON CONFLICT` 3.24+ |
| `UPDATE … FROM` | from-join | `FROM` | join-set | 3.33+ |
| `DELETE` with join | from-join | `USING` | from-join | no |

The remaining flags (`OrderByInSubqueryNeedsLimit`, `LimitInInSubquery`, `OrderByInSetOperand`,
`ParenthesizedSetOperands`, `IntersectBindsTighter`, `UnquotedFoldsToLower`, `RecursiveKeyword`) are read from
`DialectCapabilities` directly; `SqlDialects.cs` is the source of truth for every cell above.

## Generator rules

- **Object names are always quoted** (`"Researcher"`, `[Researcher]`), with the quote character doubled inside the name.
- **Simple table and window aliases are bare** (`r.[FullName]`); they are quoted when they are reserved words (of any of the
  four dialects) or contain anything but letters, digits and `_`. On postgres only lower-case names stay bare, because
  postgres folds unquoted names to lower case. Select-item aliases (`AS "x"`) are always quoted.
- **Problems instead of rewrites.** A model the dialect cannot express yields `Sql = null` and a list of
  `GenerationProblem(Code, Message, Node)`. Nothing is silently changed into something similar.
- **Predicate as value.** A predicate (`=`, `LIKE`, `IN`, `EXISTS`, `AND` …) used where a value is expected, such as a select
  item, is `predicate-as-value`, because SQL Server has no boolean type. The reverse, a value in `WHERE`, `ON`, `HAVING` or
  under `NOT`/`AND`/`OR` (`WHERE [a]`), is `value-as-predicate`. A boolean literal used as a predicate becomes `1 = 1` /
  `1 = 0`; a `Fragment` is passed through in either position.
- **Limits.** SQL Server writes `TOP (n)` on a plain `SELECT` and `OFFSET … FETCH` when there is an offset or a set
  operation. Others write `LIMIT n OFFSET m`. `OFFSET` without `ORDER BY` is `offset-needs-order-by`.
- **Narrow strings.** `LiteralKind.NarrowString` is a `'…'` literal without the `N` prefix; it exists so that SQL Server
  `varchar` text can be read and written back exactly. Plain strings are `N'…'` on SQL Server.
- **Output shape.** Upper-case keywords, one clause per line, nested queries indented two spaces, one statement ending in `;`.
  Parentheses are added only where the tree needs them.

Problem codes the generator can emit: `dml-not-yet`, `nested-query-body`, `nested-with`, `order-by-needs-limit`,
`limit-in-in-subquery`, `invalid-limit`, `offset-needs-order-by`, `empty-with`, `empty-cte-columns`, `intersect-except`,
`intersect-except-all`, `order-by-in-set-operand`, `parenthesized-query`, `empty-values`, `empty-select`, `join-needs-on`,
`join-unexpected-on`, `right-join`, `full-join`, `predicate-as-value`, `value-as-predicate`, `quantified-subquery`, `invalid-quantified-op`,
`invalid-number`, `invalid-boolean`, `concat-arity`, `window-functions`, `invalid-frame`, `groups-frame`, `range-offset`,
`frame-exclude`, `invalid-frame-offset`, `named-window`.

### Semantics that differ between dialects

- **CONCAT and NULL.** SQL Server `CONCAT()` treats NULL as `''`; `||` (postgres, sqlite) and MySQL `CONCAT()` return NULL if
  any part is NULL. The same model can produce different results per server.
- **MySQL string escaping.** Literals escape `\` as `\\` and `'` as `''`. This assumes `NO_BACKSLASH_ESCAPES` is **off**; with it
  on, the generated literal means something else.
- **LIKE and backslash.** The generator emits no `ESCAPE` clause. A backslash in a pattern is an escape character on MySQL (and
  PostgreSQL), but an ordinary character on SQL Server and SQLite. The builder passes the pattern through unchanged.

## Reader

Reading goes through `ReadBack.ReadAndVerify` (below). Internally a reader per provider sits behind `ISqlReader`; no parser
type leaves `Reading/`.

| Provider | Parser |
|---|---|
| `sqlserver` | `Microsoft.SqlServer.TransactSql.ScriptDom` 180.37.3 (same version as the host formatter) |
| `postgres`, `mysql`, `sqlite` | `SqlParserCS` 0.6.5 |

The result is a `ReadResult(Outcome, Statement, Reasons, FragmentCount, CommentCount)`:

- **Read**: the whole query maps onto the model.
- **ReadWithFragments**: it maps, but some pieces are kept as verbatim text (`FragmentSource` / `Fragment` expressions), for
  example table hints, `COLLATE`, or an unknown function. `FragmentCount` says how many.
- **CannotShow**: no statement; `Reasons` lists each finding as `ReadReason(Code, Message, Line)` with a 1-based line.

`CommentCount` is the number of comments that reading dropped. Comments are not part of the model.

**Refused** (a script scan runs first and reports every finding with its line): `multiple-statements`, `set-variable`,
`procedure-call`, `control-flow`, `dml-not-yet`, `not-a-query` (DDL and other statements), `parse-error`, and
`cannot-represent` for query shapes the model does not hold (T-SQL `FOR XML/JSON`, `SELECT INTO`, `OPTION (…)`, `COMPUTE`,
`ROLLUP`/`CUBE`/`GROUPING SETS`, graph `MATCH`). Dialect-specific constructs the scanner refuses: `escape-string` and
`prefixed-string` (postgres `E'…'`, `B'…'`; `E'…'` on mysql and sqlite), `unterminated-quote` (a string, quoted name or
comment that runs to the end of the text, refused before any parser sees it), `adjacent-strings`, `sqlite-cast`, `mysql-modifier`, `mysql-operator`,
`mysql-like-escape`, `mysql-double-dash`, `mysql-executable-comment` (`/*! … */`, MariaDB `/*M! … */`).

**Known gaps**

- `EXCLUDE` in a window frame is not parsed by SqlParserCS 0.6.5 (`parse-error` on postgres and sqlite).
- `OPTION (…)` is refused rather than kept, because the model has no verbatim clause slot. Follow-up: add one on `Query`.
- Redundant parentheses and similar spellings are refused (see below).

### Read-back verification

`ReadBack.ReadAndVerify(sql, providerId, serverVersion)` reads the query, generates it again from the model, lexes both
texts the way that server does (`SqlTokens`), and compares the **tokens**. Equal tokens: the result is returned as read.
Any difference, or a model the generator cannot write: `CannotShow` with `would-change` (naming the first differing token
and its line) or `cannot-generate`.

The rule is deliberate: **a false refusal is acceptable, a false acceptance is not.** The builder may decline a query it could
have shown, for example `((1 + 2))` or a differently spelled but equivalent form. It must never show a query whose
regenerated SQL means something other than what the user typed.

## Tests

`tests/DataTray.Extensions.QueryBuilder.Tests`, run with `dotnet test tests/DataTray.Extensions.QueryBuilder.Tests`.

- **Goldens**: `Fixtures/<case>/` holds `model.json` plus, per dialect, either `<dialect>.sql` or `<dialect>.problems.txt`.
  Versions are pinned in `Fixtures/_versions.json`.
- **Three validity proofs**, so a golden cannot be merely self-consistent: SQLite runs `EXPLAIN` on every sqlite golden against
  a demo schema (`Fixtures/_schema/sqlite.sql`); ScriptDom parses every sqlserver golden; SqlParserCS parses the postgres and
  mysql goldens.
- **Round-trip**: every SQL golden is read back and must equal its model. Cases the reader cannot keep are named in the test
  with the expected reason code (`parse-error`, `would-change`).
- **Read-back**: `ReadBackTests`, `SqlReaderTests` and `SqlTokensTests` cover refusals, fragments, line numbers and lexing.

**Adding a golden**: create `Fixtures/<new-case>/model.json` (copy a neighbour), write the expected `<dialect>.sql` for each
of the four dialects, or `<dialect>.problems.txt` with one problem code per line where the dialect cannot express it. Every
golden test, proof and round-trip picks the folder up automatically. Keep the SQL in the fixed output shape above.
