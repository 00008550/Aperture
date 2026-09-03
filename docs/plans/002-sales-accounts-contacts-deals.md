# 002 — Sales: accounts, contacts, deals + the deal state machine

Status: in-progress
Roadmap: ARCHITECTURE.md §13 item 002
Measured: `scripts/measure.sh` on 2026-09-03, commit `173baee` (branch `feat/009-P4-scoped-connection-reader-role`, with 009 P1–P5 merged to `master`)

## Ground truth

Measured, not taken from the docs. Output of `scripts/measure.sh` on 2026-09-03:

**endpoints** — 3 mapped routes, 0 without a policy:
```
MapGet("/api/me")               RequireAuthorization()   src/Aperture.Api/Endpoints/MeEndpoints.cs:36
MapHealthChecks("/health/live") AllowAnonymous()         src/Aperture.Api/Program.cs:34
MapHealthChecks("/health/ready")AllowAnonymous()         src/Aperture.Api/Program.cs:35
```
There is **no Sales endpoint of any kind yet.** Everything in this plan is greenfield behind the spine.

**permissions** — 17 declared, 9 never enforced. The Sales-relevant ones already in the registry
(`Aperture.SharedKernel/Authorization/Permissions.cs`) and their enforcement state today:
```
accounts.read           enforced in 1 file(s)     accounts.write          *** DECLARED, NEVER ENFORCED ***
contacts.read           enforced in 1 file(s)     contacts.write          *** DECLARED, NEVER ENFORCED ***
deals.read              enforced in 2 file(s)     deals.write             *** DECLARED, NEVER ENFORCED ***
deals.discount.approve  *** DECLARED, NEVER ENFORCED ***
```
The "enforced in N file(s)" for the `*.read` constants is their appearance in seed/role fixtures and
the policy-provider tests, **not** an endpoint — there is no Sales endpoint. 002 is what turns
`accounts.write`, `contacts.write`, `deals.write`, and `deals.discount.approve` from declared into
enforced. **No new permission needs to be added**; the registry already anticipated Sales.

**schema** — 10 tables, all in the `access` schema (`AccessDbContext`). There is **no `sales` schema
and no Sales migration.** Widest Access table is `audit_events` (11 columns). For reference, this
plan's widest table will be **`sales.deals`** (owner, account, stage, frozen price-list version,
amount, discount %, pending-approval fields, lost-reason code, the five scope columns, timestamps,
`xmin`) — it is a subsystem, not an entity, and P5/P6 are built around it.

**tests** — 3 test projects (Api 39, SharedKernel 55, Access 52); **Modules without tests: none**
*today only because Sales does not exist yet*. `find` reports no `Aperture.Modules.Sales*`. 002 must
create `src/Modules/Sales/Aperture.Modules.Sales.Tests/` and wire it into `Aperture.slnx`, or every
`dotnet test` run is green about code it never touched.

**rawsql** — 0 production call sites; 44 exempt (test fixtures + the sanctioned wrapper
`Aperture.SharedKernel/Data/ScopedConnection.cs`). **gate** — PASSED (both invariants).

**The owed 009 wiring, confirmed by measurement.** `grep` for `NpgsqlDataSource`/`ScopedConnection`
across `src` outside the wrapper and tests returns **nothing**: the reader `NpgsqlDataSource` and
`ScopedConnection` are **not registered in API DI**. 009-P4's own portion notes flagged this — "the
API gains a second connection string", "nothing connects as this role until it is wired". 002 is the
**first real consumer** and therefore owns that wiring: bind the reader connection string (distinct
from the EF owner `ConnectionStrings:Aperture`), source the `aperture_reader` password from a deploy
secret, build the reader `NpgsqlDataSource`, register `ScopedConnection`, and subscribe the host to
its `ActivitySource` (`Aperture.SharedKernel.Data.ScopedConnection`).

### What the spine already provides that 002 MUST reuse, not reinvent

- **Tenancy:** `ITenantContext`/`TenantId`/`UserId` (`SharedKernel/Multitenancy`); the per-context
  global query filter convention on `ITenantOwned`, applied in `AccessDbContext.OnModelCreating` by
  reflection and asserted by a convention test. Sales copies this convention verbatim.
