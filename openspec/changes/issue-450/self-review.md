# Self-Review - Issue #450 Pi Workflow Path

Scope: current issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against the issue-designated product/runtime contracts, current domain/wire/read models, repository architecture, and testing rules. This review modifies no plan artifact.

## Findings

### F-1 High: The promised explicit turn deadline has no authoritative dispatch source

The runtime requirement and canonical Pi design say each Workflow turn uses an executor-declared deadline, defaults to 60 minutes when omitted, and permits an explicit override (`specs/pi-runtime/spec.md:93-107`; `design/runtimes/pi.md:183-205`). The Pi Action input is explicitly limited to `prompt`, `session`, and `options.model` / `options.variant` (`specs/pi-workflow-action/spec.md:17-59`; `proposal.md:7`), while the rendered dispatch envelope has no deadline field (`packages/runner/src/core/types.ts:151-186`). The only current override precedent is OpenCode's undocumented `with.timeout` lookup (`packages/runner/src/actions/opencode.ts:251-254`), which the Pi input contract rejects as an unknown field.

An implementer can provide the 60-minute default but cannot implement the promised explicit deadline without inventing a second configuration channel or violating the Action schema. Define the deadline's product/DSL owner and units, add it to the Workflow task/dispatch/executor contract rather than Pi-specific Action input, specify validation/defaulting and retry/cleanup propagation, and assign the required Server/Runner/documentation tests. If explicit override is intentionally outside this issue, remove that promise consistently and specify the fixed executor default.

### F-2 High: Cache-write usage has no persisted or Web projection field

The canonical Pi event map includes distinct `cacheRead` and `cacheWrite` usage (`design/runtimes/pi.md:213-223`). The change requires every supplied input/output/cache-read/cache-write/thought dimension to be durable and rendered (`tasks.json:98,160,184`; `specs/pi-workflow-session/spec.md:122-128`). However, the current Session domain and API carry only `CachedReadTokens`, not cache-write tokens (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.cs:273-282`; `packages/server/src/Mohist.Server/Sessions/AgentSessionReadModels.cs:6-18`), and the Web usage type likewise exposes only `cachedReadTokens` (`packages/web/src/entities/coder-session/model/types.ts:26-37`). D9 incorrectly says existing runtime-neutral Session usage DTOs already suffice and limits Web work to classification (`design.md:164-170`).

The plan must explicitly add `cachedWriteTokens` through the Session domain state/transition, Orleans serialization IDs, runtime-event parsing, API/read models, mappings, Web types/rendering, outbox/restart reconciliation, and duplicate-safe tests. Assign Server ownership to T-003/T-004 and Web presentation to T-007, and update proposal/design impact so this persisted/API schema change is not hidden behind generic "usage facts" wording.

## Structural Checks

- `tasks.json` parses as valid JSON.
- All seven task IDs and dependencies resolve; the graph is acyclic, every dependency points to a lower priority, and every implementation task reaches T-001.
- All task spec paths and requirement anchors resolve.
- All three proposal capabilities have matching spec files; 21 requirements contain 77 correctly headed scenarios.
- The SDK smoke gate and repository-wide Node 22.19 migration are now correctly represented in the executable graph.
- Other product, runtime, Session, failure, audit, cleanup, credential, and scope contracts are internally consistent and have task/test ownership.

## Verdict

The plan is close, but builders still have to invent the explicit deadline transport and cannot satisfy the promised cache-write usage projection with the existing Session/Web model. Both contracts need explicit model, wire, ownership, and test changes before implementation.

<promise>FAIL</promise>
