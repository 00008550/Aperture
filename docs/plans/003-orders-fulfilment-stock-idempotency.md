# 003 — Orders, fulfilment, stock reservation, idempotency

Status: draft            <!-- draft → approved → in-progress → done -->
Roadmap: ARCHITECTURE.md §13 item 003 — "Orders, fulfilment, stock reservation, idempotency. The
contended, transactional core. Needs 002's deals to exist."

Measured: 2026-09-05, at `master` commit `52c0d43` (002-P6, plan 002 `done`).

```
scripts/measure.sh endpoints   → 16 mapped routes, 0 without a policy. No /api/orders* route exists.
scripts/measure.sh permissions → 17 declared, 5 never enforced. orders.read/orders.write/orders.confirm/
                                  orders.credit.override are DECLARED (Permissions.cs:27-35). The measure
                                  reports orders.read/confirm/credit.override as "enforced in N file(s)"
                                  but those Ns are the registry + TEST references only — there is no
                                  Orders endpoint that gates them. Only orders.write reads honestly
                                  ("DECLARED, NEVER ENFORCED"). Treat all four as unenforced until 003.
scripts/measure.sh schema      → 14 tables. Sales schema: accounts(12), contacts(13), deal_lines(7),
                                  deals(16). Access schema: 10 tables incl. audit_events(11). No `orders`
                                  schema, no OrdersDbContext, no Orders migrations folder.
scripts/measure.sh tests       → 4 test projects, 0 modules without tests. No Orders test project.
                                  SharedKernel 55, Access 52, Sales 71, Api 71.
```

## Ground truth

**What exists that 003 builds on (read, not assumed):**

- **The scope spine is complete and proven.** EF write path with the tenant global filter + `xmin`
  (`SalesDbContext`, `Deal.cs`), the RLS reader grid via `ScopedConnection.QueryAsync` +
  `ScopeColumns.For(alias)` (`DealService.cs:23-37,301-312`), fail-closed `WhereInScope` on an empty
  scope set (`DealService.CreateAsync`), keyset pagination with an opaque base64 cursor. 003 reuses
  all of it verbatim.
- **The audit seam** `IAuditTrail.RecordAsync(AuditEntry)` is host-side, invoked from the endpoint
  after the module write settles (`DealEndpoints.cs:144`), because Access owns the audit schema and a
  cross-schema atomic write would breach CLAUDE.md §1. 003's credit-override "who + why" uses the same
  seam.
- **Module composition** is `AddXModule(ownerConnectionString)` + `AddScopedReader(...)` in
  `Program.cs`; endpoints mapped at the bottom; every route carries `.RequirePermission(...)`.
- **The reader-role / RLS convention** is `ScopeRlsPolicy.Enable("<schema>","<table>")` in the
  migration, plus a reader GRANT. `deal_lines` is `ITenantOwned` but **not** `IScopedResource` (no RLS,
  loaded only with its parent). 003's `order_lines`, `shipments` and `stock_items` follow the same
  parent-loaded pattern; `orders` itself is `IScopedResource` with the five denormalised scope columns.

**What does NOT yet exist (the honest gaps 003 must fill):**

- **The Orders module.** `src/Modules/Orders` is absent — no project, no schema, no DbContext, no test
  project in `Aperture.slnx`. It must be created from scratch on the Sales template.
- **The deal→order contract.** `Aperture.Contracts` contains **only** `Events/IIntegrationEvent.cs`.
  The "synchronous Contracts read of a won deal" that 002's design decisions describe (plan 002 lines
  103-104, 195-196, 278-280) **was designed but never built** — there is no `IWonDealSource`, no query
  interface, no cross-module read surface at all. 002 shipped `GET /api/deals/{id}` (an HTTP surface),
  not an in-process contract. 003 must define and implement that contract (P1).
- **No outbox, no worker consumer, no idempotency-key table** anywhere in `src/`.

## Domain behaviour

DOMAIN.md §2 (Order) and §5 (the failures that are the acceptance criteria):

- **Order is created from a won deal, and only from a won deal.** Lines are copied from the deal.
- Lifecycle: `draft → confirmed → reserved → picking → shipped → delivered | cancelled | returned`.
  Terminal: `delivered`, `cancelled`, `returned` (by construction, targets only — like `Deal`'s
  `won`/`lost`).
