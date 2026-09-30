---
description: "SQL lexer, scope reader and completion: the rules and evidence behind packages A-R."
paths:
  - "src/PgNimbus.Core/Text/**"
  - "src/PgNimbus.Core/Schema/**"
  - "src/PgNimbus.App/Completion/**"
  - "**/QueryEditorPanel*"
  - "**/*Completion*"
  - "tools/CompletionBench/**"
---

<!-- Moved out of .claude/CLAUDE.md so it loads only when working on these paths. Same rule applies: keep it current in the same PR. -->

# SQL text and completion

- **SQL text has one lexer, and completion reads one statement the way the
  server would** (2026-09, first delivery of
  [`docs/dev/design/sql-editing-experience.md`](docs/dev/design/sql-editing-experience.md),
  packages A–E). `Text/SqlLexer` (Core-pure, unit-tested, including a
  generative "tokens exactly tile any text" check — it runs per keystroke on the
  UI thread, so a zero-width token would hang the app) is the single definition
  of strings (`E'…'` backslash escapes, `U&`/`B`/`X`/`N` prefixes), quoted
  identifiers, `$tag1$`/`$тег$` dollar quotes and nested comments. A `--`
  comment ends at `\n` *or* `\r`, as the server's scanner has it: ending it at
  `\n` alone let a bare `\r` hide `; COMMIT; CREATE …` from the splitter, and
  Explain's one-statement check passed text the server then ran (2026-09,
  review of the audit fixes).
  `SqlScriptSplitter` and `SqlCompletionContext`'s caret/mask scans ride it;
  before that each had its own scanner and they disagreed — `E'can\'t;stop'`
  split in two, and completion opened inside `$tag1$…$tag1$`. `SqlFormatter`
  reads it too (its `Tokenize` is an adapter: runs of operator characters are one
  operator, `$1` a word, brackets operators); its own scanner had formatted
  `1_000` as `1 _000` and `N'x'` as `N 'x'`, both changes of meaning. Its
  round-trip check still compares two runs of one tokenizer, which a misreading
  tokenizer passes on both sides, so `SqlFormatterLexicalTests` pins literal
  expected output for the lexically tricky inputs. `BrowseSqlParser` reads it
  as well, keeping its contract: any still-open token means "not a browse
  query", and only a plain `'…'` string is a literal a typed chip may take —
  E/B/X/N/U& strings, dollar quotes, `U&"…"` and `$1` stay raw, verbatim chips
  (its own scanner had closed a nested comment at the first `*/`, keeping the
  rest as a raw condition that would have gone back into the SQL broken, and
  had read `0x1F` as a typed value).
  Five rules the provider now keeps, each a reproduced bug in the audit:
  (a) **The statement is the unit** — `CompletionStatementSpan` is the text
  between the real `;` tokens around the caret, the part right of it included (a
  FROM typed after the select list names the list's sources); a caret right after
  `;` is a new, empty statement. That is deliberately *not* `StatementSpanAt`,
  which picks the previous statement from a trailing gap so Run/Format have
  something to act on. (b) **Names fold like the server folds them**: a bare
  identifier is ASCII-lowercased, a quoted one kept exact (`TableRef` and
  qualifier chains carry the folded name), and a short table name resolves along
  `search_path` (`SchemaService.GetSearchPathAsync`, from a pooled connection, so
  the connection *default*); unknown path → only a name exactly one schema has.
  `missing.users` never borrows `public.users`' columns and `public.users.` never
  lists `audit.users`' — the old cache merged same-named tables under the short
  name. (c) **A column two sources share is offered per source, qualified**
  (`u.id` / `o.id`, `SqlCompletionData.DisplayText`), unless USING/NATURAL merged
  it; a self-join is two sources. (d) **An accept is one edit**:
  `Text/CompletionEdits.Plan` (Core-pure) decides the replaced range — the whole
  token, past the caret and including a quoted identifier's quotes — a callable's
  parens (reusing a `(` already there), and the auto-alias (skipped when one is
  already typed), and `SqlCompletionData.Complete` applies it as a single
  `Document.Replace`. The alias used to be `Dispatcher.Post`ed a frame later: two
  Undo steps, and a quick tab switch could land it in another document. The
  popup's filter starts at the word start (`CompletionToken.FilterStart`), so
  Ctrl+Space after `sel` filters on `sel`; a word that matches nothing *closes*
  the popup (a hidden one still sat on the keyboard) and Backspace reopens it.
  (e) **Expand `*` declines rather than change the result**: a bare `*` over
  `USING`/`NATURAL` or a FROM item it can't read (subquery, function, LATERAL)
  refuses, and the reason goes to the tab's status line. The catalog behind all
  of this is one immutable snapshot (`SqlCompletionProvider.Load(CompletionCatalog)`
  — also the test seam: `tests/PgNimbus.App.Tests/CompletionProviderTests` runs the
  audit's catalog in memory), a refresh that finishes after a newer one started
  is dropped, relation names come without `pg_total_relation_size`
  (`GetRelationNamesAsync`), and foreign keys touching an excluded schema are
  dropped with it.
  **What a name can refer to is decided per block, not per statement**
  (package F). `Text/SqlScopes.cs` (`SqlScopeModel`, Core-pure, unit-tested in
  `SqlScopeModelTests`) reads the statement's tokens into queries and blocks: a
  query is an optional WITH list plus its set-operation branches, a block is one
  SELECT / VALUES / INSERT / UPDATE / DELETE / MERGE with its sources, its output
  items (select list, RETURNING, `column1…N`) and the queries nested in it. Each
  nested query carries a role, and the role *is* the visibility rule, taken from
  PostgreSQL: `Expression` (EXISTS/IN/scalar) sees every level around it,
  `Derived` (a FROM subquery, an INSERT's source query) sees the levels above its
  block but never its FROM siblings, `Lateral` also sees the FROM items before
  it, `Cte` sees what its owning query sees plus the CTEs before it (all of them
  under RECURSIVE). The provider asks `BlockAt(caret)` and resolves only through
  `VisibleSources` (innermost level first; an inner name hides an outer one) and
  `VisibleCtes`. Outer-level columns are offered *qualified* (`u.name`), since a
  bare name that also exists inside would bind to the inner column. A derived
  table or CTE is resolved through its first branch's output, stars spelled out
  through that branch's own sources, a column alias list renaming positionally;
  a CTE reaching itself through a star stops (visited set) instead of recursing.
  A select list whose block has sources is scoped like a predicate: another
  branch's or the catalog's columns aren't legal there.
  `SqlCompletionContext.ExtractCteDefinitions`, the older whole-statement
  reading, reads at most `MaxCteDefinitions` (32) CTEs and no body past
  `MaxCteBodyLength` (32K characters; that CTE is known by name only): every body
  was read on its own and a nested WITH's outer bodies hold all the inner ones,
  so 2,000 nested CTEs (42k characters, still on the UI thread) cost 3.4 s per
  popup (2026-09, review of the audit fixes). Three things to keep:
  the reader never guesses — past `SqlScopeModel.MaxDepth` (32) nested queries a
  query is `IsOpaque` and the caret inside it gets **no** columns, not the outer
  ones (since 2026-09, security audit finding 16, parenthesized expression groups
  and nested join trees count against the same depth and go opaque the same way,
  and `IsQueryStart` walks a paren run instead of recursing: `SELECT ` +
  `(`×20000 used to overflow the stack, which .NET cannot catch, per keystroke
  and after every Run; `SqlKeywordGrammar.Governing` had the same recursion per
  unclosed group and is iterative now. `Text/HostileText` is the shared
  generator and `ParserRobustnessTests` runs every UI-thread reader —
  `SqlScriptSplitter`, `SqlFormatter`, `SqlCompletionContext`, `SqlCallSite`,
  `SqlKeywordGrammar`, `SqlValueSlot`, `SqlStatementInspector`, the scope model
  — over every printable character in every position and a hundred thousand
  nested parens on a 256 KB stack, a quarter of a production thread's); a
  statement with no query in it (DDL, SET) has `Root == null` and keeps
  the old whole-statement reading (`ExtractTables`), which is also still what
  `CompletionEdits`' alias picking and `ExpandSelectStar` use; and only EXPLAIN
  may be followed by DML — after `CREATE …`, `UPDATE`/`TABLE` are DDL words.
  **Some positions take exactly one relation's bare columns, and nothing else**
  (package G). A block also records its top-level clause keywords
  (`SqlBlock.Clauses` / `ClauseAt`) and the insides of `INSERT INTO t (…)`,
  `ON CONFLICT (…)` and each `JOIN … USING (…)`; the provider's
  `ColumnListCompletions` answers those before any clause logic: the target's
  columns in the INSERT list, the conflict target and a SET assignment's left
  side (`SqlScopeModel.IsAssignmentTarget`: after SET or a top-level comma,
  before `=`; unqualified, since `SET t.col` is an error), and in USING only the
  columns *that* join's right item shares with the items before it. A column
  already written in the list is left out. `excluded` (the proposed row) is
  offered from `DO` to RETURNING and never in RETURNING (`SqlBlock.SeesExcluded`).
  Output aliases are offered in ORDER BY / GROUP BY and not in WHERE/HAVING — the
  clause, not the block, decides. A select-list/RETURNING/SET-value caret in any
  block with sources is scoped like a predicate. JOIN … ON offers **one condition
  per FK constraint** (`ForeignKeyMatcher.BuildJoinConditions`, closest table
  first, the constraint name in the row's detail): two FKs between one pair are
  two different joins, and the old "first edge found" picked one at random.
  `ForeignKeyInfo.ConstraintName` carries the name (`con.conname`; the query
  groups by it, so a composite key stays one row).
  **Argument hints and cast types** (package H). `Text/SqlCallSite.At`
  (Core-pure, `SqlCallSiteTests`) reads the innermost call around the caret
  from lexer tokens — commas inside strings, `ARRAY[…]`, nested calls and
  subqueries don't count; a `(` after a keyword (`IN`, `VALUES`, `EXISTS` …)
  is not a call; `arg => …` / `arg := …` names the argument — and
  `Schema/SignatureHints.For` picks the overloads that can still take that
  argument (enough parameters, or a VARIADIC last), falling back to all of them
  unmarked rather than hiding the hint. Overloads come from pg_catalog *and*
  the schemas: `CompletionCatalog.BuiltinFunctions` is pg_catalog's list, read
  at refresh for hints only and never offered as candidates (thousands of
  internal overloads). The hint is a `Popup` in `QueryEditorPanel.axaml` (no
  new permanent control, UI rule 1) anchored just above the call's line so the
  completion list below the caret never covers it — except when the editor
  has no room above that line (a call on its first visible line), where it
  opens below the line instead: the popup lives in the window's overlay layer,
  which nothing clips to the editor, so it used to open over the toolbar
  (found live 2026-09-22; test `A_signature_hint_on_the_first_line_…`, screenshot
  scenario `main-window-signature-hint`). It opens on `(`, on
  accepting a function, or on Ctrl+Shift+Space (`CommandId.ParameterHints`,
  literal Ctrl like completion), follows the caret while live, and closes when
  the caret leaves every call or on Escape. One landmine: the auto-closed `)`
  is inserted *at* the caret, which moves the caret past it for a moment — that
  reads as leaving the call, so the hint is opened only after the caret is put
  back. Casts: after `::` or `CAST(… AS`, the list is types only —
  `SchemaService.GetTypesAsync` (base/enum/range/multirange types, domains and
  free-standing composites; not table row types, arrays or pseudo-types), a
  built-in inserted by its `format_type` spelling when that is one word
  (`integer`) else by pg_type's name (`timestamptz`), a user type
  schema-qualified off the search_path; a short built-in list stands in until
  the catalog is read. The record is `DataTypeInfo` — `TypeInfo` collides with
  TUnit's and System.Reflection's.
  **The catalog has a lifecycle, and the keystroke path is measured**
  (package I; numbers in the design doc's §8.1, from `tools/CompletionBench`).
  `RefreshAsync` never throws: a failed read keeps the previous snapshot and
  sets `Status` stale, which `MainViewModel` shows on the tab's status line;
  a newer refresh cancels an older one mid-read, `Dispose` (called first in the
  window's `Closed`, which is also the switch-connection path) cancels
  everything, and the snapshot is built on the thread pool — 0.5 s for a
  million columns that used to land on the UI thread. After a run that got
  through, `SqlStatementInspector.ChangesCatalog` (CREATE/ALTER/DROP/IMPORT,
  `SELECT … INTO`) triggers a refresh; inside an explicit transaction it waits
  for the transaction's end (the DDL is invisible to the pooled connection the
  catalog is read from until then), and a `SET search_path` there
  (`SetsSearchPath`) sets `SessionSearchPathChanged`, under which short names
  resolve as if the path were unknown until the transaction ends — a SET
  outside one does not outlive its statement, because the pool resets the
  session. Two performance rules that the numbers forced: the snapshot's lists
  are never regrouped per popup (`Merge` puts the per-caret items in front of
  an already-unique list; the dedupe key is cached on the item) — regrouping a
  million-column catalog cost 35 ms per open; and each keystroke ranks only
  the previous keystroke's matches (`CompletionRanker.Rank(…, within, out
  matched)`, exact because a subsequence of the longer query is one of the
  shorter — a generative test holds it to ranking everything). Documents of
  `QueryEditorPanel.BackgroundCompletionThreshold` (50k) characters or more are
  read for completion on the thread pool; the answer is shown only when the
  request number, `_documentEdits` and the caret are all unchanged.
  **Enter accepts only what was chosen, and only if it changes the text** (the
  §6.1 rule, decided 2026-09-22 and tightened by the second audit on
  2026-09-27; `Text/CompletionAcceptance`, Core-pure, unit-tested). Two
  conditions, both required: the accept must change the text — a row whose name
  is already written in full (`customer_id⏎`, `DESC⏎`, `true⏎` against `TRUE`:
  letter case is no change for anything unquoted) is left alone, and a name
  typed in full is not schema-qualified behind the user's back either (that is
  what kept `UPDATE customers⏎` from becoming `commerce.customers`); and the row
  must have been chosen (Ctrl+Space, `_completionExplicit`; arrows or mouse,
  `_userPickedCompletion`) or be the one whose name starts with what was typed.
  In a **new-name position** (`SqlCompletionContext.IsNewNamePosition`: an alias
  after a FROM/JOIN/UPDATE/MERGE item or after AS, a CTE name, the object a
  CREATE names, a column in a table definition, ADD COLUMN, RENAME … TO) only
  a chosen row is taken. (A third case, rows marked as guesses — catalog-wide
  columns, pg_catalog's rarer functions — was dropped on 2026-09-27: with the
  exact name ranked first, `SELECT query⏎` keeps `query` and the literal
  replay stays at 0 divergences without it.) Chosen but unchanged is still a newline; a chosen
  callable still gets its parens. A list that opened by itself with nothing
  typed, or holding only a loose fuzzy match, closes on Enter and the editor
  writes the newline. Tab always accepts. The two states look different: a highlight
  Enter would not take gets the `tentative` class on the list (an outline, not
  the fill; `Theme.axaml`). That style must target the row's
  `/template/ ContentPresenter#PART_ContentPresenter`, not the `ListBoxItem`:
  Fluent paints the selected fill on the template part, so a `Background` on the
  item is never drawn — which is how every tentative row shipped filled anyway,
  class set and all (found live 2026-09-22; the test reads the part's brush,
  not the class). The interception is in the tunneled
  `OnSqlEditorKeyDown`, which runs before the completion window's own Enter.
  **A finished FROM item is followed by a clause, not a relation** (2026-09-22):
  `SqlCompletionContext.IsAfterCompleteFromItem` (read up to the *start* of the
  word being typed, since the popup opens on its first letter) boosts
  `FromItemFollowItems` — WHERE, JOIN, LEFT, … in that order — so
  `FROM customers c w` preselects WHERE, not WHEN/WITH, which can't go there.
  A finished JOIN target still gets ON/USING first, and keeps it once a word is
  under way *if the alias is already written* (`JOIN customers c o` → ON; it
  used to fall back to FK-neighbour tables and Tab wrote a table), but not
  without one, where that word may be the alias. **The stock `CompletionWindow`
  re-selects a row on every caret move** (`SelectItem` on the typed prefix, by
  its own rules), and the `SelectionChanged` handler used to record that as the
  user's pick, which then outranked the ranking: `commerce.orde` kept
  `order_items` highlighted under `orders` and Tab wrote it.
  `_completionCaretMoving` — set by the editor's caret handler, which is
  subscribed before any popup exists and so runs ahead of the window's — makes
  that move not count; arrows and the mouse still do. Related landmine, fixed the
  same day: the scope reader used to read a dangling `FROM commerce.` as a
  *table* named `commerce`, so the `commerce.|` qualifier resolved to that
  columnless phantom source and the schema's tables never showed;
  `ReadRelationName` now returns it as schema `commerce` with an empty name.
  That bug predates 0.13 and outlived every test because each one put the caret
  into already-finished text; `CompletionProviderTests.Every_word_of_a_statement_is_offered_while_it_is_typed`
  replays statements word by word (left to right, and filling one word back in)
  and is the check for that whole class — give it a statement when adding
  grammar the provider has to follow.
  Not done yet: a token cache per document version, the first full ranking
  over a ~1M-row list (3.5 ms median), and package J.
  **The second audit measured typing, not parsing** (2026-09-27,
  [`docs/dev/design/sql-completion-audit-2.md`](docs/dev/design/sql-completion-audit-2.md),
  packages K–R, J folded into them). Typed without looking at the popup, with
  Enter at each line end, the 25-query corpus came out changed in 51 places:
  Enter took a row identical to the typed word (swallowing the newline), turned
  an end-of-line alias into a keyword or a table (`FROM customers c⏎` →
  `CROSS`), swapped a bare table for another schema's (`UPDATE customers⏎` →
  `UPDATE commerce.customers`) and `IS NULL` for `nullif(`. The stand is
  `tools/CompletionBench/Audit` (catalog snapshot, corpus, SaaS schema); the
  measures are `CompletionBench quality|cases|hints|dump` and
  `CompletionTypingReplayTests`. **Package K made the literal replay pass and
  it now runs in every build** (0 divergences with the auto-alias off and on);
  since package L the keystroke-saving oracle does too, with a floor
  (`IsGreaterThanOrEqualTo`) that every package raising the saving raises —
  about a minute of CI, the price of a ranking change that costs keystrokes
  failing the build instead of going unnoticed. Besides the Enter rule above, K ranks a name equal to
  what was typed first whatever its fuzzy score (`CompletionRanker`: `NULL`
  over `nullif`, `DESC` over `description`), and makes table position write a
  relation bare when its bare name finds it along the search_path
  (`TableRefItem`: `customers`, not `public.customers`), ranking it above
  same-named relations elsewhere (`PathTablePriority`) — schema-qualified
  otherwise, and under `SessionSearchPathChanged` only a name exactly one schema
  has goes bare (`TableRefItemsUnknownPath`). An FK-neighbour table no longer
  counts the relation being typed as "already joined", which had hidden the
  path's own table from the JOIN list.
  **Package L ranks in the order §6.2 of the audit sets** (2026-09-27).
  `CompletionRanker` sorts by match tier first (`CompletionMatchTier`: the
  exact name, then a prefix, then the starts of the name's parts — `oi` for
  `order_items` — then a substring, then any subsequence), and only within a
  tier by the context priority, then usage, then the fuzzy score and length:
  `em` → `email` over `error_message`, whose abbreviation used to out-score the
  real prefix. Which keywords may appear at all is `Text/SqlKeywordGrammar`
  (Core-pure, `SqlKeywordGrammarTests`), a deliberately local reading — the
  previous token and the clause word governing the caret at its paren depth —
  that answers `StatementStart` (the commands alone), `AfterOperand` (only
  what can continue a finished expression in that clause: `c.id = 1 |` →
  AND/OR/ORDER/GROUP/…, never a column, never ON), `KeywordsOnly` (after IS,
  ORDER, INSERT, UNION, a CTE body …), `Operand` (the keywords that can start
  an expression join the columns and functions, above the catalog, and every
  other keyword leaves the list) or `Unknown`, where the provider keeps its
  old list — DDL and utility statements, table positions and new names, all
  read elsewhere. It says Unknown rather than guess. The catalog marks
  machinery instead of guessing it from names: `FunctionInfo.IsInternal` is
  one EXISTS per place the server keeps it (a type's I/O and support
  functions, an operator's selectivity estimators, a boolean operator's
  implementation or one described as "implementation of …", `pg_amproc`, an
  aggregate's state functions, an access-method handler, `internal`/`cstring`
  and handler pseudo-types) — 372 of the stand's 379 extension functions, while
  `similarity` and `l2_distance` stay; an on-path function pg_catalog also has
  (pgcrypto's `gen_random_uuid`) is one row, not two; a partition
  (`CompletionRelationInfo.IsPartition`, relispartition) is offered only after
  its schema's `.`, under its parent. Usage is per connection:
  `Text/CompletionUsage` (accept count, then recency; a thousand rows, the
  least recently used dropped) replaced the session-only `CompletionRecency`,
  and `Settings/CompletionUsageStore` keeps it in its own
  `completion-usage.json` keyed `host/database` (the last 20 connections),
  written off the UI thread after every accept — its own file because
  settings.json is rewritten by every preference toggle. In a JOIN's ON, the
  columns of `alias.` that a foreign key ties to the other side come first, an
  FK the statement doesn't use yet before one it does (`u.id = i.` →
  `assignee_id`, `reporter_id`); after `schema.` in a JOIN, the tables an FK
  reaches first and the ones already joined last. One performance rule it
  forced: the snapshot's catalog-wide lists are `CandidateList`s, each row's
  kind kept in a byte array beside it, because `Merge` reading `Kind` off a
  hundred thousand rows was a cache miss per row (1–2 ms per open on the
  million-column bench; now an array copy, 0.1–0.8 ms).
  **Package M: a keyword never written alone is offered with the words that
  follow it** (C01–C03). `ORDER BY`, `GROUP BY`, `IS NOT NULL`, `LEFT JOIN`,
  `NULLS LAST`, `DO UPDATE SET`, `INSERT INTO` … are one row each (the lone
  `ORDER`/`GROUP` rows are gone), and their initials find them because the
  ranker's part starts split at spaces (`ob`, `inn`, `lj`). That forced one
  editor rule: **a character that can't be part of a name closes the popup**
  (`OnSqlTextEntered`; inside a quoted name it doesn't). Before it, the list
  kept filtering across the space — `IS N` still matched the `IS NULL` row —
  and an accept, which replaces only the word under the caret, wrote
  `IS IS NULL`; and a typed `*` matched the new star row and Enter wrote `**`.
  The grammar also reads what a call is (`SqlKeywordGrammar.At`'s `callKind`,
  prokind from the snapshot, `Snapshot.CallKindOf`): after a window function's
  call only OVER, after an aggregate's FILTER and OVER too, after a plain
  function's neither; `OVER (…)`, `FILTER (…)` and `WITHIN GROUP (…)` govern
  only their own parentheses. `*` is offered first after SELECT and inside
  `count(`; CASE, `ON CONFLICT` (`ON |` after VALUES → CONFLICT, `DO UPDATE |`
  → SET) and MERGE (`INTO`, `USING`, `ON`, `WHEN [NOT] MATCHED`, the action
  after THEN) are read; a MERGE's or DELETE's own `USING` is table position
  (`SqlCompletionContext.ClauseBefore`). The C01 table is
  `CompletionProviderTests.C01_…` row by row. `CompletionBench quality` credits
  a phrase row for its first word when the query goes on with the rest.
  **Package O widened the catalog** (E01–E07). The system catalogs are read like
  a schema (`SqlCompletionProvider.SystemSchemas`: pg_catalog, information_schema
  — relations and columns, not functions) and ranked under the user's own
  (`SystemTablePriority`; a short list of everyday ones, pg_stat_activity first,
  a little higher; information_schema's rarer views under its schema row and its
  `_pg_*` plumbing not at all). **pg_catalog is searched first** unless the
  search_path names it elsewhere — `ResolveShort` checks it before the path,
  which is also what makes `pg_class` insert bare. pg_catalog's own functions
  are candidates now, one row per name, what `IsInternal` marks left out, under
  the curated list (which grew the everyday admin ones: pg_size_pretty,
  pg_terminate_backend …). A thousand rarely typed names are the only longer
  match for many a word typed in full (`query` → `querytree(`); the literal
  replay found that over an old snapshot with no `query` column to match
  exactly, and what holds it now is the exact name ranking first.
  Rows describe what they name (E06): a column's PK / identity / generated /
  default / NOT NULL / the FK it follows / comment (`TableColumn`'s new init
  props, read by `GetAllColumnsAsync`, which now covers foreign tables too), a
  relation's kind, row estimate and comment, a function's signatures **with
  their DEFAULTs** (`FunctionInfo.FullArguments`, `pg_get_function_arguments`,
  kept apart from the identity `Arguments` DROP FUNCTION needs) and comment; the
  argument hint parses the full form, drops OUT parameters and marks
  `SqlParameter.HasDefault`. **Values** (E07) come from `Text/SqlValueSlot`
  (Core-pure, `SqlValueSlotTests`): the right side of `=`/`<>`/`!=`/`IN (…)` with
  a column gets its enum's labels (quoted; bare inside the quotes — the one
  place completion answers inside a string, and the editor opens the list on the
  `'` itself) or TRUE/FALSE for a boolean, above everything (`ValuePriority`);
  `nextval('`/`currval('`/`setval('` get the sequences (bare when on the path),
  `date_trunc('` the units, `extract(` the fields alone. The column's type is
  matched to an enum by format_type's spelling, which is what both the column's
  `DataType` and `DataTypeInfo.DisplayName` hold. Sequences, roles, settings and
  extensions are in the snapshot too (`CompletionCatalog.Sequences/Roles/Settings/
  Extensions`, each read with `ReadOptionalAsync` so a server that refuses one
  costs only those candidates); roles, settings and extensions are package N's.
  `CompletionBench quality` now says why each never-offered word is: a new name,
  one declared later in the query (package R), a DDL word (package N), or other
  — O's criterion is the last, now 0.
  **Package P offers whole constructs** (§6.4), all `SqlCompletionKind.Snippet`
  rows except the window call: after JOIN, each FK neighbour's row is followed
  by one row per foreign key tying it to the statement — `customers c ON c.id =
  o.customer_id`, the alias the auto-alias would pick, inserted as is (no second
  auto-alias) and after `schema.` too; neighbours keep their discovery order with
  each one's joins right under it (`- 0.01 × index`), and the plain table row stays
  first so a typed prefix + Enter still writes just the table. **Join conditions
  now name the joined table first** (`ForeignKeyMatcher.BuildJoinConditions`,
  also what the ON row writes): every JOIN in the corpus is written that way.
  After `INSERT INTO t`, the column list with `VALUES ()` — every writable column
  (no generated one, no GENERATED ALWAYS identity) and, if different, the
  required ones — the caret in the first value (`SqlKeywordAdvice.AfterInsertTarget`
  says where). After SELECT with sources, every column as one row; after
  `GROUP BY`, the select list's non-aggregate items (`SqlOutputItem.Expression`,
  the item's span without its alias; an aggregate or window call is told by the
  snapshot's call kinds), only when there is an aggregate to group for; right
  after a `*` (Ctrl+Space), the palette's star expansion as a row
  (`SqlCompletionData.ReplaceFrom` starts the replaced range at the star). A
  window-only function inserts with its window, `row_number() OVER (|)` or
  `lag(|) OVER ()`; after a call, OVER and FILTER come with their parentheses
  (`OVER (|)`, `FILTER (WHERE |)`); `CASE WHEN` is a phrase; in `DO UPDATE SET`,
  `col = excluded.col` per column. Where the caret lands is
  `SqlCompletionData.CaretIndex`, applied by `CompletionEdits.Plan`'s long
  overload. Inside a VALUES row the argument hint names the column the value
  goes into (`SqlCallSite.ValuesRowAt`, an INSERT's VALUES being its own block
  whose `Query.Owner` is the INSERT). The popup also opens after `BY `.
  `PGNIMBUS_ORACLE_TRACE=1` makes the oracle print, per query, what it typed and
  what it accepted — how the remaining gap to §7's 45% was read: aliases used
  before the FROM that declares them (package R) and keywords written in lower
  case, which the uppercase rows can't write (F02, package Q).
  **Package N reads DDL and utility statements through a slot grammar** (D01,
  D02): `Text/SqlCommandGrammar` (Core-pure, `SqlCommandGrammarTests`, with a
  generative "answers on any text" check since it runs per keystroke) takes the
  statements `SqlKeywordGrammar` leaves alone — CREATE (table, index, view,
  function, schema, extension, sequence, type/domain, trigger, role), ALTER
  (table actions per clause, other objects' RENAME/OWNER/SET SCHEMA, SYSTEM),
  DROP, COMMENT ON, GRANT/REVOKE, TRUNCATE, VACUUM, ANALYZE, REINDEX, CLUSTER,
  REFRESH, SET/SHOW/RESET, COPY, LISTEN/NOTIFY, LOCK, BEGIN, REASSIGN and
  EXPLAIN's `(…)` options — and answers a `SqlCommandAdvice`: the keywords that
  come next and one `SqlObjectKind` (a relation or a kind of one, an index, a
  sequence, a function overload, a schema, a type, a role, an installed or an
  available extension, a setting or one setting's values, the columns of the
  relation the statement names, an index method, a channel, a language). The
  caret is always at the end of what it reads, so each rule is "what comes next
  here"; where it can't tell it answers null and the provider keeps its general
  list. The provider turns the kind into rows (`CommandObjects`): `DROP VIEW`
  lists views only, `DROP FUNCTION` one row per overload with its identity
  arguments (`saas.account_mrr(p_account_id bigint, p_at date)`), `SET
  search_path TO` the schemas, an enum setting its values; after `schema.` in
  such a slot, that schema's objects of the slot's kind, bare. A created
  object's name offers the existing schemas (its qualifier), and
  `NotifyChannels` (the monitor's channels, copied on the UI thread) feed
  LISTEN. Index names are read into the snapshot (`GetIndexNamesAsync`,
  `CompletionCatalog.Indexes`), and the everyday types rank first in a type
  slot or a cast (`bi` → bigint, not bit). New row kinds: Role, Setting,
  Extension, Index.
  **Package Q: what the list shows, and three settings** (G01–G06, H01–H02,
  F02, F05, §6.7). A row shows the letters the query matched in bold
  (`CompletionRanker.MatchedPositions`, the same tier reading the ranker sorts
  by — the prefix, the parts' starts, a substring, else the leftmost
  subsequence; drawn by `Completion/CompletionLabel`, which reads the query off
  the list's `Tag`, set where the editor filters). The detail column is
  smaller and dimmer, the tip beside the selected row is a title and a body,
  and a hovered row gets a light wash (`CompletionHoverBrush`, on the template
  part for the same reason as `tentative`). Home/End close the list and move
  the caret; the argument hint closes when the editor loses focus or the
  command palette opens (in the headless session the palette never takes
  focus, so the second is watched on `CommandPalette.IsOpen`). Hints put an
  ordinary overload before a polymorphic one (`upper(text)` before
  `upper(anyrange)`) and know SQL's own call forms that pg_proc lists without
  their keywords or not at all (`SignatureHints.SpecialForms`: extract,
  substring, position, trim, overlay, coalesce, greatest, least, nullif).
  Typing `(` right after a function name being completed takes that function
  (`coun(` → `count(|)`, F05). The Preferences page's **Completion** section
  holds the three §6.7 settings, all in `AppSettings`: keyword case
  (`CompletionKeywordCase`, `Text/KeywordCasing`: *as typed* by default — a
  keyword started in lower case is written in lower case, `tr` → `true`,
  anything else upper — or always UPPER / lower; applied in
  `SqlCompletionData.InsertTextFor`, which the oracle uses too), always write
  the table's schema (`CompletionAlwaysQualifyTables`, off: the provider
  rebuilds its snapshot off the UI thread with every table row qualified), and
  Enter accepts a suggestion (`CompletionEnterAccepts`, on: off leaves Tab as
  the only accept). No new permanent control. Screenshot scenario
  `main-window-completion`.
  **Package R: columns before their FROM** (E08). Typed left to right, a
  select list comes before the FROM that declares its aliases, and a third of
  the corpus's dot references (`SELECT c.fi`) used to get nothing. Now, in a
  SELECT block's select list, a qualifier nothing declares — no source, CTE,
  table or schema of that name — is read as the alias it will be
  (`FutureAliasColumns`): a CTE or table whose name it shortens
  (`Text/AliasGuess.Fit`: the name itself, its initials as the auto-alias
  writes them, those with a number, or the start of the name — `inv` →
  `invoices`), the likeliest fit and the search_path's first, at most
  `FutureAliasTables` (12) relations, each row naming its relation on the
  right. Only in the select list: in WHERE the FROM is already written and an
  unknown qualifier is a typo, not a plan. And in a top-level select list with
  no FROM at all, a bare word gets one row per table having a column that
  starts with it — `first_name · customers` — whose accept writes the FROM too
  (`SqlCompletionData.AppendClause`, applied by `CompletionEdits.AppendClause`
  as the same single edit: at the end of the statement, which is its `;` or a
  blank line, the caret staying on the column). They stand in for the
  catalog-wide row of the same name (Merge hides it), not at a new name (after
  AS), not in a subquery or a UNION branch. The candidates come from a
  `ColumnIndex` built with the snapshot (the user's columns sorted by name,
  ignoring case, as two arrays of references): a prefix is a binary search,
  capped at `FromlessColumnRows` (200); with nothing typed, which is how the
  list opens after `SELECT `, it is the search_path's tables' columns, up to
  2000, because the popup filters the list it opened with instead of asking
  again. A name typed in full stays as typed (Enter's "must change the text"
  rule doesn't count the FROM a row would bring). The oracle leaves the FROM-writing rows
  alone (the corpus goes on with the select list where they write a FROM).
