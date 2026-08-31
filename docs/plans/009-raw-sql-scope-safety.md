# 009 — Raw SQL scope safety: a correct Dapper path, and a gate that fails without it

Status: in-progress      <!-- draft → approved → in-progress → done -->
Roadmap: ARCHITECTURE.md §13 — not a numbered roadmap item; this is the raw-SQL half of 001's
authorization spine, split out (see *Why its own plan* below). Sequence it **immediately after 001**
and **before 002**; the plan number is a file identifier, not a build order.
Measured: `scripts/measure.sh` on 2026-08-30, branch `chore/plan-lifecycle-states` (356555d), with
`feat/001-P4-scope-sql-predicate` (897e627) read alongside.

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
| A `ScopedSql`/`IScopedQuery` wrapper that is the only type able to reach Dapper | **Essential** | The strongest available lever *and* it is nearly free today, because Dapper is not yet referenced. The package reference lands in exactly one project; every other project physically cannot call `connection.QueryAsync`. This is a constraint enforced by the compiler and the project graph, not by a reviewer's attention. Cost after the first twenty grids exist: a migration. Cost today: one `PackageVersion` line and one `csproj`. |
| A source-scanning test over `src/` + a `scripts/measure.sh rawsql` mode | **Essential** | Cheap — one test method and ~20 lines of shell — and it catches the two things the wrapper cannot: a second project adding the Dapper package, and `FromSqlRaw`/`ExecuteSqlRaw`/`NpgsqlConnection` reached through EF or Npgsql directly, neither of which the wrapper gates. There is direct precedent: `measure.sh`'s comment already describes the endpoint-policy gate as "the cheap copy that runs without a build" paired with a real architecture test. Mirror that shape exactly. |
| Differential test: EF predicate vs SQL fragment, same rows, real PostgreSQL | **Essential** | Two hand-written encodings of one rule *will* drift. Asserting they select the identical id set is the only mechanism that catches drift rather than documenting the hope of avoiding it. |
| A Roslyn analyser | **Deferred** | It would be more precise than a grep — it sees semantics, not text, and it can flag `IDbConnection.QueryAsync` reached through an interface the grep cannot follow. But it is a separate analyser project, a `Microsoft.CodeAnalysis` version to keep in step with the SDK, an analyser test harness, packaging into every `csproj`, and a suppression story. Against zero call sites that is machinery in place of a decision. **Trigger:** the grep produces its first false positive that someone wants to suppress, or raw-SQL call sites pass ~20 across ≥3 modules. Until then the grep is, honestly, ~90% as effective for ~5% of the cost. |
| An approval test snapshotting every raw-SQL call site | **Rejected** | It converts every legitimate query into a snapshot diff, and that diff is reviewed by the same humans who would have missed the missing `tenant_id` in the first place. It generates review load precisely proportional to healthy activity and detection power that does not scale with it. The wrapper makes the same guarantee structurally. |
| Generating the whole `SELECT` — a scoped query builder / mini-ORM | **Rejected** | §4 says the knowledge that matters in a list query is the SQL and it is written as SQL. A builder that owns the whole statement re-invents the ORM the design deliberately stepped out of, and the first join it cannot express is the day it gets bypassed. Produce a **fragment**; let the developer write the query. |
| Postgres Row-Level Security as the enforcement point | **Deferred** | Genuinely attractive — it is the one mechanism that cannot be bypassed by forgetting. But it needs a per-request `SET LOCAL` on a pooled connection, policies expressive enough for the union of five scope kinds, and it moves an authorization decision into a place `dotnet test` cannot easily see. **Trigger:** a second consumer of the database that is not this API (a reporting tool, a replica for analytics). Worth an ADR then; premature now, and the fragment builder is not wasted work if RLS later arrives — it becomes defence in depth. |

## Failure modes

