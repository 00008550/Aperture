# 010 — Reactive console: a living Sales surface

Status: in-progress      <!-- draft → approved → in-progress → done -->
Roadmap: ARCHITECTURE.md §13 — item 010 (Reactive console: a living Sales surface). The §13 row now exists (user-approved additive edit, 2026-09-05); §11 (Frontend) governs the invariants this plan must honour.
Measured: 2026-09-05 — see *Ground truth* for the exact `scripts/measure.sh endpoints` output and the frontend inventory this plan was written against.

## Ground truth

**Measured `scripts/measure.sh endpoints` (2026-09-05):** 16 mapped routes, 0 without a policy. The
13 Sales routes this experience binds to, with their policies:

```
POST  /api/accounts                       accounts.write
GET   /api/accounts                       accounts.read
GET   /api/accounts/{id}                  accounts.read
PATCH /api/accounts/{id}                  accounts.write
POST  /api/accounts/{accountId}/contacts  contacts.write
GET   /api/contacts                       contacts.read
POST  /api/contacts/{id}/depart           contacts.write
POST  /api/deals                          deals.write
GET   /api/deals                          deals.read
GET   /api/deals/{id}                     deals.read
POST  /api/deals/{id}/lines               deals.write
POST  /api/deals/{id}/transition          deals.write
POST  /api/deals/{id}/approve-discount    deals.discount.approve
GET   /api/me                             (RequireAuthorization)
```

**Frontend inventory (read in full, 2026-09-05).** `frontend/console/` — React 19 + TS + Vite 8 +
TanStack Query 5, `vitest`. Files: `App.tsx`, `Navigation.tsx` (+ test), `SessionPanels.tsx`,
`SignIn.tsx`, `useSession.ts`, `auth.ts`, `api.ts`, `permissions.ts`, `styles.css`, `main.tsx`,
`App.test.tsx`, `test/setup.ts`. `npm run build` is **green** (68 modules, 229 kB JS). What exists
today, precisely:

- **Auth:** a bearer token pasted into `SignIn.tsx`, held in `sessionStorage` via `auth.ts`
  (`useSyncExternalStore`, tab-scoped, fail-closed reads). No token-issuing endpoint — the console
  takes the token it is given.
- **Session:** `useSession.ts` runs `GET /api/me` through TanStack Query, keyed on the token; `can()`
  fails closed (`?? false`). `SessionPanels.tsx` renders tenant / user / permissions / **data scopes**,
  with the empty-scope card stating "nothing is visible to you at all" (DOMAIN.md §5.1 lesson).
- **Navigation:** `Navigation.tsx` gates six section links by permission — **disabled, not hidden**
  (locked affordance + `Requires <permission>` title). Links are in-page anchors (`#accounts` …);
  **there are no actual screens behind them.** No router, no data grids, no create/edit forms.
- **Design system:** `styles.css` — a dark-only token set (`--bg #0f1115`, `--panel`, `--accent
  #4c8dff`), a fixed 232px sidebar + `main` shell, `.card`/`.grid`/`.pill` primitives, system-ui font.
  No animation, no canvas, no motion of any kind.
- **Contracts already typed for us** (server is source of truth): `AccountView`, `ContactView`,
  `DealView` (+ `DealLineView`), and the `…Page { Items, NextCursor }` keyset shape; request records
  `CreateAccountRequest`, `UpdateAccountRequest` (with `ExpectedVersion` xmin), `CreateContactRequest`,
  `CreateDealRequest`, `AddDealLineRequest`, `TransitionDealRequest`, `ApproveDiscountRequest`.
- **Launch:** `.claude/launch.json` → `console` on port 5173 (`npm run dev`), `api` on 5080. Every
  visual portion is browser-verifiable via `preview_start` against these.

