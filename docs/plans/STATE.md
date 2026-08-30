# Plan state

The index for `docs/plans/`. `/ap-cycle` reads this first to work out where the cycle stands.

## Active

| Plan | Title | Status | Next portion |
|---|---|---|---|
| 001 | Tenancy, identity and the authorization spine | in-review | P4 (after 001-P3 merges) |

Statuses: `draft` → `approved` → `in-progress` → `in-review` → `merged`.
**Only the user moves a plan from `draft` to `approved`.**

## Queue

| Plan | Title | Status |
|---|---|---|
| 002 | Sales: accounts, contacts, deals + deal state machine | not written |
| 003 | Orders, fulfilment, stock reservation, idempotency | not written |
| 004 | Outbox, worker, dead-letter handling | not written |
| 005 | Comms timeline + SignalR | not written |
| 006 | Supplier feed connector | not written |
| 007 | AI assistant: tool calling, structured output, RAG | not written |
| 008 | OpenTelemetry, dashboards, load test | not written |

## Shipped

| Portion | Title | Verified | PR |
|---|---|---|---|
| 001-P1 | Tenant context, data scopes, permission registry | build clean (warnings-as-errors), 19 tests passing | merged — body in [`pr/001-P1.md`](pr/001-P1.md) |
| 001-P2 | Access schema, tenant query-filter convention | build clean, 34 tests passing (15 against real PostgreSQL), gate passed | see [`pr/001-P2.md`](pr/001-P2.md) |
| 001-P3 | JWT auth, permission policy provider, `GET /api/me` | build clean, 66 tests passing (32 new, against the real host and PostgreSQL), gate passed; 2 review findings fixed | in review |
