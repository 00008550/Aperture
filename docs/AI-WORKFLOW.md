# How this repository is built

Aperture is written with coding agents. This document explains the workflow, because the workflow is
the point of the repository as much as the code is.

The short version: **one agent researches, a different agent implements, a third agent judges, and a
human owns every gate between them.** No agent both writes code and approves it.

---

## Why not just prompt a model

The failure mode of agentic development is not bad code. It is *plausible* code — code that
compiles, reads well, passes the tests it wrote for itself, and is wrong about something the model
could not see. Three specific ways that happens:

1. **The model trusts a summary.** Ask it what the system does and it reads the docs, which are a
   snapshot of what someone believed months ago. It then plans against a system that does not exist.
2. **The model tests its own understanding.** An author-written test encodes the author's mental
   model. When that model is wrong, the test passes and confirms the error.
3. **The model does the adjacent thing.** Asked for a slice, it also refactors a neighbour, and now
   the diff is too large to review, so review degrades into skimming.

Every rule below exists to close one of those.

## The three roles

| Agent | Writes | Never does | Model |
|---|---|---|---|
| `ap-surveyor` | `docs/plans/**`, factual corrections to `docs/` | Touch `src/` or `frontend/`. Approve its own plan. | opus |
| `ap-builder` | product code, tests, migrations | More than one portion. Commit to `master`. | opus |
| `ap-reviewer` | plan status only | Edit a single line of the code it grades | opus |

The separation is not ceremony. `ap-reviewer` has no write access to `src/` **by tool configuration**,
not by instruction, because an agent that *can* fix what it found will fix it and report a clean
review. The independence has to be structural to survive a long session.

## The loop

```
        ┌──────────────┐
        │ ap-surveyor  │  measures the code, corrects the docs,
        └──────┬───────┘  writes docs/plans/NNN-*.md as `draft`
               │
        ╔══════▼═══════╗
        ║ HUMAN GATE   ║  only a person moves a plan draft → approved
        ╚══════╤═══════╝
               │
        ┌──────▼───────┐
        │  ap-builder  │  exactly one portion, on its own branch
        └──────┬───────┘
               │
        ┌──────▼───────┐   findings
        │ ap-reviewer  ├──────────────► back to builder (Mode B), max 3 rounds
        └──────┬───────┘
               │ ship
        ╔══════▼═══════╗
        ║ HUMAN GATE   ║  the person reads the PR and merges
        ╚══════════════╝
```

`/ap-cycle` is the orchestrator that drives one step of this and stops at every gate. It is a slash
command, not an agent, because the orchestration decision — is this plan ready, is this finding
right — is where a human's judgment is cheapest to apply and most valuable.

## The four rules that do the work

### 1. Measure, never trust a summary

Every plan starts by running `scripts/measure.sh` and pasting the real numbers into the plan's
*Ground truth* section: every route and its authorization policy, every declared permission and
whether anything enforces it, table and column counts per schema, and which modules have no tests.

Documents are treated as hypotheses about the code. `ARCHITECTURE.md` §12 carries an explicit
header saying a `✅` means nothing until measured — and correcting that table is part of the
surveyor's job on every run, not a cleanup task for later.

This rule comes from experience on a larger codebase, where an inventory document claimed
"247 entities" for a schema that actually had 579 tables and 8,173 columns, and a coverage matrix
marked the single most important aggregate as built when only a fragment of it existed. Both errors
survived several review passes, because each pass trusted the pass before it.

### 2. One portion at a time

A portion is one vertical slice: one aggregate, its migration, its service, its endpoint, its
contract, its screen, its tests. Roughly eight files. If it crosses two modules, it is two portions.

The constraint is not about the model's context window — it is about the reviewer's. A 40-file diff
gets skimmed by a human and by an agent alike, and a skimmed review is worse than no review because
it produces a false signal.

### 3. The plan's edge cases are the test list

`ap-surveyor` writes a *Failure modes* table — tenancy, authorization, consistency, concurrency,
idempotency, ordering, failure, backward compatibility, observability — and answers every row for
the thing being built. Then it writes *Edge cases* as Given/When/Then.

`ap-builder` must produce a test named for each one. `ap-reviewer` checks that mapping literally.
This is what stops the author from grading their own understanding: the cases were decided by a
different agent, before the code existed, from the domain rather than from the implementation.

### 4. Findings need a failure scenario

A review finding must name a concrete input that produces a wrong result. "Consider extracting this"
is not a finding. An empty findings list is explicitly a good outcome, and the reviewer is told to
say "nothing survived verification" rather than manufacture nits — because a reviewer that always
finds three things is one nobody can calibrate.

The builder is allowed to **dispute** a finding with a reason. Caving to a wrong finding is worse
than the finding, because it lands wrong code with a review's blessing attached.

## Where the human actually is

Four places, and they are the four decisions that matter:

- **Plan approval.** Portions, order, scope, and every product call the surveyor flagged.
- **Design changes.** The surveyor may propose an amendment to the architecture invariants; it may
  not apply one.
- **Disputed findings.** When builder and reviewer disagree, a person decides.
- **Merge.** Always.

Everything else — reading the schema, writing the migration, wiring the endpoint, writing the tests,
running the browser check — is delegated. The division is: **the model does the work, the human owns
the decisions and can explain every line of the result.**

## Choosing models

- **Survey and review** run on the strongest available reasoning model. Both are judgment tasks over
  a large, partly-implicit context, and both are where an error is most expensive — a wrong plan
  costs every portion built on it.
- **Implementation** runs on the same model here, because the portions are architecture-sensitive.
  For mechanical portions (a migration, a DTO, a generated client) a faster model is sufficient, and
  the reviewer is unchanged either way — which is precisely what makes trading down safe.
- **Debugging** benefits from a fresh session over a long one: a model that has been reasoning
  inside a wrong assumption for twenty turns will keep reasoning inside it. Restarting with the
  measured facts is faster than arguing.

## Context management

- `CLAUDE.md` holds the invariants, and it is short enough to be read every time. Invariants that
  live in a long document are invariants nobody reads.
- Plans carry their own context: the ground-truth measurement, the cited architecture sections, the
  edge cases. A builder session starts from the plan, not from the conversation that produced it.
- `docs/plans/STATE.md` is the handoff point between sessions. The workflow survives a closed
  terminal because the state is in the repo, not in a context window.
- Agent definitions name the specific smells to hunt (fail-open filters, unfiltered raw SQL,
  check-then-insert races) rather than saying "review carefully". A generic instruction produces a
  generic review.

---

## Reading this repo as a reviewer

- `CLAUDE.md` — the binding invariants.
- `.claude/agents/` — the three role definitions. The interesting parts are the *boundaries*
  sections: what each agent is forbidden to do, and why.
- `.claude/commands/ap-cycle.md` — the state machine, including where it stops for a human.
- `docs/plans/` — plans and their portions. `STATE.md` is the index.
- `docs/plans/pr/` — the PR bodies the reviewer produced, including the real command output it
  verified against.