**Record correction made by this survey:** ARCHITECTURE.md §12's "React console shell + auth" row was
`◐ partial`; that is still accurate for *today*, so it is left as-is, but its *Verified by* was only
`npm run build`. I have appended the measured file inventory and the fact that **no data-bound screens
exist behind the nav** so the row does not read as "the console is done". See the §12 edit.

## Domain behaviour

This plan adds **no domain rules** — every rule already lives server-side (DOMAIN.md §2 deal state
machine; §5.1 empty-scope-denies) and is enforced by 002's endpoints. The experience must *surface*
that behaviour honestly, never re-implement it:

- **The deal lifecycle** `new → qualified → quoted → negotiation → won|lost` is table-driven on the
  server. The UI offers only the legal next moves it can infer, but **treats the server as the
  authority**: a `422` (illegal edge / rule guard), a `409` (stale `xmin`), and the `200 +
  PendingApproval` discount-hold (rule 3) are all first-class UI states, not errors to swallow.
- **Empty scope denies** (DOMAIN.md §5.1). A grid for a user with no scopes shows the *stated*
  "nothing is visible" surface `SessionPanels` already pioneered — never an ambiguous empty list.
- **Permission gating is convenience, never security** (§11): the UI disables what `can()` refuses;
  the server denies it regardless. Every write control is gated the way `Navigation.tsx` already gates.

## Design decisions

Aesthetic direction (frontend-design skill): **"quiet but alive — maximal."** The user's exact steer
(2026-09-05): *"Quiet but alive, everything should be alive and with life and in maximum
interaction."* Interpret this precisely: a **restrained, tasteful palette and motion — never loud,
grainy, or noisy — yet EVERYTHING on the surface is subtly alive and reactive.** Maximum
interactivity, minimum visual shouting. This is an **instrument panel over a living field**: a
precise, data-first console *floating on* a reactive block-field, where every screen, control, and
data element carries life (micro-interactions, reactive hover/focus states, the block-field
breathing behind) while staying elegant and non-distracting. This is a **cross-cutting UX principle
on all data screens** (P4–P7), not just the backdrop: hover, focus, selection, and state changes are
animated with restraint; nothing is inert, nothing shouts. DFII ≈ 12 (Impact 4, Fit 3, Feasibility 3,
Performance 3, Consistency-risk 1): the risk is letting motion fight legibility — controlled by
keeping the field strictly behind a `z-index` layer, pausing it when a modal/grid has focus, and
keeping every micro-interaction low-amplitude and theme-token-driven.

### Design direction — "Aurora Glass" (locked 2026-09-05)

The user ran a design spike and **chose "Aurora Glass"** over two alternatives ("Bento Ops" and
"Kinetic Paper"). This is the **locked visual direction** — the builder implements exactly this,
not a fresh interpretation of "quiet but alive". The user approved the direction and **explicitly
deferred further polish** ("later we can polish and make better"): the direction is fixed, but
fine-tuning (pane size, bend strength, reflection intensity, idle shimmer) happens *during the
build*, not as a re-decision.

**Canonical visual reference:** <https://claude.ai/code/artifact/7766ad20-8691-420f-a98e-02af93eab177>
— cite and match this artifact. Note: it is a design spike using sample data on a single screen,
**not production code** — match its look and feel, not its structure.

The spec:

- **Feel:** Linear/Attio-calm and premium; hierarchy comes from **depth and light**, not heavy
  borders.
- **Panels:** refined **frosted glass** — a translucent surface with `backdrop-blur`, subtle
  hairline borders, a soft shadow, and a **large radius (~16px)**.
- **Accent:** **teal + indigo.** Light theme `--accent: #28b6a4`, `--accent-2: #6f83f5`; dark theme
  `--accent: #41d6c3`, `--accent-2: #8a9bff`. Neutrals are **cool-slate** (a chosen slate scale, not
  default grey). Semantic good/warn/bad stay **separate from the accent**.