- **RBAC:** `Permissions` registry + `.RequirePermission("…")` policy (`Aperture.Api/Authorization`).
- **ABAC / EF path:** `DataScope`/`DataScopeSet`, `IScopedResource`, and `ScopeQuerying.ToPredicate`
  (`WhereInScope<T>()`, 001-P4) for `IQueryable` writes/reads.
- **ABAC / raw path (009):** `ScopeColumns`, `ScopeSql.ToSqlFragment`, `ScopedConnection` (the only
  raw door), `ScopeRlsPolicy.Enable(schema, table)` (the RLS convention), `ScopeSessionContext`
  (session GUCs). The differential test in Access proves EF, fragment, and RLS agree.
- **Persistence conventions:** `TypedIdConverters`, per-table `ToTable(name, schema)`, snake_case
  columns, module-owned `__migrations` history table (`AccessNpgsqlOptions`).
- **Principal & audit:** `GetAccessPrincipal()` yields `TenantId`, `UserId`, `Permissions`, and the
  resolved `Scopes` (`DataScopeSet`) for the request; `IAuditTrail` (001-P6) writes an audit row in
  the same unit of work as a mutation.
- **Test harness:** Access's `PostgresFixture` (real PostgreSQL via Testcontainers, migrated by the
  real migration, reader role provisioned with a test password, `ScopeRlsPolicy.Enable` on a probe).
  Sales's fixture mirrors it.

### Corrections made to Aperture's records (Rule zero)

Two §12 rows contradicted measurement (009 is fully merged; Dapper is present). Corrected in place —
see the diff in *Open questions → record corrections applied*. No §1–§11 design section was changed.

## Domain behaviour

From `DOMAIN.md` §2 (verbatim rules), the states and precedence:

**Account** — a company we sell to; credit limit, payment terms, an **owning agent**, a **region**.
Deduplicated on **tax identifier**: the same company arriving twice is one account (natural key).

**Contact** — a person at an account; belongs to **exactly one account**. A person who moves is a
**new** contact; the old one is marked **departed**, never deleted (history stays attributable).

**Deal** — an intent to sell, **owned by one agent**. Lifecycle:
`new → qualified → quoted → negotiation → won | lost`. Business-enforced rules:
1. **Won** requires **≥1 line with a price and a quantity**.
2. Moving to **quoted freezes the price-list version** used (a later price change must not alter an
   outstanding quote).
3. A **discount above the agent's threshold requires the lead's approval**; the deal stays in
   `negotiation` with a **pending approval** rather than advancing.
4. **Lost requires a reason code** ("no reason" was the most expensive missing field).

`won` and `lost` are terminal. `ARCHITECTURE.md` §5 requires the transitions to live in **one
table-driven definition** per aggregate, and an illegal transition to be a **domain error**, not a
forgotten `if`.

Deal → Order is out of 002's scope (Orders is 003). The link is a **synchronous contract read**
("Order is created from a won deal, and only from a won deal") that 003 makes against Sales; 002
exposes the query, not an event.

## Design decisions

