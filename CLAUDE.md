# Aperture — working agreement

**Aperture** is a multi-tenant B2B **order & deal desk**: accounts and contacts → deals → orders →
fulfilment, with a unified communication timeline, supplier integrations, and an in-product AI
assistant. One deployable API, modular monolith, PostgreSQL.

Stack: **.NET 10 / ASP.NET Core / EF Core + Dapper / PostgreSQL / SignalR** · **React 19 + TypeScript
+ TanStack Query** · Docker · RabbitMQ.

Repo: `E:\Work\GitProjects\Aperture`. Default branch: `master`.

## Source of truth

| Question | Read |
|---|---|
| How is the system designed? | `docs/ARCHITECTURE.md` (§12 = capability matrix, §13 = roadmap) |
| How does the AI-assisted workflow run? | `docs/AI-WORKFLOW.md` |
| What is the domain, in the business's words? | `docs/DOMAIN.md` |
| What is being built right now? | `docs/plans/STATE.md` → `docs/plans/NNN-*.md` |
| What is actually implemented? | the code — see *Rule zero* below |

Never contradict `ARCHITECTURE.md` silently. If a task requires a design change, propose it and stop.

## Rule zero — measure, never trust a summary

Every document in `docs/` is a **derived artifact**. The migrations, the endpoint list, and the
permission registry are the truth; the docs are hypotheses about them. Before planning in an area,
measure it:

```bash
scripts/measure.sh endpoints      # every mapped route + its authorization policy
scripts/measure.sh permissions    # every permission constant + where it is enforced
scripts/measure.sh schema         # tables and column counts per module schema
scripts/measure.sh tests          # test projects, test counts, and modules with none
```

A `✅` in the capability matrix means nothing until `scripts/measure.sh` agrees with it. Never write
"X is covered" from a document; write it from a file you opened or a command you ran.

## Architecture invariants

1. **Modular monolith.** Modules live in `src/Modules/<Name>`, one deployable API. A module owns a
   **PostgreSQL schema** and reaches another module **only through `Aperture.Contracts`** — never a
   cross-schema query, a project reference into another module's internals, or a shared entity type.
2. **Multi-tenancy is not optional.** Every tenant-owned table carries `tenant_id`. Every query goes
   through the module's `DbContext`, which applies the tenant filter as a global query filter.
   Raw SQL (Dapper) must pass `tenant_id` explicitly — there is no ambient safety net there.
3. **Fail closed.** An unresolved permission, an empty scope set, or a missing tenant context denies.
   Never `if (scopes.Count == 0) return everything;` — that pattern is the single most expensive bug
   class this design exists to prevent.
4. **Every endpoint carries an authorization policy.** No exceptions, not even "temporarily". An
   endpoint with `[AllowAnonymous]` must say why in a comment.
5. **API-first.** Every capability is a REST endpoint. No logic reachable only from the React app.
6. **Writes that cross a boundary go through the outbox.** In-transaction outbox row → dispatcher →
   RabbitMQ. No `IPublishEndpoint.Publish` inside a business transaction.
7. **Idempotency at every ingress.** External callers and webhooks supply an idempotency key; the
   handler is keyed on it and replays return the original result, not a duplicate write.
8. **Real-time is a projection, never a source of truth.** SignalR pushes what the read model already
   holds. A client that reconnects and re-fetches must converge to the same state.
9. **AI is a tool caller, not an oracle.** The assistant reaches the domain through the same REST
   contracts and the same authorization policies as a human. It never gets a privileged data path.

## Layout

```
src/Aperture.Api            API host, composition root, endpoint mapping
src/Aperture.SharedKernel   cross-cutting primitives only — not a dumping ground
src/Aperture.Contracts      cross-module contracts + integration events
src/Modules/Access          tenants, users, roles, permissions, data scopes, audit
src/Modules/Sales           accounts, contacts, deals
src/Modules/Orders          orders, lines, fulfilment state machine
src/Modules/Comms           communication timeline, threads, channels
src/Modules/Assistant       AI: tool calling, RAG, structured output
src/Aperture.Worker         outbox dispatch, jobs, integration consumers
frontend/console            React 19 + TS + Vite ("Console" design system)
```

## Commands

```bash
dotnet build Aperture.slnx
dotnet test  Aperture.slnx
cd frontend/console && npm run build
cd frontend/console && npm run test
docker compose -f deploy/docker-compose.yml up -d db mq
```

**Never report tests as passing for code that has none.** `scripts/measure.sh tests` lists which
modules have no test project. If a portion adds behaviour to one of them, create
`src/Modules/<Name>/Aperture.Modules.<Name>.Tests/`, wire it into `Aperture.slnx`, and say you did.

For UI work, run the console via preview_start (`.claude/launch.json` → `console`, port 5173) and
verify in the browser. Never ask the user to check manually.

## Plans — how work is queued

`docs/plans/` holds numbered plan files `NNN-<slug>.md`, each broken into **portions** small enough
to build, test, and review on their own. `docs/plans/STATE.md` is the index.

Plans are written by `ap-surveyor`, executed one portion at a time by `ap-builder`, and judged by
`ap-reviewer`. A portion's checkbox is ticked only after review passes. Only the **user** moves a
plan from `draft` to `approved`.

## Git

- `master` is the default branch — **never commit directly to it.** Branch as
  `feat/NNN-P<n>-<slug>` or `fix/NNN-P<n>-<slug>`.
- Every portion branch targets `master` directly. **Never stack PRs** — GitHub only auto-retargets
  an open PR when its base is deleted on merge; otherwise the stacked merge lands on the
  intermediate branch and `master` silently never receives the commits.
- Land the plan file before the first portion branch, so portions that cite it can target `master`.
- Commit or push only when the workflow calls for it.
