# 011 — Hardening follow-ups (Sales API errors, deal-line integrity, read-model names, a11y, detector drift)

Status: in-progress         <!-- draft → approved → in-progress → done -->
Roadmap: ARCHITECTURE.md §13 — interstitial hardening between 010 (done) and 003 (draft); no new capability
Measured: 2026-09-24 on `master` @ 231c593 (branch `docs/011-hardening-plan`)

```
endpoints    18 mapped routes, 0 without a policy (13 Sales; /api/dev/* + /health/* AllowAnonymous)
permissions  17 declared, 5 never enforced (admin.integrations, assistant.use, audit.read,
             orders.write, timeline.write — all belong to unbuilt plans 003–007; not a 011 finding)
schema       14 tables; sales: accounts 12, contacts 13, deals 16 (widest), deal_lines 7 columns
tests        Api 80, SharedKernel 55, Access 52, Sales 71 test methods; modules without tests: none;
             14 frontend spec files
rawsql       0 production raw-SQL call sites, 68 exempt — but see F5: the pattern is narrower than GATE 2's
```

## Ground truth

Every follow-up below came from a reviewer (STATE.md *Shipped* rows 010-P5a…P8, 002-P5/P6). Each was
re-measured against the code; verdicts:

| # | Follow-up | Verdict | Evidence |
|---|---|---|---|
| F1 | Domain validation → 500 | **Confirmed, wider than reported** | `Program.cs` has no `UseExceptionHandler`/`AddProblemDetails`/`IExceptionHandler` (grep: zero hits). Guards throwing `ArgumentException`/`ArgumentOutOfRangeException` on caller input: `Account.cs:118,122,125` (name, creditLimit, paymentTermsDays — on create **and** `PATCH`), `Contact.cs:136` (name), `Deal.cs:263,267,271` (name, amount, discountPct), `DealLine.cs:73,77,80` (productRef, unitPrice, quantity ≤ 0). None is caught by `AccountEndpoints`/`ContactEndpoints`/`DealEndpoints`. `ArgumentNullException.ThrowIfNull` guards are programming-error guards (DI, null aggregates) — correctly 500. |
| F2 | Account name in Sales read models | **Confirmed**; fix is sound under RLS | Grids resolve names via `useAccountLookup` → `useAccounts({limit: 50})` (`screens/AccountName.tsx`); `useAccounts` has `retry: false` (`data/useAccounts.ts:41,64`). The reviewer's "dev tenant has 5 accounts" note is right that the 50 cap is not *observed* in dev, but by code any account past the first page renders as a short id. RLS: `ScopeRlsPolicy.Enable` on `sales.accounts`, `sales.contacts`, `sales.deals` (migrations `AddContacts`, `AddDeals`); child scope columns are denormalised from the account and re-stamped in the same unit of work on reassignment (002-P4 edge 8). |
| F3 | `aria-selected` on plain `<tr>` | **Confirmed** | `AccountsGrid.tsx:142`, `ContactsGrid.tsx:164`, `DealsGrid.tsx:159`; tests assert it at `AccountsScreen.test.tsx:209`, `DealsScreen.test.tsx:287`. Styling keys off `data-selected` (`styles.css:502,507,651`), not `aria-selected`, so the fix is attribute-only. |
| F4 | 010 edge 8 "shown only" vs disabled-not-hidden | **Confirmed — corrected in place this run** (see *Record corrections*) | `DealsScreen.tsx:22`: "each disabled — never hidden — without it". |
| F5 | `rawsql` vs GATE 2 pattern drift | **Confirmed, and there is a third detector** | `measure.sh:152` `RAWSQL_PATTERN='Dapper\|NpgsqlConnection\|FromSqlRaw\|FromSqlInterpolated\|ExecuteSqlRaw'`; GATE 2 (`measure.sh:232`) matches `FromSql(Raw\|Interpolated)?\|ExecuteSql(Raw\|Interpolated)?\|\.Query…<\|Dapper`. `RawSqlIsScopedTests.cs:25` — the *build-failing* invariant-2 check — uses the same narrow list as `rawsql`. So `DemoSeed.cs:209,212` (`ExecuteSqlAsync`) is seen by GATE 2 only; an `ExecuteSqlAsync` in production code would pass the build test and `rawsql`. |
| F6 | Add-line doesn't bump the deal's `xmin` | **Confirmed, and a worse sibling found** | `DealService.AddLineAsync` saves only the new `deal_lines` row; `deals.xmin` is unchanged. More serious: **`Deal.AddLine` has no stage guard** — a line can be added to a `won`, `lost` or `quoted` deal. On `quoted`/`negotiation` the new line carries whatever `priceListVersion` the caller sent (or null), not the frozen one, so an outstanding quote *can* change — the exact thing DOMAIN.md §2 rule 2 forbids. And a line added concurrently with the `→quoted` transition lands unfrozen, because the transition's `xmin` check never sees the line insert. The reviewer's "safe because lines cannot be edited/deleted" holds for edit/delete, not for *add*. → needs a portion now. |
| F7a | Malformed cursor → 500 | **Confirmed, still true** | `DecodeCursor` in `AccountService.cs:302`, `ContactService.cs:232`, `DealService.cs:418` throws `ArgumentException`; `Convert.FromBase64String` throws `FormatException` on non-base64 first; `new DateTimeOffset(ticks…)` throws `ArgumentOutOfRangeException` for out-of-range ticks. All three → 500. |
| F7b | "unreproduced duplicate-key warnings" (010 row) | **Not measurable from code** | Not reproduced by the reviewer either. Left out of scope; see *Open questions*. |
| F7c | 010-P5…P8 PR bodies | **Absent** | `docs/plans/pr/` holds only `010-P1…P4.md`. Follow-ups were sourced from STATE.md rows instead. Record gap noted, not a portion. |

