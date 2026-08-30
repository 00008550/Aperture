# Aperture — architecture

Design record for the platform described in `DOMAIN.md`. Sections §1–§11 are design and change only
by explicit decision; §12 (capability matrix) and §13 (roadmap) are living records that
`ap-surveyor` corrects against measurement on every survey.

**Status of this document:** it describes the target. What is actually built is §12, and §12 is only
trustworthy to the extent `scripts/measure.sh` agrees with it. See §12's header.

---

## §1 Shape: modular monolith, and why not microservices

One deployable ASP.NET Core process. Modules are compile-time boundaries, not network boundaries.

The team is small and the domain boundaries are not yet proven. Splitting now would buy independent
deployment — which nobody has asked for — at the price of distributed transactions, network failure
modes on every call, and a schema shape that is expensive to change once it is behind an RPC
contract. The monolith keeps the boundary cheap to move while it is still moving.

What makes the boundary real rather than aspirational:

- Each module owns a **PostgreSQL schema** (`access`, `sales`, `orders`, `comms`, `assistant`).
  Its `DbContext` maps only its own schema.
- A module's internal types are `internal`. Its project exposes one public registration extension.
- Cross-module communication is `Aperture.Contracts` only: request/response interfaces for
  synchronous needs, integration events for asynchronous ones.
- The one architectural test that matters (`Aperture.ArchitectureTests`) fails the build on a
  project reference between two modules and on an entity type declared outside its owning module.

**The escape hatch, stated explicitly:** a module may read another's data through a contract call
that hits the other module's own service in-process. That is a function call, not a query. It costs
a method hop today and becomes an HTTP call the day a module moves out — which is exactly the
property the boundary is for.

## §2 Multi-tenancy

**Shared database, shared schema, `tenant_id` on every tenant-owned table.**

Row-level isolation, not schema-per-tenant, because the tenant count is tens and the migration cost
of schema-per-tenant grows linearly with it. The tradeoff accepted: isolation depends on a filter
being correct, so the filter is made structural.

- `ITenantContext` is resolved per request from the authenticated principal. It is **not** taken
  from a header or a query parameter — a client cannot name its own tenant.
- Every module `DbContext` applies `HasQueryFilter(e => e.TenantId == _tenant.TenantId)` to every
  tenant-owned entity. Forgetting it is caught by a convention test that enumerates entity types.
- **Dapper and raw SQL do not inherit the filter.** Every raw statement passes `tenant_id`
  explicitly, and the reviewer checks each one by hand. This is the acknowledged sharp edge of the
  design.
- Background work has no HTTP principal, so a job carries its tenant explicitly and
  `ITenantContext` throws when read outside an established scope. **No ambient default, ever** —
  a default tenant is how cross-tenant writes happen.
- Postgres RLS is a **deferred** second belt: valuable, but it needs a connection-level session
  variable per request and it complicates pooling. The trigger for adopting it is the first
  external-facing tenant, or the first tenant-isolation incident, whichever comes first.

## §3 Authorization: RBAC for verbs, ABAC for rows

Two independent questions, answered separately, because conflating them is what produced incident
§5.1 in `DOMAIN.md`.

**RBAC — *may this user perform this action?***
Permissions are string constants (`deals.read`, `deals.write`, `orders.confirm`,
`orders.credit.override`). A role is a named set of them; a user holds roles per tenant. Endpoints
require a permission by policy. There is no role check anywhere in business logic — only permission
checks, so roles stay a purely administrative concept.

**ABAC — *which rows?***
A user's access is a set of **data scopes**, each a typed predicate:

```
Self                    rows they own
Team(teamId)            rows owned by members of that team
Region(regionId)        rows whose account sits in that region
Account(accountId)      one named account (for a key-account handler)
AllTenant               everything in the tenant — an explicit, auditable grant
```

Scopes **compose as a union**, and the union is materialised into a single SQL predicate rather than
filtered in memory. Filtering after fetch is both a performance bug and a leak waiting for a
`.ToList()` in the wrong place.

**Fail closed, stated as an invariant:** an empty scope set yields a predicate that matches nothing.
The type system helps here — the resolver returns a `DataScopeSet` whose empty case is a distinct
state, not an empty list, so "no scopes" cannot be mistaken for "unfiltered". This is a direct
response to `DOMAIN.md` §5.1.

**Field-level** rules (cost price hidden from fulfilment) are a projection concern: the DTO the
endpoint returns is chosen by permission, rather than nulling fields on one shared DTO. A field that
was never selected cannot leak through a serialization change.

## §4 Data access: EF Core for writes, Dapper for reads

- **EF Core** owns the write model: change tracking, concurrency tokens, migrations. Aggregates are
  loaded and saved whole.
