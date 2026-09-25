# 003 — Orders, fulfilment, stock reservation, idempotency

Status: draft            <!-- draft → approved → in-progress → done -->
Roadmap: ARCHITECTURE.md §13 item 003 — "Orders, fulfilment, stock reservation, idempotency. The
contended, transactional core. Needs 002's deals to exist."

Measured: 2026-09-25, at `master` commit `dc43301` (011-P7, plan 011 `done`). Re-survey of the
2026-09-05 draft (measured at `52c0d43`), which predated plans 009's close-out, 010 and 011.

```
scripts/measure.sh endpoints   → 18 mapped routes, 0 without a policy. 13 Sales routes, /api/me,
                                  2 /api/dev/* (AllowAnonymous, Development only), 2 health checks.
                                  No /api/orders* or /api/stock* route exists.
scripts/measure.sh permissions → 17 declared, 5 never enforced (admin.integrations, assistant.use,
                                  audit.read, orders.write, timeline.write). orders.read (5 files),
                                  orders.confirm (2), orders.credit.override (2) read as "enforced" but
                                  every hit is Permissions.cs, Api tests, or DemoSeed.cs (Lara's persona
                                  holds orders.read) — no endpoint gates any orders.* permission.
                                  stock.read / stock.write do not exist.
scripts/measure.sh schema      → 14 tables. sales: accounts(12), contacts(13), deals(16), deal_lines(7).
                                  access: 10 tables incl. audit_events(11) (the tool attributes it to
                                  SalesConfigurations.cs — a measure.sh display quirk, the table is
                                  access-owned). No orders schema, no OrdersDbContext, no Orders migrations.
scripts/measure.sh tests       → 4 test projects, 0 modules without tests. Test METHODS:
                                  Api 104, SharedKernel 61, Access 52, Sales 104 (dotnet test at 011-P7:
                                  414 cases incl. theory rows). 14 frontend spec files (200 tests).
                                  No Orders test project (there is no Orders module).
```

## Ground truth

**What changed since the 2026-09-05 draft, and why it changes this plan:**

- **Errors are contracts now (ARCHITECTURE §5, 011-P1/P2).** `DomainValidationException(field, msg)`
  (`SharedKernel/Domain/DomainValidationException.cs`) thrown by the aggregate is mapped by the one host
  handler `Api/Errors/ApiExceptionHandler.cs` to a 400 `ValidationProblemDetails` with `errors.<field>`;
  unhandled exceptions are an opaque 500 with `traceId`. Malformed cursors are
  `DomainValidationException("cursor")`. **003 follows this, not the old ad-hoc `{ error = … }` bodies:**
  every caller-input 400 (quantity, missing override reason, missing `Idempotency-Key`, malformed cursor)
  is a `DomainValidationException`; 422 = well-formed but the state forbids; 409 = lost race / stale token,
  with current state. The one surviving ad-hoc 400 is `DealEndpoints.cs:206` (approve-discount reason) —
  noted in §12, not 003's to fix.
- **A won deal is now genuinely immutable (011-P3).** `Deal.AddLine` returns 422 on `won`/`lost`, stamps
  or enforces the frozen price-list version, and forces the header modified so every add-line moves the
  deal's `xmin`. So the won-deal snapshot 003 copies cannot drift under it; no deal lock is needed at
  order creation.
- **Raw SQL is gated harder (011-P7).** `scripts/rawsql-rules.txt` is the single pattern for all three
  detectors and it matches `FromSql*`, `ExecuteSql*` and `SqlQuery*`, with exemptions only for
  `.Tests/`, `SharedKernel/Data/`, and `Development/DemoSeed.cs`. **The old draft's "reserve via EF owner
  `FromSql` … `FOR UPDATE`" would now fail the build** (`RawSqlIsScopedTests`). The reservation design is
  revised below to a LINQ `ExecuteUpdateAsync` conditional decrement, which takes the same row lock with
  no raw SQL.
