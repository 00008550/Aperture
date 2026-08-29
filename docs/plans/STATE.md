# Plan state

The index for `docs/plans/`. `/ap-cycle` reads this first to work out where the cycle stands.

## Active

| Plan | Title | Status | Next portion |
|---|---|---|---|
| 001 | Tenancy, identity and the authorization spine | approved | P2 |

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
| 001-P1 | Tenant context, data scopes, permission registry | `dotnet test` — see PR body | `docs/plans/pr/001-P1.md` (no remote configured) |