- **Dapper** owns list and report queries. Grids need shaped, paged, joined projections; expressing
  those through the ORM produces either a slow query or an unreadable one. The knowledge that
  matters here is the SQL, and it is written as SQL.
- **Migrations** are EF-generated then hand-reviewed, and follow **expand → backfill → contract**:
  a deploy is never allowed to require code and schema to switch at the same instant. A destructive
  step ships at least one release after the code that stopped using the column.
- Every list endpoint is **keyset-paginated** (`(created_at, id)` after a cursor), not offset —
  offset pagination degrades exactly where the data grows and skips rows under concurrent insert.

## §5 Consistency and concurrency

- One HTTP command = one transaction = one aggregate. Cross-aggregate effects are events.
- **Optimistic concurrency** by default: `xmin` as a concurrency token, a conflict surfaces as
  `409` with the current state so the client can re-apply. Cheap and correct for the contention level
  the domain has.
- **Pessimistic where the domain is genuinely contended**: stock reservation takes
  `SELECT ... FOR UPDATE` on the stock row. Two agents confirming the last unit is a documented,
  frequent event (`DOMAIN.md` §2), and optimistic retry there converts a lost update into a
  livelock under load.
- **Idempotency** is a first-class ingress concern. Every state-changing external entry — API
  command with an `Idempotency-Key` header, webhook, queue consumer — writes to an
  `access.idempotency_keys` table inside the same transaction as the effect. A replay returns the
  stored response and performs no second write. This is the direct answer to `DOMAIN.md` §5.3 and
  §5.4.
- **State machines are explicit.** `Deal` and `Order` transitions live in one table-driven
  definition per aggregate, and an illegal transition is a domain error, not an `if` someone forgot.
  A delivery webhook that would move `delivered → shipped` is rejected by the machine, with the
  attempt logged (§5.4 again).

## §6 Messaging: outbox first

RabbitMQ. Kafka is **deferred** — nothing in the current domain needs replayable, partitioned,
long-retention streams, and running it would be infrastructure without a consumer. The trigger for
adopting it is the first genuine event-replay or analytics-stream requirement.

- **Transactional outbox.** A handler writes state and an outbox row in one transaction. A
  dispatcher polls and publishes. There is no `Publish` inside a business transaction, because
  "committed but not published" and "published but not committed" are both silent data bugs.
- **At-least-once, therefore idempotent consumers.** Every consumer is keyed on the message id, in
  the same table as §5.
- **Retry with backoff, then a dead-letter queue** with the original message, the exception, and the
  attempt count. A poison message must be visible and replayable, not silently dropped.
- Ordering is guaranteed per aggregate only, by routing on the aggregate id. Consumers that need
  more must tolerate reordering explicitly.

## §7 Real-time: SignalR over a read model

A `NotificationsHub` with per-tenant, per-user groups. It pushes **notifications about changes**,
never the authoritative state: "order 8123 changed" plus a small payload the client may use
optimistically.

Every hub method authorises like an endpoint — group membership is derived from the authenticated
principal, never from a client-supplied group name. A reconnecting client re-fetches and converges;
the invariant is that dropping every WebSocket frame degrades freshness and nothing else.

## §8 Integrations

Each external system sits behind a connector in `Aperture.Worker` with the same shape:

- **Anti-corruption layer.** The supplier's model never reaches a domain type.
- **Inbound** is idempotent by external id + payload hash. The supplier feed (`DOMAIN.md` §3) lands
  in a staging table, is validated whole, and is promoted in one transaction — a partial catalogue is
  never visible, which is what makes a mid-import failure survivable.
- **Outbound** is a queued command with retry, a circuit breaker, and an idempotency key the remote
  side honours. The accounting system returning success twice is an expected case, not an incident.
- Every call is recorded with correlation id, duration, and outcome — an integration you cannot
  audit is one you cannot debug at 2am.

## §9 The AI assistant

Implemented as a module (`src/Modules/Assistant`), against an **OpenAI-compatible** chat-completions
API so the provider is configuration (hosted, OpenRouter, or a local model) rather than a
dependency.

- **Tool calling over the domain's own contracts.** Each tool is a thin wrapper over a REST endpoint
  the user could call themselves, executed **as the signed-in user** — same permissions, same
  scopes, same audit. No service account, no privileged read path. This is what makes the feature
  safe to enable for every role.
- **Structured output** for anything that becomes data: a JSON schema per tool result, validated
  before it is used. A model that returns prose where an enum was required is a failed call, not a
  parse-and-hope.
- **RAG over tenant content** — timeline entries, notes, documents — with embeddings stored in
  `pgvector`. Retrieval is filtered by tenant **and** by the caller's data scopes before the vector
  search, not after: a scope filter applied to results is an information leak through ranking.