| Decision | Class | Reason |
|---|---|---|
| Sales owns the `sales` schema; its own `SalesDbContext`, its own `ITenantOwned` marker mirroring Access's convention | **Essential** | ARCHITECTURE.md §1 — a module owns a schema and reaches others only via `Aperture.Contracts`. |
| Reuse the spine wholesale (query filter, `WhereInScope`, `ScopedConnection`, RLS convention, permission policies) | **Essential** | Reinventing any of it re-opens DOMAIN.md §5.1 (the region leak). |
| EF Core for writes (aggregates loaded/saved whole, `xmin` concurrency token); Dapper via `ScopedConnection` for list/report grids | **Essential** | ARCHITECTURE.md §4. Grids are shaped, paged, joined projections. |
| **Denormalize the five scope columns** (`tenant_id, owner_user_id, team_id, region_id, account_id`) onto every Sales row, kept in sync on write | **Essential** | Both scope paths (EF `IScopedResource` and RLS policy) read columns *on the row*. Contacts and deals inherit owner/team/region/account from their account at write time. A join-to-resolve-scope approach cannot be expressed in the RLS `USING` predicate, which is single-table. The cost — re-stamping children when an account is reassigned — is accepted and tested (edge cases). |
| **Account** row carries `account_id = its own id` | **Essential** | So a `DataScope.Account(x)` grant admits account `x` itself, uniformly with its children. |
| Deal is an aggregate that **owns its lines** (`sales.deal_lines`); a line has price + quantity + frozen price-list version | **Essential** | Rules 1 and 2 are evaluated over the lines; they are loaded and saved with the deal. |
| Table-driven state machine (one definition table of legal `(from,to)` edges + guard per edge); illegal transition → 409 domain error | **Essential** | ARCHITECTURE.md §5. Guards encode rules 1, 3, 4. |
| Tax-identifier dedup enforced by a **unique index** `(tenant_id, tax_id)` + a contract-level upsert-or-reject on create | **Essential** | DOMAIN.md §2; also the cheapest correct idempotency for account creation. |
| **Optimistic concurrency (`xmin`)** on deal transitions and account edits; conflict → 409 with current state | **Essential** | ARCHITECTURE.md §5. Two users editing the same deal is the contended case here; there is no last-unit contention in Sales (that is Orders/stock, 003). |
| **No outbox / integration events in 002** | **Deferred** — trigger: 004 lands the outbox; 003 needs the won-deal signal | Invariant 6 forbids `Publish` inside a transaction, and the outbox does not exist yet. 003 reads the won deal via a synchronous `Aperture.Contracts` call (the §1 escape hatch), so 002 needs no event. Emitting `DealWon` now would mean either violating invariant 6 or building the outbox early — both worse than a contract read. |
| **Generic `Idempotency-Key` ingress table** | **Deferred** — trigger: 003, where it lands with orders/webhooks | Sales writes are internal, console-driven; the double-click risk is covered structurally by the tax-id unique index (accounts) and by optimistic-token transitions (deals: a replayed transition from a stale state 409s). A generic `access.idempotency_keys` table is 003's concern (invariant 7 targets external callers and webhooks, which Sales has none of). Flagged as an open question. |
| Field-level hiding of **cost price** from Fulfilment (DOMAIN.md §1) | **Deferred** — trigger: cost price is introduced (it is an Orders/pricing concern, 003+) | 002's account/deal DTOs carry no cost field to hide yet. When one appears, it is a per-permission projection (ARCHITECTURE.md §3), not a nulled shared DTO. |
| A configurable, per-tenant **stage vocabulary** | **Rejected** for 002 | The five stages are fixed in DOMAIN.md and the rules are written against them by name. A tenant-configurable stage table would add a join and a class of "unknown stage" bugs for a flexibility no requirement asks for. Revisit only if a tenant genuinely needs a different pipeline. (Open question, with recommendation.) |
| Contact belongs to **many** accounts | **Rejected** for 002 | DOMAIN.md §2 is explicit: "belongs to exactly one account". A join table would contradict the source of truth. |
| Event-sourcing the deal lifecycle | **Rejected** | The state machine + an audit row per transition (via `IAuditTrail`) answers "who moved this deal and when" without an event store. Event sourcing here is pattern-for-its-own-sake; its cost is a projection layer nothing needs. |

## Failure modes