- **Read models carry names (011-P4).** Grids LEFT JOIN `sales.accounts` under RLS to return
  `accountName`. Orders cannot join across schemas (§1), so the order's `accountName` is **copied into the
  snapshot at creation** (display-only, may go stale on rename — acceptable for a label).
- **A dev seed exists (010-P5a).** `Api/Development/DemoSeed.cs` seeds tenant `northwind-demo`, five
  personas (Ada has `Permissions.All` — she automatically gains `stock.*` when added; Lara holds only
  `orders.read`), accounts with credit limits, and deals, idempotently through module services. 003 extends
  it with stock rows and a won deal so the Orders endpoints have data in Development.

**What exists that 003 builds on (read, not assumed):**

- Scope spine: EF tenant filter + `xmin` (`SalesConfigurations.cs:46,123`), `WhereInScope` fail-closed,
  RLS grids via `ScopedConnection` (`DealService.cs`), keyset cursors with 400 on malformed input.
  Services take an explicit `DataScopeSet` parameter (`DealService.cs:58…`) — the convention 003 keeps.
- Audit seam `IAuditTrail.RecordAsync` invoked host-side from the endpoint (not in the module
  transaction — §1). The credit-override who + why uses it.
- Module composition in `Program.cs` (`AddXModule` + `AddScopedReader`), every route `.RequirePermission`.

**What does NOT exist (the gaps 003 fills):**

- `src/Modules/Orders` — no project, schema, DbContext, test project.
- Any in-process cross-module read. `Aperture.Contracts` holds only `Events/IIntegrationEvent.cs` and
  **references nothing** — it cannot yet name `DataScopeSet`. 002's designed-but-unbuilt "won-deal
  contract" is still unbuilt.
- Any idempotency-key table, any `ExecuteUpdateAsync` call, any outbox.

**Records corrected this survey:** ARCHITECTURE §12 Sales and Console rows (the "Known gaps (plan 011)"
are closed; counts re-measured; residual approve-discount `{error}` body noted). Plan 011 → `done`.

## Domain behaviour

DOMAIN.md §2 (Order), §5 (failures as acceptance criteria):

- An order is created **from a won deal, and only from a won deal** (`Deal ──(won)──> Order`); lines are
  copied from the deal.
- Lifecycle `draft → confirmed → reserved → picking → shipped → delivered`, plus `cancelled`
  (from `draft`/`confirmed`/`reserved`) and `returned` (from `delivered`). Terminal: `cancelled` and
  `returned`; `delivered`'s only exit is `returned`. Table-driven like `DealStateMachine`.
- **Confirm checks credit:** account outstanding balance + this order ≤ credit limit, unless finance
  overrides with a recorded who + why (§2, §5.5).
- **Reservation decrements available stock;** two agents on the last unit → exactly one wins, the other is
  told immediately (§2).
- **Partial shipment:** backordered lines stay open, one order not two (§2).
- **Cancel after reservation releases stock** (§5.2).
- **Idempotency** on every state-changing ingress (§5.3/§5.4, invariant 7).

## Design decisions

