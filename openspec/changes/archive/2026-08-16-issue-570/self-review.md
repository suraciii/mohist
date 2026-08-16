# Self-Review: Issue 570 Plan — Runner Loss Work Recovery & Resource Isolation

Round: **re-review (verify dispositions)**. Round 1 (full sweep, commits up to
`e63659516`) FAILed on one must-fix: M-1, D7/T-007's false claim that Linux
RLIMIT enforcement rides "the existing spawn seam". This round verifies the
dispositions landed after that review — `8bb9de3e7` (design D7 M-1 fix),
`f5b1ffd41` (tasks T-007 M-1 fix), `91a5e3075` (D4 note dispositioning
observation O1) — against the issue and the codebase, rather than re-sweeping.

## Verdict

**PASS** — the single must-fix finding (M-1) is fixed properly and verifiably;
the fixes introduced no regressions; no pre-existing problem meeting the
must-fix bar surfaced in this round's verification. The plan is ready to build.

## Disposition verification

### M-1 — fixed properly (design `8bb9de3e7`, tasks `f5b1ffd41`)

Round 1 required either (a) a concretely specified RLIMIT application approach
with trade-offs evaluated, or (b) dropping RLIMIT for watchdog + wall-clock.
The fix takes (a), and every load-bearing new claim was verified against the
codebase:

- **The seam claim is now accurate.** D7 states Node's `spawn`/`SpawnOptions`
  carries no resource-limit support (true on the repo's Node 22;
  `resourceLimits` is worker-threads only) and specifies the actual mechanism:
  an optional per-command resource-limit option on `CommandLineOptions`,
  "layered in exactly like `timeoutMs`, omitted ⇒ byte-identical spawn" —
  verified in `packages/runner/src/system/process.ts`: `CommandLineOptions`
  already carries `timeoutMs` with exactly that omitted-⇒-byte-identical
  contract, so the layering precedent exists as described.
- **The application point is real.** The process-action spawn path is
  `packages/runner/src/actions/built-in-core.ts` line 13, which calls
  `runCommand(command, args, …)` — the wrapper construction at the
  `runCommand` boundary reaches it as claimed, and `ProcessSpawner` (pins
  `SpawnOptions`) genuinely stays unchanged; only `command`/`args` are
  wrapped.
- **The process-kill/timeout invariants hold.** D7's claim that "`prlimit`
  sets its own limits and `exec`s the target in place, so the child PID and
  its detached process-group leadership are stable" is correct (util-linux
  `prlimit` applies limits to itself and `exec`s the command in place, like
  `nice`/`time`). Verified against `process.ts`: `detached: true` makes the
  child its own group leader; `killProcess` uses `process.kill(-pid)` group
  kill; `runCommand` completes on `close` with group SIGKILL on direct-child
  exit — all keyed on the stable PID, none broken by the in-place exec.
  `registerExternalProcess`/`assertExternalProcessAllowed` pose no barrier:
  the production process policy is a no-op (no command allowlist), and the
  optional `commandRunner` resource is test injection only, so the wrapper
  applies on the production spawn path.
- **Trade-offs are now evaluated, not assumed.** D7 adds rejected
  alternatives — post-spawn `prlimit(2)` (spawn-to-limit race window; same
  host binary, so the wrapper is strictly better), shell `ulimit` preexec
  (shell quoting over arbitrary action args, flag variance; the wrapper keeps
  a single `exec` and argv-array spawn with `shell: false`), watchdog-only
  primary (a work allocating faster than one sample is the exact incident
  class) — plus a detection-latency risk bullet covering watchdog-only hosts
  and RLIMIT_AS (virtual-space, conservative) vs RLIMIT_DATA (Linux ≥ 4.7,
  closer to RSS) semantics.
- **The host-binary reliance claim is consistent with the repo.** `git` and
  `gh` are already spawned as host binaries through `runCommand`
  (`runtime/workspace-entity.ts`, `actions/github-pr-runtime.ts` et al.), and
  probing `prlimit` at startup adds no npm/native dependency — the
  no-new-external-dependencies constraint holds.