**Console already parses RFC 7807.** `frontend/console/src/api.ts:25–44` reads `error`, `detail`, `message`
and the per-field `errors` map — a 400 `ValidationProblemDetails` needs no console change to display.

### Record corrections made this run

1. **`docs/plans/010-reactive-console-experience.md` edge 8** (F4). Was: "the approve control appears only
   for a user with `deals.discount.approve`." Now: "the approve control is enabled only for a user with
   `deals.discount.approve` — for anyone else it is rendered **disabled, not hidden**, with the reason",
   plus a dated italic note explaining the correction. The P7 *Done when* ("approve control shown **only**
   for … holders") was amended the same way. Why: the shipped console (by user/orchestrator decision)
   disables rather than hides; the plan must describe what was accepted.
2. **`docs/ARCHITECTURE.md` §12:**
   - *Scope → SQL predicate for raw SQL / Dapper*: removed the stale "Not yet wired into API DI — first
     consumer is 002"; added the measured three-detector disagreement (F5).
   - *Dapper (as a dependency)*: removed "no production call site exists until 002's grids"; the three Sales
     grids are its call sites.
   - *Sales*: re-measured (Api 80, 18 routes total) and recorded the known gaps (F1, F6, F7a).
   - *React console*: was "◐ partial … **No data-bound screens exist behind the nav yet**" — false since 010
     shipped. Now "✅ built (Sales only)" with the measured facts and the known gaps (F2, F3).

## Domain behaviour

- **Input validity is a domain rule, not a transport rule.** DOMAIN.md does not spell out "name required"
  or "discount 0–100", but the aggregates do, and they are the single definition. A caller that violates
  one has made a *client error*: 400 with which field and why. It is not a server fault (500), and it is
  not a state-machine refusal (422 — reserved for a well-formed request the current state forbids, as the
  transition endpoint already does).
- **Deal lines and the quote (DOMAIN.md §2 rules 1–2).** A deal is *won* only with ≥1 priced line; moving
  to *quoted* freezes the price-list version "so a later price change does not silently alter an
  outstanding quote". Adding a line to a quoted deal with a different (or no) version is such a silent
  change. `won`/`lost` are terminal — a line added after them changes a closed deal's value, which the
  order created from a won deal (003) will read.
- **Precedence for add-line** (recommended; product call confirmed in *Open questions* Q1):
  1. deal not visible in scope → 404 (existing non-leaking deny);
  2. line fields invalid → 400 (F1);
  3. deal is `won` or `lost` → 422 "cannot add a line to a closed deal";
  4. deal has a `FrozenPriceListVersion` and the request names a different non-null version → 422; if the
     request names none, the line takes the frozen version;
  5. `expectedVersion` supplied and stale, or a concurrent writer committed first → 409 with the current deal.

## Design decisions

| Structure | Class | Reason |
|---|---|---|
| `DomainValidationException(field, message)` in `Aperture.SharedKernel` thrown by the aggregates' input guards | **Essential** | One definition of validity (the aggregate) and one type the host can recognise. Keeping `ArgumentException` and mapping *it* to 400 is rejected below. SharedKernel is the right home: it is a cross-cutting primitive every module's domain needs, not a Sales type. |
| One `IExceptionHandler` in `Aperture.Api` mapping `DomainValidationException` → 400 `ValidationProblemDetails` (`errors: { field: [message] }`), plus `AddProblemDetails()` + `UseExceptionHandler()` so every *other* unhandled exception is a 500 ProblemDetails with a `traceId` and **no** exception message, type or stack | **Essential** | Invariants 5 and 9: the API (and the assistant, 007) is the product; today an assistant tool call with discount 101 gets an opaque 500 and cannot self-correct. One handler covers every current and future module endpoint; the endpoint switch statements stay about outcomes. |
| Mapping `ArgumentException` (the BCL type) → 400 | **Rejected** | `ArgumentException` is also thrown by framework and programming errors (e.g. `PermissionPolicyProvider` tests assert one); mapping it wholesale would turn server bugs into "your fault" 400s and could echo internal parameter names. |
| Endpoint-level request validation (FluentValidation / DataAnnotations) duplicating the guards | **Rejected** | Two sources of truth that drift; the assistant and the console would each learn different rules. Revisit only if a rule is genuinely transport-only (none today). |
| Malformed cursor → `DomainValidationException("cursor", …)` inside each `DecodeCursor`, catching `FormatException`/`ArgumentOutOfRangeException` | **Essential** | Same handler, no new mechanism. A shared cursor codec in SharedKernel is **Deferred** until a fourth list endpoint (Orders, 003) — three copies of a 15-line function is below the extraction threshold, and 003 will want the same `(created_at, id)` codec. |
| Add-line stage guard in `Deal.AddLine` returning a domain outcome (not an exception) | **Essential** | Rule 2 and terminal-state integrity. An outcome enum mirrors `Transition`, keeping "state forbids it" (422) distinct from "input invalid" (400). |
| Touch the deal row on add-line so `xmin` moves (force the header `UPDATE`, e.g. mark an existing property modified) + optional `expectedVersion` on `AddDealLineRequest` | **Essential** | The aggregate is deal+lines (§5 "one aggregate"); its concurrency token must cover the lines, or `add-line ∥ →quoted` loses the freeze. Adding a `lines_version`/`updated_at` column is **Rejected**: a migration for what an existing token already provides. |
| `SELECT … FOR UPDATE` on the deal for add-line | **Rejected** | §5: pessimistic only where contention is genuine (stock). Deal edits are low-contention; optimistic 409 is the house pattern. |
| `accountName` on `ContactView`/`DealView` via `LEFT JOIN sales.accounts` in the Dapper grid (under RLS) and a scoped account lookup on the EF detail path | **Essential** | Removes a client-side N-way join that silently degrades past 50 rows and on transient errors. Same schema, same module — no boundary crossed. |
| Server-side name as `INNER JOIN` | **Rejected** | Would *hide the child* whenever the account is not visible. `LEFT JOIN` degrades to `accountName: null` (fail closed on the name, not on the row). |
| A denormalised `account_name` column on contacts/deals | **Rejected** | Needs a re-stamp path on every account rename; the join is cheap (PK lookup) and always current. |
| Full ARIA `role="grid"` with roving tabindex | **Rejected** for now | A grid role obliges arrow-key cell navigation; the rows are "open this record" links, not a spreadsheet. `aria-current="true"` on the selected row is valid on any element and says exactly "this is the one open in the detail". |
| One raw-SQL entry-point pattern shared by `measure.sh rawsql`, GATE 2 and `RawSqlIsScopedTests`, with the `DemoSeed.cs` exact-path exemption stated identically in all three | **Essential** | Three detectors of one invariant that disagree is how a bypass ships green. |

## Failure modes

| Concern | Answer |
|---|---|
| **Tenancy** | Unchanged write paths (EF global filter). The new `accountName` join runs inside `ScopedConnection` as `aperture_reader`; `sales.accounts` has its own RLS policy, so a joined account row from another tenant is invisible to the DBMS itself — the join cannot widen tenancy. |
| **Authorization** | No new routes, no policy change (`endpoints`: 18/0 must hold). `accountName` exposure: a contact/deal is visible iff its denormalised scope columns match a grant; its account carries the *same* `tenant_id/owner/team/region` and `account_id = id` (002-P4 edge 8 re-stamps children in the same transaction), so any grant admitting the child admits the account — the name is learnable exactly when `GET /api/accounts/{id}` would return it. If the two ever diverge, RLS on `accounts` returns no row and `LEFT JOIN` yields `null`: fail closed. **Caveat:** a caller with `contacts.read`/`deals.read` but **without** `accounts.read` would now see names they cannot fetch directly — see Q2. Empty scope set: `ScopedConnection` already refuses (fail closed), unchanged. |
| **Consistency** | One command, one transaction, one aggregate: add-line writes the line and the deal-header touch in one `SaveChanges`. The validation handler runs after the exception unwinds — nothing was saved (guards run before `SaveChanges`). |
| **Concurrency** | Add-line participates in the deal's `xmin`: `add-line ∥ transition`, `add-line ∥ approve-discount`, `add-line ∥ add-line` — the loser gets `DbUpdateConcurrencyException` → 409 with the current deal, as `TransitionAsync` already does. Two concurrent add-lines: one 409s and re-applies; accepted cost of low contention. |
| **Idempotency** | Unchanged and out of scope: add-line has no idempotency key today (invariant 7 applies to *external* ingress, which 003 introduces). The new optional `expectedVersion` makes a double-click's second submit 409 instead of duplicating a line, when the client sends it — the console should (P3). |
| **Ordering** | N/A — no events. |
| **Failure** | Validation failure: 400, no write. Any other exception: 500 ProblemDetails with `traceId`, logged once by the handler at `Error` with the exception; never the message in the body. |
| **Backward compatibility** | No migration in the whole plan. Response shapes: 400 bodies are new (previously 500s had no contract). `accountName` is an **added** nullable field — old clients ignore it. `expectedVersion` on add-line is optional; omitting it keeps current behaviour except that the stage guard and the header touch now apply. Add-line on a `won`/`lost` deal changes from 200 to 422 — a deliberate behaviour change (Q1). |
| **Observability** | Handler logs `validation.field` and route template at `Information` for 400s (not `Error` — client mistakes are not incidents) and full exception at `Error` for 500s; ProblemDetails carries `traceId` = `Activity.Current?.Id ?? TraceIdentifier` (the id the audit rows already use), so "why did this assistant call fail" joins log ↔ response. |

## Edge cases

1. **Blank deal name.** *Given* `POST /api/deals` with `name: "  "`, *then* 400 ProblemDetails, `errors.name` present, no deal row.
2. **Negative amount.** `amount: -1` → 400, `errors.amount`.
3. **Discount bounds.** `discountPct: -0.01` → 400; `100.01` → 400; `0` and `100` → 201 (boundaries inclusive).
4. **Account create/PATCH guards.** blank `name`, `creditLimit: -1`, `paymentTermsDays: -1` → 400 on **both** `POST /api/accounts` and `PATCH /api/accounts/{id}`; the PATCH leaves the row and its `xmin` unchanged.
5. **Contact blank name.** `POST /api/accounts/{id}/contacts` with blank name → 400.
6. **Line guards.** add-line with blank `productRef`, `unitPrice: -1`, `quantity: 0`, `quantity: -3` → 400; `unitPrice: 0`, `quantity: 1` → 200.
7. **Nothing leaks.** For every case above the body contains no stack trace, no exception type name (`ArgumentException`, `DomainValidationException`), no namespace; `Content-Type: application/problem+json`.
8. **Genuine 500 is opaque.** *Given* a handler that throws an unexpected exception (test-only route or fault-injected service), *then* 500 ProblemDetails with `traceId` and a generic title, and the exception message is **not** in the body.
9. **Authorization precedes validation.** A caller without `deals.write` posting an invalid deal gets 403, not 400 (policy runs before the body is bound/validated) — an unauthorised caller learns nothing about the rules.
10. **Scope precedes validation for add-line.** An invalid line on a deal outside the caller's scope → 404, not 400 (the deal is loaded through scope before `AddLine` runs).
11. **Malformed cursors.** `cursor=%%%` (not base64), `cursor=<base64 "abc">` (no separator), `cursor=<base64 "99999999999999999999:guid">` (overflow), `cursor=<base64 "1:not-a-guid">`, `cursor=<base64 "-1:<guid>">` (ticks out of range) on each of `/api/accounts`, `/api/contacts`, `/api/deals` → 400 `errors.cursor`; `cursor=` (empty) → first page as today.
12. **Add-line on terminal deal.** *Given* a `won` deal, *when* a line is added, *then* 422 and the line count is unchanged; same for `lost`.
13. **Add-line after quote keeps the freeze.** *Given* a deal `quoted` at `v1`, *when* a line is added with no version, *then* the line's `priceListVersion` is `v1`; *when* added with `v2`, *then* 422 and no line; *when* added with `v1`, *then* 200.
14. **Add-line bumps the version.** *Given* deal at version N, *when* a line is added, *then* the returned deal's `version` ≠ N and a transition sent with `expectedVersion: N` → 409.
15. **Add-line ∥ quote.** *Given* two contexts loaded at version N, *when* one commits `→quoted` and the other then commits add-line, *then* the add-line 409s with the quoted deal (and no unfrozen line exists).
16. **Stale add-line.** add-line with `expectedVersion` ≠ current → 409 with current deal, no line.
17. **accountName on grids and detail.** Contacts grid, deals grid and `GET /api/deals/{id}` carry the parent's current name; after `PATCH` renames the account, the next read shows the new name.
18. **accountName is not a scope leak.** Differential test: for a caller with scope `S`, every non-null `accountName` returned by the contacts/deals grids belongs to an account id that `GET /api/accounts` (same `S`) also returns. With the account made invisible by direct RLS-context manipulation in the test, the child row still returns (if visible) with `accountName: null`.
19. **Empty scope set.** Grids with an empty `DataScopeSet` still deny (existing behaviour); the join introduces no path around it.
20. **Console, >50 accounts.** *Given* 60 accounts and a deal on the 60th, *then* the deals grid shows its name (not a short id); *given* the accounts read errors, names still render (they come from the deal payload).
21. **Selected row a11y.** The selected row has `aria-current="true"`, no row has `aria-selected`; exactly one row is current; none when nothing is selected. Across Accounts, Contacts, Deals.
22. **Detector parity.** A planted `db.Database.ExecuteSqlAsync($"…")` in a production file fails `RawSqlIsScopedTests`, is listed by `measure.sh rawsql` as a production call site, **and** fails GATE 2; `DemoSeed.cs` is exempt in all three by exact path; any other file under `Development/` is not.

## Target design

- **SharedKernel:** `Aperture.SharedKernel/Domain/DomainValidationException.cs` — `sealed class DomainValidationException(string field, string message) : Exception`, `Field` property. (Placement per §1: primitives only.)
- **Sales domain:** `Account`, `Contact`, `Deal`, `DealLine` private `Require`/`NonNegative`/`Percentage`/`Positive` throw it with a camelCase field name matching the JSON property. `Deal.AddLine` returns a `DealLineAddition` outcome (`Added | DealClosed | PriceListVersionMismatch`) and stamps the frozen version.
- **Sales application:** `DealService.AddLineAsync` handles the outcome, honours optional `ExpectedVersion`, forces the header update, catches `DbUpdateConcurrencyException` → `Conflict`. `DealLineAddStatus` gains `DealClosed`, `PriceListVersionMismatch`, `Conflict`. Each `DecodeCursor` throws `DomainValidationException("cursor", …)`. `ContactView`/`DealView` gain `string? AccountName`; grid SQL `LEFT JOIN sales.accounts a ON a.id = c.account_id` (tenant-equal by RLS; also add `AND a.tenant_id = c.tenant_id` for index use and belt-and-braces); EF paths resolve the name with `_db.Accounts.WhereInScope(scopes)`.
- **Api:** `Aperture.Api/Errors/DomainValidationExceptionHandler.cs` (`IExceptionHandler`), `Program.cs` registers `AddProblemDetails()` + the handler + `UseExceptionHandler()`. `DealEndpoints.AddDealLine` maps the new statuses (422/422/409). No new route; policies unchanged.
- **Console:** `api.ts` types gain `accountName?: string | null`; grids/detail use it, falling back to the short id; `useAccountLookup` survives only as the create-panel `options` source. Grids: `aria-current={selected ? 'true' : undefined}`.
- **Detectors:** `measure.sh` gets one `RAWSQL_PATTERN` used by both `rawsql` and GATE 2, and one `RAWSQL_EXEMPT_FILES` list; `RawSqlIsScopedTests.RawSqlEntryPoint` mirrors the same alternation with a comment pointing at `measure.sh`.

## Out of scope for this plan

- Idempotency keys on Sales writes (003 introduces the ingress mechanism).
- Line edit/delete endpoints; per-line concurrency.
- Changing the existing `{ error: "…" }` bodies of 404/409/422 to ProblemDetails (worth doing once, repo-wide — Q3; not here).
- The unreproduced React duplicate-key warnings (F7b), the five never-enforced permissions (their plans own them), OpenTelemetry (008).
- Any new console screen or visual redesign.

## Portions

### [x] P1 — Domain validation is a 400, and unhandled errors are opaque ProblemDetails
**Touches:** `Aperture.SharedKernel/Domain/DomainValidationException.cs` (new); `Sales/Domain/Account.cs`, `Contact.cs`, `Deal.cs`, `DealLine.cs` (guards throw it); `Aperture.Api/Errors/DomainValidationExceptionHandler.cs` (new); `Aperture.Api/Program.cs`; `Aperture.Api.Tests/DomainValidationEndpointTests.cs` (new); existing Sales domain tests that assert `ArgumentException` updated.
**Done when:** every caller-input guard in the four Sales aggregates throws `DomainValidationException`; every such violation through any Sales route returns 400 `application/problem+json` with `errors.<field>`; any other unhandled exception returns a 500 ProblemDetails with `traceId` and no internals; `ArgumentNullException` programming guards are untouched. `endpoints` still 18/0.
**Tests:** edges 1–9 (every guard × every route that reaches it, including account `PATCH`; boundaries 0/100; 403-before-400); a fault-injected 500 asserts body contains neither the message nor a type name; Sales domain unit tests assert field names.
**Risk:** medium — touches the host pipeline for every route; requires Q4's §5 note.

### [x] P2 — Malformed list cursors are a 400
**Touches:** `Sales/Application/AccountService.cs`, `ContactService.cs`, `DealService.cs` (`DecodeCursor`); `Aperture.Api.Tests` cursor tests (new file or the three existing endpoint test files).
**Done when:** all five malformed-cursor shapes in edge 11 return 400 `errors.cursor` on all three grids; empty cursor unchanged; a valid cursor still pages identically.
**Tests:** edge 11 × 3 endpoints; one regression that a real `nextCursor` round-trips.
**Risk:** low.
**Added 2026-09-25 (orchestrator-approved P1 reviewer follow-up):** `ApiExceptionHandler` keeps the right status (400 validation/cursor, BadHttpRequest passthrough, 500 otherwise) when `Accept` excludes JSON — falls back to writing the same internals-free problem+json instead of rethrowing to an empty 500.

### [ ] P3 — Deal-line integrity: stage guard, frozen version, and the deal's `xmin`
**Touches:** `Sales/Domain/Deal.cs` (`AddLine` outcome + freeze stamping), `Sales/Application/DealService.cs`, `DealModels.cs` (`AddDealLineRequest.ExpectedVersion`, new statuses), `Aperture.Api/Endpoints/DealEndpoints.cs`; `frontend/console/src/data/useDeals.ts` (send `expectedVersion` on add-line, treat 409 like transition's) + its spec; Sales and Api tests.
**Done when:** add-line on `won`/`lost` → 422; on a frozen deal the line takes the frozen version or 422s on a mismatch; every successful add-line changes the deal's `version`; a stale or raced add-line → 409 with the current deal; the console's add-line sends the version it has.
**Tests:** edges 10, 12–16 — including the two-context race (edge 15) against real PostgreSQL, as 002-P5's concurrent-transition test does; console spec for 409 on add-line.
**Risk:** medium — behaviour change on a shipped endpoint (Q1) and an EF force-update trick that must be proven to emit the `xmin` predicate.

### [ ] P4 — `accountName` in the contact and deal read models (API)
**Touches:** `Sales/Application/ContactModels.cs`, `ContactService.cs`, `DealModels.cs`, `DealService.cs` (grid SQL `LEFT JOIN`, EF detail lookup); `Sales.Tests` (RLS differential); `Api.Tests` (payload).
**Done when:** contacts grid, deals grid, deal detail, and the create/transition/add-line responses carry `accountName` (current name, or `null` when the account is not visible); RLS differential proves no name outside the caller's `accounts` scope; `rawsql` still 0 production sites.
**Tests:** edges 17–19; differential across Self/Team/Region/Account grants on **both** the EF and `ScopedConnection` paths; rename-then-read.
**Risk:** medium — a join inside the RLS read path; the differential test is the gate. Blocked on Q2.

### [ ] P5 — Console uses the server's account name
**Touches:** `frontend/console/src/api.ts` (types), `screens/AccountName.tsx`, `screens/contacts/ContactsGrid.tsx` + `ContactsScreen.tsx`, `screens/deals/DealsGrid.tsx` + `DealsScreen.tsx` (+ deal detail), their specs.
**Done when:** grids and deal detail render `accountName` from the row, fall back to the short id only when it is `null`; no grid issues an accounts request just to label rows; the create-contact/deal account picker still works. Browser-verified in light and dark.
**Tests:** edge 20 (60 accounts; accounts read failing); existing grid specs updated.
**Risk:** low.

### [ ] P6 — Selected grid row: `aria-current`, not `aria-selected`
**Touches:** `screens/accounts/AccountsGrid.tsx`, `screens/contacts/ContactsGrid.tsx`, `screens/deals/DealsGrid.tsx`, `AccountsScreen.test.tsx`, `ContactsScreen.test.tsx`, `DealsScreen.test.tsx`.
**Done when:** edge 21 holds on all three grids; `data-selected` styling unchanged; `accesslint`/axe (as 010-P8 used) reports no `aria-allowed-attr` violation on the grids.
**Tests:** edge 21 × 3 screens.
**Risk:** low.

### [ ] P7 — One raw-SQL entry-point pattern across all three detectors
**Touches:** `scripts/measure.sh` (shared `RAWSQL_PATTERN`, shared exempt-file list, used by `rawsql` and GATE 2); `Aperture.SharedKernel.Tests/Architecture/RawSqlIsScopedTests.cs` (same alternation + `DemoSeed.cs` exact-path exemption + a planted-fixture case).
**Done when:** `rawsql` lists `DemoSeed.cs:209,212` as exempt (by exact path) rather than not at all; the architecture test and both `measure.sh` modes agree on every match in `src/`; the builder shows, in the PR body, a planted `ExecuteSqlAsync` going red in all three and green when removed.
**Tests:** edge 22; the architecture test's fixture list gains `ExecuteSqlAsync`, `ExecuteSql`, `FromSql`, `ExecuteSqlInterpolated`, `.Query<` cases.
**Risk:** low — but a widened architecture test may flag something new in `src/`; if it does, that is a finding to report, not to exempt.

## Open questions for the user

**Resolved 2026-09-24 — the user approved the plan and answered every question below:**
(1) add-line after quote is **allowed**: once a price-list version is frozen, a new line must use it
(none → stamped, different → 422); `won`/`lost` reject new lines (422) — P3 builds to this.
(2) `accountName` is **shown** to callers who can read the contact/deal even without `accounts.read`
(it only appears where the account is in the caller's scope) — P4 builds to this.
(3) error-body unification (`{error}` → ProblemDetails for 404/409/422) is **Deferred** — trigger: the
first new module's endpoints (003) or the assistant's tool contracts (007), whichever lands first.
(4) the §5 "Errors are contracts" addition is **approved and applied** to `docs/ARCHITECTURE.md` §5
verbatim as proposed below.
(5) the React duplicate-key warnings get no portion; the builder watches for them during the P5/P6
browser verification and reports any reproduction. Build to these answers.

1. **Add-line after quote (product call, blocks P3).** Recommended: allowed in `new`/`qualified`/`quoted`/`negotiation`; once a version is frozen, the line must use it (none → stamped, different → 422); `won`/`lost` → 422. Alternative: forbid add-line from `quoted` onward entirely (a changed quote must go back through a re-quote). Which is the business rule?
2. **`accountName` for callers without `accounts.read` (blocks P4).** A role with `deals.read` but not `accounts.read` would now see account *names* on deals it can already see (it already sees the `accountId`). Recommended: acceptable — the name is a label on a record the caller may read, like a CRM "account" column — but it is a permission-surface change, so it is yours. Alternative: return `accountName` only when the principal holds `accounts.read` (endpoint masks it).
3. **Error-body shape (not in this plan).** 404/409/422 bodies are `{ error }`; P1 makes 400/500 RFC 7807. Recommend a later one-portion pass converting all to ProblemDetails (the console already reads both). Confirm it is wanted, and when.
4. **Proposed §5 addition (design section — not applied).** P1 establishes a cross-cutting error policy. Proposed diff to `docs/ARCHITECTURE.md` §5, after the *Optimistic concurrency* bullet:
   ```diff
   +- **Errors are contracts.** A request that violates an aggregate's input rule is a `400`
   +  `ValidationProblemDetails` naming the field — raised as `DomainValidationException` by the aggregate,
   +  mapped by one host `IExceptionHandler`, never re-validated at the endpoint. A well-formed request the
   +  current state forbids is a `422`; a lost concurrency race is a `409` with current state. Anything
   +  else is a `500` ProblemDetails carrying a `traceId` and no exception detail. The assistant (§9)
   +  depends on this: it can only self-correct from a 400 that says what was wrong.
   ```
   Approving the plan without this diff leaves P1 implementing a policy §5 does not state; please approve, amend, or reject it.
5. **Duplicate-key warnings (F7b).** Reported during 010 but never reproduced; nothing in code points to it. Drop, or ask the builder to capture it during P5/P6 browser verification?
