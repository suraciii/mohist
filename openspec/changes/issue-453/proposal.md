## Why

Issue owners cannot quickly determine whether a running, waiting, or blocked issue needs them because status and actions are duplicated across divergent page regions, while mobile hides essential decision context and secondary actions. Consolidating these signals now creates one trustworthy place to understand the issue and act without exposing implementation details or dead controls.

## What Changes

- Present the current issue and workflow status once, in the sticky status headline, including the current task without duplicate status or task pills and repeated stage metadata.
- Consolidate workflow controls, issue lifecycle controls, and agent delegation into one runtime decision surface, removing the separate rail Actions card and duplicate workflow approval controls.
- Make every action applicable to the current state reachable on narrow viewports, together with the rationale and next-action context available on desktop.
- Give every unavailable action a visible, accessible, plain-language reason and make disabled controls visually distinct from enabled controls, including Stop and Rebase.
- Remove the permanently disabled View transcript action and offer a working transcript action whenever an execution session exists.
- Continue deriving status and action availability from the existing runtime decision projection; no lifecycle actions or authorization rules are added.

## Capabilities

- `issue-decision-surface`: Defines the issue detail page's single status statement and trustworthy, responsive action surface, including decision context, action reachability, disabled explanations, approvals, lifecycle actions, agent delegation, and session transcript access.

## Impact

- Affects the Web issue detail page, runtime decision presentation, workflow view, branch controls, responsive action controls, issue lifecycle actions, and their page/component/browser specifications under `packages/web/`.
- Removes redundant status metadata and action entry points while preserving the existing runtime decision and server-authorized action contracts.
- No server API, persistence model, runner, CLI, or dependency changes are expected.