- **Confirm checks credit:** the account's outstanding balance + this order must not exceed the credit
  limit, *unless finance overrides* — the override is recorded with **who and why** (DOMAIN §2, §5.5:
  "nobody could reconstruct who approved… because approvals were a boolean").
- **Reservation decrements available stock.** Two agents confirming the last unit at the same moment is
  frequent; exactly one wins, the other is told immediately (DOMAIN §2; ARCHITECTURE §5 — pessimistic
  `SELECT … FOR UPDATE`, because optimistic retry here turns a lost update into a livelock).
- **Partial shipment:** backordered lines stay open, the customer sees one order not two (DOMAIN §2).
- **Cancellation after reservation must release the stock** (DOMAIN §5.2 — the leak that lost inventory
  accuracy over a year).
- **Idempotency** (DOMAIN §5.3/§5.4; ARCHITECTURE §5, invariant 7): a state-changing command carrying an
  `Idempotency-Key` writes the key in the same transaction as the effect; a replay returns the stored
  result and performs no second write.

## Design decisions

| Structure | Classification | Reason |
|---|---|---|
| **Orders module owning the `orders` schema**, on the Sales template | **Essential** | CLAUDE.md §1. Orders is a distinct aggregate boundary from Sales; a cross-schema query would breach the invariant. |
| **`Order` aggregate root owning `OrderLine` + `Shipment`** (loaded/saved whole) | **Essential** | ARCHITECTURE §4/§5 — one command = one transaction = one aggregate. Lines and shipments are only meaningful with their order. |
| **Synchronous `Aperture.Contracts` read of a won deal** (`IWonDealSource` → `WonDealSnapshot`), implemented by Sales | **Essential** | The §1-sanctioned cross-module path. Order creation *pulls* the deal; Sales emits no event, so no outbox is needed in 003 (confirms 002's deferral). The DTO carries no Sales entity type. |
| **Synchronous `IAccountCreditReader`** for current credit limit at confirm time | **Essential** | Credit limit lives in Sales and can change; the check must read it live, not snapshot it at creation. |
| **`orders.stock_items` (tenant_id, product_ref → available_qty, reserved_qty)** owned by Orders | **Essential now, revisited at 006** | Reservation needs a lockable row to `FOR UPDATE`. 006's supplier feed later becomes the authoritative writer; for 003 a minimal admin-seeded stock table is the smallest correct home. Seeded/managed via an admin endpoint gated by the new `stock.write` permission (below). |
| **Pessimistic `SELECT … FOR UPDATE` on the stock row** for reservation | **Essential** | ARCHITECTURE §5 names this exact case. Optimistic retry on last-unit contention livelocks under load. Executed through the EF **owner** context (`FromSql`), not the read-only reader path. |
| **Optimistic `xmin` token on `orders`** for every non-reservation transition | **Essential** | ARCHITECTURE §5; identical to `Deal`. Contention on the order row itself is low; only the stock row is genuinely contended. |
| **Generic idempotency-key table, `orders.idempotency_keys`, in the Orders schema** | **Essential** | Invariant 7. It must be written in the **same transaction** as the effect; the effect is an Orders write. ARCHITECTURE §5 now specifies a **per-module `<schema>.idempotency_keys`** table (edit applied 2026-09-05, user approved) precisely so the key and effect commit atomically without the cross-schema transaction that a shared `access.idempotency_keys` would force — the same §1 breach the audit seam already sidesteps. Each module owning its own key table is the invariant-respecting choice; §5 no longer conflicts. |
| **`Idempotency-Key` REQUIRED on every order command** (create/confirm/reserve/cancel/ship), console **and** external — missing header → 400 | **Essential** | Uniform ingress discipline (invariant 7 read strictly). There is no "internal/console caller" exemption to drift out of sync with external callers; one rule at every ingress is cheaper to keep honest than two. User decision 2026-09-05. |
| **`orders.stock_items` placeholder stock table + dedicated `stock.write`/`stock.read` permissions** | **Essential now, superseded by 006** | Reservation needs a lockable row to `FOR UPDATE`. Until 006's supplier feed becomes the authoritative writer, a minimal admin-seeded stock table in the `orders` schema is the smallest correct home. Seeding it is an **admin** concern, not order-writing, so it is gated by a **new `stock.write`** permission (and `stock.read` for its read side) distinct from `orders.write`. User decision 2026-09-05. 006 replaces the seeding path. |
| **Outbox + worker in 003** | **Rejected for 003 / Deferred to 004** | 003 has **no cross-boundary write**: every external dependency (won deal, credit limit) is a synchronous *pull*, and no downstream consumer of order events exists until 005 (timeline) / 006 (integrations). Building the outbox here is infrastructure without a consumer. Keep it in 004, as §13 sequences. Confirmed by the user 2026-09-05: 003 needs no outbox and emits no events; every cross-module dependency is a synchronous pull. |
| **`DealWon` / `OrderConfirmed` integration events** | **Deferred to 004** | Emitting them now forces either an inline `Publish` (invariant 6 breach) or an early outbox. The pull model avoids both. |
| **Field-level hiding of cost/margin from Fulfilment** | **Deferred** — trigger: cost price is introduced (006 pricing) | Order DTOs in 003 carry no cost field to hide yet (DOMAIN §1). When one appears it is a per-permission projection (ARCHITECTURE §3). |
| **Backorder as a second order** | **Rejected** | DOMAIN §2 is explicit: "the customer sees one order, not two." Backordered lines stay open on the same order. |

## Failure modes

| Concern | Answer |
|---|---|
| **Tenancy** | `orders`, `order_lines`, `shipments`, `stock_items` all carry `tenant_id`; `OrdersDbContext` applies the global tenant filter; `orders` and `stock_items` additionally have RLS via `ScopeRlsPolicy`. The won-deal / credit reads pass the caller's `DataScopeSet`, so Orders never sees a Sales row the caller can't. |
| **Authorization** | `orders.read` (grid/get), `orders.write` (create, reserve, cancel, ship), `orders.confirm` (confirm), `orders.credit.override` (override the credit block — independently grantable, DOMAIN §1 finance-only). **Stock management is a separate concern**: `stock.write` gates the admin stock-seeding endpoint and `stock.read` its read side — deliberately distinct from `orders.write` so an order-writer cannot alter the stock ledger. Row scope narrows below the permission via `WhereInScope` / RLS. An **empty scope set → `WhereInScope` yields `1=0` → deny** (not-found), exactly as Sales. |
| **Consistency** | One command = one transaction = the `Order` aggregate (+ its idempotency key + the stock row for reserve). Read-your-writes: the command response is the just-written state read back through EF. The credit balance (sum of the account's confirmed orders) is eventually consistent only across *concurrent* confirms — mitigated by the `xmin` order token and re-checking on conflict. |
| **Concurrency** | Two confirms of the last unit at reserve time: `SELECT … FOR UPDATE` on the `stock_items` row serialises them; the loser sees `available_qty < required` and gets a 409 "insufficient stock" immediately. The order row itself uses `xmin`; a stale transition 409s with current state. |
| **Idempotency** | `Idempotency-Key` header is **required** on create/confirm/reserve/cancel/ship (console and external alike); a missing header is a 400 before any load. First call writes `orders.idempotency_keys(tenant_id, key)` + the effect in one transaction; a replay hits the unique index, returns the stored response, writes nothing. Reserve is *also* naturally idempotent by order state (already-`reserved` → no-op), belt-and-braces. |
| **Ordering** | No cross-service ordering in 003 (no events emitted). Within an order, transitions are serialised by the `xmin` token; the state machine rejects any out-of-order move (e.g. `shipped → confirmed`). |
| **Failure** | Timeout mid-reserve: the transaction rolls back, the `FOR UPDATE` lock releases, no stock is decremented — reservation is all-or-nothing. No worker/DLQ in 003 (no async work); poison-message handling is 004's. |
| **Backward compatibility** | New schema, new tables — expand-only, no destructive step. `orders.*` permission constants already exist (Permissions.cs), so no permission migration. Deploys cleanly beside running 002 code (which never touches `orders`). |
| **Observability** | The credit-override audit row (who + why) via `IAuditTrail`, `CorrelationId` from `Activity.Current`. The scoped-read span already emits tenant + scope-count. "Why is this order stuck?" is answerable from the order's stage + the audit trail; full spans are 008. |

## Edge cases

Given/When/Then — these become the builder's test list verbatim.

1. **Create from non-won deal.** Given a deal in `negotiation`, When POST /api/orders {dealId}, Then
   422 "an order can be created only from a won deal", no order written.
2. **Create from won deal.** Given a `won` deal with two priced lines, When create, Then a `draft`
   order with two lines copied, tenant + five scope columns inherited from the deal's account.
3. **Create from out-of-scope / cross-tenant / unknown deal.** Then non-leaking 404 (the `IWonDealSource`
   read is scope-filtered; empty scope set denies).
4. **Confirm within credit.** Given balance + order ≤ limit, When confirm, Then `confirmed`.
5. **Confirm over credit, no override.** Then blocked (422 "exceeds credit limit"), stays `draft`, no
   override recorded.
6. **Confirm over credit with `orders.credit.override` + reason.** Then `confirmed`, an audit row records
   who + why. Missing reason → 400. Caller lacking the permission → 403 before load.
7. **Reserve last unit, two concurrent confirmers.** Then exactly one reaches `reserved`; the other gets
   409 "insufficient stock" and the order stays `confirmed`. `available_qty` decremented exactly once.
8. **Cancel after reserve.** Then reserved stock is released back to `available_qty`; order `cancelled`.
   Cancel before reserve releases nothing.
9. **Illegal transition** (e.g. `draft → shipped`, or any move out of a terminal state). Then 422, nothing
   changes, the attempt is audited.
10. **Stale `xmin` on a transition.** Then 409 with current state.
11. **Partial shipment.** Given an order with a 5-unit line and 3 in stock, When ship 3, Then that line is
    partially shipped, the remaining 2 stay open (backordered) on the **same** order; order moves to a
    partially-shipped/`shipped` state per the machine, not split into two.
12. **Duplicate command with same `Idempotency-Key`.** Then the second performs no write and returns the
    first response (same order id, same state). A *different* key on the same intent creates/acts anew.
12a. **Order command with no `Idempotency-Key` header** (console or external). Then 400 "Idempotency-Key
    required", rejected before any load — no exemption for internal/console callers.
13. **Empty scope set** on any read or command → deny (not-found / `1=0`), never "all orders".
14. **Delivery webhook replays / out-of-order** (DOMAIN §5.4, `delivered → shipped`) — **out of scope for
    003** (webhooks are 006); the state machine already forbids the backward edge, which is the durable
    half of the defence.

## Target design

**Module:** `src/Modules/Orders/Aperture.Modules.Orders` (+ `…Orders.Tests`), schema `orders`, wired into
`Aperture.slnx` and `Program.cs` on the Sales template (CLAUDE.md §1, ARCHITECTURE §1).

**Schema (`orders`):**
- `orders` — `IScopedResource` + `ITenantOwned`, five denormalised scope columns inherited from the deal's
  account, `xmin` token, `stage`, `deal_id`, `account_id`, `credit_override_by`/`credit_override_reason`
  (nullable), timestamps. RLS via `ScopeRlsPolicy.Enable("orders","orders")`.
- `order_lines` — `ITenantOwned`, **not** `IScopedResource` (loaded with the parent): `product_ref`,
  `unit_price`, `quantity`, `quantity_shipped`.
- `shipments` — `ITenantOwned`, parent-loaded: lines/quantities shipped, `shipped_at`.
- `stock_items` — `ITenantOwned` + `IScopedResource`? **No** — stock is tenant-owned but not agent-scoped
  (fulfilment sees all tenant stock, DOMAIN §1). RLS asserts tenant only. `(tenant_id, product_ref)` unique,
  `available_qty`, `reserved_qty`. **Placeholder stock source** until 006's supplier feed supersedes it:
  seeded/managed through an admin endpoint gated by the new `stock.write` permission.
- `idempotency_keys` — `(tenant_id, key)` unique, stored response payload + status.

**Contracts (`Aperture.Contracts`):**
- `IWonDealSource.GetWonDealAsync(DataScopeSet, Guid dealId, ct) → WonDealSnapshot?` — implemented in Sales,
  returns null for non-won/out-of-scope/unknown. `WonDealSnapshot` record: dealId, accountId, tenant, scope
  facts, lines (productRef, unitPrice, quantity). No Sales entity type crosses.
- `IAccountCreditReader.GetCreditLimitAsync(DataScopeSet, Guid accountId, ct) → decimal?` — Sales side.

**Endpoints (all policied — invariant 4):**
- `POST /api/orders` → `orders.write` (create from won deal)
- `GET /api/orders` → `orders.read` (scoped grid, keyset)
- `GET /api/orders/{id}` → `orders.read`
- `POST /api/orders/{id}/confirm` → `orders.confirm` (credit check; override needs `orders.credit.override`)
- `POST /api/orders/{id}/reserve` → `orders.write` (FOR UPDATE)
- `POST /api/orders/{id}/cancel` → `orders.write` (releases reservation)
- `POST /api/orders/{id}/ship` → `orders.write` (partial shipment)
- `PUT /api/stock/{productRef}` → `stock.write` (admin seed/adjust the placeholder stock ledger; superseded by 006)
- `GET /api/stock` / `GET /api/stock/{productRef}` → `stock.read` (read the stock ledger)

All order commands (`POST /api/orders`, `…/confirm`, `…/reserve`, `…/cancel`, `…/ship`) **require** an
`Idempotency-Key` header; a request without it is rejected 400 before load.

New permission constants `stock.write` and `stock.read` are added to the `Permissions` registry when P4 is
built (they do not exist today — see Ground truth).

**Screens:** deferred (console Orders grid/forms are a follow-on, consistent with 002 deferring Sales
screens to the console-UI plan 010).

## Out of scope for this plan

- Outbox, integration events, the worker, DLQ (004). Orders emits **no** events in 003.
- Delivery / accounting / supplier webhooks and their idempotent inbound handling (006).
- Timeline entries for order events (005).
- Cost-price / margin field hiding (introduced with pricing, 006+).
- Console Orders screens (010).

## Portions

### [ ] P1 — The won-deal read contract (Sales side)
**Touches:** `src/Aperture.Contracts` (new `IWonDealSource`, `WonDealSnapshot`, `IAccountCreditReader`),
`src/Modules/Sales/…/Application` (implementations + registration in `SalesModule`),
`src/Modules/Sales/…Tests`.
**Done when:** an in-process caller can synchronously read a won deal's snapshot (deal id, account id,
tenant, scope facts, lines) and an account's current credit limit, both filtered by the caller's
`DataScopeSet`; a non-won, out-of-scope, cross-tenant, or unknown deal returns null. No Sales entity type
appears in the contract.
**Tests:** won deal returns full snapshot; `negotiation` deal → null; out-of-scope/cross-tenant/empty-scope
→ null; credit-limit read scope-filtered; DTO carries no `Deal`/`Account` type (contract-surface test).
**Risk:** low

### [ ] P2 — Orders module foundation + create order from a won deal
**Touches:** new `Aperture.Modules.Orders` project + `…Orders.Tests` (both wired into `Aperture.slnx`),
`OrdersDbContext` + `orders` schema migration for `orders` and `order_lines` (+ RLS on `orders`, reader
GRANT), `Order`/`OrderLine` domain, `OrderService` (create + get + grid), `OrderEndpoints`, `Program.cs`
registration (`AddOrdersModule`, map endpoints).
**Done when:** `POST /api/orders {dealId}` creates a `draft` order **only** from a won deal (else 422/404),
copies the deal's lines, inherits tenant + five scope columns from the deal's account; `GET /api/orders`
returns a scoped keyset grid and `GET /api/orders/{id}` the single order with lines; `orders.read`/
`orders.write` turn declared→enforced.
**Tests:** edges 1, 2, 3, 13; grid scope-isolation on both the EF read and the RLS reader path; 401/403 at
the wire; module has a test project (measure.sh tests shows it).
**Risk:** high (new module, new schema, first Orders slice)

### [ ] P3 — Order state machine + confirm with credit check and recorded override
**Touches:** `Orders/Domain/OrderStateMachine`, `OrderService.ConfirmAsync` (reads `IAccountCreditReader` +
sums the account's confirmed-order balance from Orders' own rows), `OrderEndpoints` (confirm + override),
`IAuditTrail` call in the endpoint, tests.
**Done when:** `draft → confirmed` passes only when balance + this order ≤ credit limit, or when a caller
with `orders.credit.override` supplies a reason (audited who + why); illegal/terminal transitions → 422 and
are audited; stale `xmin` → 409 with current state.
**Tests:** edges 4, 5, 6, 9, 10; override missing reason → 400; caller without `orders.credit.override` →
403; audit row asserted.
**Risk:** medium

### [ ] P4 — Stock + reservation under contention; cancellation releases stock
**Touches:** `stock_items` table + migration (tenant-RLS, admin-seeded **placeholder** — 006's feed
supersedes it), new `stock.write`/`stock.read` constants added to the `Permissions` registry,
`StockService` + admin stock endpoints (`PUT /api/stock/{productRef}` gated `stock.write`, `GET /api/stock`
+ `GET /api/stock/{productRef}` gated `stock.read`), `OrderService.ReserveAsync` with `SELECT … FOR UPDATE`
on the stock row (EF owner `FromSql`), `CancelAsync` releasing reserved qty, `OrderEndpoints` (reserve,
cancel), `Program.cs` (map stock endpoints), tests incl. a real-contention test.
**Done when:** an admin with `stock.write` can seed/adjust a product's stock via `PUT /api/stock/{productRef}`
(a caller with only `orders.write` is 403); `confirmed → reserved` decrements `available_qty` under a row
lock; two concurrent reservers of the last unit → exactly one `reserved`, the other 409 "insufficient stock",
stock decremented once; `cancel` after reserve returns the reserved qty to `available_qty`; cancel before
reserve releases nothing.
**Tests:** edges 7, 8; `stock.write` gates seeding while `orders.write` alone does not; the FOR UPDATE
contention test against real PostgreSQL; reserve is a no-op on an already-reserved order.
**Risk:** high (the genuinely contended core)

### [ ] P5 — Required idempotency-key ingress on order commands
**Touches:** `orders.idempotency_keys` table + migration, an `IdempotencyStore` in Orders, a wrapper around
create/confirm/reserve/cancel/ship keyed on the `Idempotency-Key` header (key + effect in one transaction),
`OrderEndpoints`, tests.
**Done when:** every order command **requires** the `Idempotency-Key` header (console and external) — a
request without it is 400 before load; a command carrying a key writes the key in the same transaction as
its effect; a replay with the same key returns the stored response and performs no second write; a different
key acts anew.
**Tests:** edges 12, 12a; missing header → 400 on each command (no console exemption); replayed
confirm/reserve do not double-apply; concurrent duplicate submits resolve to one write via the unique index.
**Risk:** medium

### [ ] P6 — Fulfilment: partial shipment, backorder, terminal transitions
**Touches:** `shipments` table + migration, `Shipment` under `Order`, `OrderService.ShipAsync` (partial),
`picking`/`shipped`/`delivered`/`returned` transitions in the state machine, `OrderEndpoints` (ship), tests.
**Done when:** a partial shipment leaves backordered lines open on the **same** order (not a second order);
the order advances through `reserved → picking → shipped` and reaches `delivered`/`returned` per the machine;
backward edges (`delivered → shipped`) are rejected.
**Tests:** edge 11, 14 (the state-machine half); a fully-shipped order reaches `shipped`; terminal states are
targets-only.
**Risk:** medium

## Open questions for the user

All four resolved 2026-09-05 (user approved). Kept here for the decision record.

1. **RESOLVED (2026-09-05, user approved) — Outbox lives in 004, not 003.** 003 needs no outbox and emits
   no events; every cross-module dependency is a synchronous *pull* (won-deal snapshot, credit limit), and
   003 has no event consumer until 005 (timeline) / 006 (integrations). This keeps §13's ordering and 002's
   deferral. Folded into the *Design decisions* table ("Outbox + worker in 003", "`DealWon`/`OrderConfirmed`
   integration events") and *Out of scope*.

2. **RESOLVED (2026-09-05, user approved) — Per-module `orders.idempotency_keys`, and the §5 edit is
   applied.** The key table lives in the writing module's own schema so the key row commits in the SAME
   transaction as the effect it guards; a cross-schema `access.idempotency_keys` would breach §1. The
   ARCHITECTURE §5 wording edit was applied (per-module `<schema>.idempotency_keys`). Folded into the
   *Design decisions* row ("Generic idempotency-key table…") and the *Target design* schema; §5 no longer
   conflicts.

3. **RESOLVED (2026-09-05, user approved) — Minimal admin-seeded placeholder stock table, gated by a new
   `stock.write` permission.** `orders.stock_items` is the placeholder stock source until 006's supplier
   feed replaces it; seeding/adjusting it is an admin concern gated by a dedicated `stock.write` (with
   `stock.read` for the read side), distinct from `orders.write`. 006 supersedes this placeholder. Folded
   into the *Design decisions* table, *Failure modes* (Authorization), *Target design* (schema + endpoints),
   and **P4**.

4. **RESOLVED (2026-09-05, user approved) — `Idempotency-Key` is REQUIRED on all order commands.** Console
   and external order-command ingress must supply the header; a missing key is a 400 before load. Rationale:
   uniform ingress discipline, no "internal caller" exemption to drift. Folded into the *Design decisions*
   table, *Failure modes* (Idempotency), *Edge cases* (12a), *Target design*, and **P5**.
</content>
</invoke>
