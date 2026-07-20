# Self-Review - Issue #450 Pi Workflow Path

Scope: current issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against the issue-designated product/runtime contracts, current affected callers, repository architecture, and testing rules. This review modifies no plan artifact.

## Findings

### F-1 High: The SDK smoke is called a hard gate but does not gate all implementation tasks

The runtime spec and design require the pinned real-SDK smoke to complete and any drift to be reconciled before implementation proceeds or product code relies on the assumed API (`specs/pi-runtime/spec.md:1-15`; `design.md:11,46-52,211-212`). T-001 likewise calls itself a hard gate (`tasks.json:6-26`). T-002 correctly depends on T-001, but T-003 and T-005 are product implementation tasks with empty dependency lists (`tasks.json:59-83,117-135`). They can run before or concurrently with the smoke.

Even though T-003/T-005 are not Pi SDK adapters, the plan's stated migration order says no product code begins before the gate, and both can be invalidated by the contract reconciliation T-001 is allowed to make. Make every implementation task depend directly or transitively on T-001, or narrow the normative gate explicitly to SDK-dependent tasks and explain why the independent Server/coordination slices cannot consume smoke-driven design changes. The current prose and graph must agree.

### F-2 Medium: The Node 22.19 migration omits active version declarations

T-001 says it raises every documented and enforced Node floor, but its concrete acceptance/output names root/Runner manifests, `CONTRIBUTING.md`, CI, and Docker (`tasks.json:9-23`; `design.md:164-170,211`). The repository also pins `.nvmrc` to the unspecified major `22` (`.nvmrc:1`) and tells self-host source installers to use `Node 22` (`docs/self-host.md:72-75`). Both are active installation/version guidance and can select an unsupported pre-22.19 release while all listed T-001 criteria pass.

T-001 must enumerate `.nvmrc` and every source-install prerequisite, including `docs/self-host.md`, and verify all CI workflows such as `.github/workflows/ci.yml` and `web-test-shuffle.yml` use an explicit compatible release. The migration should have one repository-wide search/assertion proving no active Node declaration remains below or ambiguously before 22.19.

## Structural Checks

- `tasks.json` parses as valid JSON.
- All seven task IDs and dependencies resolve; the graph is acyclic and every existing dependency points to a lower priority.
- All task spec paths and requirement anchors resolve.
- All three proposal capabilities have matching spec files; 21 requirements contain 77 correctly headed scenarios.
- The product, runtime, Session, failure, audit, cleanup, credential, and scope contracts are otherwise internally consistent and have task/test ownership.
- The temporary current-Action plumbing is consistently identified as issue #447-owned debt rather than target architecture.

## Verdict

The plan's behavior and architecture contracts are ready, but the executable graph does not enforce its mandatory pre-implementation SDK gate, and the breaking Node floor migration can leave active version guidance ambiguous. Fix these two task-plan gaps before build execution.

<promise>FAIL</promise>
