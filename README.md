# Aperture

[![CI](https://github.com/00008550/Aperture/actions/workflows/ci.yml/badge.svg)](https://github.com/00008550/Aperture/actions/workflows/ci.yml)

A multi-tenant B2B **order & deal desk** — accounts and contacts → deals → orders → fulfilment, with
a unified communication timeline, supplier integrations, and an in-product AI assistant.

**.NET 10 · ASP.NET Core · EF Core + Dapper · PostgreSQL · SignalR · RabbitMQ · React 19 + TypeScript · Docker**

---

## What this repository is for

Two things, and the second is the reason it exists:

1. **A system.** A modular monolith with real multi-tenancy, RBAC + ABAC authorization that fails
   closed, an outbox, idempotent ingress, and an AI assistant that reaches the domain only through
   the same permissions a human has.
2. **A demonstration of how it is built** — with coding agents, in a workflow where one agent
   researches, a different one implements, a third judges, and a human owns every gate.
   → **[`docs/AI-WORKFLOW.md`](docs/AI-WORKFLOW.md)** is the interesting document.

## Read in this order

| | |
|---|---|
| [`docs/AI-WORKFLOW.md`](docs/AI-WORKFLOW.md) | How the repo is built: the three agent roles, the loop, the four rules, where the human is. |
| [`docs/DOMAIN.md`](docs/DOMAIN.md) | The business, its objects, its rules, and the five production failures the design answers. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Design decisions with their tradeoffs. §12 = what is actually built, §13 = roadmap. |
| [`CLAUDE.md`](CLAUDE.md) | The binding invariants every agent reads first. |
| [`docs/plans/`](docs/plans/) | The work queue. Each plan is broken into reviewable portions. |
| [`.claude/agents/`](.claude/agents/) | The three role definitions — read the *boundaries* sections. |

## Design decisions worth arguing about

Each of these is a tradeoff, stated with its cost in `ARCHITECTURE.md`:

- **Modular monolith, not microservices** (§1) — the boundary is enforced by an architecture test,
  so it stays cheap to move while the domain is still moving.
- **Shared schema with `tenant_id`, not schema-per-tenant** (§2) — with the acknowledged sharp edge
  that Dapper does not inherit the global query filter.
- **RBAC and ABAC kept separate** (§3) — permissions answer *may they*, data scopes answer *which
  rows*, and an empty scope set is a distinct type-level state that matches nothing.
- **EF Core for writes, Dapper for reads** (§4) — grids need SQL, and the SQL is written as SQL.
- **Optimistic concurrency by default, `FOR UPDATE` on stock** (§5) — the one genuinely contended row.
- **Outbox before any publish** (§6) — "committed but not published" is a silent data bug.
- **RabbitMQ now, Kafka deferred** (§6) — with the trigger that would change that named.
- **The AI assistant has no privileged data path** (§9) — it calls the API as the signed-in user.

## Running it

```bash
docker compose -f deploy/docker-compose.yml up -d db mq
dotnet build Aperture.slnx
dotnet test  Aperture.slnx
dotnet run --project src/Aperture.Api          # http://localhost:5080
cd frontend/console && npm install && npm run dev   # http://localhost:5173
```

## Measuring it

Documents drift; the code does not. Before planning anything, the workflow measures:

```bash
scripts/measure.sh gate           # the two invariants CI fails the build on
scripts/measure.sh endpoints      # every route and its authorization policy
scripts/measure.sh permissions    # declared permissions vs. enforced ones
scripts/measure.sh schema         # tables and mapped columns per module schema
scripts/measure.sh tests          # test counts, and modules that have none
```

`ARCHITECTURE.md` §12 is corrected against that output on every survey. A ✅ nobody measured is a
bug in the document.

## CI

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs three jobs on every push and PR:

- **Backend** — build in Release with warnings-as-errors, run the tests, then **assert from the
  `.trx` that a non-zero number of tests actually executed**. A run that discovers zero tests must
  not read as success; that was a review finding on the very first portion.
- **Frontend** — `npm ci` and `tsc -b && vite build`, with `strict`, `noUncheckedIndexedAccess` and
  `exactOptionalPropertyTypes` on.
- **Architecture invariants** — `scripts/measure.sh gate` fails the build on the two rules from
  `CLAUDE.md` worth failing a build over: a mapped route with no authorization policy, and a raw
  SQL call with no tenant predicate. The full measurement is published to the run summary, so every
  build records what the code actually contains.

The migration job that 001-P2 needs — apply EF migrations against a real Postgres service container
and assert every tenant-owned entity carries the query filter — is deliberately **not** written yet.
There is no schema to migrate, and a job that tests nothing is the failure this repo is about.