| Structure | Classification | Reason |
|---|---|---|
| Orders module owning the `orders` schema, on the Sales template | **Essential** | CLAUDE.md §1. |
| `Order` root owning `OrderLine` + `Shipment` (loaded/saved whole) | **Essential** | ARCHITECTURE §5: one command = one transaction = one aggregate. |
| Synchronous `Aperture.Contracts` read: `IWonDealSource` → `WonDealSnapshot`, `IAccountCreditReader`, implemented in Sales | **Essential** | The §1-sanctioned cross-module path; a pull needs no outbox. Contracts gains a ProjectReference to `Aperture.SharedKernel` so the signature can take a `DataScopeSet` (the explicit-parameter convention); no Sales type crosses. |
| Credit limit read **live** at confirm, not snapshotted | **Essential** | It lives in Sales and changes. |
| **Per-account credit serialisation** via `orders.account_credit_locks(tenant_id, account_id)` touched by `ExecuteUpdateAsync` at the start of the confirm transaction | **Essential** (new) | The old draft claimed the order's `xmin` mitigates concurrent confirms. It does not: two confirms of **different** orders for one account touch different rows, so both can pass the SUM check and jointly exceed the limit. Taking a row lock on a per-account row serialises them. Row created in the order-create transaction (unique `(tenant_id, account_id)`; a unique-violation on create means it exists). |
| Outstanding balance = SUM of the account's orders in `confirmed/reserved/picking/shipped` (computed, not stored); `draft`, `delivered`, `cancelled`, `returned` do not count | **Essential** — user decision 2026-09-25 (delivered is treated as settled until payments exist) | No invoicing/payments exist; a stored running balance is the drift bug class of DOMAIN §5.2. |
| `orders.stock_items` placeholder, gated by new `stock.write` / `stock.read` | **Essential now, superseded by 006** | User decision 2026-09-05 (unchanged). Tenant-owned, **not** agent-scoped (fulfilment sees all tenant stock). |
| Reservation as a **conditional atomic decrement** — `ExecuteUpdateAsync(… WHERE tenant_id=@t AND product_ref=@p AND available_qty >= @q)`; 0 rows affected → insufficient | **Essential** (revised) | Same guarantee §5 wants from `SELECT … FOR UPDATE` (the UPDATE takes the row lock; the loser blocks, then re-evaluates the predicate and affects 0 rows — no retry, no livelock) with no raw SQL, so `rawsql-rules.txt` stays unexempted. Multi-line orders decrement in `product_ref` order inside one transaction (deadlock-free lock order); any 0 → rollback. §5 reworded accordingly (user-approved 2026-09-25). |
| Adding a sanctioned raw-SQL locking helper under `SharedKernel/Data/` to keep literal `FOR UPDATE` | **Rejected** | Widens the one exempt directory for a mechanism LINQ already provides. |
| Optimistic `xmin` on `orders` for every transition | **Essential** | Low contention on the order row itself; identical to `Deal`. |
| Per-module `orders.idempotency_keys` (key + effect in one transaction) | **Essential** | User decision 2026-09-05; §5 already says so. Row stores a request fingerprint (method + route + body hash), status code and response body. |
| `Idempotency-Key` **required** on every order command, console and external | **Essential** | User decision 2026-09-05. Missing → `DomainValidationException("Idempotency-Key", …)` → 400 via the one handler, before load. |
| Same key, different request fingerprint → **422** | **Essential** (new) | A reused key with a different body is a client bug; replaying the old response would silently mis-answer it. Matches the IETF Idempotency-Key draft. |
| Errors via `DomainValidationException` / 422 / 409-with-state | **Essential** | ARCHITECTURE §5 "Errors are contracts" (011). |
| At most one live order per won deal — partial unique `(tenant_id, deal_id) WHERE stage <> 'cancelled'`; a second create → 409 with the existing order | **Essential** — user decision 2026-09-25 | Idempotency keys stop a double-click, not two tabs with two keys. The unique index is the race-proof guard; the service maps its violation to 409 + the existing order. A cancelled order frees the deal for a new one. |
| `accountName` copied onto the order at creation | **Essential** | No cross-schema join (§1); label only. |
| Outbox + worker; `DealWon`/`OrderConfirmed` events | **Deferred to 004** | User decision 2026-09-05 (unchanged): no consumer until 005/006. |
| Field-level cost/margin hiding | **Deferred** — trigger: cost price exists (006) | No cost field yet. |
| Backorder as a second order | **Rejected** | DOMAIN §2. |
| Console Orders screens | **Deferred** — a follow-on console plan | Same split as 002 → 010. |

## Failure modes