| Concern | Answer |
|---|---|
| **Tenancy** | Every Sales table is `ITenantOwned`; `SalesDbContext` applies the global query filter by the same reflection convention as Access, asserted by a Sales convention test. Raw grids go through `ScopedConnection` (reader role) whose RLS policy re-asserts `tenant_id` below the SQL. Two belts, both structural. |
| **Authorization** | RBAC by `.RequirePermission` on every endpoint (`accounts.*`, `contacts.*`, `deals.*`, `deals.discount.approve`). ABAC by the request's resolved `DataScopeSet`: EF writes/reads via `WhereInScope`, raw grids via `ScopedConnection`. **Empty scope set denies** — `WhereInScope` yields a `1=0` predicate and the RLS policy admits no row; a Sales test asserts an agent with no grants sees zero accounts/deals, not all. |
| **Consistency** | One command = one transaction = one aggregate (account, or deal+lines). The deal and its lines are saved whole. Cross-aggregate effect (deal→order) is a later synchronous read, not a write here. Read-your-writes holds through EF; a raw grid on `ScopedConnection` sees committed state only (separate reader connection) — the list endpoints tolerate that (they are not read-back-after-write paths). |
| **Concurrency** | `xmin` concurrency token on `sales.accounts` and `sales.deals`. Two leads transitioning the same deal in the same second: the second commit fails the token check → 409 with current state, the client re-applies against the new state. No lost update. (No `FOR UPDATE` in Sales — there is no last-unit contention here; that is Orders.) |
| **Idempotency** | Account create: `(tenant_id, tax_id)` unique index makes a double-submit a no-op-or-conflict, returning the existing account rather than a duplicate (DOMAIN.md dedup rule). Deal transition: keyed on the optimistic token, so a replayed transition from an already-advanced state 409s rather than double-advancing. A generic idempotency-key table is deferred to 003. |
| **Ordering** | No async events in 002, so no cross-aggregate ordering to guard. Within a deal, transitions are serialized by the `xmin` token: an out-of-order transition attempt fails the guard or the token. |
| **Failure** | No worker/queue path in 002. A timeout mid-write rolls back the single transaction (account or deal+lines) atomically — a half-written deal (deal without its lines, or a transition without its audit row) cannot commit because they share one unit of work. |
| **Backward compatibility** | Additive only: a new schema, new tables, new endpoints. Migrations follow expand→backfill→contract, but 002 is all expand (nothing exists to contract). Enabling RLS on Sales tables is invisible to EF (owner role, `NO FORCE`), so the policy migration deploys while old code runs. The reader connection string is new config; absent it, `ScopedConnection` is simply unused until an endpoint calls it — no existing path changes. |
| **Observability** | `ScopedConnection` already emits a span per raw read tagged with tenant id and per-kind scope **counts** (never scope values), the reader role, and whether context was established; 002 subscribes the host to that `ActivitySource`. Each deal transition writes an `IAuditTrail` row (actor, tenant, from→to, reason) — "why is this deal stuck / who moved it" is answerable from the trail. Empty-scope reads log at Information; unset tenant at Warning (both already in the wrapper). |

## Edge cases

Given/When/Then — these become the builder's test list verbatim.

1. **Tenant isolation.** Given accounts in tenants A and B with the same GUIDs reused as owner/team/region ids; When an agent in A lists accounts; Then only A's rows return, through both the EF write-model read and the `ScopedConnection` grid.
2. **Empty scope set denies.** Given an agent with a valid tenant but **zero** scope grants; When they list accounts/deals; Then zero rows (not all) via EF and via RLS; and `ScopedConnection` logs the empty-scope Information line.
3. **Scope union.** Given an agent granted `Self ∪ Team(t) ∪ Region(r)`; When they list deals; Then exactly the union of their own, their team's, and that region's deals — no more.
4. **Absent scope column narrows.** Given a deal with `team_id = NULL`; When a `Team(t)` agent lists; Then that deal is excluded (NULL ≠ ANY), matching the RLS policy and the EF predicate.
5. **Account tax-id dedup.** Given an account with tax id `X` in tenant A; When a create with tax id `X` arrives again in A; Then the existing account is returned / the create is rejected as a duplicate, never a second row. Given tax id `X` in tenant B; Then that is a distinct account (dedup is per tenant).
6. **Contact belongs to one account.** Given a create with two account ids; Then it is rejected. Given a contact whose person moved; When marked departed; Then the row remains and is excluded from active lists but visible in history.
7. **Contact scope inheritance.** Given account `acc` owned by agent `u` in region `r`; When a contact is created under `acc`; Then the contact's `owner_user_id/region_id/account_id` equal the account's; and a `Region(r)` agent sees the contact.
8. **Account reassignment re-stamps children.** Given account `acc` reassigned from agent `u1` to `u2` (and/or region `r1→r2`); Then its contacts and deals have their denormalized scope columns updated in the same transaction; and a `Self(u1)` agent no longer sees them while `Self(u2)` does.
9. **Won requires a priced line.** Given a deal in `negotiation` with no line, or a line missing price or quantity; When transitioning to `won`; Then a domain error (422/409), no state change.
10. **Quoted freezes price-list version.** Given a deal moved to `quoted` when the price-list version is `v1`; When the version later becomes `v2`; Then the deal's lines still reference `v1`.
11. **Lost requires a reason.** Given a deal transitioned to `lost` with no reason code; Then rejected; With a reason code; Then accepted, terminal, and the reason is persisted and audited.
12. **Illegal transition rejected.** Given a deal in `new`; When transitioning directly to `won`; Then rejected by the machine as an illegal edge, with the attempt auditable. Given a deal in `won` (terminal); When any transition attempted; Then rejected.
13. **Discount over threshold holds in negotiation.** Given a discount above the agent's threshold; When the agent tries to advance; Then the deal stays in `negotiation` with a **pending approval** recorded; the agent alone cannot clear it.
14. **Lead approves discount.** Given a pending approval; When a user with `deals.discount.approve` approves; Then the approval records who and why (audited) and the deal may advance; a user without that permission is denied.
15. **Concurrent deal edit.** Given two users load the same deal; When both transition it in the same second; Then exactly one commits and the other gets 409 with the current state.
16. **Keyset pagination stability.** Given more deals than one page; When paging by `(created_at, id)` under concurrent insert; Then no row is skipped or duplicated across pages.
17. **Every Sales endpoint carries a policy.** `measure.sh endpoints` shows every new route with a non-empty policy; `measure.sh gate` stays green.
18. **Convention test.** Every `ITenantOwned` Sales entity has the global query filter applied (reflection test), mirroring Access.

