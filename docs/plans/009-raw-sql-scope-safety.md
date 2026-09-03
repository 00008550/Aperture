# 009 — Raw SQL scope safety: a correct Dapper path, and a gate that fails without it

Status: in-progress      <!-- draft → approved → in-progress → done -->
Roadmap: ARCHITECTURE.md §13 — not a numbered roadmap item; this is the raw-SQL half of 001's
authorization spine, split out (see *Why its own plan* below). Sequence it **immediately after 001**
and **before 002**; the plan number is a file identifier, not a build order.
Measured: `scripts/measure.sh` on 2026-08-30, branch `chore/plan-lifecycle-states` (356555d), with
`feat/001-P4-scope-sql-predicate` (897e627) read alongside.

## Revision 2026-09-01 — the placeholder is a by-convention belt, and that is a fail-open

`ap-reviewer` surfaced a residual fail-open in the mechanism P3 chose. `ScopedConnection` checks the
`/**scope**/` placeholder is *present*, not that it actually *gates* the rows. A caller can satisfy
the check and still read unscoped:

- `WHERE 1=1 OR /**scope**/` — the `OR` neutralises the fragment (`AND` binds tighter than `OR`, so
  the tenant/scope conjunct is `OR`-ed away).
- `WHERE ... -- /**scope**/` — the placeholder sits in a comment; it is present, and inert.
- `/**scope**/` parked in a non-gating subquery or a `UNION` branch.

The guarantee plan 009 exists to make — *you cannot get unscoped rows from the raw-SQL path* — is
delivered today only by a reviewer reading every call site. P4's differential test does not catch
this: it exercises correct usage. The user's decision: **make the scope guarantee structural, not
by-convention** — a developer must not be able to express an unscoped or scope-inert raw read through
the sanctioned path at all.

**Measured 2026-09-01** (branch `feat/009-P2-scope-sql-fragment`, `b2edc74`):

- `scripts/measure.sh rawsql` — **0 production raw-SQL call sites**, 33 exempt (test fixtures + the
  sanctioned wrapper on the P3 branch). Still prevention, not remediation.
- **009-P1 and 009-P2 are merged to `master`** (`Authorization/ScopeSql.cs`, `ScopeFragment.cs`,
  `ScopeColumns.cs`, `ScopeParameterBag.cs`, `DataScope.ToSqlFragment` are on `master`).
- **009-P3 is NOT merged.** `git ls-tree master -- src/Aperture.SharedKernel/Data/` is empty, no
  Dapper `PackageVersion` on `master`, and PR #24 is `OPEN`. **Correction to the record:**
  `docs/plans/STATE.md`'s *Shipped* table listed 009-P3 as shipped against PR #24 — that was wrong;
  it is held, deliberately, pending this rethink. Corrected in STATE (moved out of *Shipped* to *In
  flight, under revision*). The plan's P3 checkbox is un-ticked here for the same reason: it is not
  merged and its content is changing.

The rest of this file below the *Design decisions* table is revised. **P1 and P2 stand as shipped
fact and are unchanged.** The mechanism from P3 onward is redesigned around Postgres row-level
security; see the reasoning in *Design decisions*.

## Ground truth

Measured, not quoted.

**Dapper does not exist in this repository.**

```
$ find src -name "*.csproj" | xargs grep -l -i dapper
(no output)
$ grep -i dapper Directory.Packages.props
(no output)
$ grep -rn "QueryAsync\|ExecuteAsync\|NpgsqlConnection\|FromSqlRaw\|FromSqlInterpolated\|ExecuteSqlRaw" --include=*.cs src/
src/Aperture.Worker/Worker.cs:5:          protected override async Task ExecuteAsync(...)      <- BackgroundService, not SQL
src/Modules/Access/Aperture.Modules.Access.Tests/AccessSchemaTests.cs:53,114,126               <- test fixture, asserts the RLS/constraint behaviour
```

So there are **zero production raw-SQL call sites**, and the question "do they pass `tenant_id`?"
has no instances to answer. This is the single most important measurement in the plan, and it
inverts the framing the task arrived with: **this is prevention, not remediation.** Nothing is
broken today. What is true is that the moment someone writes the first `Dapper` call — and §4 says
they will, because Dapper is meant to own every list and report query — the fail-open path is
one line of code away and nothing but a reviewer stands in front of it.

That timing is the whole argument for doing this now. The negative mechanism (below) costs almost
nothing while the call-site count is zero and the package is not yet referenced; it becomes a
migration once there are forty grids.

**What 001-P4 built** (read on the branch, not from its PR body):

| File | What it is |
|---|---|
| `src/Aperture.SharedKernel/Authorization/DataScope.cs` | Closed hierarchy — `Self`, `Team`, `Region`, `Account`, `AllTenant`. Private ctor. Each case owns **both** `Admits(IScopedResource)` and `ToPredicateBody(ScopeRowExpressions)`. Abstract, deliberately, so a new scope kind fails to compile until it says how it filters. |
| `src/Aperture.SharedKernel/Authorization/ScopeQuerying.cs` | `ToPredicate<T>()` → `Expression<Func<T,bool>>`, `WhereInScope<T>()` → `IQueryable<T>`. Empty set returns `Expression.Constant(false)` **before** the row members are bound. Tenant equality is `AndAlso`-ed on top of the union and cannot be reached past. |
| `src/Aperture.SharedKernel/Authorization/ScopeRowExpressions.cs` | Binds the five `IScopedResource` members on the **concrete** entity type via reflection (binding on the interface would silently client-evaluate). `Parameterised<T>` boxes values in a field access so EF emits a SQL parameter rather than an inlined literal. |

