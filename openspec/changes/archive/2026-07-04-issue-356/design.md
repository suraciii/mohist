## Context

`design/testing.md` lists "no real wall clock" as a hard constraint and names the
injection seam: C# services take `TimeProvider`, tests use `FakeTimeProvider`.
`SystemUpdateService` (`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs`)
is one of the hottest wall-clock readers in the server — every persisted
transition timestamp in the system-update flow (creation, superseded,
waiting-for-reconnect, ready, succeeded, recovered, CLI-outcome, command
start/finish, failed, and the transition-log fallback) currently reads
`DateTimeOffset.UtcNow` directly. None of these time-driven branches can be
exercised deterministically today without real `Task.Delay` / polling — the
exact flaky recipe `design/testing.md` calls out.

The service is already designed for testability: it has a `public` constructor
(used by DI) and an `internal` constructor (used by specs) that accepts a
`Func<CancellationToken, Task<SystemInfoResponse>>` delegate to fake system-info.
What it lacks is an injectable clock. The established in-repo pattern is
`AttachmentService` (`packages/server/src/Mohist.Server/Issue/Services/Attachments/AttachmentService.cs:36`),
which holds a `private readonly TimeProvider _time` and offers two constructors —
one defaulting to `TimeProvider.System`, one accepting an explicit instance.

A grep of the current file confirms **13** `DateTimeOffset.UtcNow` call sites
(lines `65, 124, 147, 164, 182, 209, 297, 494, 558, 639, 646, 708, 724`), not the
16 cited in the issue body — those line numbers are stale from a longer
historical revision. Two of the thirteen sit in `private static` helpers
(`CreateFailedTransition:699`, `ApplyTransitionLog:721`), which is the only
structural wrinkle in an otherwise mechanical replacement.

This change is also a prerequisite for issue#02 (fire-and-forget stale-state
self-healing reconciler), which cannot be tested deterministically until this
service's clock is injectable.

Stakeholders: server update flow (runtime), `SystemUpdateServiceSpecs` (test
surface), and the downstream issue#02 reconciler.

## Goals / Non-Goals

**Goals:**
- Give `SystemUpdateService` an injected `TimeProvider`, mirroring the existing
  `AttachmentService` two-constructor pattern.
- Replace all 13 `DateTimeOffset.UtcNow` reads in the file with
  `_time.GetUtcNow()`, verifying each is a "now" read (not a run-elapsed
  duration).
- Keep production wiring identical in shape: the service stays an
  `ISingletonService` and resolves the real clock (`TimeProvider.System`) with
  no new DI registration.
- Thread `FakeTimeProvider` through the `SystemUpdateServiceSpecs` helpers
  (`CreateService` / `CreateConsistencyService`) and add/convert at least one
  spec that drives a time-dependent transition via `FakeTimeProvider.Advance`
  with no real waiting.
- Zero `DateTimeOffset.UtcNow` residue in the service file (grep gate).

**Non-Goals:**
- No changes to `ProcessSystemUpdateCommandRunner` or `HttpSystemReadinessProbe`
  internals.
- No sweep of `DateTimeOffset.UtcNow` in other files (`WorkflowRun.*.cs`,
  `*ProfileManager.cs`, etc.) — that is a separate, larger de-wall-clocking
  effort.
- No rewrite of unaffected `SystemUpdateServiceSpecs` cases; only helpers and
  time-sensitive cases change.
- No new time abstraction — only the framework `TimeProvider`.
- No observable behavior change to the update flow.

## Decisions

### D1: Mirror the `AttachmentService` two-constructor pattern

Add a `private readonly TimeProvider _time` field. The existing `public`
constructor defaults the clock to `TimeProvider.System`; the existing `internal`
test-facing constructor accepts an explicit `TimeProvider` instance (appended as
the last parameter to preserve positional call-site compatibility as much as
possible).

**Rationale:** This is the established in-repo convention (`AttachmentService`,
`WorkflowArtifactUploadService`). It makes the clock injectable for tests
without requiring a DI resolution of `TimeProvider` for this singleton — the
public constructor hard-codes `TimeProvider.System`, exactly as `AttachmentService`
does.

**Alternative considered:** Register `TimeProvider` as a DI dependency and take
it on a single constructor. Rejected: it diverges from the existing in-repo
pattern, adds a needless DI resolution for a singleton that already hard-codes
the system clock, and would force every test constructor call to pass the
clock even when irrelevant.

**Note on constructor visibility:** The spec refers to an "internal test-facing
constructor". `SystemUpdateService` *already* has an `internal` constructor (the
one accepting the `getSystemInfo` delegate used by `CreateService`); the public
constructor delegates to it. We add `TimeProvider` to **both** — `TimeProvider.System`
on the public one, the explicit instance on the internal one. (`AttachmentService`
itself uses two `public` constructors; the visibility choice here follows
`SystemUpdateService`'s existing structure, not `AttachmentService`'s.)

