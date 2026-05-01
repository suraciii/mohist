## Self-Review: Issue #120 — Commits-first Changes view

### Review Date: 2026-05-01

### Completeness

- All 4 capabilities from proposal have corresponding spec files: `changes-commits-first` (new), `http-api` (modified), `web-ui` (modified), `session-timeline-ui` (modified)
- All 7 proposal "What Changes" items covered by specs
- Edge cases covered: binary files, empty state, backward compatibility, merge commits, file list truncation at 5
- 3 tasks cover all specs: T-001 (diff API), T-002 (commits API), T-003 (frontend overhaul)

### Consistency

- Proposal Capabilities match spec directories one-to-one
- Design decisions D1-D5 align with specs and tasks
- Task acceptance criteria cover all requirements from all specs
- Naming consistent across artifacts

### Issues Found and Fixed

1. **`specs/session-timeline-ui/spec.md` — wrong stage names**: Referenced nonexistent stages (`implementing`, `waiting-review`, `waiting-design-review`) instead of actual Stage enum values. Fixed to use `explore`, `plan` — matching design D4 and the `Stage` enum in `types.ts`.

### Dependency Validation

- T-001: `dependsOn: []` (first backend task)
- T-002: `dependsOn: []` (independent backend task)
- T-003: `dependsOn: ["T-001", "T-002"]` (needs both API shapes ready)
- No cycles, all references valid, all priorities strictly increasing

### Verdict

All artifacts pass review after the one fix.