**How much is reusable for a SQL-fragment path?** The valuable, hard-won parts are reusable; the
mechanical part is not.

- **Reusable, and this is most of the value:** the closed `DataScope` hierarchy; the
  "each case owns its own translation, abstract not `switch`" discipline; the semantics — union of
  grants, tenant `AND`-ed on top, empty set matches nothing, `NULL` team/region/account narrows;
  and the parameterisation requirement.
- **Not reusable:** `ScopeRowExpressions` entirely. It is `System.Linq.Expressions` over CLR
  properties resolved by reflection. A SQL fragment needs *column names and a table alias*, which
  is a different input (there is no `T` to reflect over — Dapper maps to a DTO shaped like the
  projection, not like the row).
- Therefore the SQL path is a **sibling** of the expression path under the same hierarchy, not a
  layer over it: a third abstract member on `DataScope`, exactly as `ToPredicateBody` is a second.
  That keeps the three forms of one rule in one file where they cannot drift.

Rest of the measured picture (all four measurements ran; the ones that bear on this plan):

```
== ENDPOINTS ==   3 mapped routes, 0 without a policy
                  /api/me RequireAuthorization; /health/live, /health/ready AllowAnonymous
== SCHEMA ==      access schema, 9 tables. Widest: memberships(5), tenants(5), users(5),
                  scope_grants(5). Nothing here is a wide table — the domain has not landed yet.
== TESTS ==       Aperture.Api.Tests 32 · Aperture.SharedKernel.Tests 16 ·
                  Aperture.Modules.Access.Tests 15.  Modules without tests: none.
                  Frontend: 0 spec files under frontend/.
== PERMISSIONS == 17 declared, 13 never enforced (expected — the modules they gate are unbuilt)
```

Note the SharedKernel count is 16 on `master`'s line of development; the P4 branch takes the
solution to 84 total. Both numbers are stated because neither alone is honest right now.

### Corrections made to Aperture's records

`docs/ARCHITECTURE.md` §12, three rows added:

1. **Scope → SQL predicate (EF / `IQueryable` only)** — `◐ on branch, unmerged`, pointing at PR #17
   and saying in the row itself not to read it as shipped. §12 had no row for P4 at all, so the
   matrix's most recently built capability was invisible in it.
2. **Scope → SQL predicate for raw SQL / Dapper** — `☐ planned`, stating explicitly that
   `WhereInScope` covers `IQueryable` only and invariant 2 is currently enforced by review alone.
   This is the gap this plan closes, and it should be legible in the matrix before it is closed.
3. **Dapper (as a dependency)** — `☐ not present`, with the measurement and the date, and noting
   that §4's "Dapper owns list and report queries" is design intent rather than a description of
   the code. That sentence has been read as a statement of fact by at least two agents.

Nothing was removed. No `✅` was changed — the existing ones (P1, P2, P3 rows) are supported by
test counts I re-measured and they agree.

### Why its own plan and not portions appended to 001

Considered appending as 001-P7/P8, since this is unambiguously the authorization spine and 001 still
has P5 and P6 unbuilt. Rejected on three grounds, in order of weight:

1. **File collision, concretely.** `git diff --name-only master...feat/001-P4-scope-sql-predicate`
   returns `docs/plans/001-authorization-spine.md`, `docs/plans/STATE.md`,
   `docs/plans/pr/001-P4.md`, `scripts/measure.sh`, and five source files. Appending portions means
   editing the first of those while PR #17 is open against it.
2. **Lifecycle.** 001 is past `draft` — the user approved a portion list that did not contain these.
   Appending new portions to an approved plan silently expands an approved scope, which is the
   mechanism the `draft → approved` gate exists to prevent. A new `draft` plan puts the decision
   back where it belongs.
3. **It is genuinely separable.** These portions do not depend on P5 (console) or P6 (audit), and
   P5/P6 do not depend on them. Only P2 of this plan depends on 001-P4 merging.

`scripts/measure.sh` is also touched by PR #17 and by P1 below — see P1's *Touches* note.

## Domain behaviour

The rule is unchanged from 001 and `DOMAIN.md` §5.1; only the execution path is new.

- A principal's visibility is `tenant_id = @tenant AND (grant₁ OR grant₂ OR …)`.
- Grants are a **union**. A lead with `Team(A)` and `Region(North)` sees rows in either.
- `AllTenant` means *everything inside this tenant*, never everything. The tenant equality is
  conjoined outside the union and no grant can reach past it.
- **Absent data narrows.** A row with `team_id IS NULL` is in nobody's team scope. In SQL this is
  free — `NULL = @p` is unknown, therefore not true — but only if the fragment is written as an
  equality and never as `IS NOT DISTINCT FROM` or a `COALESCE(team_id, @p)`.