## Target design

**Module:** `src/Modules/Sales/Aperture.Modules.Sales` (+ `…Sales.Tests`), one public
`SalesModule.AddSalesModule(...)` registration surface; internals `internal` (ARCHITECTURE.md §1).

**Schema (`sales`):**
- `accounts` — id, tenant_id, name, tax_id, credit_limit, payment_terms, owner_user_id, region_id,
  team_id, account_id(=id), created_at, xmin. Unique `(tenant_id, tax_id)`.
- `contacts` — id, tenant_id, account_id, name, channels (email/phone/messenger), is_departed,
  denormalized owner_user_id/team_id/region_id, created_at.
- `deals` — id, tenant_id, name, stage, owner_user_id, account_id, region_id, team_id,
  amount, discount_pct, frozen_price_list_version, pending_approval fields, lost_reason_code,
  created_at, xmin. (The widest table; the subsystem P5/P6 are built around.)
- `deal_lines` — id, tenant_id, deal_id, product_ref, unit_price, quantity, price_list_version.
- Each table adopts `ScopeRlsPolicy.Enable("sales", <table>)` in its migration (reader-role RLS).

**Contracts (`Aperture.Contracts`):** a synchronous request/response interface exposing "get won deal
by id" and "list deals for account", for 003 to read — no integration event in 002.

**Endpoints (all `.RequirePermission(...)`):**
- `POST /api/accounts` `accounts.write`; `GET /api/accounts` (grid) `accounts.read`; `GET /api/accounts/{id}` `accounts.read`; `PATCH /api/accounts/{id}` (incl. reassignment) `accounts.write`.
- `POST /api/accounts/{id}/contacts` `contacts.write`; `GET /api/contacts` `contacts.read`; `POST /api/contacts/{id}/depart` `contacts.write`.
- `POST /api/deals` `deals.write`; `GET /api/deals` (grid) `deals.read`; `GET /api/deals/{id}` `deals.read`; `POST /api/deals/{id}/lines` `deals.write`.
- `POST /api/deals/{id}/transition` `deals.write` (body: target stage + optional reason); discount over-threshold path returns pending-approval state.
- `POST /api/deals/{id}/approve-discount` `deals.discount.approve`.

**Screens:** deferred — the console shell (001-P5) exists; Sales grids/forms are a follow-on and are
**out of scope for this plan** (see below). Endpoints are API-first and fully testable without UI.

## Out of scope for this plan

- Orders, order creation from a won deal, stock, credit check (003).
- Outbox, integration events, the worker (004); no `DealWon` event is emitted here.
- Generic `Idempotency-Key` ingress table (003).
- Comms timeline entries on accounts/deals (005).
- Cost-price field-level projection (no cost field exists in Sales yet).
- The React console Sales screens (a later frontend plan); 002 is backend + contracts + tests.
- Inbound email threading onto deals/accounts (005/006).

## Portions

