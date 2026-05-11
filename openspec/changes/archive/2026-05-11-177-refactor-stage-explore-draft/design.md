## Context

The codebase still exposes `draft` and `explore` in the shared stage model even though the real issue pipeline has already moved to `backlog -> plan -> build -> check -> integrate -> done`. This creates a mismatch between core type definitions, workflow defaults, stage-order helpers, transition guards, and UI rendering.

This mismatch is not just cosmetic. Stage order and transition tables are reused by start, approval, recovery, resume, and rewind-adjacent logic. As long as deprecated stages remain in the canonical model, new stage-sensitive features can accidentally preserve behavior that no runner actually executes.

The change must preserve Explore as a separate product capability. Explore sessions, APIs, and UI remain valid, but they must no longer be represented through the pipeline `Stage` enum.

## Goals / Non-Goals

**Goals:**
- Establish a single canonical pipeline stage model shared by backend and frontend: `Backlog`, `Plan`, `Build`, `Check`, `Integrate`, `Done`.
- Make stage ordering and legality checks reflect the real runner pipeline so approval, recovery, resume, and future rewind logic operate on the same semantics.
- Remove `explore` from the default workflow definition so declared workflow stages match the stages that can actually run.
- Preserve issue creation and start behavior: new issues begin in `backlog`, then start into `plan`.
- Preserve existing Check and Integrate lifecycle behavior while removing any dependency on deprecated `draft` or `explore` transitions.
- Keep Explore functionality available through its existing domain model, routes, and storage rather than through pipeline stage state.

**Non-Goals:**
- Reworking the Explore feature, session lifecycle, or issue association model.
- Changing Integrate execution semantics beyond replacing deprecated stage references.
- Designing or implementing the full rewind feature from #176.
- Introducing a new generalized workflow DSL or stage abstraction.

## Decisions

### D1: Make the shared `Stage` enum represent only pipeline-visible stages

The canonical `Stage` enum should only contain stages that participate in issue pipeline progression and can be observed in ordering, transition, runner, and UI pipeline logic. That enum becomes:

- `Backlog`
- `Plan`
- `Build`
- `Check`
- `Integrate`
- `Done`

`Draft` and `Explore` are removed instead of being kept as deprecated aliases. The purpose of this change is to eliminate ambiguous semantics at the type level, not just hide old values in selected code paths. Keeping deprecated variants in the shared enum would continue to allow new code to compile against invalid pipeline states.

This decision also implies that any domain needing to express Explore state must use a dedicated type or existing explore-specific fields rather than the pipeline enum.

**Alternatives considered:**
- Keep `Draft` and `Explore` in the enum with comments or deprecation markers: rejected because transition and ordering helpers would still have to defensively handle invalid pipeline states.
- Map `Draft` to `Backlog` at runtime: rejected because it preserves silent ambiguity and complicates stage comparison logic.

### D2: Define one canonical stage order and reuse it everywhere pipeline order matters

`STAGE_ORDER` should be redefined as:

`backlog -> plan -> build -> check -> integrate -> done`

All helpers that compare stages, determine whether a stage is ahead/behind another stage, compute next stages, or validate rewind/recovery targets should use this same order. The implementation should not maintain separate backend and frontend notions of order beyond unavoidable mirrored constants; if duplication exists today, both copies must be updated together in this change.

`Backlog` belongs in the canonical order even if it is not always emitted as a runnable workflow task. It is the user-visible initial issue state and must be part of legality checks for start, resume, and stage comparisons.

**Alternatives considered:**
- Keep `backlog` outside `STAGE_ORDER` and treat it as a pre-pipeline pseudo-stage: rejected because it forces special cases in startability and rewind/recovery logic.
- Include Explore in order but mark it non-runnable: rejected because ordering is precisely what drives the incorrect mental model and invalid comparisons.

### D3: Transition rules should model only actual pipeline progression plus explicit recovery loops

`STAGE_TRANSITIONS` should be reduced to the transitions that correspond to current real behavior:

- `backlog -> plan`
- `plan -> build`
- `build -> check`
- `check -> integrate`
- `check -> build`
- `integrate -> done`
- optionally `integrate -> build` if current recovery semantics depend on rerunning from Build after Integrate

The key design rule is that recovery loops must be explicit and justified by current runtime behavior, not inherited from legacy stages. If code today uses transition tables to validate approval rejection, manual recovery, or stage restart targets, those paths should continue to work through `check -> build` and, if presently supported, `integrate -> build`.

The design intentionally does not add additional shortcuts such as `backlog -> build` or `plan -> check`, because they would weaken the model that #176 depends on.