| Concern | Answer |
|---|---|
| **Tenancy** | The fragment always emits `{alias}.tenant_id = @__scope_tenant` conjoined outside the union — same structure as `ScopeQuerying`, and for the same reason: no grant, including `AllTenant`, can be `OR`-ed past it. On a join, the caller passes an alias per scoped table and gets one fragment per alias; the plan does **not** attempt to infer which tables in a join need scoping. That inference is the thing that would be silently wrong, so it is the developer's explicit call, and the review checklist says so. |
| **Authorization** | The same `DataScopeSet` the EF path takes, resolved by the same P3 pipeline. There is no second resolution path — that is the point. **Empty scope set:** the builder returns the fragment `1 = 0` (with the tenant term still present) and an empty parameter set, decided before any column is bound, mirroring `ScopeQuerying`'s early return. It never returns `null`, never returns an empty string, and never returns `TRUE`. A `null`/empty return is the specific shape that a caller writing `WHERE 1=1 {fragment}` turns into an unfiltered scan. |
| **Consistency** | Read-only. Dapper reads run outside the EF change tracker, so a Dapper list query in the same request as an uncommitted EF write is **not** read-your-writes — it reads committed state. Callers that need read-your-writes must read through EF. Stated in the wrapper's XML doc, and P4's test asserts the read-committed behaviour so the claim is not just prose. No transaction is opened by the wrapper; it participates in an ambient one if given a connection that has one. |
| **Concurrency** | None introduced — no writes. The wrapper deliberately exposes query-shaped methods only; `Execute`/`ExecuteScalar` for writes are **out of scope** and absent, so there is no raw-SQL write path to reason about yet. Adding one is a design change and goes to the user. |
| **Idempotency** | Not applicable — reads. Retrying a scoped read is free. |
| **Ordering** | Not applicable. The fragment contributes to `WHERE` only; it never emits `ORDER BY` and must not, because §4's keyset pagination owns ordering and a translator quietly appending an order would break the cursor. Asserted by a test on the emitted fragment text. |
| **Failure** | The builder is pure and total — it throws only on a null argument or an alias that is not a valid identifier. A malformed alias is an `ArgumentException` at the call site, not a sanitised-and-continued string, because sanitising an identifier is how injection sneaks back in. Connection failures are the caller's ordinary Npgsql failures; the wrapper adds no retry (a retry policy that hides a saturated pool is worse than the timeout). |
| **Backward compatibility** | Nothing to migrate — zero call sites, no schema change, no migration. This plan is purely additive to the project graph. The one compatibility question is the **package reference**: Dapper is added to `Directory.Packages.props` once, centrally pinned, per the file's existing rationale about transitive version drift. The gate in P1 lands before the package in P3 so the constraint exists before the thing it constrains. |
| **Observability** | The fragment carries a stable comment marker (e.g. `/* scope */`) so a scoped predicate is greppable in `pg_stat_statements` and in logs — which makes "was this query scoped?" answerable in production, not just in review. The wrapper opens an activity span with tags for tenant id, scope-kind counts (not scope values — those are row identifiers and do not belong in telemetry), and the emitted fragment's shape. An empty-scope query is logged at `Information` with the principal, because "user sees nothing" is a support ticket, and the answer must be one log line rather than a debugging session. |

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

## Target design

Per `ARCHITECTURE.md` §3 (authorization), §4 (data access) and invariant 2 in `CLAUDE.md`.

**Module:** none. This is `Aperture.SharedKernel` — cross-cutting authorization primitive, the same
home as `ScopeQuerying`. §1 says SharedKernel is not a dumping ground; this qualifies on the same
grounds P4 did.

```
src/Aperture.SharedKernel/Authorization/
    DataScope.cs               (modified) + abstract ToSqlFragment(...)
    ScopeSql.cs                (new)      DataScopeSet -> ScopeFragment
    ScopeFragment.cs           (new)      readonly record: Sql + IReadOnlyDictionary<string, object?>
    ScopeColumns.cs            (new)      alias + the five column names, with the P2 snake_case default
src/Aperture.SharedKernel/Data/
    ScopedConnection.cs        (new)      the only type that references Dapper
src/Aperture.SharedKernel.Tests/Authorization/ScopeSqlTests.cs                 (new)
src/Aperture.SharedKernel.Tests/Architecture/RawSqlIsScopedTests.cs            (new)
src/Modules/Access/Aperture.Modules.Access.Tests/ScopeSqlEquivalenceTests.cs   (new)
scripts/measure.sh             (modified) + `rawsql` mode
```

**Schema:** no change. No migration.

**Contracts / events:** none. Nothing crosses a module boundary.

**Endpoints:** none. This plan adds no route — which is why `measure.sh endpoints` must still read
`0 without a policy` after every portion, and is the cheapest regression check available.

**Screens:** none.

## Out of scope for this plan

- Any raw-SQL **write** path (`Execute`, `ExecuteScalar`, bulk insert). Reads only.
- A Roslyn analyser (deferred above, with a stated trigger).
- Postgres RLS (deferred above, with a stated trigger).
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

