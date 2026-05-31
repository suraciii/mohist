## Context

Post-update smoke validation is currently documented only implicitly. The proposal asks for a short rendered-context note so maintainers, reviewers, and agents have a shared local verification path after updating Mohist. The spec requires the change artifacts to include a concise note that mentions running `mo update`, checking `GET /api/health`, and opening `/issues`.

This change is documentation-only. It must not alter server APIs, web UI behavior, runner behavior, storage, dependencies, or the update command implementation.

## Goals / Non-Goals

**Goals:**

- Add a concise rendered-context documentation note for local post-update smoke validation.
- Ensure the note explicitly includes all required validation steps: `mo update`, `GET /api/health`, and `/issues`.
- Keep the artifact easy for agents and reviewers to discover during change review.

**Non-Goals:**

- Do not implement or change the `mo update` command.
- Do not add automated smoke tests or CI checks.
- Do not change health endpoint behavior, issue UI routing, server startup, or release automation.

## Decisions

1. Add the note as change artifact documentation rather than runtime code.

   Rationale: The requirement is to record expected rendered-context guidance, and the proposal explicitly limits impact to documentation/change artifact content.

   Alternatives considered: Add runtime checks to `mo update` or server startup. Rejected because that would introduce behavior changes outside the requested scope.

2. Keep the note short and checklist-oriented.

   Rationale: The note is intended for smoke validation context, not a full troubleshooting guide. A compact sentence or small checklist reduces maintenance cost and makes the required path obvious.

   Alternatives considered: Create a longer runbook with setup, failure diagnostics, and recovery steps. Rejected because the spec only requires the expected local validation path.

3. Use exact endpoint and route wording in the note.

   Rationale: The spec requires explicit mentions of `mo update`, `GET /api/health`, and `/issues`. Preserving these exact tokens makes review and automated text validation straightforward.

   Alternatives considered: Use looser wording such as "run the updater," "check health," or "open the issue list." Rejected because it could fail the requirement and be less actionable.

## Risks / Trade-offs

- [Risk] The note could become stale if the update command, health endpoint, or issue route changes. -> Mitigation: Keep the wording minimal and colocated with the change artifacts so future spec updates can adjust it cheaply.
- [Risk] A documentation-only note does not guarantee maintainers actually perform the smoke validation. -> Mitigation: Phrase it as the expected local post-update path and leave automated enforcement out of scope for this issue.
- [Risk] Over-documenting the path could duplicate future user docs. -> Mitigation: Limit this change to the required rendered-context note rather than a broad documentation expansion.

## Migration Plan

1. Add the rendered-context note in the appropriate issue-3 change artifact content.
2. Review the artifact to confirm it explicitly mentions `mo update`, `GET /api/health`, and `/issues`.
3. No deployment migration is required because no runtime behavior, schema, dependency, or API contract changes are made.

Rollback strategy: remove or revert the documentation note. No data cleanup or service rollback is required.

## Open Questions

- None. The proposal and spec provide the required note content and scope.
