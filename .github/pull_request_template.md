<!--
This template mirrors what ap-reviewer produces (see docs/plans/pr/ for a real example).
A PR here is one portion of one plan. If it is more than that, it is two PRs.
-->

## Portion

Plan: `docs/plans/NNN-<slug>.md` · Portion: `P<n>` · Roadmap: `ARCHITECTURE.md` §13 item NNN

## What it does

<!-- What changed and why this shape. Cite the ARCHITECTURE.md sections it implements. -->

## Which failure this answers

<!-- Which rule in DOMAIN.md, or which of the five production failures in DOMAIN.md §5. -->

## How it was verified

<!-- Real command output. Not "tests pass" — the actual counts. -->

```
dotnet build Aperture.slnx
dotnet test  Aperture.slnx
cd frontend/console && npm run build
bash scripts/measure.sh gate
```

Every Given/When/Then in the plan's *Edge cases* maps to a named test:

| Plan case | Test |
|---|---|
|  |  |

## Review findings

<!-- Findings raised, and for each: fixed (with the commit) or disputed (with the reason).
     "Nothing survived verification" is a valid and good outcome. -->

## Out of scope, deliberately

<!-- What this portion does not do, so the reviewer does not go looking for it. -->

## Next portion

<!-- Which portion follows, and anything still waiting on a decision from the repo owner. -->

## Checklist

- [ ] Exactly one portion of one plan — no drive-by fixes
- [ ] Every new endpoint carries an authorization policy
- [ ] Every raw SQL / Dapper call passes `tenant_id` explicitly
- [ ] No cross-module coupling — contracts and events only
- [ ] Migration is additive and deployable alongside the running code
- [ ] The portion's checkbox is ticked and `docs/plans/STATE.md` is updated
