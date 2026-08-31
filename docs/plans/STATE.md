# Plan state

The index for `docs/plans/`. `/ap-cycle` reads this first to work out where the cycle stands.

## Active

| Plan | Title | Status | In flight | Next portion |
|---|---|---|---|---|
| 001 | Tenancy, identity and the authorization spine | done | — | — |
| 009 | [Raw SQL scope safety: a correct Dapper path, and a gate](009-raw-sql-scope-safety.md) | in-progress | P1 | P2 |

Statuses describe the **plan**, not a portion: `draft` → `approved` → `in-progress` → `done`.
A plan is `in-progress` from the moment its first portion is built until its last portion merges;
it does not move backwards as portions come and go. Portion-level state lives in the checkboxes in
the plan file and in *In flight* above (`—` when no branch is open).

**Only the user moves a plan from `draft` to `approved`.** `ap-builder` sets `in-progress`;
`ap-reviewer` sets `done` when the last portion ships.

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

**009 is sequenced next, before 002** — the number is a file identifier, not a build order. It closes
the raw-SQL half of 001's authorization spine (`WhereInScope` covers `IQueryable` only). Measured
2026-08-30: Dapper is not referenced anywhere and there are zero production raw-SQL call sites, so
this is prevention while it is cheap rather than a retrofit after 002 lands the first grids.

## Shipped

| Portion | Title | Verified | PR |
|---|---|---|---|
| 001-P1 | Tenant context, data scopes, permission registry | build clean (warnings-as-errors), 19 tests passing | merged — body in [`pr/001-P1.md`](pr/001-P1.md) |
| 001-P2 | Access schema, tenant query-filter convention | build clean, 34 tests passing (15 against real PostgreSQL), gate passed | see [`pr/001-P2.md`](pr/001-P2.md) |
| 001-P3 | JWT auth, permission policy provider, `GET /api/me` | build clean, 66 tests passing (32 new, against the real host and PostgreSQL), gate passed; 2 review findings fixed and mutation-checked | [#15](https://github.com/00008550/Aperture/pull/15) — body in [`pr/001-P3.md`](pr/001-P3.md) |
| 001-P4 | Scope → SQL predicate translation | build clean, 84 tests passing (23 against real PostgreSQL, asserting the generated WHERE clause); no review findings | [#17](https://github.com/00008550/Aperture/pull/17) — body in [`pr/001-P4.md`](pr/001-P4.md) |
| 001-P5 | Console sign-in, session, permission-gated navigation | build clean; frontend 11/11; .NET 97 passing (SharedKernel 29, Access 23, Api 45 incl. `ConsoleGatedRouteTests` 13 against real PostgreSQL); 1 review finding (test-harness slug collision) fixed and re-run green | see [`pr/001-P5.md`](pr/001-P5.md) |
| 001-P6 | Audit trail for authorization decisions and mutations | build clean (warnings-as-errors); 107 tests passing (SharedKernel 29, Access 29, Api 49; audit deny/mutation/tenant-isolation/fail-closed against real PostgreSQL and the real host pipeline); `measure.sh endpoints` unchanged (3 routes, 0 unpoliced); no review findings | body in [`pr/001-P6.md`](pr/001-P6.md) |
| 009-P1 | The gate, before there is anything to gate | build clean; SharedKernel 41/41; `measure.sh rawsql` 0 production call sites (exit 0); planted `NpgsqlConnection` + Dapper `PackageReference` both caught then reverted; `measure.sh endpoints` unchanged (3 routes, 0 unpoliced); no review findings | PR_URL_PLACEHOLDER |