**Alternatives considered:**
- Encode only straight-line transitions and special-case recovery elsewhere: rejected because it spreads stage legality across multiple mechanisms.
- Preserve legacy transitions for compatibility: rejected because they are the source of the inconsistency this change is meant to remove.

### D4: Treat `workflow.yaml` as runner-stage configuration, not the full issue lifecycle model

The built-in default workflow should stop declaring `explore`. Its stage list should align with runner-executed stages:

`plan -> build -> check -> integrate -> done`

`Backlog` should remain the issue's persisted starting state, but it does not need to be a runnable workflow stage in the default configuration if the workflow file is meant to describe executable tasks only. This keeps the distinction clear:

- issue lifecycle state includes `backlog`
- workflow stage configuration covers stages with runners/tasks

If the current loader or validation code assumes every enum stage must appear in `workflow.yaml`, that assumption should be relaxed. The source of truth for lifecycle state is the canonical stage model; the source of truth for runnable tasks is the workflow definition.

**Alternatives considered:**
- Add `backlog` to `DEFAULT_WORKFLOW.stages`: acceptable but not preferred unless the current workflow loader is simpler with a fully explicit list.
- Leave `explore` in defaults and ignore it at runtime: rejected because it preserves the exact misleading configuration surface this issue is removing.

### D5: Separate pipeline stage cleanup from Explore feature preservation

The implementation should search globally for `Stage.Draft`, `Stage.Explore`, raw string comparisons against `draft` and `explore`, and stage-derived UI lists. Each usage should be classified as one of two categories:

- pipeline logic that must be migrated to the backlog-first model
- Explore-domain logic that should stop depending on `Stage`

For Explore-domain logic, the preferred change is to express behavior in terms of dedicated Explore concepts already present in the codebase, such as explore session entities, route names, or view state. The design should avoid introducing a new compatibility layer unless a narrow boundary requires temporary normalization of persisted data.

This classification step is important because a blind removal of enum values can accidentally break non-pipeline Explore surfaces if they reused the shared stage enum for convenience.

**Alternatives considered:**
- Rename `Stage.Explore` to something non-pipeline and keep using it in Explore UI: rejected because the shared enum would still mix unrelated concepts.
- Introduce a second "pipeline stage" enum while keeping the old shared enum: rejected because it adds complexity when the real problem is overloading one type.

## Risks / Trade-offs

- [Persisted or fixture data may still contain `draft` or `explore`] → Audit test fixtures and any persisted issue-stage reads; if production persistence can contain old values, normalize at repository or deserialization boundaries instead of reintroducing deprecated enum members.
- [Frontend and backend can drift if they mirror stage constants separately] → Update both in the same change and add/adjust tests that assert the visible pipeline order.
- [Removing legacy transitions can break recovery paths that implicitly relied on them] → Verify existing approval/recovery tests and preserve only the specific non-linear transitions that correspond to current behavior.
- [Workflow validation may currently assume every stage in the enum is configurable] → Adjust validation to distinguish lifecycle states from runnable workflow stages.
- [Explore UI or API code may have opportunistically reused pipeline stage helpers] → Replace those references with Explore-specific state checks and add focused regression coverage for Explore surfaces.

## Migration Plan

1. Update the shared stage definitions and canonical ordering constants to remove `Draft` and `Explore`, and add `Backlog` to the ordered pipeline model.
2. Update backend transition tables and all stage legality checks used by issue start, approval, resume, recovery, and related services.
3. Update the default workflow configuration and any workflow validation so `explore` is no longer declared as a runnable stage.
4. Update frontend stage enums, stage-order helpers, and pipeline presentation logic to reflect the backlog-first pipeline while preserving dedicated Explore screens.
5. Sweep tests and fixtures for deprecated stage values, replacing pipeline assertions with `backlog -> plan -> build -> check -> integrate -> done` semantics.
6. Run backend and frontend test suites covering workflow progression, approval/recovery, workflow loading, and Explore surfaces.

Rollback strategy:

- If the change causes regressions before release, revert the refactor as one unit rather than partially restoring `draft`/`explore` in selected files.
- If persisted data compatibility becomes the only blocker, restore behavior through a narrow read-time normalization layer for old values instead of re-expanding the canonical enum.

## Open Questions

- Does any non-test persisted issue data in active environments still store `draft` as a stage value, or has all creation flow already been migrated to `backlog`?
- Is `integrate -> build` required by current recovery behavior, or can Integrate remain strictly forward-only to `done`?
