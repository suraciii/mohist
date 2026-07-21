## Why

Workflow authors cannot correctly use most built-in Actions without reading implementation source: only `mohist/opencode` and `mohist/pi` have contract pages, while the Git, GitHub PR, `core/*`, and OpenSpec Actions either have no page or only an input list with no outputs, error codes, or usable examples. With Action manifests now stable and authoritative (post-#445 single-channel input contract), every built-in Action can be documented accurately from its manifest so that reading docs alone is sufficient to write a correct task.

## What Changes

- Bring every supported built-in Action's product contract page to the same three-part shape—inputs (name, required, default), output fields, and an error code catalog—mirroring the declarations in `packages/runner/src/actions/built-ins.ts`.
- Add contract coverage for the Actions that currently have none: `mohist/github-pr-checks`, `core/process`, `core/script`, `core/artifact-exists`, `core/marker`, `mohist/openspec-tasks`, `mohist/openspec-artifacts`, and `mohist/archive-change`.
- Expand the existing Git (`docs/actions/git.md`) and GitHub PR (`docs/actions/github-pr.md`) group pages beyond input lists to include outputs, error codes, and a directly usable example per Action.
- Update `docs/actions/README.md` to enumerate every supported built-in Action and link to its contract page, and remove the "OpenSpec 和 `core/*` 的独立产品契约页仍待补齐" gap footnote.
- Each example is a self-contained snippet that can be pasted directly into a Workflow definition.
- Preserve `mohist/pi`'s own 实装差距 note (Pi runtime gaps remain) and do not document tombstoned Actions (e.g. `mohist/acp-agent`).

## Capabilities

- `action-contract-pages`: Every supported built-in Action has a product contract page that mirrors its manifest—declared inputs (required, default), output fields, error code catalog, and a directly usable example. The overview page lists all supported built-in Actions and links to each contract page; no "remaining Actions have no contract page" gap footnote remains.

## Impact

- Documentation only: `docs/actions/README.md`, `docs/actions/git.md`, `docs/actions/github-pr.md`, plus new contract pages under `docs/actions/` covering the `core/*` and OpenSpec Actions.
- The authoritative source for content is the Runner manifest registry in `packages/runner/src/actions/built-ins.ts`; documentation mirrors it and does not change runtime behavior.
- No code, API, runner, server, CLI, Web, dependency, or storage changes; no migration.
- Does not alter `mohist/pi`'s existing runtime-gap footnote and does not introduce pages for removed Actions.