### [x] P1 — Sales module foundation + the owed reader-role DI wiring
**Touches:** new `src/Modules/Sales/Aperture.Modules.Sales/{csproj, SalesModule.cs, SalesDbContext.cs, Domain/ITenantOwned.cs, Persistence/SalesNpgsqlOptions.cs, Persistence/Migrations/*_InitialSalesSchema.cs}` (empty `sales` schema + reader grants); new `…Sales.Tests/{csproj, PostgresFixture.cs, ScopeReaderWiringTests.cs, TenantQueryFilterConventionTests.cs}`; `Aperture.slnx`; `src/Aperture.Api/Program.cs` (register `AddSalesModule`, build the reader `NpgsqlDataSource` from a **distinct** reader connection string + secret-sourced `aperture_reader` password, register `ScopedConnection`, subscribe to its `ActivitySource`); `appsettings` reader connection-string key.
**Done when:** the API boots with `ScopedConnection` resolvable from DI over a reader `NpgsqlDataSource`; a reader-role connection that establishes no session context returns **zero** rows against a probe (fail-closed) in a real-PostgreSQL test; the `sales` schema and its `__migrations` history exist; the Sales tenant-filter convention harness is in place. `measure.sh gate` and `rawsql` stay green (reader path is the sanctioned wrapper).
**Tests:** ScopedConnection resolves from the host container; reader connects and is RLS-bound (unset-context → 0 rows, edge 2 mechanism); the convention test scaffold runs (no entities yet — asserts trivially, tightened in P2).
**Risk:** high — cross-cutting DI, a second connection string and secret, and the first host subscription to the scoped-read span. Review the connection-string/secret binding and that the reader role is never the EF owner.

### [x] P2 — Accounts: aggregate, tax-id dedup, write endpoints + scoped grid
**Touches:** `Sales/Domain/Account.cs`, `Persistence/Configurations/SalesConfigurations.cs` (+ Account), a migration adding `sales.accounts` + `ScopeRlsPolicy.Enable`, `Sales` application service, `Aperture.Api/Endpoints/AccountEndpoints.cs`, tests.
**Done when:** create/read/update accounts under `accounts.write`/`accounts.read`; the accounts grid is served through `ScopedConnection` (keyset-paginated); `(tenant_id, tax_id)` dedup holds; `xmin` concurrency on update.
**Tests:** edges 1, 2, 3, 4, 5, 16, 17, 18 (for accounts); tenant isolation and empty-scope deny via **both** EF and RLS; tax-id dedup within/across tenants; keyset pagination under concurrent insert.
**Risk:** medium — first real scoped grid; the differential (EF vs RLS) agreement must hold on Sales rows.

### [ ] P3 — Contacts: one-account rule, departed-not-deleted, scope inheritance
**Touches:** `Sales/Domain/Contact.cs`, config + migration for `sales.contacts` (+ RLS), service, `Aperture.Api/Endpoints/ContactEndpoints.cs`, tests.
**Done when:** a contact belongs to exactly one account; create denormalizes owner/team/region/account from the parent account; `depart` marks the row without deleting; grid under `contacts.read`.
**Tests:** edges 6, 7; departed excluded from active lists but present in history; scope inheritance visible through a `Region`/`Account` grant; tenant isolation.
**Risk:** low.

### [ ] P4 — Deals + deal lines: aggregate, creation, scoped grid (no transitions yet)
**Touches:** `Sales/Domain/Deal.cs`, `Sales/Domain/DealLine.cs`, config + migration for `sales.deals` and `sales.deal_lines` (+ RLS on both), service, `Aperture.Api/Endpoints/DealEndpoints.cs` (create, get, add line, grid), tests.
**Done when:** a deal is created in `new`, owned by one agent, scope columns inherited from its account; lines (price, quantity, price-list version) are added and saved with the deal; grid under `deals.read`, `xmin` on the deal.
**Tests:** create/read/add-line; scope filtering EF + RLS on deals; tenant isolation; account-reassignment re-stamps a deal's scope columns (edge 8, deals half).
**Risk:** medium — the widest table and the aggregate boundary (deal owns lines).

### [ ] P5 — Deal state machine: table-driven transitions + rules 1, 2, 4
**Touches:** `Sales/Domain/DealStateMachine.cs` (the legal-edge table + guards), `Deal` transition methods, `POST /api/deals/{id}/transition`, an `IAuditTrail` row per transition, tests. No new tables.
**Done when:** `new→qualified→quoted→negotiation→won|lost` enforced by one definition; `won` guarded by "≥1 priced line"; `quoted` freezes the price-list version onto the lines; `lost` requires a reason code; illegal/terminal transitions are domain errors (409/422) and audited; concurrent transition 409s.
**Tests:** edges 9, 10, 11, 12, 15; each legal edge; every illegal edge rejected; audit row written with from→to and reason.
**Risk:** medium — the rule guards and the frozen-price semantics.

