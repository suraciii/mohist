# Self-Review — issue-480

Reviewer role: assess whether the plan artifacts (`proposal.md`, `specs/`, `design.md`, `tasks.json`) are ready to build, against issue #480's acceptance criteria. No product files were modified.

## Summary

The plan cleanly separates the three concerns the issue mandates — `runner` (Server-registered resource), `server` (connected application), `service` (local managed process) — and is internally consistent across all four artifacts. Specs are well-formed (4-hashtag scenarios, normative SHALL/MUST, no delta headers, every requirement has ≥1 scenario). The task graph is a valid acyclic DAG (T-001 → {T-002, T-003} → T-004) with test-inclusive acceptance criteria. All 8 issue acceptance criteria are covered. Findings below are minor / non-blocking.

## Acceptance-criteria coverage

| AC | Covered by | Status |
|---|---|---|
| `runner list/view/status` reads only Server resources, no local service manager | `runner-resource-commands` (surface + endpoint scenario "without invoking any local service manager"); T-002 | ✅ |
| `server status/health/info/logs` reads only connected app; `project status` gone | `server-commands` (surface + "`server status` reports overall Server status" + legacy-removal scenario); T-003 | ✅ |
| `service …` target `server`/`runner`, local-only | `service-lifecycle` (target + unified-lifecycle reqs); T-001 | ✅ |
| `server logs` app logs vs `service logs server` service-manager logs; non-interchangeable | `server-commands` + `service-lifecycle` (both carry the "MUST NOT claim interchangeability" clause + cross-reference help scenarios); T-001/T-003 | ✅ |
| local Runner stopped → `runner view/status` still report Server facts | `runner-resource-commands` ("Remote runner facts are independent of local service state"); T-002 | ✅ |
| `service` no Project; `runner` follows `--project` | `service-lifecycle` ("do not parse Project") + `runner-resource-commands` ("shared contract"); T-001/T-002 | ✅ |
| `install`/`update` root-level only | `service-lifecycle` ("Install and update remain root-level only"); T-004 verifies | ✅ |
| old paths removed from tree AND hints, no alias | `service-lifecycle`/`server-commands` removal scenarios + design D6 + T-004 (ServerUnavailableMessage, NdjsonStream, Update.Finalize, system-info degraded) | ✅ |

## Findings

### F1 — `server status` does not restate the no-`--project` constraint (minor, non-blocking)
`server-commands/spec.md` "server status reports overall Server status" describes the `/api/status?all=true` endpoint and `project status` removal, but does not state that `server status` accepts no `--project` / `--project-id` and no positional arg. The relocated behavior (`project status`) had an explicit spec asserting exactly this (`CliProjectStatusCommandSpecs` — endpoint aggregates `all=true`, so project scoping is semantically wrong). Risk is low: the natural implementation copies the former 1-line `project status` handler (no options) and `health`/`info`/`logs` carry no `--project`. Recommend the fixer add one scenario asserting `mo server status --help` advertises no `--project`, to carry the constraint forward. Does not block building.

### F2 — install/update root-level scenario is partial (minor, non-blocking)
`service-lifecycle/spec.md` "Install and update remain root-level only" has a single scenario asserting `service install server` and `runner install` fail. It does not also assert `server install` / `server update` / `runner update` fail. `server install` never existed, so the risk of a stray duplicate is negligible; the requirement text already covers all three groups normatively. Optional: broaden the scenario. Non-blocking.

### F3 — `runner show`→`view` rename is AC-mandated but creates a transient cross-command inconsistency (observation, non-blocking)
The issue names `view` three times (User Voice, Product Shape, AC), so renaming is faithful and correct — keeping `show` would violate the AC. Side effect: `runner view` will temporarily coexist with `project show` / `issue show`. This is a known, separately-tracked unification gap (`docs/cli-reference.md` "实装差距" → target unify reads to `view`/`edit`), not a defect of this plan. Noted only so the implementer expects that existing `CliRunnerCommandSpecs` assert `show` heavily and must be rewritten (T-002 already states this). No action required for this issue.

## Verdict

Specs are testable and complete against the ACs; the design gives reasoned decisions with alternatives and a bounded CLI-only risk profile; the task graph is deliverable, dependency-valid, and test-inclusive. No problem rises to "must be fixed before build."

<promise>PASS</promise>
