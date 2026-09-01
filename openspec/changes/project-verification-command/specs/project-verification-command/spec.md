# Project verification command specification

## Requirement: first-class Project configuration

A Project MUST own exactly one deterministic verification command as a dedicated property, not as a Project/Issue/Run Variable. New Project creation MUST reject a missing command. Existing Projects with no command MUST remain readable but MUST fail before work starts with code `project-verification-config-missing` and an actionable instruction to configure `mo project workflow verification set` or the Project Settings Workflows section.

The command MUST be non-whitespace, NUL-free, no larger than 4096 UTF-8 bytes, and preserved exactly after validation. A dedicated replace-only API MUST return the updated Project. Clear/unset is not supported.

## Requirement: immutable startup fact

When a WorkflowRun binds, the selected Profile MUST read the current Project command once and freeze it into `BoundWorkflowStart` and `WorkflowRun`. Binding replay MUST compare the command and reject conflicting startup facts. Project edits after binding MUST affect only future WorkflowRuns.

## Requirement: generic verification task

The built-in `mohist/local` and `mohist/github-pr` Profiles MUST execute one ordinary `core/script` verification task in `REPOS/${{ repository.name }}`. Its `run` MUST resolve from `workflow.verification.command` in the bound Run snapshot. It MUST have a 900000 ms timeout. Exit zero MUST pass; non-zero or timeout MUST be represented by normal task failure and explicit Builder recovery, which retries the same frozen task.

GitHub required checks MUST remain a separate remote gate. No built-in task may consume `vars.ci.verify` for new Runs.

## Requirement: recovery integrity

Workflow recovery MUST preserve source-attempt attribution and duplicate-chain fencing for ordinary tasks. A same-definition self-retry MUST be reconstructed from the persisted source Task so a Runner report cannot redefine the command or action. Recovery helper task declarations remain subject to the Workflow-owned contract.

## Requirement: lane semantics removal

New production status, scheduling, and persistence behavior MUST NOT depend on Mohist-specific verification lane IDs or lane outcomes. Existing bound definitions MUST remain immutable and executable as ordinary tasks. A one-way idempotent state cleanup MAY remove obsolete persisted lane fields but MUST NOT rewrite bound definitions. Active no-snapshot runs MUST be drained or stopped operationally before deployment; the service MUST NOT silently rebind them to current Profile content.

## Requirement: configuration surfaces

CLI MUST provide the primary set/view acceptance surface using Project resolution and body input conventions. Web Project Settings MUST provide a complete fallback editor using the same Server API and invalidate Project queries after mutation. Generic Variable commands MUST remain available for unrelated variables but MUST not configure built-in verification.
