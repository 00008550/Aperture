# 001 — Tenancy, identity and the authorization spine

Status: in-review        <!-- draft → approved → in-progress → in-review → merged -->
Roadmap: ARCHITECTURE.md §13 item 001
Measured: `scripts/measure.sh all`, 2026-08-29, on commit `2232426`

## Ground truth

```
== ENDPOINTS (route -> policy) ==
  0 mapped routes, 0 without a policy

== PERMISSIONS (declared -> enforced) ==
  no Permissions.cs found

== SCHEMA (module -> schema -> tables -> mapped columns) ==
  no EF entity configurations found yet
  0 Migrations folder(s)

== TESTS ==
  src/Aperture.SharedKernel.Tests           1 test methods   (template placeholder)
  src/Modules/Access/Aperture.Modules.Access.Tests   1 test methods   (template placeholder)

== MODULES WITHOUT A TEST PROJECT ==
  none

== FRONTEND TESTS ==
  0 spec files under frontend/
```

So the honest position: **nothing of the authorization model exists.** Two test projects exist but
contain only the `dotnet new xunit` placeholder, which is worse than no tests — a green
`dotnet test` currently proves nothing and looks like it proves something. P1 deletes both
placeholders.

**Correction to the measurement tool itself:** `measure.sh endpoints` does not match
`MapHealthChecks`, so the two probe routes in `src/Aperture.Api/Program.cs` are invisible to it.
They are deliberately anonymous and carry no tenant data, but the blind spot is real — any future
`MapX(...)` helper will also be missed. Widening the pattern is queued for 001-P3, when there are
real routes to measure and the change can be verified rather than assumed.

**Correction to `ARCHITECTURE.md` §12:** the matrix claimed *Access module: tenants, users, roles,
scopes* as `◐ partial`. The module contains one empty registration extension. Left as `◐` only
because the project and its boundary exist; the row now names the plan that fills it.

## Domain behaviour

From `DOMAIN.md` §1 and §5.1. Six roles, each seeing a different slice of the same tables, across
tenants that must never see each other. The production incident this plan exists to make impossible:
**a report showed one region's data to another, because "no regions selected" was treated as "all
regions".**

Two separate questions, and conflating them is what produced that bug:

- *May this user perform this action?* — a **permission**. `deals.read`, `orders.confirm`.
- *Which rows?* — a **data scope**. Self, Team, Region, Account, AllTenant.

Scopes **compose as a union**: a lead with `Team(A)` and `Region(North)` sees the union of both, and
a user with `AllTenant` sees everything in their tenant and nothing outside it. A user with **no
scopes sees nothing** — not everything.

## Design decisions

| Structure | Class | Reason |
|---|---|---|
| `TenantId` / `UserId` as strongly-typed ids | **Essential** | The cross-tenant bug class is "the right `Guid` in the wrong parameter". The compiler can eliminate it for the cost of two structs. |
| `ITenantContext` that throws when unset | **Essential** | An ambient default tenant is how background jobs write into the wrong tenant. Throwing turns a silent data corruption into a stack trace. |
| `DataScopeSet` with a distinct empty state | **Essential** | This is the §5.1 incident encoded in the type system: `DataScopeSet.None` is a value that matches nothing, so "no scopes" cannot be mistaken for "unfiltered" by a caller reading `.Count == 0`. |
| Scope union evaluated in memory (`Matches`) | **Essential now** | P1 defines and tests the semantics against plain objects. Semantics first, SQL second. |
| Scope → SQL predicate translation | **Deferred to P4** | It needs a real queryable surface to be verified against. Writing it now would mean testing a translator against no schema. |
| Permission strings + a generated registry | **Essential** | Endpoints, the React console and the AI assistant's tools must agree on one list. One source, three consumers. |
| A `Role` type in `SharedKernel` | **Rejected** | Roles are an administrative grouping owned by the Access module. If `SharedKernel` knows what a role is, every module is coupled to the access model, and business logic starts checking roles instead of permissions. |
| Claims-based permission caching | **Deferred** | Needed when the permission lookup shows up in a trace. The trigger is a measured p95, not a guess. |
| Postgres RLS as a second belt | **Deferred** | Real value, but it needs a per-request session variable and complicates pooling. Trigger: first external-facing tenant, or the first isolation incident. |