- **T-007 matches the amended design.** The false note ("Linux RLIMIT seam
  exists in the spawn path") is replaced by the corrected statement; the
  description names the mechanism; a new acceptance criterion covers the
  prlimit-unavailable fallback deterministically ("forced off in tests"); and
  the output now names `system/process.ts` and `actions/built-in-core.ts`
  alongside the executor/watchdog files. The fallback AC is testable as
  written (the probe is a startup fact injectable in tests).

With the mechanism now specified, D7/T-007 can be executed as written and the
issue's resource-isolation goal — "资源失控也不应让同一 Runner 上的多个长任务级联拖垮整个执行平面"
and AC "有确定性测试覆盖资源级联保护" — is satisfiable by the plan (T-007
AC1/AC2 cover the memory-burning child on both prlimit and fallback hosts).

### O1 — addressed with the D4 note (`91a5e3075`), and the note is accurate

The note claims the landed `runner-work-result-recovery` spec's "process
restarts before a result exists" MUSTs remain satisfied. Verified against the
spec text: (1) "MUST refuse to execute that dispatch again" — D4's fence never
opens (`begin()` never returns `new` for a `started` identity), and T-008 AC6
tests exactly that; (2) "MUST leave Workflow outcome arbitration to the
existing unresolved, authoritative-result, and explicit-stop paths" — D4.3's
wire `unknown` feeds the existing unresolved arbitration
(`ObserveAgentResultUnknownAsync`) and non-agent `runner-restarted` failures
reconcile as ordinary reports under the original identity, i.e. through those
same paths rather than fabricated beside them. The note's argument is sound;
no spec amendment is required.

## Regression check

`git diff e63659516 HEAD` over the change dir touches only `design.md` (D7
body, D7 alternatives, one risk bullet, the D4 note) and `tasks.json` (T-007
description, one added AC, output, notes). No claim verified in round 1 —
closeout seams, Unknown projection, redelivery desired-set, fence semantics,
deadline arithmetic, bounded drain/shutdown targets, migration order — was
altered. The added text was itself verified against the code (above). No
regression meeting (or approaching) the must-fix bar.

## Pre-existing problems missed in round 1

None found meeting the must-fix bar. This round additionally probed seams the
M-1 fix newly depends on — the production process policy (no-op, no allowlist
to deny a `prlimit` spawn), the `commandRunner`/`RunnerResources` injection
surface (test-only), and PID/Pgid stability across the in-place exec — all
check out. Nothing to escalate; nothing to record beyond the observations
below.

## Observations (do not affect the verdict; carried/updated from round 1)

- **O2 (unchanged) — wrong test directory name in T-006 output.** Still says
  "packages/runner/test"; the actual directory is `packages/runner/tests`
  (colocated `*.test.ts` also exists). Cosmetic — the task says "colocated
  and/or" — and never affected the verdict.
- **O3 (unchanged) — shared-runtime per-work memory isolation is
  approximate.** Runtime-backed turns get budget containment + bounded
  quarantine + deployment `MemoryMax`, not per-work OS limits. Documented
  trade-off (D7, Risks); implementation reviewers should keep the residual
  OOM-the-runner case in view.
- **O4 (unchanged) — open questions left open** (`RunnerLossRecoveryTimeout`
  default tuning, `AdmissionReady` gating, per-work-type containment
  defaults, `TaskInterrupted` event shape). Each has a stated current
  proposal; resolve during T-001/T-002/T-003/T-007.
- **O5 (unchanged) — T-005's "Unknown-job-report dead-end gap" may be
  smaller than described**; current code already settles reports against
  Unknown jobs and acks terminal-job reports `Stale`. The task's value is
  mostly deterministic proof; check whether
  `runner-or-work-mismatch` (non-ack, retried forever) is in scope while
  implementing.
- **O6 (unchanged) — rollback note relies on `FailActiveWorkAsync` remaining
  available.** It does (T-001 replaces the call, not the seam); flagging the
  coupling only.
- **O7 (new, minor) — prlimit probe placement is not pinned to a file.**
  D7/T-007 say "probed once at runner startup" but the T-007 output list does
  not name the startup/config file that holds the probe. Builder's choice;
  the AC ("forced off in tests") keeps it verifiable regardless.

<promise>PASS</promise>
