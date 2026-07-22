## Why

Workflow Profile documents currently mix profile identity and description, executable Definition content, and default Variables in one YAML shape. That makes the Definition language ambiguous ahead of stricter validation and lets runtime behavior depend on configuration embedded in an asset that does not own it.

## What Changes

- Separate Workflow Profile metadata (`id`, `name`, and `description`) from its Workflow Definition; profile identity and display information no longer come from the Definition.
- Make a Workflow Definition a pure workflow-language document containing only `approval` and `stages`; remove Definition support for profile metadata, top-level `variables`, `defaults`, and `artifacts`, and for Variables embedded in stages.
- Move built-in Profile names and descriptions into the built-in catalog while retaining the existing default selection and executable stages.
- Remove embedded Definition Variables from profile parsing and per-stage live reads. Effective Variables continue to merge only Project, Issue, and WorkflowRun resources at their established precedence.
- Preserve the ability to run built-in workflows without explicit variable configuration: Issue Variables provide an empty `vars.agent`, and WorkflowRun Variables initialize `vars.archive` as an empty string. Stage and Run overrides, including archive updates consumed after retries or re-entry, remain effective.
- **BREAKING**: Direct Definition documents no longer accept or return `id`, `name`, `description`, `variables` at any level, `defaults`, or top-level `artifacts`.

## Capabilities

- `workflow-profile-assets`: Profile metadata and Definition are distinct assets, including profile persistence/loading, Project and Issue selection, and the built-in catalog's names, descriptions, and default choice.
- `workflow-definition-language`: A Definition contains only workflow behavior (`approval` and `stages`) and rejects or omits profile metadata, embedded Variables (including stage Variables), and non-language top-level fields across direct documents and built-in definitions.
- `workflow-variable-resolution`: Effective Variables are owned solely by Project, Issue, and WorkflowRun resources, with preserved precedence, stage overrides, live Run writes, and built-in `agent` and `archive` initialization.

## Impact

- **Server** (`packages/server`): changes Workflow Definition types and YAML serialization, profile catalog and template resolution, Project/Issue profile persistence and selection, and WorkflowRun variable initialization and live stage dispatch reads.
- **APIs and clients**: profile and template read/write surfaces must use the separated Profile/Definition representation; built-in profile display and selection remain stable for Web and CLI consumers.
- **Built-in assets**: `mohist/local` and `mohist/github-pr` definitions become pure Definitions, while their catalog metadata moves out of YAML.
- **Dependencies and tests**: no new dependencies. High-risk spec and unit coverage must cover pure Definition parsing/serialization, catalog metadata, profile selection, variable precedence, built-in defaults, and retry or stage re-entry observing updated Run Variables.