## Failure modes

| Concern | Answer for this plan |
|---|---|
| **Tenancy** | `ITenantContext` is resolved from the authenticated principal only — never a header or query parameter. Reading it outside an established scope throws. |
| **Authorization** | Permission gates the verb; `DataScopeSet` narrows the rows. Empty set denies. Unknown permission denies. |
| **Consistency** | P1 is pure in-memory primitives; no transaction boundary yet. P2 introduces the schema and owns that question. |
| **Concurrency** | `TenantScope` uses `AsyncLocal` and restores the previous value on dispose, so nested and concurrent scopes cannot leak into each other — the failure this would otherwise cause is a request answering with another request's tenant under load. |
| **Idempotency** | Not applicable to P1 (no writes). Introduced in P3 with the first command endpoint. |
| **Ordering** | Not applicable. |
| **Failure** | Every deny path is a distinct, testable outcome rather than an exception, so a caller cannot swallow it with a `catch`. |
| **Backward compatibility** | New assembly surface only; nothing to migrate. |
| **Observability** | Deny reasons are enumerable values, so an authorization failure can be logged with *why* rather than a bare 403. Wiring lands with the first endpoint (P3). |

## Edge cases

These are the test names. Each has a test.

1. **Given** a user with no scopes, **when** any row is evaluated, **then** it does not match.
   *(the §5.1 incident)*
2. **Given** a user with `AllTenant`, **when** a row from another tenant is evaluated, **then** it
   does not match. *(AllTenant is not "all data")*
3. **Given** a user with `Self`, **when** a row owned by another user is evaluated, **then** it does
   not match; **and** their own row matches.
4. **Given** a user with `Team(A)` and `Region(North)`, **when** a row in Team B / North is
   evaluated, **then** it matches — union, not intersection.
5. **Given** a user with `Team(A)`, **when** a row whose team is null is evaluated, **then** it does
   not match. *(absent data must not widen)*
6. **Given** two scope sets containing the same scopes in a different order, **then** they are equal.
   *(so caching and comparison are safe)*
7. **Given** no ambient tenant, **when** `ITenantContext.TenantId` is read, **then** it throws.
8. **Given** a nested tenant scope, **when** it is disposed, **then** the outer tenant is restored.
9. **Given** a permission set, **when** an unknown permission string is checked, **then** it denies.
10. **Given** a permission set, **when** a permission differing only in case is checked, **then** it
    denies — permissions are exact, ordinal strings, not a fuzzy match.
11. **Given** the registry, **when** it is enumerated, **then** every declared permission is unique
    and non-empty. *(a duplicated constant silently grants two things one name)*

## Target design

`src/Aperture.SharedKernel` gains:

```
Multitenancy/TenantId.cs          readonly record struct over Guid
Multitenancy/UserId.cs            readonly record struct over Guid
Multitenancy/ITenantContext.cs    ambient tenant, throws when unset
Multitenancy/TenantContext.cs     AsyncLocal implementation + TenantScope
Authorization/Permissions.cs      the registry (ARCHITECTURE.md §3)
Authorization/PermissionSet.cs    ordinal, immutable, fail-closed
Authorization/DataScope.cs        Self | Team | Region | Account | AllTenant
Authorization/DataScopeSet.cs     union semantics, distinct empty state
Authorization/IScopedResource.cs  what a scope evaluates against
```

No endpoints, no schema, no DI registration in this portion — those are P2 and P3, and adding them
here would put untestable code in the diff.

## Out of scope for this plan

