## Why

Workflow Profiles are currently split between a system-template catalog, project template storage, and legacy configuration commands, so a Project cannot manage the workflow definitions it actually uses through one coherent `mo workflow` surface. Projects need stable, selectable Profile identities without freezing an active WorkflowRun to an obsolete Definition; this is now possible because #432 provides the Definition contract and #446 provides save-time Action-contract validation.

## What Changes

- Make WorkflowProfile a Project-scoped collection managed exclusively through `mo workflow list`, `view`, `create`, `edit`, and `delete`; Profile IDs are stable within the Project and may contain `/`.
- Include read-only built-in `mohist/*` Profiles alongside custom Profiles. Built-ins can be viewed and selected, but cannot be edited or deleted.
- Initialize every new Project's default Profile to the built-in `mohist/local`; migrated Projects whose legacy cascade selected the system fallback receive that same explicit default.
- Add `workflow view <profile> --yaml` to return the verbatim Definition source for new or edited Profiles and documented canonical YAML for migrated legacy Profiles; keep it mutually exclusive with JSON field selection rather than introducing general YAML output.
- Validate Profile saves through the authoritative Definition validator and the reported Action catalog, preserving their distinct error sources and avoiding CLI-side validation copies.
- Let `mo project workflow set-default <profile>` select a Profile from the current Project collection, and let issue create/edit explicitly select a Profile or return to inheriting the Project default with `--inherit-workflow-profile`.
- Bind a newly started WorkflowRun to its selected Profile ID, without storing a Definition snapshot. Later Stage initialization reads that Profile's current Definition; initialized Stages, accepted attempts, and historical results remain unchanged.
- Refuse to delete a Profile still referenced by the Project default, any Issue including a terminal Issue, or an active WorkflowRun, identifying every blocking reference relationship. `WorkflowProfileReferenceCoordinator` serializes Project-default writes, Run bindings, and deletion; `IssueRepositoryCoordinatorGrain` continues to serialize Issue creation, repository lifecycle, and Issue Profile selection. A nullable custom-Profile foreign-key backing column makes a concurrent Issue write or deletion resolve as either a committed blocker or a retryable not-found conflict, while built-ins are immutable.
- **BREAKING** Replace the legacy project-template/Profile command surface with the `mo workflow` collection and `mo project workflow set-default` command surface.

## Capabilities

- `workflow-profile-collection`: Project-scoped Profile list, view, create, edit, and delete behavior, including stable slash-capable IDs, built-in read-only Profiles in the same collection, verbatim or canonical Definition YAML reads, save-time Definition and Action-contract validation, and deletion protection with actionable reference reporting.
- `workflow-profile-selection`: Project default Profile selection and Issue explicit-versus-inherited Profile selection, including local mutual exclusion of `--workflow-profile` and `--inherit-workflow-profile` and selection only from the current Project collection.
- `workflow-profile-live-resolution`: WorkflowRun binding to a Profile ID and live Definition resolution for uninitialized future Stages, while preserving already initialized Stages, accepted attempts, and history against later Profile or selection changes.

## Impact

- **Server** (`packages/server`): Workflow Profile persistence, Project and Issue selection references, Profile APIs, reference checks, and `WorkflowProfileManager`/WorkflowRun resolution move from the template cascade to the Project collection contract.
- **CLI** (`packages/cli`): adds the `mo workflow` management verbs and `mo project workflow set-default`; issue create/edit gains Profile selection and inheritance flags; legacy template/Profile management commands are removed, with no aliases or redirects to the new owned surfaces.
- **Tests**: Server and CLI specs cover collection ownership, built-in immutability, validation hand-off, reference protection, selection precedence, and live Stage resolution.
- **Docs** (`docs`, `design`): workflow Profile ownership and CLI reference move from templates to the Project-scoped collection model; plan artifacts conform to `design/architecture.md`: `IssueRepositoryCoordinatorGrain` owns Issue selection, while `WorkflowProfileReferenceCoordinator` owns Project-default, Run-binding, and deletion operations.
- **Dependencies**: consumes the existing #432 Definition validator and #446 Action catalog validation; no new external dependency.
- **Risk**: high. The change crosses persistent Profile identity, Project/Issue/Run references, deletion safety, and the Definition source observed by active workflows.
