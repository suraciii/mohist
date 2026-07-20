## Why

Parent/sub-issue progress and per-issue repository assignment now exist, but the Web UI does not make those relationships visible or manageable, forcing operators to reconstruct a composite issue's state and repository placement elsewhere. The board, detail view, and creation flow need to expose these capabilities now so multi-repository work can be understood and created from the primary operational interface.

## What Changes

- Show each issue's target repository on board cards and issue details, and add a repository filter to the board; single-repository projects retain a low-friction default experience.
- Identify parent issues on board cards with `X/Y done` progress and an attention indicator when any child is blocked.
- Replace workflow-oriented content on a parent issue's detail page with overall progress, blocked count, and a navigable list of every child issue including status and target repository.
- Add a navigable parent-issue backlink to child issue details.
- Extend New Issue so users can select a declared target repository and an eligible parent issue, with understandable feedback when the requested repository or parent relationship violates issue constraints.
- Add the read data needed by the Web UI to present child issue identity, title, status, health, and repository without changing composite-issue lifecycle or repository-binding rules.
- Do not add relationship drag-and-drop or a cross-repository diff aggregation view.

## Capabilities

- `issue-board-composite-repository`: Board-card presentation for parent progress, blocked-child attention, and target repository, plus repository-based board filtering across desktop and mobile layouts.
- `composite-issue-detail`: Parent detail progress and navigable child listing, suppression of workflow panels for parents, and navigable parent backlinks for child issues.
- `issue-creation-assignment`: New Issue selection and submission of a target repository and eligible parent issue, including clear validation failures for invalid assignments.

## Impact

- **Web** (`packages/web`): issue models and API clients; kanban card, query state, filter controls, and responsive board behavior; issue detail reading flow and relationship metadata; New Issue form and assignment pickers.
- **Server API/read models** (`packages/server`): additive issue read data or query surface for child rows and blocked-child progress required by board and detail views; existing create fields and domain validation remain authoritative.
- **Contracts**: issue list/detail responses and Web request typing gain composite/repository presentation data; no breaking API change is intended.
- **Systems and dependencies**: no new external dependency and no change to Issue lifecycle, composite advancement, Workflow execution, repository binding, Runner behavior, CLI behavior, or persistence authority.
