# Design: Accounts and permissions (roles, grants, RLS)

Status: research / proposal · Owner: pgNimbus · Created 2026-08-23

## Motivation

pgNimbus today has exactly one thing in this area: a read-only `Roles` group in
the schema tree (`SchemaService.GetRolesAsync` → `RolesGroupNode`/`RoleNode`),
listing non-`pg_*` roles with four attribute flags. There is no way to see who
can read a table, no way to grant anything, and no way to see why a query got
`permission denied`.

That is a real gap for the "second job" of a GUI client. Query editing is the
first job; the second is answering *"can this app user read this table, and if
not, why?"* — and the current answer is "go write `aclexplode` by hand".

The opportunity is that the incumbents are all weak here in the **same** way:
they render `GRANT` forms, but none of them answers the effective-permission
question. That is where a Postgres-first tool can win, exactly like the plan
analyzer did against pgAdmin's plain plan tree.

## What the incumbents actually ship

| Tool | Roles | Object grants | Effective / resolved view | Default privileges | RLS |
|---|---|---|---|---|---|
| **pgAdmin 4** | Full Login/Group Role dialog: General / Definition / Privileges / Membership / Parameters / Security. Also the 3-step **Grant Wizard** (objects → privileges → review SQL). | Yes, per object, plus the wizard for bulk | **No** — it shows stored ACLs, not what a role can do | Yes, a tab on Database/Schema | Yes, RLS Policy dialog |
| **DBeaver CE** | Role node with a Permissions tab | Per-object permission grid | **No** | No | Requested in 2019 ([#5499](https://github.com/dbeaver/dbeaver/issues/5499)), not shipped as an editor |
| **DataGrip** | Create/Modify Role dialogs, Grants pane with a "with grant option" dropdown, SQL preview | Yes, via the Grants pane | **No** | No | No |
| **TablePlus** | Tools → User Management; database-level privileges | **"Coming soon"** placeholder for per-table privileges ([#3375](https://github.com/TablePlus/TablePlus/issues/3375), open) | No | No | No |
| **HeidiSQL / Navicat / Beekeeper** | User manager exists but is MySQL-shaped (per-database privilege matrix); Postgres role semantics (inheritance, default ACLs, ownership) are not modeled | Partial | No | No | No |
| **psql / scripts** | `\du`, `\dp`, `pg_permissions`, hand-written `aclexplode` queries | — | `has_*_privilege()` if you know to reach for it | `\ddp` | `pg_policies` |

Everyone renders the *stored* ACL. Nobody renders the *resolved* answer.

## What users complain about

Grouped by how often the complaint shows up, with the underlying Postgres
behaviour that causes it:

1. **"What can this user actually do?"** Postgres has no built-in view of final
   computed rights. `information_schema.role_table_grants` does **not** expand
   role inheritance; `has_table_privilege()` does, but you have to know it
   exists. Every tool shows the raw ACL, so a permission granted through a group
   role is invisible in the UI even though it works. *(see
   [Geeky Tidbits](https://www.geekytidbits.com/postgres-privilege-helper-queries/),
   [Illuminated Computing](https://illuminatedcomputing.com/posts/2017/03/postgres-permissions/))*
2. **The `GRANT ON ALL TABLES` trap.** `GRANT … ON ALL TABLES IN SCHEMA` applies
   only to tables that exist *right now*; the next migration creates a table the
   role cannot read. The fix is `ALTER DEFAULT PRIVILEGES`, which most tools do
   not expose at all, and which has its own trap — per-schema defaults *add* to
   global defaults and cannot subtract from them. *(pgsql-general threads;
   [Cybertec](https://www.cybertec-postgresql.com/en/postgresql-alter-default-privileges-permissions-explained/),
   [Percona](https://www.percona.com/blog/dispelling-myths-about-postgresql-default-privileges/))*
3. **`role … cannot be dropped because some objects depend on it` (2BP01).** The
   correct recipe is `REASSIGN OWNED BY … TO …; DROP OWNED BY …; DROP ROLE …`,
   repeated per database, and it is not discoverable from any error message.
   Cloud users hit this constantly. *(AWS, Azure and Neon all publish
   knowledge-base articles for this single error —
   [repost.aws](https://repost.aws/knowledge-center/rds-postgresql-drop-user-role),
   [Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/2169271/postgres-16-2bp01-role-prod-infra-dt-sp-cannot-be),
   [PG docs 21.4](https://www.postgresql.org/docs/current/role-removal.html))*
4. **Bulk grants are painful, and the bulk tool is buggy.** pgAdmin's Grant
   Wizard can only grant, never revoke or edit
   ([#7891](https://github.com/pgadmin-org/pgadmin4/issues/7891)), and granting
   "everything in a schema" silently skips the schema's own `USAGE`
   ([#8954](https://github.com/pgadmin-org/pgadmin4/issues/8954)) — which means
   the user grants all the table privileges and still gets `permission denied`.
5. **The privilege UI lies or goes blank.** DBeaver dropped `SELECT` on
   materialized views from its permission list
   ([#6288](https://github.com/dbeaver/dbeaver/issues/6288)); pgAdmin silently
   turned saved privileges into `WITH GRANT OPTION`
   ([#8369](https://github.com/pgadmin-org/pgadmin4/issues/8369)). A NULL
   `relacl` means *"owner has everything, defaults apply"*, not *"no
   privileges"* — tools that render it as an empty grid teach the wrong thing.
6. **Per-table privileges are simply missing** in the fast/modern clients — the
   TablePlus issue is literally a "coming soon" screen with a user asking to
   stop typing `GRANT` statements by hand.
7. **Column-level grants and RLS are invisible.** Both are Postgres-first
   features; only pgAdmin edits RLS, nobody surfaces column ACLs well.
8. **Non-superuser reality.** On RDS / Neon / Supabase you are not superuser,
   `pg_authid` is unreadable, and tools that assume superuser show errors
   instead of the (perfectly readable) `pg_roles` data.

## Design principles for our version

Same shape as the plan analyzer, and the same reason it worked:

- **Answer the question, don't render the catalog.** The headline feature is
  "who can do what, and through which grant" — resolved through role
  inheritance, ownership, `PUBLIC`, and superuser, not a raw ACL dump.
- **Every mutation is generated SQL the user can read and run.** No hidden
  writes. Follows the existing `DdlTemplates` precedent: a `CREATE TABLE`
  template in a tab beats a dialog that can only express what its combo box
  lists. Grants are the same — show the `GRANT`/`REVOKE` script, let it be
  edited, then run it.
- **Core-pure and unit-tested where the logic is.** ACL parsing, the effective
  matrix, the drop-role recipe are `PgNimbus.Core` siblings of `PlanAnalyzer` /
  `BlockingTree` / `JsonTree`. No Avalonia types.
- **UI rule 1 still applies.** Zero new toolbar buttons. Entry points are the
  schema tree context menu and the command palette.
- **Never let a password reach disk.** `QueryHistoryStore` and `CrashLog` both
  persist statement text. Any `PASSWORD` literal must be redacted before it can
  reach either, and password entry must not flow through the normal query path.

## Proposed plan

### v1 — read and explain (no writes)

The differentiator, and the lowest-risk half.

1. **`Security/RoleService`** (Core) — extend beyond `GetRolesAsync`: full role
   detail (`rolvaliduntil`, `rolconnlimit`, `rolbypassrls`, `rolreplication`,
   `rolinherit`, `rolconfig`), membership edges from `pg_auth_members`
   (including `admin_option`, and `inherit_option`/`set_option` guarded by
   server version ≥ 16), and role comments from `pg_shdescription`.
2. **`Security/RoleGraph`** (Core-pure, unit-tested) — flattens membership into
   an inheritance forest: direct members, transitive members, cycle-safe, with
   the `NOINHERIT` distinction marked (member but does not inherit → must
   `SET ROLE`). Same builder shape as `BlockingTree.Build`.
3. **Role detail panel** — a tab in the object panel showing attributes, the
   membership tree (both directions: "member of" / "members"), and role-level
   `ALTER ROLE … SET` settings.
4. **`Security/PrivilegeService` + `AclEntry`** (Core) — `aclexplode()` over
   `pg_class`/`pg_namespace`/`pg_proc`/`pg_type`/`pg_database` plus
   `pg_attribute.attacl` for column grants, and `pg_default_acl` for defaults.
   NULL ACL is modeled explicitly as `DefaultAcl`, never as "empty".
5. **Object → Permissions tab** — per table/view/schema/function: a grid of
   grantee × privilege, with a **Source** column saying *direct*, *via role X*,
   *PUBLIC*, *owner*, or *superuser*. This is the thing nobody else has.
6. **"Explain access…" command** — pick a role and an object, get a plain
   sentence: *"`app_ro` can SELECT `sales.orders` — inherited from `readers`,
   granted by `postgres`. It cannot INSERT."* Cross-checked against
   `has_table_privilege()` so the answer matches what the server will actually
   do, with the `USAGE`-on-schema prerequisite called out separately (complaint
   4 above).
7. **Default privileges view** — `pg_default_acl` rendered per creator-role and
   schema, with the "this does not affect existing objects / per-schema adds to
   global" caveat stated in the panel, not in a doc.
8. **RLS visibility** — `pg_policies` on the table's Permissions tab, plus the
   `relrowsecurity` / `relforcerowsecurity` flags, and a warning when RLS is
   enabled but the connected role bypasses it (owner or `BYPASSRLS`) — which is
   the classic "it works for me, not for the app" footgun.

### v2 — write, as generated SQL

9. **Grant editor** — toggle a cell in the permissions grid, accumulate a
   pending change set (the `PendingChangeSet` pattern the results grid already
   uses), preview the `GRANT`/`REVOKE` script, apply in one transaction. Must
   include the schema `USAGE` grant that pgAdmin forgets.
10. **Bulk grant across a schema** — grant to all tables/sequences/functions,
    *and* offer the matching `ALTER DEFAULT PRIVILEGES` in the same generated
    script, since that is the only correct answer to "give this role read access
    to this schema".
11. **Create / alter role** — name, login, password, `VALID UNTIL`,
    `CONNECTION LIMIT`, memberships. Password handling: never interpolated into
    history-visible text; redacted in query history and the crash log; the
    generated-SQL preview shows `PASSWORD '••••'` while the executed statement
    carries the real value. Offer the predefined roles (`pg_read_all_data`,
    `pg_write_all_data`, `pg_monitor`) as one-click memberships, since that is
    now the sane answer to "make a read-only user".
12. **Drop role, done properly** — detect 2BP01 up front by listing what the
    role owns and what it has been granted, then offer the real recipe
    (`REASSIGN OWNED BY … TO …` → `DROP OWNED BY …` → `DROP ROLE …`) as an
    editable script, with the "run it in every database" caveat surfaced. This
    alone answers the single most-searched Postgres role error.

### Out of scope (stated so it is not re-litigated)

- `pg_hba.conf` editing — needs filesystem access to the server; wrong tool.
- Password *policies*, expiry alerts, rotation workflows — DBA-suite territory.
- Reading `pg_authid` (password hashes) — superuser-only and pointless here.
- Cluster-wide role sync across databases — we hold one connection.
- Our own app-level user management (DBeaver Team Edition's model). pgNimbus
  manages *Postgres* accounts; it does not have accounts of its own.

## Landmines

- **Server version.** `pg_auth_members.inherit_option`/`set_option` are PG16+;
  `rolbypassrls` is PG9.5+; predefined roles `pg_read_all_data` /
  `pg_write_all_data` are PG14+. Every catalog query needs a version guard or a
  fallback column list.
- **Not superuser.** `pg_roles` is world-readable, `pg_authid` is not.
  `pg_default_acl`, `pg_policies`, `pg_class.relacl` are all readable by
  ordinary roles. Nothing in v1 needs superuser — keep it that way, and let
  v2's writes fail with the server's own error rather than a pre-flight check
  that guesses wrong on RDS.
- **NULL ACL ≠ no privileges.** Model it as a distinct state or repeat
  complaint 5.
- **`has_table_privilege` on a dropped or invisible object throws** rather than
  returning false; wrap per-object lookups so one bad row does not fail the
  grid (same discipline as `QueryEngine.ReadValue`).
- **Passwords in history.** `QueryHistoryStore` writes SQL to disk in the clear.
  Redaction must live in Core, next to the statement inspector, and be
  unit-tested — not bolted onto one call site in the App.

## Suggested first PR

v1 items 1, 2, 3 — `RoleService` + `RoleGraph` + the role detail panel. Purely
additive, no writes, testable without a privileged server, and it makes the
existing dead-end `Roles` node worth expanding. Items 4–6 (the effective
permission matrix) are the actual differentiator and should follow immediately
as the second PR.
