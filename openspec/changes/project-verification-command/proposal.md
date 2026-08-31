# Project verification command

## Why

Built-in Mohist Workflows currently encode a six-lane verification topology and expose verification through mutable Variables. That couples the server to one repository layout and allows a later Variable edit to affect an in-flight run. Verification policy belongs to the Project, while orchestration and recovery belong to the Workflow.

## Proposal

Add one first-class, non-secret `Project.verificationCommand` string. New Projects must provide it; migrated Projects with no value are rejected before work starts until configured. A dedicated Project API, CLI command, and Web settings control own this value. The command is validated as nonblank, NUL-free, and at most 4 KiB UTF-8, and is replace-only.

At WorkflowRun binding, the selected built-in Profile reads the Project command once and freezes it in `BoundWorkflowStart` and the persisted Run. Built-in local and GitHub PR Profiles execute one ordinary `core/script` task from `REPOS/${{ repository.name }}`, with a 900000 ms timeout and explicit script-failed/timeout Builder recovery. The command is dispatched through `workflow.verification.command`; it is never read from Project/Issue/Run Variables or live Project state.

Replace Mohist-specific six-lane scheduling, classification, and status semantics with ordinary task semantics. Generalize recovery source attribution, duplicate fencing, and persisted-source self-retry reconstruction so Runner follow-ups cannot redefine a Workflow-owned retry. Keep historical bound definitions immutable. Active no-snapshot runs must be drained or stopped operationally before deployment; no compatibility execution path is added. Retain only historical readability where ordinary persisted data permits it.

## Scope

- Project persistence, read/write API, CLI, Web fallback, and creation configuration.
- Bind-time command snapshot and dispatch template context.
- Built-in Profile task shape and generic recovery correctness.
- Removal of six-lane production semantics and one-way state cleanup of persisted lane fields.
- Product and design documentation.

Out of scope: custom Profile inheritance/clone, default Profile selection, bound-definition read bugs, prompt scanning, and direct custom-profile grain validation.