- **The empty set admits nothing.** `DOMAIN.md` §5.1 is the incident where "no regions selected" was
  read as "all regions". In SQL its form is an omitted `WHERE`, which is the *default* state of a
  hand-written query rather than something a developer has to type. That asymmetry — fail-open is
  the path of least effort in raw SQL, and fail-closed is the path of least effort in EF — is the
  precise reason the negative mechanism in P1/P3 is worth more here than it would be in the EF path.

Precedence: there is none. Grants only ever widen within the tenant; nothing subtracts. If a
subtractive grant is ever wanted, it is a design change and goes to the user, not into a translator.

## Design decisions

| Structure | Class | Why |
|---|---|---|
| `ToSqlFragment` as a third abstract member on `DataScope` | **Essential** | Same reason `ToPredicateBody` is abstract rather than a `switch` in the translator: a sixth scope kind must fail to compile, not fall into a `default:` that filters nothing. Putting it beside the other two keeps one rule in one file. |
| `ScopeSql` static translator (set → fragment + parameters) | **Essential** | The positive half. Without a correct path that is *easier* than the wrong one, the negative half is just an obstacle developers route around. |
| Explicit column/alias mapping passed in by the caller | **Essential** | There is no entity type to reflect over on the Dapper path. Making the caller name the alias is also what makes the fragment safe to `AND` into a join. |
| A `ScopedConnection` wrapper that is the only type able to reach Dapper | **Essential** | The strongest available lever *and* nearly free today, because Dapper is not yet referenced. The package reference lands in exactly one project; every other project physically cannot call `connection.QueryAsync`. A compiler-and-project-graph constraint, not a reviewer's attention. **But** it only guarantees *that raw SQL goes through this type* — not *that the SQL this type runs is scoped*. That second guarantee is the fail-open the 2026-09-01 revision closes, and the wrapper alone cannot make it (see the next two rows). |
| The `/**scope**/` placeholder — caller writes SQL, marks where the fragment goes, wrapper substitutes it | **Rejected (was the P3 mechanism)** | This is the residual fail-open. The wrapper can check the token is *present*; it cannot check the token *gates* the rows, because that is a property of SQL semantics the wrapper would have to parse the statement to know. `WHERE 1=1 OR /**scope**/`, `-- /**scope**/`, or the token in a `UNION` branch all pass and all leak. **Any in-app scheme that concatenates the scope term with caller-authored SQL has this hole:** a trailing `--`, an `OR`, or an unbalanced `)` in the caller's text neutralises an appended `WHERE`. Wrapping the caller's query as a subquery (`SELECT … FROM (callerSql) _s WHERE scope`) moves the hole, it does not close it — an unbalanced `)` in `callerSql` escapes the wrapper's parens. Validating the caller's SQL for these tokens is a parser in disguise and is back to by-convention. The lesson: **no in-app string composition is structural.** Enforcement has to live below the SQL string. |
| A source-scanning test over `src/` + a `scripts/measure.sh rawsql` mode | **Essential** | Cheap — one test method and ~20 lines of shell — and it catches the two things the wrapper cannot: a second project adding the Dapper package, and `FromSqlRaw`/`ExecuteSqlRaw`/`NpgsqlConnection` reached through EF or Npgsql directly, neither of which the wrapper gates. There is direct precedent: `measure.sh`'s comment already describes the endpoint-policy gate as "the cheap copy that runs without a build" paired with a real architecture test. Mirror that shape exactly. |
| Differential test: EF predicate vs SQL fragment, same rows, real PostgreSQL | **Essential** | Two hand-written encodings of one rule *will* drift. Asserting they select the identical id set is the only mechanism that catches drift rather than documenting the hope of avoiding it. |
| A Roslyn analyser | **Deferred** | It would be more precise than a grep — it sees semantics, not text, and it can flag `IDbConnection.QueryAsync` reached through an interface the grep cannot follow. But it is a separate analyser project, a `Microsoft.CodeAnalysis` version to keep in step with the SDK, an analyser test harness, packaging into every `csproj`, and a suppression story. Against zero call sites that is machinery in place of a decision. **Trigger:** the grep produces its first false positive that someone wants to suppress, or raw-SQL call sites pass ~20 across ≥3 modules. Until then the grep is, honestly, ~90% as effective for ~5% of the cost. |
| An approval test snapshotting every raw-SQL call site | **Rejected** | It converts every legitimate query into a snapshot diff, and that diff is reviewed by the same humans who would have missed the missing `tenant_id` in the first place. It generates review load precisely proportional to healthy activity and detection power that does not scale with it. The wrapper makes the same guarantee structurally. |
| Generating the whole `SELECT` — a scoped query builder / mini-ORM | **Rejected** | §4 says the knowledge that matters in a list query is the SQL and it is written as SQL. A builder that owns the whole statement re-invents the ORM the design deliberately stepped out of, and the first join it cannot express is the day it gets bypassed. Still rejected. |
| A narrower "wrapper owns the `WHERE`/`ORDER BY`/`LIMIT` tail; caller supplies `SELECT`/`FROM`/`JOIN` + *structured*, parameterised filters" (the reviewer's "whole-statement ownership") | **Rejected** | Better than the placeholder — the caller writes no boolean adjacent to the scope term — but *still not structural*. The caller's `FROM`/`JOIN` text is free SQL, and a `WHERE … --` or an unbalanced `)` inside it neutralises the wrapper's appended scope `WHERE` exactly as the placeholder does. Closing that needs token-validation of the `FROM` clause, i.e. a parser, i.e. by-convention again. It also imposes a real ergonomic cost (filters become structured objects, complex analytic `WHERE` must route through EF) and dents §4's "written as SQL" — paying an ergonomic price for a guarantee it does not actually deliver. Recorded because it is the obvious middle path and it does not work. |
| **Postgres Row-Level Security as the enforcement point** | **Essential — this rethink is the trigger** | The one mechanism that is structural: the DBMS applies the policy to *every* row returned, regardless of how the caller wrote the `SELECT` — subqueries, `UNION`s, comments and all — because it is enforced below the SQL string, not by composing it. An unscoped read cannot be *expressed*. It also realises CLAUDE.md invariant 3 literally: a connection with **no** session context set returns **zero** rows (fail-closed by default), not everything. The plan previously deferred RLS with trigger "first external-facing tenant or first isolation incident"; the reviewer's finding — that no in-app scheme is structural — *is* that trigger, a design-level isolation gap surfaced before a grid ships. **Blast radius is contained by role separation** (below): RLS binds only a dedicated least-privilege reader role that `ScopedConnection` uses; EF stays on the owner role and is untouched, so this is not a repo-wide session-management change. The 009-P2 fragment is **not** wasted — it is retained as the in-app first belt and a query-plan aid, exactly as this row predicted in its earlier *Deferred* form ("defence in depth"). |

## Failure modes

| Concern | Answer |
|---|---|
| **Tenancy** | Two belts. (1) The 009-P2 fragment still emits `{alias}.tenant_id = @__scope_tenant` conjoined outside the union, as the in-app first belt. (2) The **row-security policy** on each scoped table re-asserts `tenant_id = current_setting('app.tenant_id')::uuid` at the DBMS, independent of the caller's SQL — this is the belt the placeholder lacked. On a join, the caller still names an alias per scoped table for the fragment, but the RLS policy binds each *table* regardless of alias, so a joined scoped table cannot be left unfiltered even if the developer forgets its fragment. The plan still does not *infer* joins for the fragment; RLS makes the forgotten-join case fail closed rather than leak. |
| **Authorization** | One resolution path: the same `DataScopeSet` the EF path takes. `ScopedConnection` translates it into six session settings via parameterised `set_config('app.tenant_id'|'app.user_id'|'app.teams'|'app.regions'|'app.accounts'|'app.all_tenant', …, is_local => true)`. The policy is the SQL form of the union: `all_tenant OR owner_user_id = app.user_id OR team_id = ANY(app.teams) OR …`. **Empty scope set:** the arrays are empty and `app.user_id`/`app.all_tenant` are unset, so `= ANY('{}')` is false and every branch is false — the policy admits nothing, at the DBMS, with no in-app decision required. **Unset context** (a reader-role connection that never ran `set_config`): `current_setting('app.tenant_id', true)` is `NULL`, `NULL = tenant_id` is unknown, the policy denies — **zero rows by default.** The fragment's `1 = 0` empty-set form is retained as the first belt but is no longer the thing standing between a bug and a leak. |
| **Consistency** | Read-only. `ScopedConnection` now **opens a transaction** per read to carry `SET LOCAL`/`set_config(is_local => true)` — session settings must be transaction-local or they leak across pooled reuse. It is a read-only transaction (`READ COMMITTED`, no writes), commits immediately after the read, and does not participate in an ambient EF transaction (the reader role is a *separate connection/pool* from EF's owner-role connection — see Backward compatibility). So a Dapper read still sees committed state, not a same-request uncommitted EF write; read-your-writes still means "read through EF". The differential test asserts read-committed behaviour. |
| **Concurrency** | None introduced — no writes. The wrapper exposes query-shaped methods only; `Execute`/`ExecuteScalar` remain **out of scope** and absent. The per-read transaction is read-only and short; it takes no row locks. The RLS policies are `USING` (read) only — no `WITH CHECK`, because there is no write path to check. |
| **Idempotency** | Not applicable — reads. Retrying a scoped read is free. |
| **Ordering** | Not applicable. The fragment contributes to `WHERE` only; it never emits `ORDER BY` and must not, because §4's keyset pagination owns ordering and a translator quietly appending an order would break the cursor. Asserted by a test on the emitted fragment text. |
| **Failure** | The builder is pure and total — it throws only on a null argument or an alias that is not a valid identifier. A malformed alias is an `ArgumentException` at the call site, not a sanitised-and-continued string, because sanitising an identifier is how injection sneaks back in. Connection failures are the caller's ordinary Npgsql failures; the wrapper adds no retry (a retry policy that hides a saturated pool is worse than the timeout). |
| **Backward compatibility** | Zero call sites, so nothing to retrofit. Two additive changes carry compatibility weight. (1) **A dedicated reader role.** RLS binds a least-privilege login role (`aperture_reader`, `SELECT`-only, **not** the table owner — a table owner bypasses RLS unless `FORCE ROW LEVEL SECURITY`, and we deliberately do **not** force it, so EF/migrations on the owner role are entirely unaffected). Provisioning the role is an idempotent migration/bootstrap step; the API gains a second connection string. Expand-only — no existing connection changes. (2) **Enabling RLS on a table is not itself breaking** for the owner role, so migrations enabling policies deploy while old code runs; the reader role simply does not exist as a caller until P4 wires it. Dapper is still added to `Directory.Packages.props` once, centrally pinned; the P1 gate already guards it. |
| **Observability** | Unchanged from the P3 design and extended. The fragment still carries the `/* scope */` marker, greppable in `pg_stat_statements`. The wrapper still opens an activity span tagged with tenant id and scope-**kind counts** (never scope values), plus a tag for the reader role and whether session context was established. An empty-scope read is still logged at `Information` with the principal — "user sees nothing" is a support ticket answerable in one line. **New:** because RLS can silently return zero rows when context is *unset* (a wiring bug, not an empty grant), the wrapper asserts context was set before the read and logs at `Warning` if `set_config` did not run — the one failure mode RLS introduces (silent deny on misconfiguration) is made loud. |

## Edge cases

These are the builder's test list, verbatim.

1. **Given** a scope set with one `Self` grant, **when** translated, **then** the fragment is
   `(a.tenant_id = @__scope_tenant) AND (a.owner_user_id = @__scope_p0)` and the parameter bag holds
   exactly those two values.
2. **Given** a set with `Team(A)` and `Region(North)`, **when** translated, **then** the grants are
   `OR`-ed inside a single parenthesised group that is `AND`-ed with the tenant term — assert the
   parentheses, because precedence is where this class of bug lives.
3. **Given** an **empty** scope set, **when** translated, **then** the fragment matches nothing
   (`1 = 0` present, tenant term present), the result is neither `null` nor empty, and — executed
   against real data — returns zero rows even when rows exist for that tenant.
4. **Given** a scope set containing `AllTenant`, **when** executed against a database holding rows
   for two tenants, **then** rows of the other tenant are not returned.
5. **Given** rows with `team_id IS NULL`, **when** a `Team(A)` scope is applied, **then** those rows
   are absent. Same for `region_id IS NULL` under `Region`, and `account_id IS NULL` under `Account`.
6. **Given** duplicate grants (`Team(A)` twice) or the same set built in a different order,
   **when** translated, **then** the parameter set and the row result are identical — set semantics
   survive translation.
7. **Given** any scope set, **when** translated, **then** **no scope value appears as a literal** in
   the fragment text; every one is a parameter. Assert by searching the fragment for the `Guid`'s
   string form. This is both an injection property and a plan-cache property.
8. **Given** two calls with different alias arguments, **when** both fragments are `AND`-ed into one
   query, **then** the parameter names do not collide (prefix or ordinal must be caller-scoped).
9. **Given** an alias that is not a plain identifier (`"o; DROP"`, `""`, `null`), **when**
   translated, **then** `ArgumentException` — never a sanitised string, never a silent default.
10. **Given** the same `DataScopeSet` and the same rows, **when** fetched once via EF
    `WhereInScope<T>()` and once via the Dapper fragment, **then** the returned id sets are equal.
    Run for every scope kind, the union, and the empty set. **This is the anti-drift test.**
11. **Given** a new `DataScope` case is added without a `ToSqlFragment` implementation, **then** the
    solution does not compile. Verified by review of the abstract member, not by a test.
12. **Given** a source file under `src/` outside the sanctioned wrapper project that references
    `Dapper`, `NpgsqlConnection`, `FromSqlRaw`, `FromSqlInterpolated` or `ExecuteSqlRaw`, **then**
    the architecture test fails and names the file and line.
13. **Given** a `csproj` other than the sanctioned one adds a Dapper `PackageReference`, **then**
    the same test fails.
14. **Given** a test-project file uses `NpgsqlConnection` (as `AccessSchemaTests.cs` legitimately
    does today), **then** the test does **not** fail — the rule scopes to production code, and the
    exemption is expressed as a path rule, not a magic comment a developer can paste anywhere.
15. **Given** the emitted fragment, **then** it contains no `ORDER BY`, no `LIMIT`, and no trailing
    semicolon.

New cases from the 2026-09-01 revision — these are the ones that make the guarantee *structural*,
and they are the tests P4's differential set could not have caught:

16. **Given** a caller that deliberately neutralises the in-app fragment — passes SQL whose scope
    predicate is `OR`-ed away (`WHERE 1=1 OR (…scope…)`), commented out, or omitted entirely —
    **when** run through `ScopedConnection` as the reader role against rows of two tenants and
    multiple scopes, **then** it **still returns only in-scope rows**, because RLS re-filters at the
    DBMS. **This is the anti-bypass test and the whole point of the revision.**
17. **Given** a connection as the reader role that has **not** established session context
    (`set_config` never ran), **when** it selects from a scoped table that has rows, **then** it
    returns **zero** rows — fail-closed by default, not "everything".
18. **Given** a connection as the **owner** role (EF/migrations), **when** it selects from the same
    table, **then** RLS does **not** apply (owner bypass, `FORCE` off) and EF behaviour is
    unchanged — the blast radius is contained to the reader role. Asserted so a future
    `FORCE ROW LEVEL SECURITY` added by accident is caught.
19. **Given** two consecutive reads on the **same pooled** reader connection with different scope
    sets, **when** the second runs, **then** it sees only its own scope — the first read's
    `set_config(is_local => true)` did not survive its transaction. Guards the pooling-leak failure
    mode `SET LOCAL` exists to prevent.

## Target design

Per `ARCHITECTURE.md` §3 (authorization), §4 (data access) and invariant 2 in `CLAUDE.md`.

**Module:** none. This is `Aperture.SharedKernel` — cross-cutting authorization primitive, the same
home as `ScopeQuerying`. §1 says SharedKernel is not a dumping ground; this qualifies on the same
grounds P4 did.

Shipped (P1/P2), unchanged:

```
src/Aperture.SharedKernel/Authorization/
    DataScope.cs               abstract ToSqlFragment(...)            [P2, on master]
    ScopeSql.cs / ScopeFragment.cs / ScopeColumns.cs / ScopeParameterBag.cs  [P2, on master]
src/Aperture.SharedKernel.Tests/Authorization/ScopeSqlTests.cs        [P2, on master]
src/Aperture.SharedKernel.Tests/Architecture/RawSqlIsScopedTests.cs   [P1, on master]
scripts/measure.sh  (`rawsql` mode)                                   [P1, on master]
```

New / revised by this revision:

```
src/Aperture.SharedKernel/Data/
    ScopedConnection.cs        (revised)  only Dapper caller; connects as the reader role,
                                          opens a read-only tx, sets the six session GUCs via
                                          parameterised set_config, then reads. No placeholder.
    ScopeSessionContext.cs     (new)      DataScopeSet -> the six set_config statements + params
src/Aperture.SharedKernel/Data/RowLevelSecurity/
    ScopeRlsPolicy.sql.cs (or a .sql resource + convention)  the policy template a scoped table
                                          adopts: ENABLE ROW LEVEL SECURITY + the USING union
src/Modules/Access/…/Migrations/         reader role bootstrap (idempotent) + RLS on the
                                          differential-test probe table
src/Aperture.SharedKernel.Tests/Data/ScopedConnectionTests.cs        (revised — no placeholder)
src/Modules/Access/Aperture.Modules.Access.Tests/ScopeRlsEquivalenceTests.cs  (new/renamed)
```

**Schema:** a change now. Enabling RLS and its policies on each scoped table, plus provisioning the
least-privilege `aperture_reader` login role. Both are additive and expand-only (the owner role is
unaffected because `FORCE ROW LEVEL SECURITY` is deliberately off). No column changes. Because no
domain grid table exists yet, this revision lands the **role, the session-context mechanism, and the
policy convention/template**, and enables RLS only on the probe table the differential test uses;
real Sales/Orders tables adopt the convention when they land (002+).

**Contracts / events:** none. Nothing crosses a module boundary.

**Endpoints:** none. This plan adds no route — which is why `measure.sh endpoints` must still read
`0 without a policy` after every portion, and is the cheapest regression check available.

**Screens:** none.

## Out of scope for this plan

- Any raw-SQL **write** path (`Execute`, `ExecuteScalar`, bulk insert). Reads only.
- A Roslyn analyser (deferred above, with a stated trigger).
- A raw-SQL **write** path with RLS `WITH CHECK` policies — reads only; a write path is a separate
  design change (see *Concurrency*).
- Rolling RLS out to real domain tables. This revision lands the role, the session-context
  mechanism, and the policy **convention**, and enables RLS on the probe table only. Sales/Orders
  tables adopt the convention as they are built (002+).
- Any actual list or report query. This plan builds the path; 002 is the first traveller on it.
- Keyset pagination helpers. §4 mandates keyset paging, but it belongs with the first real grid, not
  with the scope translator — coupling them would give the translator an opinion about `ORDER BY`,
  which edge case 15 exists to prevent.
- 001-P5 (console) and 001-P6 (audit). Unaffected, unblocked, untouched.
- `docs/plans/STATE.md`'s Active and Shipped tables (PR #17 edits both).

## Portions

### [x] P1 — The gate, before there is anything to gate
**Touches:** `src/Aperture.SharedKernel.Tests/Architecture/RawSqlIsScopedTests.cs` (new),
`scripts/measure.sh` (new `rawsql` mode), `.github/pull_request_template.md` (point the existing
raw-SQL checkbox at the gate), `docs/ARCHITECTURE.md` §12 row.
*Note:* `scripts/measure.sh` is also modified by PR #17. Rebase on `master` after #17 merges before
opening this portion's PR; the changes are in different functions and should not textually conflict,
but do not assume it.
**Done when:** `scripts/measure.sh rawsql` lists every raw-SQL touchpoint in `src/` with its file and
line and prints a count, and it currently prints **zero production call sites**; and a test in
`Aperture.SharedKernel.Tests` fails the build if a file under `src/` outside the sanctioned data
project references Dapper, `NpgsqlConnection`, `FromSqlRaw`, `FromSqlInterpolated` or
`ExecuteSqlRaw`. Test projects are exempt **by path rule**.
**Tests:** edge cases 12, 13, 14. Plus: the test's own detector is verified against a fixture string,
not only against the live tree — a scanner that finds nothing because its regex is broken passes
identically to one that finds nothing because the code is clean, and that failure is invisible.
**Risk:** low. No production code. Deliberately first: the constraint must exist before the thing it
constrains, or P3 lands the Dapper reference into a repository with nothing watching it.

### [x] P2 — `DataScopeSet` → SQL fragment + parameters
**Touches:** `src/Aperture.SharedKernel/Authorization/DataScope.cs` (add abstract
`ToSqlFragment`), `ScopeSql.cs`, `ScopeFragment.cs`, `ScopeColumns.cs` (all new),
`src/Aperture.SharedKernel.Tests/Authorization/ScopeSqlTests.cs` (new).
**Depends on:** PR #17 merging — this modifies `DataScope.cs`, which #17 rewrites.
**Done when:** `scopes.ToSqlFragment(ScopeColumns.For("o"))` returns SQL text plus a parameter bag;
every scope value is a parameter; the tenant term is conjoined outside the grant union; the empty set
yields a fragment matching nothing; and a new `DataScope` case cannot compile without an
implementation.
**Tests:** edge cases 1, 2, 3 (fragment-text half), 6, 7, 8, 9, 15. Unit tests only — no database
yet. Assert the **text**, not a row count: a row count passes just as well for a wrong-but-harmless
fragment, which is the same trap 001-P4's plan called out.
**Risk:** medium — string-built SQL. Mitigated by 7 and 9, and by the fragment never containing a
caller-supplied value other than the validated alias.

> **Held-work note.** The old P3 (`ScopedConnection` with a `/**scope**/` placeholder) is in open
> PR #24, **not merged**, and is **superseded** by the P3/P4 below. Its wrapper skeleton,
> observability, "only door / requires `DataScopeSet` + `ScopeColumns`" property, and its Dapper
> package wiring are **reused**; its placeholder-substitution mechanism is **removed**. Treat PR #24
> as the branch these portions revise, not as shippable — do not merge it as-is. The plan's P3
> checkbox is un-ticked accordingly (it was never merged).

### [x] P3 — RLS foundation: reader role, policy convention, session context (fail-closed by default)
**Touches:** an Access migration provisioning the idempotent least-privilege `aperture_reader` login
role (`SELECT`-only, not owner); a migration enabling `ROW LEVEL SECURITY` + the `USING` policy on
the differential-test probe table; `src/Aperture.SharedKernel/Data/ScopeSessionContext.cs` (new —
`DataScopeSet` → the six parameterised `set_config` statements); the policy template/convention under
`Data/RowLevelSecurity/`; a Testcontainers test in the Access test project.
**Depends on:** nothing new (P1/P2 on master). Does **not** depend on Dapper being referenced — this
portion is pure schema + a translation function, testable via EF/Npgsql already present in tests.
**Done when:** on a real PostgreSQL container, a connection **as the reader role** with **no** session
context returns **zero** rows from the probe table though rows exist (edge 17); with full context set
via `ScopeSessionContext` it returns exactly the in-scope rows for every scope kind, the union, and
the empty set; a connection **as the owner role** sees RLS **not** applied (edge 18); and
`FORCE ROW LEVEL SECURITY` is asserted **off**.
**Tests:** edge cases 3 (execution half), 4, 5, 17, 18. Seed at least two tenants and rows with
`NULL` team/region/account. The policy `USING` union must be proven, at the DBMS, to equal the P2
fragment's intent — this is where the third encoding is pinned honest.
**Risk:** medium–high — it is the structural core and a schema change. The role/policy SQL is
hand-written and reviewed, not EF-generated. Worth reviewing the policy predicate specifically.

### [x] P4 — `ScopedConnection` revised: the only door, and it cannot run unscoped
**Touches:** `Directory.Packages.props` (one Dapper `PackageVersion`),
`src/Aperture.SharedKernel/Aperture.SharedKernel.csproj`, `Data/ScopedConnection.cs` (revised on the
PR #24 branch — remove the placeholder; open a read-only tx; call `ScopeSessionContext`; connect as
the reader role), `RawSqlIsScopedTests.cs` (tighten to "exactly one project references Dapper").
**Depends on:** P3 (needs the reader role, the policy, and `ScopeSessionContext`).
**Done when:** a scoped read is issuable only by supplying a `DataScopeSet` and a `ScopeColumns` (no
overload omits either); the wrapper opens a read-only transaction, establishes session context, and
runs the read as the reader role — there is **no placeholder and no caller-supplied WHERE splice**;
the in-app P2 fragment is still `AND`-ed in as the first belt; Dapper is referenced by exactly one
project (P1 test proves it); and the observability of *Observability* is emitted, including the
`Warning` when context was not established.
**Tests:** the one-project rule (13); no-unscoped-overload (reflection over the public surface);
**edge case 16 — the anti-bypass test**: pass SQL that `OR`s/​comments/​omits the fragment and prove
RLS still returns only in-scope rows on a real container; **edge 19** — two reads on one pooled
reader connection do not leak context; the empty-scope `Information` line and the unset-context
`Warning` line are emitted.
**Risk:** high — this API is what every future read query uses, and its signature is expensive to
change later. Review the method signatures and the transaction/pooling handling specifically.

### [ ] P5 — Differential equivalence on real PostgreSQL: EF and the raw path agree
**Touches:** `src/Modules/Access/…/ScopeRlsEquivalenceTests.cs` (new/renamed from the old
`ScopeSqlEquivalenceTests`), reusing `PostgresFixture` and the `ScopeProbe` 001-P4 added.
**Depends on:** P4.
**Done when:** for every scope kind, the union, and the empty set, the id set returned by EF
`WhereInScope<T>()` (owner role) equals the id set returned through `ScopedConnection` (reader role +
RLS) against the same seeded rows — including `NULL` team/region/account and a second tenant.
**Tests:** edge case 10 (the anti-drift test), now proving the *three* encodings — EF expression, P2
fragment, RLS policy — select the identical id set. Seed adversarially: two tenants, null scope
columns, duplicate grants.
**Risk:** medium — Testcontainers and adversarial seed data. If the seed is easy, it is too tidy.

## Open questions for the user

**1. The key decision — adopt Postgres RLS now, with its costs, as the structural belt?**
This revision concludes that *no in-app string-composition scheme is structural* (see *Design
decisions*): the placeholder, the "wrapper owns the WHERE tail", and the subquery-wrap all leak to a
trailing `--`, an `OR`, or an unbalanced `)` in caller SQL. The only mechanism that makes an unscoped
raw read *impossible to express* is enforcement below the SQL string — RLS. **My recommendation:
adopt it**, contained to a dedicated reader role so EF is untouched. The costs you are accepting:

- a second, least-privilege DB **login role** and connection string for the API;
- a short **read-only transaction per raw read** to carry `SET LOCAL`/`set_config` session context
  (the pooling-safety requirement) — negligible latency, but it is a real behaviour;
- a **third encoding** of the scope rule (EF expression, P2 fragment, RLS policy) whose agreement is
  pinned by P5's differential test rather than by inspection;
- a class of failure RLS introduces — a *misconfigured* reader connection returns **zero** rows
  silently — mitigated by the `Warning`-on-unset-context in *Observability*, but real.

The alternative I considered and rejected — the reviewer's "whole-statement ownership" with
structured filters — is lighter (no DB change) but **is not actually structural** (its `FROM` clause
is still free SQL that a `--` defeats) *and* costs ergonomics. Paying for a guarantee it does not
deliver is worse than paying for RLS. If you disagree with adopting RLS now, the honest fallback is
**not** that middle path but to keep P3's placeholder and accept the guarantee stays by-convention —
which is the thing you asked to fix. So this is really: RLS now, or the gap stays open.

**2. This makes an ARCHITECTURE.md design-section change — approve the intent?** §3/§4/§2 currently
frame RLS as *deferred* and raw SQL's tenant-passing as "the acknowledged sharp edge… reviewer checks
each one by hand". After this plan that is false. These are §1–§11 design sections, so I have **not**
edited them — proposed diffs, yours to accept:

   ```diff
   # §2, the RLS-deferred note:
   -  Postgres RLS is a **deferred** second belt: valuable, but it needs a connection-level session
   -  variable per request and it complicates pooling. The trigger … whichever comes first.
   +  Postgres RLS is the **enforcement belt for the raw-SQL read path** (009): a dedicated
   +  least-privilege reader role is subject to row-security policies that re-assert tenant + scope
   +  from per-request session context, so an unscoped raw read cannot be expressed and an
   +  unconfigured connection returns nothing. It is scoped to that reader role — the owner role EF
   +  uses is unaffected (FORCE is off). Broader RLS (all readers) remains deferred to the first
   +  external DB consumer.

   # §2, the "sharp edge" bullet:
   -  **Dapper and raw SQL do not inherit the filter.** Every raw statement passes `tenant_id`
   -  explicitly, and the reviewer checks each one by hand. This is the acknowledged sharp edge …
   +  **Dapper and raw SQL do not inherit the EF filter — RLS supplies it instead.** Raw reads go
   +  through `ScopedConnection` as the reader role, and row-security policies enforce tenant + scope
   +  at the DBMS regardless of how the query is written (009).

   # §4, the Dapper bullet:
   -  knowledge that matters here is the SQL, and it is written as SQL.
   +  knowledge that matters here is the SQL, and it is written as SQL. Dapper is reached only through
   +  `ScopedConnection` (referenced by exactly one project; an architecture test enforces it), which
   +  runs as an RLS-bound reader role — the correct path is the only path (009).
   ```

**3. Strengthen CLAUDE.md invariant 2?** It says raw SQL "must pass `tenant_id` explicitly — there is
no ambient safety net there." After this plan there *is* one. Proposed (yours to apply, per the
invariant boundary):

   ```diff
   -   Raw SQL (Dapper) must pass `tenant_id` explicitly — there is no ambient safety net there.
   +   Raw SQL reaches the database only through `ScopedConnection`, as an RLS-bound reader role that
   +   requires a `DataScopeSet`; row-security policies enforce tenant + scope at the DBMS. Referencing
   +   Dapper or `NpgsqlConnection` anywhere else in `src/` fails the build.
   ```

**4. Sequencing against 002.** Unchanged from the prior revision: land this before the first real
grid or it becomes a retrofit. RLS makes that argument slightly stronger — adopting the policy
convention before tables exist is cheaper than adding policies to live tables under
expand→backfill→contract.

**5. Does `AllTenant` need a distinct SQL/policy form?** In the RLS policy it becomes
`current_setting('app.all_tenant')::bool`, leaving no scope-column predicate. Correct, but a human
reading `pg_stat_statements` cannot distinguish "admin, all-tenant grant" from a misconfiguration.
A marker (comment or a tag column) would make the audit trail readable. Related to plan 001's still-
open question 1.
