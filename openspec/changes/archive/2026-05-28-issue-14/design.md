## Context

The proposal describes a documentation-only change for users who have just run `mo update` and need a quick smoke test before starting or resuming Mohist workflow work. The current troubleshooting guide already covers stage-specific failures, Web UI issues, coder agent configuration, recovery commands, and general help surfaces, but it does not provide a concise post-update health check path.

The OpenSpec `specs/` directory for this change is empty, so there are no spec-level product behavior requirements to implement. The affected surface is limited to `docs/TROUBLESHOOTING.md`. Runtime behavior, workflow stages, CLI/API contracts, storage, dependency versions, and configuration semantics must remain unchanged.

Stakeholders are Mohist users operating the local server/runner after an update, maintainers who need a low-maintenance troubleshooting guide, and support/debugging workflows that benefit from consistent first checks.

## Goals / Non-Goals

**Goals:**

- Add a concise post-`mo update` troubleshooting note to `docs/TROUBLESHOOTING.md`.
- Point users to existing health/status checks and log surfaces that can verify local server and runner readiness.
- Keep the guidance actionable without duplicating the full troubleshooting guide.
- Preserve existing documentation style and command conventions.

**Non-Goals:**

- No runtime, CLI, API, workflow, storage, provider, or configuration changes.
- No new health check command or runner diagnostic feature.
- No new OpenSpec capability/spec requirement, because the proposal explicitly states this is documentation-only.
- No restructuring of the troubleshooting guide beyond the minimal placement needed for discoverability.

## Decisions

### Decision 1: Implement as a Troubleshooting Guide Section

Add the post-update smoke test guidance directly to `docs/TROUBLESHOOTING.md` rather than creating a separate document.

Rationale: Users looking for recovery and readiness checks already land in the troubleshooting guide. Keeping the note in the same document avoids an additional navigation path and keeps the change small.

Alternatives considered:

- Create a new dedicated post-update document. This would improve future expansion room, but it is too much structure for a short smoke test and risks fragmentation.
- Add the note only to README or release notes. This would be more visible in some contexts, but the proposal specifically targets troubleshooting guidance and existing recovery surfaces.

### Decision 2: Place the Note Near General Operational Checks

Place the section where users can find it before or near issue-specific failure recovery, with commands that validate server status, logs, Web UI visibility, and basic workflow readiness.

Rationale: A post-update check is an environment-readiness workflow, not a Plan/Build/Check/Integrate failure. It should not be buried under a stage-specific heading.

Alternatives considered:

- Put it under `获取帮助`. This would make it a last-resort item, but the goal is proactive verification immediately after update.
- Put it under Web UI or Coder Agent problems. That would incorrectly frame post-update verification as a UI/provider-specific issue.

### Decision 3: Reference Existing Commands and Surfaces Only

Use existing commands and surfaces such as `mo server status`, `mo server logs`, Web UI access, issue logs, and existing validation commands instead of inventing new diagnostics.

Rationale: The proposal requires no CLI/API behavior change. Documentation should align with currently supported recovery paths and avoid promising capabilities that do not exist.

Alternatives considered:

- Document an ideal `mo doctor`-style flow. This may be useful in the future, but it would be misleading unless the command exists.
- Include internal implementation details about runner health. This could help maintainers, but it increases user-facing complexity and may become stale quickly.

## Risks / Trade-offs

- [Risk] The guide may reference a command or page whose behavior changes later -> Mitigation: Prefer stable, already documented commands and avoid over-specific output expectations.
- [Risk] A concise smoke test may not diagnose every post-update failure -> Mitigation: Link the checks to existing logs and stage-specific troubleshooting sections rather than trying to be exhaustive.
- [Risk] Adding another section can make the troubleshooting guide longer -> Mitigation: Keep the note short, procedural, and scoped to post-update readiness.
- [Risk] Empty specs mean no acceptance scenarios constrain wording -> Mitigation: Treat the proposal as the source of truth and keep the change documentation-only.

## Migration Plan

1. Update `docs/TROUBLESHOOTING.md` with a short post-`mo update` verification section.
2. Validate the documentation for readability, command formatting, and consistency with existing guide style.
3. No runtime deployment or data migration is required.
4. Rollback is a simple documentation revert of the added section if the guidance proves inaccurate or redundant.

## Open Questions

- Should Mohist eventually provide a single `mo doctor` or equivalent command for post-update checks?
- Should release/update workflows link directly to this troubleshooting section after `mo update` completes?