| Concern | Answer |
|---|---|
| **Tenancy** | All Orders tables carry `tenant_id`; `OrdersDbContext` global filter; RLS on `orders` (tenant + scope) and `stock_items` (tenant only). Contract reads take the caller's `DataScopeSet`. `ExecuteUpdateAsync` goes through the filtered `DbSet`, and the predicate names `tenant_id` explicitly as well. |
| **Authorization** | `orders.read` (grid/get), `orders.write` (create/reserve/cancel/ship), `orders.confirm`, `orders.credit.override` (independent, finance), `stock.write` (PUT stock), `stock.read` (GET stock). 403 before load, and before the 400 idempotency check. Empty scope set → `WhereInScope` `1=0` → non-leaking 404, never "all orders". |
| **Consistency** | One command = one transaction: order aggregate + idempotency row (+ stock rows on reserve/cancel, + credit lock row on confirm). Response = the just-written state. Audit row is written host-side after commit (not atomic — accepted precedent from 002-P5). |
| **Concurrency** | Last unit: conditional decrement, exactly one wins, loser 409 "insufficient stock", order stays `confirmed`. Concurrent confirms on one account: serialised by the credit lock row. Same order, two writers: `xmin` → 409 with current state. Concurrent identical idempotent submits: unique `(tenant_id, key)` — the loser's insert fails, it re-reads and replays the winner's stored response. |
| **Idempotency** | Required header; key + effect atomic; replay returns stored status + body, no write; fingerprint mismatch → 422. Reserve/cancel are also state-idempotent. Key retention: kept indefinitely in 003 (cleanup job is 004's worker). |
| **Ordering** | No events. Transitions serialised by `xmin`; the machine rejects backward edges (`delivered → shipped`). |
| **Failure** | Any failure mid-command rolls back effect + key together, so a retry with the same key executes afresh (no stored failure). Only 2xx/4xx-domain outcomes are stored; a 500 is never stored. |
| **Backward compatibility** | New schema, expand-only. `orders.*` constants exist; `stock.*` are additive (tenants' roles gain them only by grant; the seed's admin gets them via `Permissions.All`). |
| **Observability** | Override audit (who + why, `CorrelationId`). Log fields `orderId`, `stage`, `idempotencyKey`, `replayed=true/false`, and on 409 `reason=insufficient_stock|stale_version`. Full spans/metrics are 008. |

## Edge cases

Given/When/Then — the builder's test list. Error bodies follow §5: 400 = `ValidationProblemDetails`
with `errors.<field>`; 422/409 as stated.

1. **Create from non-won deal** (`negotiation`) → 422, no order.
2. **Create from won deal** with two priced lines → `draft` order, two lines copied, tenant + five scope
   columns + `accountName` inherited.
3. **Unknown / out-of-scope / cross-tenant deal** → non-leaking 404.
4. **Confirm within credit** → `confirmed`.
5. **Confirm over credit, no override** → 422 "exceeds credit limit", stays `draft`, no audit override.
6. **Override with `orders.credit.override` + reason** → `confirmed`, audit row who + why. Missing/blank
   reason → 400 `errors.reason`. Caller without the permission asking to override → 403 before load.
7. **Last unit, two concurrent reservers** (real PostgreSQL) → exactly one `reserved`, other 409
   "insufficient stock", `available_qty` decremented once, `reserved_qty` incremented once.
7a. **Multi-line reserve, second line short** → 409, first line's decrement rolled back, order `confirmed`.
8. **Cancel after reserve** → reserved qty returned to `available_qty`, order `cancelled`. Cancel before
   reserve releases nothing.
9. **Illegal transition** (`draft → shipped`, anything out of `cancelled`/`returned`) → 422, unchanged,
   audited.
10. **Stale `xmin`** → 409 with current order.
11. **Partial shipment** — 5-unit line, ship 3 → line keeps 2 open on the **same** order.
12. **Replay, same key + same body** → stored response (same order id/state), no second write.
    Different key → acts anew (for create: see edge 19).
12a. **No `Idempotency-Key`** on any of the five commands → 400 `errors["Idempotency-Key"]`, before load,
    no console exemption. A caller lacking the permission gets 403, not 400.
12b. **Same key, different body** → 422, no write.
12c. **Two concurrent submits, same key** → one write; both responses identical.
13. **Empty scope set** on any read/command → 404 / empty grid, never all orders.
14. **Concurrent confirms of two different orders on one account**, each within limit alone, together over
    → exactly one `confirmed`, the other 422 "exceeds credit limit".
15. **Malformed cursor** on `GET /api/orders` / `GET /api/stock` → 400 `errors.cursor`.
16. **Non-positive ship quantity / ship more than open** → 400 `errors.quantity` / 422 respectively.
17. **Stock seed**: `PUT /api/stock/{productRef}` with negative quantity → 400; caller with only
    `orders.write` → 403.
19. **Second live order for a won deal** (different key, or concurrent creates) → exactly one order;
    the other gets 409 with the existing order in the body. After that order is `cancelled`, a new create
    succeeds.
20. **Outstanding balance stage set.** Given an account with orders in every stage, When confirming,
    Then only `confirmed/reserved/picking/shipped` totals count; a `delivered` order no longer consumes
    credit (`shipped → delivered` frees its amount), `draft/cancelled/returned` never do.
18. **Delivery webhook replay** (`delivered → shipped`) — webhook ingress is 006; the machine's rejection
    is tested here.

## Target design

**Module:** `src/Modules/Orders/Aperture.Modules.Orders` + `Aperture.Modules.Orders.Tests`, wired into
`Aperture.slnx` and `Program.cs` on the Sales template.

**Schema `orders`:** `orders` (`ITenantOwned` + `IScopedResource`, five scope columns, `xmin`, `stage`,
`deal_id`, `account_id`, `account_name`, `credit_override_by`/`_reason`, timestamps; RLS;
partial unique `(tenant_id, deal_id) WHERE stage <> 'cancelled'`) · `order_lines` (parent-loaded: `product_ref`, `unit_price`,
`quantity`, `quantity_shipped`) · `shipments` (parent-loaded) · `stock_items` (tenant RLS;
unique `(tenant_id, product_ref)`, `available_qty`, `reserved_qty`, checks `>= 0`) ·
`account_credit_locks` (`(tenant_id, account_id)` PK) · `idempotency_keys` (`(tenant_id, key)` unique,
`request_hash`, `status_code`, `response_body jsonb`, `created_at`).

**Contracts:** `IWonDealSource.GetWonDealAsync(DataScopeSet, Guid dealId, ct) → WonDealSnapshot?`
(null for non-won/out-of-scope/unknown — the caller distinguishes 404 vs 422 via a `DealState` enum on a
second method or a result type, builder's choice, tested); `IAccountCreditReader.GetCreditLimitAsync(
DataScopeSet, Guid accountId, ct) → decimal?`.

**Endpoints** (all policied): `POST /api/orders` (`orders.write`), `GET /api/orders`, `GET /api/orders/{id}`
(`orders.read`), `POST /api/orders/{id}/confirm` (`orders.confirm`; body `{ version, override?: { reason } }`
needs `orders.credit.override`), `…/reserve`, `…/cancel`, `…/ship` (`orders.write`),
`PUT /api/stock/{productRef}` (`stock.write`), `GET /api/stock`, `GET /api/stock/{productRef}` (`stock.read`).
Order commands require `Idempotency-Key`. Route count after 003: 18 → 28.

## Out of scope for this plan

Outbox/events/worker/DLQ and idempotency-key cleanup (004); webhooks (006); timeline (005); cost hiding;
console Orders screens; fixing `DealEndpoints.cs:206`'s ad-hoc 400.

## Portions

### [ ] P1 — The won-deal and credit read contracts (Sales side)
**Touches:** `Aperture.Contracts` (+ ProjectReference to SharedKernel; `IWonDealSource`, `WonDealSnapshot`,
`IAccountCreditReader`), Sales `Application` implementations + `SalesModule` registration, Sales tests.
**Done when:** an in-process caller gets a won deal's snapshot (ids, tenant, scope facts, `accountName`,
lines) and an account's live credit limit, both scope-filtered through `WhereInScope`; non-won is
distinguishable from absent; no Sales type in the contract.
**Tests:** won → snapshot; `negotiation` → not-won; out-of-scope/cross-tenant/empty-scope → absent;
credit read scope-filtered; contract-surface test (no `Aperture.Modules.*` type reachable).
**Risk:** low

### [ ] P2 — Orders module foundation + create from a won deal
**Touches:** new Orders project + tests (slnx), `OrdersDbContext`, migration (`orders`, `order_lines`,
`account_credit_locks`, RLS, reader GRANT), `Order`/`OrderLine`, `OrderService` (create/get/grid),
`OrderEndpoints`, `Program.cs`.
**Done when:** create yields a `draft` only from a won deal (422/404 otherwise), lines + scope + name
copied, credit lock row ensured; a second live order for the same deal → 409 with the existing order; scoped keyset grid and get; `orders.read`/`orders.write` enforced;
measure.sh shows the Orders test project and 21 routes.
**Tests:** edges 1, 2, 3, 13, 15 (orders grid), 19 (incl. concurrent creates on real PostgreSQL); EF vs RLS grid parity; 401/403.
**Risk:** high

### [ ] P3 — Order state machine + confirm with credit check, serialised per account
**Touches:** `OrderStateMachine`, `OrderService.ConfirmAsync` (lock row → live limit → SUM → transition),
confirm endpoint + host-side audit, tests.
**Done when:** confirm passes within credit or with an audited override; 422/409/400 per §5; concurrent
confirms on one account cannot jointly exceed the limit.
**Tests:** edges 4, 5, 6, 9, 10, 14 (real PostgreSQL, two contexts), 20 (later stages seeded directly until P6 builds them).
**Risk:** medium

### [ ] P4 — Stock ledger + reservation under contention; cancel releases
**Touches:** `stock_items` migration, `stock.read`/`stock.write` in `Permissions.cs` (+ console
`permissions.ts` mirror), `StockService` + stock endpoints, `ReserveAsync` (conditional
`ExecuteUpdateAsync` in `product_ref` order), `CancelAsync`, `DemoSeed` stock rows + one won deal.
**Done when:** seeded stock is adjustable only with `stock.write`; last-unit contention yields exactly
one reservation; cancel returns reserved qty; `measure.sh rawsql` still 0 production call sites.
**Tests:** edges 7, 7a, 8, 15 (stock grid), 17; reserve no-op on an already-reserved order.
**Risk:** high

### [ ] P5 — Required `Idempotency-Key` on order commands
**Touches:** `idempotency_keys` migration, `IdempotencyStore`, an endpoint filter/wrapper on the five
commands (fingerprint, store-in-transaction, replay), tests.
**Done when:** missing key → 400 before load (after authz); replay returns stored response without a
write; fingerprint mismatch → 422; concurrent duplicates collapse to one write; 500s are not stored.
**Tests:** edges 12, 12a, 12b, 12c on every command.
**Risk:** medium

### [ ] P6 — Fulfilment: partial shipment, backorder, terminal transitions
**Touches:** `shipments` migration, `Shipment`, `ShipAsync`, remaining machine edges
(`picking`/`shipped`/`delivered`/`returned`), ship endpoint, tests.
**Done when:** partial ship keeps backorder on the same order; `delivered → shipped` rejected; ship
releases `reserved_qty` for shipped units.
**Tests:** edges 11, 16, 18, 20 (`shipped → delivered` frees credit end-to-end); fully-shipped → `shipped`; terminal targets-only.
**Risk:** medium

## Open questions for the user

**Resolved 2026-09-05 (kept for the record):** outbox in 004 not 003; per-module
`orders.idempotency_keys`; `orders.stock_items` placeholder gated by `stock.write`/`stock.read`;
`Idempotency-Key` required on all order commands.

**Resolved 2026-09-25 (user, in chat):**

1. **One live order per won deal** — yes; a second attempt is 409 returning the existing order. Folded into
   *Design decisions*, edge 19, P2.
2. **Outstanding balance** = `confirmed/reserved/picking/shipped`; `delivered` stops counting. Folded into
   *Design decisions*, edge 20, P3/P6.
3. **ARCHITECTURE §5 rewording** — approved and applied (conditional decrement via EF `ExecuteUpdate`
   replaces the literal `SELECT … FOR UPDATE`).

No open questions remain.