- **Type:** **Bricolage Grotesque** (display) + **IBM Plex Sans** (body) + **IBM Plex Mono**
  (data/numerals), all via Google Fonts, each with a **real fallback stack**.
- **Themes:** both **light and dark are first-class** — this aligns with the theme-system decision
  already recorded below and built in P2; cross-reference it rather than duplicating it.
- **The reactive field (Aurora Glass form):** the field is **not small dots** but **big connected
  glass panes** — a grid of **~46px rounded tiles** separated by **thin seams**, so the whole reads
  as one continuous glass surface. Near the pointer the panes **bulge / lean / refract toward the
  cursor**, catch a **diagonal reflection streak clipped inside each pane**, and **light their edges
  as they wake**, settling calmly at rest; a **click fires an impulse ripple** across the panes.
  Motion **eases in** — quiet at rest, lively near the cursor ("quiet but alive"). Still **Canvas 2D
  + a single rAF loop**, and **reduced-motion and tab-hide safe** exactly as P1 already requires.
  **010-P1 shipped the dot version** (PR #35); upgrading the field to glass panes is an **explicit
  follow-up folded into P2** (see P2 below) and is part of the 010 build, tuned/polished later.

| Structure | Class | Reason |
|---|---|---|
| **Canvas 2D block-field** (`BlockField`) as the reactive background | **Essential** | The brief's core ask (blocks flow in/out on mouse move, click emits a ripple pulse). Canvas 2D over WebGL — see *interaction-tech decision* below. |
| **A single `requestAnimationFrame` loop + pointer state ref** | **Essential** | One loop, no per-block React state; blocks are plain objects mutated in place. React re-rendering hundreds of blocks would be the performance bug this design exists to avoid. |
| **`prefers-reduced-motion` static fallback** | **Essential** | Not optional (brief + §11 accessibility). A still, subtly-gradiented block grid with no rAF loop, reading its colours from the theme tokens. Cross-cutting Done-when on every visual portion, plus a dedicated hardening portion (P8). |
| **`react-router` as the client router** | **Essential** | Six nav sections need real routes behind them; today they are dead anchors. **User decision (2026-09-05): use `react-router`, not a hand-rolled hash router** — the mature, maintained library gives real route guards and well-understood auth/permission-gating patterns. `react-router` provides the *routing*; **authorization still flows through the existing fail-closed `can()` / `Navigation.tsx` pattern** — the router never becomes an auth authority. Added in P4. |
| **A user-selectable light + dark theme system** | **Essential** | **User decision (2026-09-05): both light and dark are first-class, with a user-facing, persisted toggle** — not dark-only. Today `styles.css` is a dark-only token set; the plan restructures tokens so both themes are first-class, the block-field reads its colours from the theme tokens, and "correct in both themes" is a Done-when on P1 and every visual portion. Built in P2. |
| **Typed API client + TanStack Query hooks** per aggregate | **Essential** | §11: TanStack Query owns all server state; no second store. Keyset-cursor pagination is built into the hooks. |
| **Optimistic updates** on reversible writes (depart, transition) | **Deferred** | §11 wants them, but only where reversible and where rollback UX is clear. Trigger: after the read-grids ship (P4–P6) and we can see which mutations are genuinely reversible. Writes land server-authoritative first (refetch-on-success), optimistic layered in P7 only for the low-risk ones. |
| **WebGL / shader backdrop** | **Rejected** *(for now)* | Costs: a shader-compile failure path, GPU-tier variance, harder `jsdom` testability, and an accessibility fallback we'd write anyway. The block count this design needs (a few hundred) runs at 60fps on Canvas 2D. Revisit only if the field grows to tens of thousands of elements. |
| **A global state store (Redux/Zustand) mirroring the server** | **Rejected** | §11 names two sources of truth for one row as *the* defect to avoid. TanStack Query is the only server-state home; `auth.ts` holds the one client fact (the token). |
| **Generated OpenAPI client** | **Deferred** | §11's end-state, but no OpenAPI document is published yet (`permissions.ts` says so). Hand-typed contracts mirror the server for now; trigger is an `/openapi.json` portion in a later plan. |

### Interaction-tech decision (the load-bearing call)

**Canvas 2D, single rAF loop, pointer-driven.** Tradeoffs weighed:

- **Canvas 2D (chosen).** Pros: universal support, no shader-compile failure mode, cheap to gate
  behind `prefers-reduced-motion`, straightforward to unit-test the *pure* pieces (grid geometry,
  pulse-decay math, reduced-motion branch) in `jsdom` without a real GL context, ~300–800 blocks at
  60fps with per-frame fill only. Cons: CPU-bound; thousands of blocks would drop frames. Mitigation:
  cap block count by viewport area, `devicePixelRatio`-aware sizing, pause the loop when the tab is
  hidden (`visibilitychange`) and when a data view is interactive.
- **WebGL/shader (rejected above).** Better ceiling, worse floor: adds a failure path and testability
  cost for a scale we do not need.
- **DOM/CSS transforms (rejected).** Hundreds of animated DOM nodes thrash layout/compositing far
  worse than one canvas; also hard to do a click-pulse ripple cleanly.

## Failure modes

This plan ships **no new server writes** — the transactional concerns are 002's and already answered.
The rows below are answered from the **frontend's** responsibility: it must not *create* a failure the
server prevents, and must *surface* the ones the server reports.

| Concern | Answer for this plan |
|---|---|
| **Tenancy** | The client never sends `tenant_id`; the server stamps it from the principal (002). The client cannot widen tenancy. `GET /api/me` is keyed on the token, so a re-sign-in as another user cannot read the prior user's cache (`useSession` already does this — grids reuse the same keying). |
| **Authorization** | Every screen is gated by `can(permission)` exactly as `Navigation.tsx` does. Write controls are disabled without the permission; the server denies regardless. **An empty scope set** renders the stated "nothing is visible" surface, never a blank grid read as "no data". |
| **Consistency** | Read-your-writes: every successful mutation invalidates its query key so the grid/detail refetches from the server (the authority). No optimistic cache write survives without a server confirmation except the reversible P7 cases, which roll back on error. |
| **Concurrency** | Two users editing one account/deal: the client round-trips the `xmin` (`ExpectedVersion`) it read; on `409` it shows a "changed by someone else — reload" state and refetches, never clobbers. This is the *whole reason* the views carry `Version`. |
| **Idempotency** | A double-clicked submit must not fire two writes. Mutation buttons disable while `isPending`; the retried request either 409s (xmin moved) or is a benign refetch. No client-generated idempotency key is needed for these synchronous writes (webhooks/retries are 003/004's concern). |
| **Ordering** | N/A at the client — there is no client event stream in this plan. SignalR live-invalidation is 005; grids are fetch/refetch only here. |
| **Failure** | Network/5xx: TanStack Query surfaces error state with a retry affordance; `401/403` drops the token to sign-in (as `useSession` already does). The block-field never blocks data: if canvas init throws, the field silently degrades to the static fallback and the app renders. |
| **Backward compatibility** | Additive only — new routes/screens layered on the existing shell. `SignIn`/`useSession`/`auth.ts` are reused unchanged. No contract or server change. The token set is restructured into light + dark themes with dark as the default fallback, so a partial rollout still renders (any un-migrated surface falls back to the dark defaults). |
| **Observability** | Dev-facing: each query/mutation carries a stable, greppable query key (`['accounts', …]`); the block-field exposes a `data-field-state` attribute (`animating`/`reduced`/`degraded`) so a browser check (and a test) can assert which mode it is in without reading pixels. |

## Edge cases

Given/When/Then — these become the builder's test list verbatim.

1. **Reduced motion.** *Given* `prefers-reduced-motion: reduce`, *when* the shell mounts, *then* no
   rAF loop runs, the field renders a single static frame, and `data-field-state="reduced"`.
2. **Canvas unsupported / init throws.** *Given* `getContext('2d')` returns null, *when* the shell
   mounts, *then* the app still renders all content and `data-field-state="degraded"`.
3. **Click pulse.** *Given* motion is on, *when* the user clicks the field, *then* a pulse originates
   at the pointer and decays to zero within a bounded time (assert the decay function, not pixels).
4. **Tab hidden.** *Given* the field is animating, *when* `document.hidden` becomes true, *then* the
   loop pauses (no rAF scheduled) and resumes on visibility.
5. **Empty scope grid.** *Given* a session with zero scopes, *when* any Sales grid loads, *then* it
   shows the stated "nothing is visible" surface, not an empty table.
6. **Permission-denied section.** *Given* a user without `deals.read`, *when* they reach `/deals`,
   *then* the screen shows the locked affordance and issues **no** `GET /api/deals`.
7. **Stale edit (409).** *Given* account detail loaded at version N, *when* PATCH returns 409, *then*
   the UI shows "changed by someone else", refetches, and does not resubmit silently.
8. **Discount hold (rule 3).** *Given* a `won` transition returns `200` with `PendingApproval` true,
   *when* rendered, *then* the deal shows a "pending approval" state (not success, not error), and the
   approve control appears only for a user with `deals.discount.approve`.
9. **Illegal transition (422).** *Given* an illegal edge, *when* the server 422s, *then* the offered
   move is shown rejected with the server's message; the UI never pre-declares an illegal move legal.
10. **Double-submit.** *Given* a create form, *when* the button is clicked twice fast, *then* only one
    request is in flight (button disabled on `isPending`).
11. **Cursor pagination.** *Given* a grid with a `NextCursor`, *when* "load more" is used, *then* the
    next page appends and the cursor advances; a null cursor hides the control.
12. **Depart is not delete.** *Given* a contact, *when* departed, *then* it leaves the active list but
    remains under `includeDeparted`, marked departed (never gone).

## Target design

**Module:** `frontend/console` only. No `src/` change (invariant 5 already satisfied — every
capability is an existing REST endpoint). **Design system:** restructure `styles.css` tokens into a
small layered system (surface / field / glass-panel `z-index` tiers) with **first-class light and dark
themes** — a user-facing, persisted theme toggle (defaulting from `prefers-color-scheme`, overridable
and remembered), plus a reduced-motion aware layer. Every colour (including the block-field's) reads
from theme tokens so both themes and the reduced-motion fallback are correct.

**New components (indicative):**
- `field/BlockField.tsx` + `field/blockField.ts` (pure geometry/pulse math) — the reactive canvas.
- `app/Shell.tsx` — layers `BlockField` behind a routed content region; reuses the existing sidebar.
- `app/router.tsx` — routes the six sections to real screens, each permission-gated.
- `data/` — `useAccounts`, `useContacts`, `useDeals` (+ detail + mutations) TanStack Query hooks over
  an extended `api.ts`; typed to the 002 contracts.
- `screens/Accounts`, `screens/Contacts`, `screens/Deals` — grids, detail, create/edit, lifecycle.

**Endpoints consumed & their policies:** exactly the 13 Sales routes above — read on `*.read`, write
on `*.write`, discount approval on `deals.discount.approve`. No new endpoint. **Screens:** Accounts,
Contacts, Deals (grid + detail + lifecycle), all floating on the block-field, all gated. Cites §11
(TanStack Query owns server state; permission-aware UI; no logic reachable only from React).

## Out of scope for this plan

- Any `src/` or contract change; any new endpoint (a `POST /api/auth/token` remains a server portion).
- SignalR / live invalidation (005). Grids are fetch/refetch only.
- The AI assistant surface (007).
- A generated OpenAPI client (deferred; hand-typed contracts for now).
- Orders screens (003 owns Orders; do not bind Orders/Timeline/Administration nav here beyond the
  existing locked placeholders).
- WebGL/shader backdrop.

## Portions

### [x] P1 — The living field (self-contained interactive shell)
**Touches:** `frontend/console/src/field/BlockField.tsx`, `field/blockField.ts`, `field/blockField.test.ts`, `styles.css` (field/layer tokens), a temporary demo mount in `App.tsx` behind the existing content.
**Done when:** a Canvas 2D block-field renders behind the current session UI; blocks flow *in and out* as the pointer moves across them; a click emits a pulse that ripples through the blocks and decays; the field runs at 60fps for the default block count; `data-field-state` reflects `animating` and the loop pauses on tab-hide. The field's motion is **quiet-but-alive-maximal**: low-amplitude, restrained, never grainy/noisy, yet continuously reactive. The field reads its colours from the theme tokens (P2 introduces the token restructure; P1 introduces field-token names it consumes) so it is **correct in both light and dark themes**. Browser-verified via `preview_start` (console, 5173) in both themes.
**Tests:** pure `blockField.ts` unit tests — grid geometry for a viewport, pointer-proximity flow function, pulse-decay to zero in bounded time (edge 3), tab-hidden pause (edge 4). No pixel assertions.
**Risk:** medium
**Follow-up (Aurora Glass, folded into P2):** P1 shipped as small **dots** (PR #35, merged). The locked "Aurora Glass" direction (see *Design direction*, above) upgrades the field to **big connected glass panes** — ~46px rounded tiles with thin seams reading as one glass surface; panes bulge/lean/refract toward the cursor, catch a diagonal reflection streak clipped inside each pane, light their edges as they wake, and a click fires an impulse ripple; motion eases in (quiet at rest, lively near the cursor). This upgrade **must land as part of the 010 build** and is scheduled in **P2** (tokens/theme portion), still Canvas 2D + single rAF loop, reduced-motion and tab-hide safe as here; pane size / bend strength / reflection / idle shimmer are tuned during the build, not re-decided.

### [ ] P2 — Aurora Glass tokens + theme system + shell layering + reduced-motion + glass-pane field upgrade (cross-cutting foundation)
**Touches:** `styles.css` (restructure into the **Aurora Glass** light + dark token sets), new `app/theme.ts` (theme state + persistence, e.g. `localStorage`), a theme toggle control in the shell/sidebar, font loading (Bricolage Grotesque / IBM Plex Sans / IBM Plex Mono via Google Fonts with fallback stacks), `field/BlockField.tsx` + `field/blockField.ts` (consume theme tokens **and upgrade the field from dots to connected glass panes**), new `app/Shell.tsx`, `App.tsx`, tests.
**Design direction (locked):** implement the **Aurora Glass** spec (see *Design direction*, above; match the referenced artifact). Panels are **frosted glass** — translucent + `backdrop-blur`, hairline borders, soft shadow, ~16px radius; hierarchy from depth and light, not heavy borders (Linear/Attio-calm, premium).
**Done when:** tokens are restructured into the **Aurora Glass** system so **light and dark are both first-class** — accent **teal + indigo** (light `--accent:#28b6a4` / `--accent-2:#6f83f5`; dark `--accent:#41d6c3` / `--accent-2:#8a9bff`), **cool-slate neutrals**, and semantic good/warn/bad kept separate from the accent; the type system loads **Bricolage Grotesque** (display) + **IBM Plex Sans** (body) + **IBM Plex Mono** (data/numerals) with real fallback stacks; content panels render as **frosted glass** (translucent, `backdrop-blur`, hairline border, soft shadow, ~16px radius); a **user-facing theme toggle** switches themes and the choice is **persisted** across reloads (defaulting from `prefers-color-scheme`); every surface — including the field — reads its colours from the theme tokens and is correct in both themes; the field sits on a dedicated `z-index` tier behind the glass content panels; the existing sidebar + session panels render above it unharmed in both themes. **The reactive field is upgraded from P1's dots to the Aurora Glass form** — big connected glass panes (~46px rounded tiles with thin seams reading as one glass surface) that bulge/lean/refract toward the cursor, catch a diagonal reflection streak clipped inside each pane, light their edges as they wake and settle calmly at rest, with a click firing an impulse ripple and motion easing in (quiet at rest, lively near the cursor) — still Canvas 2D + single rAF loop. `prefers-reduced-motion: reduce` yields a single static frame with no rAF loop (`data-field-state="reduced"`), and a canvas-init failure degrades to `data-field-state="degraded"` with the app fully usable. **Correct in both light and dark**, reduced-motion, and degrade become a **Done-when on every later visual portion**. Browser-verified via `preview_start` (console, 5173) in both themes. Pane size / bend strength / reflection / idle shimmer are tuned here during the build, not re-decided.
**Tests:** theme toggle switches token set and persists across a remount; default derives from `prefers-color-scheme`; reduced-motion branch renders no loop (edge 1); degraded branch renders content (edge 2); the block-field reads theme tokens (assert the token source, not pixels); glass-pane geometry (pane grid + seam spacing) covered by pure `blockField.ts` tests, with pulse-decay (edge 3) and tab-hidden pause (edge 4) still holding after the upgrade; `App.test.tsx` still green.
**Risk:** medium

### [ ] P3 — Data layer: typed client + query hooks (no new visuals)
**Touches:** `api.ts` (extend with Sales contracts + cursor helpers), new `data/useAccounts.ts`, `data/useContacts.ts`, `data/useDeals.ts`, `data/keys.ts`, tests with mocked `fetch`.
**Done when:** typed hooks exist for accounts/contacts/deals reads (list with keyset cursor + detail) and the write mutations, keyed by stable query keys, reusing the token/`can()` fail-closed pattern; 401/403 drops the token to sign-in as `useSession` does. No screen yet — verified by tests.
**Tests:** cursor pagination advances and stops on null (edge 11); a mutation invalidates its key; 401/403 path clears the token; empty-scope response maps to the stated-empty model (edge 5).
**Risk:** low

### [ ] P4 — Accounts screen (router + first real grid)
**Touches:** new `app/router.tsx` (react-router route table + guards), `screens/accounts/*`, `Navigation.tsx` (wire real routes, keep the fail-closed `can()` gating), `package.json` (**add `react-router` dependency**), tests.
**Design decision (Essential):** the router is **`react-router`** (user decision, 2026-09-05) — real route guards, maintained, well-understood auth/permission-gating patterns. `react-router` provides the routing; **authorization still flows through the existing fail-closed `can()` / `Navigation.tsx` pattern** — a route guard consults `can()` and denies (locked affordance, no fetch) exactly as the nav does; the router is never the auth authority.
**Done when:** the `react-router` route table puts a real Accounts screen behind the nav (gated on `accounts.read` via a `can()`-backed guard, fail-closed); a reactive grid lists accounts over the field with quiet-but-alive hover/focus/selection micro-interactions; create + edit forms bind to `POST`/`PATCH` with `xmin` round-trip; a `409` shows the "changed by someone else" state and refetches; empty scope shows the stated-empty surface. **Correct in both light and dark themes**; reduced-motion/degrade Done-when holds. Browser-verified.
**Tests:** edges 5, 7, 10; permission-denied route guard issues no GET (edge 6); grid pagination (edge 11); route guard denies fail-closed when `can()` is unresolved.
**Risk:** medium

### [ ] P5 — Contacts screen
**Touches:** `screens/contacts/*`, router wiring, tests.
**Done when:** Contacts grid (gated `contacts.read`) with create-under-account (`contacts.write`) and depart; departed contacts leave the active list but appear under `includeDeparted`, marked departed. Controls carry quiet-but-alive micro-interactions. **Correct in both light and dark themes**; reduced-motion/degrade Done-when holds. Browser-verified.
**Tests:** edge 12 (depart is not delete); create validates account-in-scope failure (404 surfaced); double-submit guarded (edge 10).
**Risk:** low

### [ ] P6 — Deals screen: grid + detail with lines
**Touches:** `screens/deals/*`, router wiring, tests.
**Done when:** Deals grid (gated `deals.read`), single-deal detail showing lines, create deal, and add-line all bound to the real endpoints, with quiet-but-alive hover/focus/selection micro-interactions. **Correct in both light and dark themes**; reduced-motion/degrade Done-when holds. Browser-verified.
**Tests:** detail includes lines while grid omits them; add-line refetches detail; pagination (edge 11).
**Risk:** medium

### [ ] P7 — Deal lifecycle: transition + discount approval
**Touches:** `screens/deals/lifecycle/*`, `data/useDeals.ts` (transition + approve mutations), tests.
**Done when:** the detail offers the lifecycle moves and treats the server as authority — `422` shows the rejected move with the server message, `409` shows stale + refetch, and the `200 + PendingApproval` discount-hold renders a distinct "pending approval" state with an approve control shown **only** for `deals.discount.approve` holders; approval sends a required reason. Optimistic update layered in only for the reversible transitions with rollback-on-error. **Correct in both light and dark themes**; reduced-motion/degrade Done-when holds. Browser-verified.
**Tests:** edges 8 (discount hold + gated approve), 9 (illegal 422), 7-style 409 on transition; approve-without-reason blocked client-side and server 400 surfaced.
**Risk:** high

### [ ] P8 — Accessibility & motion hardening pass
**Touches:** cross-cutting — `field/*`, `screens/*`, `styles.css`; `test/` a11y assertions.
**Done when:** full keyboard traversal of every screen and control; focus-visible on all interactive elements; the field is `aria-hidden` and never a focus/scroll trap; contrast meets AA in **both the light and dark themes**; reduced-motion verified across all screens (not just the shell); screen-reader labels on grids and lifecycle controls. Browser + test verified in both themes.
**Tests:** reduced-motion holds on every route (edge 1 generalized); field is `aria-hidden`; keyboard reaches every write control; denied controls are not focusable (mirrors `Navigation.tsx`).
**Risk:** medium

## Open questions for the user

All four open questions were **resolved by the user on 2026-09-05 (user approved)** and are folded
into the plan above:

1. **Roadmap placement — RESOLVED (2026-09-05, user approved).** The proposed §13 row was approved and
   **applied** to `ARCHITECTURE.md §13` (additive only):
   > `| 010 | Reactive console: a living Sales surface | The API-first slices (001/002) exist; this makes them usable and gives Aperture its distinctive face. Sequenced after 002's Sales endpoints, independent of 003. |`
   The plan header's *Roadmap* line now cites §13 item 010.
2. **Router dependency — RESOLVED (2026-09-05, user approved).** Use **`react-router`** (the mature,
   maintained library with real route-guard support), not a hand-rolled hash router. Baked into P4 as
   an Essential design decision; `react-router` provides routing only — authorization still flows
   through the fail-closed `can()` / `Navigation.tsx` pattern.
3. **Aesthetic intensity — RESOLVED (2026-09-05, user approved).** **"Quiet but alive — maximal":**
   restrained, tasteful palette and motion (never loud/grainy/noisy), yet *everything* on the surface
   is subtly alive and reactive — maximum interactivity, minimum visual shouting. Folded into the
   design direction, P1, and as a cross-cutting UX principle on P4–P7.
4. **Colour story — RESOLVED (2026-09-05, user approved).** **User-selectable light AND dark themes**,
   not dark-only: a real theme system with a user-facing, persisted toggle. Promoted to a cross-cutting
   concern, built in P2, with "correct in both light and dark" a Done-when on P1 and P4–P8.