### [ ] P3 — `ScopedConnection`: the only way to reach Dapper
**Touches:** `Directory.Packages.props` (one `PackageVersion`, Data group),
`src/Aperture.SharedKernel/Aperture.SharedKernel.csproj`,
`src/Aperture.SharedKernel/Data/ScopedConnection.cs` (new),
`src/Aperture.SharedKernel.Tests/Architecture/RawSqlIsScopedTests.cs` (tighten to
"exactly one project may reference Dapper").
**Done when:** a caller can run a scoped Dapper read only by supplying a `DataScopeSet` and a
`ScopeColumns` — there is no overload that omits either; the query method composes the fragment
itself rather than trusting the caller to interpolate it; the Dapper package is referenced by
exactly one project and the P1 test proves it; and the span/log described under *Observability* is
emitted, including the `Information` line for an empty scope set.
**Tests:** the one-project rule (13); a compile-level assertion that no unscoped query overload
exists (a `dynamic`/reflection test over the public surface is acceptable and cheap); the
empty-scope log line is emitted; the activity span carries tenant id and scope-kind counts and
**not** scope values.
**Risk:** medium — introduces a dependency and an API that every future read query will use, so its
shape is expensive to change later. Worth reviewing the method signatures specifically.

### [ ] P4 — Differential test: EF and Dapper must agree, on real PostgreSQL
**Touches:** `src/Modules/Access/Aperture.Modules.Access.Tests/ScopeSqlEquivalenceTests.cs` (new),
reusing the existing `PostgresFixture` and `ScopeProbe` that 001-P4 added.
**Done when:** for every scope kind, the union, and the empty set, the id set returned by
`WhereInScope<T>()` equals the id set returned by the Dapper fragment against the same seeded rows
in a real PostgreSQL container — including rows with `NULL` team/region/account and rows belonging
to a second tenant.
**Tests:** edge cases 3 (execution half), 4, 5, 10. Seed at least two tenants; a single-tenant
fixture cannot fail the test that matters most.
**Risk:** medium — Testcontainers, and the seed data must be adversarial rather than convenient. If
this test is easy to write, the seed is probably too tidy.

## Open questions for the user

1. **Should §4 be amended to say the raw-SQL path is gated?** §4 currently reads as though Dapper is
   freely available, and it is a design section (§1–§11), so I have not edited it. Proposed diff,
   for §4's second bullet:

   ```diff
    - **Dapper** owns list and report queries. Grids need shaped, paged, joined projections;
      expressing those through the ORM produces either a slow query or an unreadable one. The
   -  knowledge that matters here is the SQL, and it is written as SQL.
   +  knowledge that matters here is the SQL, and it is written as SQL. **Dapper is reached only
   +  through `ScopedConnection`, which cannot be called without a `DataScopeSet`** (009). The
   +  package is referenced by exactly one project and an architecture test enforces it: raw SQL is
   +  the one place where fail-open is the path of least effort, so the correct path is made the
   +  only path rather than the recommended one.
   ```

2. **Should invariant 2 in `CLAUDE.md` be strengthened?** It currently says raw SQL "must pass
   `tenant_id` explicitly — there is no ambient safety net there". After this plan there *is* a
   safety net, and the sentence would be actively misleading. Proposed:

   ```diff
   -   Raw SQL (Dapper) must pass `tenant_id` explicitly — there is no ambient safety net there.
   +   Raw SQL reaches the database only through `ScopedConnection`, which requires a
   +   `DataScopeSet` and emits the tenant predicate itself. Referencing Dapper or `NpgsqlConnection`
   +   anywhere else in `src/` fails the build.
   ```
   Both are yours to accept, not mine to apply.

3. **Sequencing against 002.** I have argued this should land before the first real list endpoint,
   because after 002 it is a migration rather than a constraint. But that delays the first visible
   domain slice by four portions of pure infrastructure. Your call — the alternative (002 first,
   this immediately after) is defensible if seeing the domain move matters more than the cost of
   retrofitting maybe five call sites.

4. **Does `AllTenant` need a distinct SQL form?** It currently emits `TRUE` inside the union, leaving
   `tenant_id = @t AND (TRUE)`. Correct, and Postgres folds it. But a human reading
   `pg_stat_statements` cannot then distinguish "admin, all-tenant grant" from "someone forgot the
   scope". Emitting a marker comment instead would make the audit trail readable at the cost of a
   little noise. Related to question 1 in plan 001, which is still open.