- **Bounded agency.** The assistant drafts; a human sends. Any tool with a side effect is either
  behind an explicit confirmation or absent. Token budget, tool-call depth, and wall-clock are
  capped per conversation.
- **Evaluated, not vibed.** A fixture set of question → expected tool sequence, run in CI. Prompt
  changes without an eval run are indistinguishable from regressions.

## §10 Observability

OpenTelemetry throughout: traces, metrics, logs, with `tenant_id`, `user_id` and `correlation_id` on
every span. Serilog to stdout in JSON. Health endpoints split `/health/live` (process) from
`/health/ready` (dependencies), because a readiness probe that reports the database is how a rolling
deploy fails safely.

The bar: **"why is this order stuck?" is answerable from telemetry alone**, without attaching a
debugger and without reproducing it.

## §11 Frontend

React 19 + TypeScript + Vite. TanStack Query owns all server state — there is no global store
mirroring the server, because two sources of truth for the same row is the defect that produces
"it's fixed after refresh".

- Generated API client from the OpenAPI document, so a contract change breaks the build rather than
  production.
- **Optimistic updates** on the small, reversible mutations (assign, status change) with rollback on
  error and a toast that names what failed.
- Permission-aware UI: the same permission constants as the backend, generated from one source. The
  UI hides what the user cannot do — **and the server denies it anyway.** The UI check is
  convenience; the server check is security. Neither substitutes for the other.
- SignalR invalidates query keys rather than writing to the cache directly, so live updates and
  refetch converge on one code path.

---

## §12 Capability matrix

**This table is a claim, not evidence.** `ap-surveyor` re-measures it with `scripts/measure.sh` on
every survey and corrects it in place. A `✅` that no measurement supports is a bug in this document.

| Capability | State | Where | Verified by |
|---|---|---|---|
| Solution skeleton, module registration | ✅ built | `src/` | `dotnet build` |
| Tenant context + fail-closed scope primitives | ✅ built | `Aperture.SharedKernel/Multitenancy`, `/Authorization` | 19 tests, `Aperture.SharedKernel.Tests` (001-P1) |
| Permission registry | ✅ built | `Aperture.SharedKernel/Authorization/Permissions.cs` | 19 tests, `Aperture.SharedKernel.Tests` (001-P1) |
| Permission policy provider | ✅ built | `Aperture.Api/Authorization` | 32 tests, `Aperture.Api.Tests` (001-P3) |
| Access module: tenants, users, roles, scopes | ✅ built | `src/Modules/Access` — 9 tables in the `access` schema | 15 tests against real PostgreSQL (001-P2) |
| Authentication (JWT) + `GET /api/me` | ✅ built | `Aperture.Api/Authentication`, `Modules/Access/Authentication` | 32 tests, `Aperture.Api.Tests` (001-P3) |
| Sales: accounts, contacts, deals | ☐ planned | — | 002 |
| Orders + fulfilment state machine | ☐ planned | — | 003 |
| Stock reservation under contention | ☐ planned | — | 003 |
| Idempotency keys at ingress | ☐ planned | — | 003 |
| Transactional outbox + dispatcher | ☐ planned | — | 004 |
| Comms timeline | ☐ planned | — | 005 |
| SignalR notifications | ☐ planned | — | 005 |
| Supplier feed connector | ☐ planned | — | 006 |
| AI assistant: tool calling | ☐ planned | — | 007 |
| RAG over timeline (pgvector) | ☐ planned | — | 007 |
| React console shell + auth | ◐ partial | `frontend/console` | `npm run build` |
| OpenTelemetry wiring | ☐ planned | — | 008 |

## §13 Roadmap

| # | Plan | Why this order |
|---|---|---|
| 001 | Tenancy, identity and the authorization spine | Nothing else can be built correctly on top of a wrong isolation model, and retrofitting scopes into existing queries is the most expensive possible refactor. |
| 002 | Sales: accounts, contacts, deals + the deal state machine | The first real domain slice; proves the scope model against actual rows. |
| 003 | Orders, fulfilment, stock reservation, idempotency | The contended, transactional core. Needs 002's deals to exist. |
| 004 | Outbox, worker, dead-letter handling | Cross-boundary effects become real once orders emit events. |
| 005 | Comms timeline + SignalR | Needs something to be a timeline *of*. |
| 006 | Supplier feed connector | The first unreliable external system, exercising 004's machinery. |
| 007 | AI assistant: tool calling, structured output, RAG | Deliberately last: it calls the endpoints, so the endpoints must exist and be correctly authorised first. Building it earlier would mean auditing it twice. |
| 008 | OpenTelemetry, dashboards, load test | Instrument what exists rather than guessing where the spans belong. |
