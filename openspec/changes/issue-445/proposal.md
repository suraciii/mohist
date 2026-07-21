## Why

Workflow definitions cannot currently reveal an Action's complete effective input because built-in Actions may also read Run Variables and reject declared delivery values after comparing them with issue-derived context. Now that Action manifests and dispatch-time validation exist, `with` can become the single visible and enforceable input channel.

## What Changes

- **BREAKING** Make the rendered and validated `with` payload the only source of built-in Action inputs; custom profiles that relied on implicit Run Variable fallbacks must bind those values explicitly, including through `${{ vars.* }}` when Variables are intended.
- Declare delivery-related repository, branch, remote, pull request, and workspace values in the affected Action manifests, and fail missing required values with `invalid-input` before the Action runs.
- Remove delivery input fallbacks and issue-backed cross-check guards that compare declared values with repository or workspace values from Run Variables. Credentials remain the delivery security boundary.
- Update the bundled `mohist/local` and `mohist/github-pr` profiles so every affected task and check passes its required inputs explicitly and the existing delivery flows continue to work.
- Align Action documentation and regression coverage with the declared input contracts so no supported built-in Action depends on an undocumented Variable input.

## Capabilities

- `action-input-sourcing`: Built-in Actions consume only manifest-declared, validated `with` inputs, reject missing required inputs with `invalid-input`, and no longer apply implicit Variable fallbacks or issue-backed delivery cross-checks.
- `builtin-profile-input-bindings`: Bundled local and GitHub PR profiles explicitly bind all required Action inputs while preserving their current end-to-end workflow and delivery behavior.

## Impact

- Runner Action manifests and handlers under `packages/runner/src/actions/`, including delivery-context resolution, workspace preparation, Git/GitHub PR Actions, and any other built-in implementation that currently reads `context.variables` for Action input.
- Bundled workflow definitions and their Server/Runner regression tests under `packages/server/src/Mohist.Server/Workflow/Services/Profiles/` and the corresponding test suites.
- Action and Workflow documentation under `docs/actions/`, `docs/workflow-definition.md`, and `docs/workflow-profiles.md`.
- The Runner catalog will expose stricter required-input declarations, but no new API shape, dependency, template-expression syntax, or server-side dispatch policy is introduced.