### D2: Two `private static` clock-reading helpers become instance methods

`CreateFailedTransition` (`:699`) and `ApplyTransitionLog` (`:721`) are
`private static` and read `DateTimeOffset.UtcNow` (`:708`, `:724`). To use
`_time.GetUtcNow()` they must either (a) lose `static` and become instance
methods, or (b) stay static and receive the timestamp as a parameter.

**Decision:** Drop `static` from both and read `_time.GetUtcNow()` inside them.

**Rationale:** Both are called only from instance methods (`:508`, `:509`,
`:670`, `:691`), so the call sites need no changes beyond removing the (absent)
class qualifier. `ApplyTransitionLog` already has a `DateTimeOffset? timestamp`
parameter whose fallback is the wall clock — that fallback becomes
`_time.GetUtcNow()`, preserving the existing "explicit timestamp wins, else now"
contract. These helpers were never pure (they read the wall clock), so losing
`static` imposes no real loss of testability or reasoning.

**Alternative considered:** Keep them `static` and compute the timestamp at each
caller, passing it in. Rejected: it spreads the "now" read across more call
sites and is more churn for no gain.

### D3: All 13 call sites are "now", none are "run elapsed"

Each of the 13 reads produces a transition/creation/log timestamp representing
the moment of the transition. None computes an elapsed duration by differencing
two `UtcNow` reads. Therefore every site is a direct, semantics-preserving
substitution of `DateTimeOffset.UtcNow` → `_time.GetUtcNow()`. The grep gate
(zero residual `DateTimeOffset.UtcNow` in the file) enforces completeness.

### D4: No DI / hosting change

`TimeProvider.System` is already registered
(`MohistServiceRegistration.cs:89`, `MohistSiloRegistration.cs:55`). Because the
public constructor hard-codes `TimeProvider.System` (D1), production resolves the
real clock with no DI change, and `SystemUpdateService` remains an
`ISingletonService` (Scrutor auto-registered). The existing registrations are
touched by this change only in that they continue to be unnecessary for this
service.

### D5: Spec helpers thread `FakeTimeProvider`

`CreateService` (`:1524`) and `CreateConsistencyService` (`:1565`) construct the
service via the `internal` constructor. They are updated to create a
`FakeTimeProvider` (or accept one) and pass it as the new trailing `TimeProvider`
argument. `Microsoft.Extensions.Time.Testing.FakeTimeProvider` is already a
project-wide dependency (used by `StagePopulationSnapshotServiceSpecs`,
`EpicReconciliationServiceSpecs`, integration fixtures, etc.).

At least one spec advances the fake clock (e.g. to a fixed offset) before
triggering a superseded-on-hash-drift or waiting-for-reconnect transition, then
asserts the persisted `UpdatedAt`/`CompletedAt`/log `At` equals the advanced
value — with no `Task.Delay` or wall-clock polling.

## Risks / Trade-offs

- [13 call sites in the core update flow, each needing a "now" vs "elapsed"
  semantic check] -> Mitigated by D3's verification and the zero-residue grep
  gate in acceptance criteria; risk is medium but bounded to a single file.
- [Issue body cites 16 sites / stale line numbers; actual file has 13] ->
  Mitigated by re-grepping the current file; the spec/proposal already
  reconciled to 13. Implementation follows the file, not the stale line list.
- [Two `static` helpers lose `static`] -> Minor; they were never pure (read the
  wall clock). No concurrent/purity-dependent callers exist.
- [Public constructor hard-codes `TimeProvider.System` instead of resolving from
  DI] -> Matches the established `AttachmentService` convention; trades a tiny
  amount of DI flexibility for consistency and fewer test call-site changes.
  Acceptable for a singleton with a single production clock.
- [FakeTimeProvider shared across helpers could leak state between specs] ->
  Mitigated by constructing a fresh `FakeTimeProvider` per `CreateService` call
  (the helpers already build fresh stores/configs per call).

## Migration Plan

This is a pure refactor with no data, schema, API, or observable-behavior
change — there is no data migration and no compatibility window.

**Deploy:**
1. Land the change on `master` behind the normal PR build (the `dotnet build`
   with `TreatWarningsAsErrors` acts as the compile/lint gate).
2. Run `dotnet test` filtered to `SystemUpdateServiceSpecs` plus the full server
   suite to confirm no behavior regression and no `DateTimeOffset.UtcNow`
   residue.

**Rollback:** Revert the commit. No runtime state depends on the change; the
service resumes reading `DateTimeOffset.UtcNow` directly.

## Open Questions

- None blocking. The only borderline call — whether to also de-wall-clock the
  `DateTimeOffset.UtcNow` inside the spec's own `CreateInfo` helper
  (`SystemUpdateServiceSpecs.cs:1557`, used to seed `RunningInfo` test data) — is
  explicitly out of scope: that is test-fixture input data, not a read by the
  system under test, and does not affect determinism of the service's
  transitions.