Physical devices, SSO/OIDC federation, per-field encryption, Postgres RLS, permission caching, and
the admin UI for managing roles. The AI assistant's tool authorization reuses this spine unchanged —
that is the point of building it first — but lands in plan 007.

## Portions

### [x] P1 — Tenant context, data scopes, permission registry
**Touches:** `src/Aperture.SharedKernel/**`, `src/Aperture.SharedKernel.Tests/**`
**Done when:** the eleven edge cases above have named passing tests; `dotnet build Aperture.slnx` is
clean with warnings-as-errors; both `dotnet new xunit` placeholder tests are deleted.
**Tests:** all eleven, by name.
**Risk:** low — pure primitives, no infrastructure.
**Reviewed:** 2 findings, both fixed (`d512c8d`, `d88fc5f`). PR body: `pr/001-P1.md`.

### [x] P2 — Access module schema: tenants, users, roles, permission grants, scope grants
**Touches:** `src/Modules/Access/**`, EF migration, `deploy/docker-compose.yml`
**Done when:** `dotnet ef database update` produces the `access` schema against the compose
Postgres; every tenant-owned entity has the global query filter; a convention test enumerates entity
types and fails if one lacks it.
**Tests:** the convention test; round-trip persistence; cross-tenant read returns nothing.
**Risk:** medium — the query-filter convention test is the load-bearing part.

### [ ] P3 — JWT authentication, permission policy provider, `GET /api/me`
**Touches:** `src/Aperture.Api/**`, `src/Modules/Access/**`
**Done when:** `/api/me` returns tenant, user, permissions and scopes for a valid token and 401
otherwise; a policy provider resolves `RequirePermission("x")` without a hand-registered policy per
permission; `measure.sh endpoints` shows every route with a policy — and the tool is widened to see
`MapHealthChecks`.
**Tests:** unauthenticated 401; valid token 200; a token naming a tenant the user does not belong to
is rejected; an endpoint mapped without a policy fails an architecture test.
**Risk:** medium.

### [ ] P4 — Scope → SQL predicate translation
**Touches:** `src/Aperture.SharedKernel/Authorization/**`, `src/Modules/Access/**`
**Done when:** a `DataScopeSet` becomes an `Expression<Func<T, bool>>` composed into the query, and
an integration test proves the filter runs **in SQL** (asserted against the generated SQL, not the
result count — a result count passes just as well for an in-memory filter, which is the bug).
**Tests:** each scope kind; the union; the empty set producing a predicate that matches nothing.
**Risk:** high — this is where a fail-open regression would be invisible.

### [ ] P5 — Console: sign-in, session, permission-gated navigation
**Touches:** `frontend/console/**`
**Done when:** the console signs in, renders the session, disables navigation the user lacks
permission for, and shows an explicit empty state for a user with no scopes; verified in the browser.
**Tests:** component tests for the gate; a test that a denied route still 403s at the API.
**Risk:** low.

### [ ] P6 — Audit trail for authorization decisions and mutations
**Touches:** `src/Modules/Access/**`
**Done when:** every deny and every mutation writes an audit row with actor, tenant, permission,
scope decision and correlation id; the AI assistant's calls are marked as such.
**Tests:** deny is audited; a mutation is audited; audit rows are tenant-scoped like everything else.
**Risk:** medium.

## Open questions for the user

1. **`AllTenant` as a grantable scope.** It is convenient for admins and it is exactly the shape that
   caused §5.1. Options: keep it explicit and audited (proposed), or remove it and require an
   enumerated union. This is a product call.
2. **Permission granularity.** `orders.confirm` and `orders.credit.override` are separate here, which
   matches `DOMAIN.md` §2's "finance overrides, recorded with who and why". Confirm that split is the
   business's, not the model's.
3. **Tenant switching.** May one user belong to several tenants and switch, or is a user scoped to
   exactly one? P2's schema shape depends on the answer; assumed **one user, many tenants** until
   told otherwise.