### [ ] P6 — Discount approval: threshold hold + lead approval
**Touches:** `Deal` pending-approval fields already added in P4/P5 config; the over-threshold guard in the transition path; `POST /api/deals/{id}/approve-discount` under `deals.discount.approve`; audit; tests. (Migration only if a pending-approval column was not added earlier — prefer adding it in P4's `deals` table to avoid a follow-on migration.)
**Done when:** a discount above the agent's threshold keeps the deal in `negotiation` with a recorded pending approval; a user with `deals.discount.approve` clears it (who + why, audited) and the deal may then advance; a user without that permission is denied; an empty scope set still denies the underlying read.
**Tests:** edges 13, 14; over-threshold blocks advance; approval records who/why; permission boundary; this is the portion that turns `deals.discount.approve` from *declared, never enforced* into enforced.
**Risk:** medium — the interaction between the approval sub-state and the state machine.

## Open questions for the user

**Resolved 2026-09-03 — the user approved the plan and accepted every recommendation below:**
(1) deal stages **fixed**; (2) discount threshold is a **tenant-wide setting**; (3) a contact belongs to
**one account**; (4) the **five scope kinds** cover Sales — denormalize all five onto every row;
(5) Idempotency-Key on Sales writes **deferred to 003**; (6) deal→order link is a **pull** (Orders reads
Sales), no `DealWon` push in 002. Build to these answers.

Product calls the code cannot answer. My recommendation on each.

1. **Is the deal stage vocabulary fixed, or tenant-configurable?** DOMAIN.md fixes five stages and
   writes the rules against them by name. **Recommendation: fixed for 002** (Rejected above); revisit
   only if a real tenant needs a different pipeline. Configurable stages add an "unknown stage" bug
   class for unrequested flexibility.

2. **Where does the discount threshold live?** The rule is "above *the agent's* threshold". Is the
   threshold per agent, per role, per team, or a single tenant-wide setting? The code has no such
   field today. **Recommendation: a tenant-wide setting on the tenant (single configurable percent)
   for 002**, with the guard reading it; per-agent thresholds are a later refinement. Confirm the
   granularity you want, because it sizes P6's data model.

3. **Can a contact belong to more than one account?** DOMAIN.md says exactly one. **Recommendation:
   one** (Rejected above). Flagging only because CRMs sometimes want the opposite; if you do, it is a
   design change to DOMAIN.md §2, not a silent schema choice.

4. **Which scope kinds actually apply to Sales rows?** The model supports Self/Team/Region/Account/
   AllTenant. Accounts naturally carry owner (Self), region (Region), the account itself (Account),
   and — via the owner's team — Team. **Recommendation: denormalize all five columns on every Sales
   row** (as designed) so no grid is a special case; a grant kind a tenant never issues simply never
   matches. Confirm there is no Sales-specific scope kind you expect that the five do not cover.

5. **Idempotency-Key on Sales writes now, or defer to 003?** Sales writes are internal/console; the
   tax-id unique index and optimistic tokens cover the realistic double-submit. **Recommendation:
   defer the generic idempotency-key table to 003** (where webhooks/orders need it), and rely on the
   natural keys in 002. Confirm you are comfortable with that boundary.

6. **What does a deal "own" vs. reference toward Orders?** 002 exposes a synchronous contract read of
   a won deal for 003; it emits no event. **Recommendation: confirm the deal→order link is a
   pull (Orders reads Sales) not a push (Sales emits `DealWon`)**, so 002 needs no outbox and stays
   within invariant 6. If you want a push, that reorders 004 before 003.

### Record corrections applied (Rule zero)

Applied directly to `ARCHITECTURE.md` §12 and `STATE.md` (living records I may edit):

- §12 "Scope → SQL predicate for raw SQL / Dapper" was `◐ planned / PR #24 held` — **009 P1–P5 are
  merged** (`master`); corrected to `✅ built` (reader-role RLS path, `ScopedConnection`).
- §12 "Dapper (as a dependency)" was `☐ not present … lands with 009-P4` — Dapper 2.1.66 is pinned in
  `Directory.Packages.props` and referenced by `Aperture.SharedKernel/Data`; corrected to `✅ present`.
- §12 "Sales: accounts, contacts, deals" left `☐ planned — 002` (accurate; this plan).
- `STATE.md` Queue: 002 moved from `not written` to `draft` with a link to this file.

No §1–§11 design section was modified. If you accept open question 6's push variant, that *would* be
a §13 ordering change — flagged, not applied.
