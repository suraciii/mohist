### Requirement: Sole Profile collection authority

`WorkflowProfileProvider` is the only authority for Profile membership, definition reads, and Profile CRUD. The legacy Project template table (`ProjectWorkflowTemplate`) and the Issue inline template column (`IssueWorkflowProfile.Template`) CRUD SHALL be removed once the existing migration is the sole path. No definition read or write SHALL fall back to the legacy template tables.

#### Scenario: built-in profile definition read

- **WHEN** the definition for a built-in `mohist/*` profile is requested
- **THEN** the provider SHALL return the in-binary authoritative definition

#### Scenario: custom profile definition read

- **WHEN** the definition for a Project custom profile is requested
- **THEN** the provider SHALL deserialize the persisted YAML source for that profile

#### Scenario: no legacy template fallback

- **WHEN** a definition is resolved for a run or issue selection
- **THEN** the resolver MUST obtain it from the Profile provider, and MUST NOT read from the legacy Project template table or the Issue inline template column

### Requirement: Profile enablement is owned by the Profile provider

Which system Profiles are enabled or disabled in a Project is a Profile-membership concern. The enable toggle (reading the disabled set and setting a Profile enabled/disabled) SHALL be owned by `IWorkflowProfileProvider`, the same authority that owns membership. It MUST NOT remain on a Profile/Variable/Prompt-mixed manager class. Disabled Profiles SHALL be excluded from the effective-selection fallback for runs that do not yet exist, preserving today's behavior.

#### Scenario: reading disabled profiles through the provider

- **WHEN** a consumer (run startup, Issue read model, metrics) needs the disabled Profile set for a Project
- **THEN** it SHALL read it from the Profile provider, not from a Profile/Variable/Prompt manager

#### Scenario: disabling the last profile is rejected

- **WHEN** a request disables a Profile such that no system Profile would remain enabled
- **THEN** the provider SHALL reject the write, preserving at least one enabled Profile

#### Scenario: disabled profile excluded from new-run fallback

- **WHEN** a run that does not yet exist resolves its effective Profile and the Project has disabled the default
- **THEN** a disabled Profile SHALL NOT be selected as the effective Profile

### Requirement: Bound profile resolution takes precedence

When a WorkflowRun has a bound Profile id in its state, resolving the run's Definition SHALL use that bound id through the Profile provider. If the bound profile has no current definition, resolution SHALL fail with a no-current-definition error rather than falling through to a different profile.

#### Scenario: bound profile resolves directly

- **WHEN** a run's state carries a bound Profile id that exists in the project collection
- **THEN** the resolved Definition SHALL be that profile's definition, without consulting issue selection or project default

#### Scenario: bound profile missing definition fails

- **WHEN** a run's state carries a bound Profile id with no current definition in the provider
- **THEN** resolution SHALL throw a no-current-definition error and SHALL NOT silently substitute another profile

### Requirement: Effective profile selection cascade

When a run has no bound profile, resolving the effective Profile SHALL apply the selection cascade in order: Issue's explicit Profile selection → Project default Profile → first enabled system Profile. Disabled profiles are excluded from the fallback only for runs that do not yet exist (run startup).

#### Scenario: issue selection wins over project default

- **WHEN** an Issue has an explicit Profile selection and the Project has a different default
- **THEN** the effective Profile SHALL be the Issue's selection

#### Scenario: project default used when issue has no selection

- **WHEN** an Issue has no explicit Profile selection and the Project has a default Profile
- **THEN** the effective Profile SHALL be the Project default

#### Scenario: no enabled profile fails

- **WHEN** no effective Profile can be resolved (no selection, no default, none enabled)
- **THEN** resolution SHALL throw a no-enabled-workflow-profile error

### Requirement: Stage hot-reload on stage entry

Resolving a stage's spec SHALL re-run the Definition resolution cascade on every call, so that Profile edits become visible to stages that enter after the edit. Already-entered stages and already-started tasks are not retroactively changed.

#### Scenario: profile edit visible to later stage

- **WHEN** a Profile definition is edited after a run has entered stage `plan`, and the run then enters stage `build`
- **THEN** the `build` stage spec SHALL reflect the edited Definition, while the already-entered `plan` stage is unchanged

### Requirement: Workflow structure for run startup

Loading the startup structure SHALL resolve the Profile (issue selection → project default) through the Profile provider and SHALL return the stage sequence with each stage's approval flag. It SHALL NOT require a fully-started run.

#### Scenario: startup structure from project default

- **WHEN** an issue with no explicit selection starts a workflow in a project whose default Profile has stages `plan` (approval) and `build` (no approval)
- **THEN** the startup structure SHALL return those two stages with their respective approval flags

### Requirement: Approval config from resolved definition

Loading the approval configuration for a run SHALL derive it from the resolved Definition (the feedback/approval task config). It SHALL NOT be sourced from a separate store or hard-coded outside the Definition.

#### Scenario: approval config read from resolved template

- **WHEN** the approval config is requested for a run whose resolved Definition declares an approval configuration
- **THEN** the returned approval config SHALL match the Definition's declaration

### Requirement: Definition resolution is decoupled from variables and prompts

The Definition resolver SHALL resolve only the Profile Definition (cascade, stages, structure, approval). It MUST NOT perform variable merging or prompt loading/rendering; those are separate concerns served by their own components.

#### Scenario: resolver has no variable or prompt responsibility

- **WHEN** the Definition resolver resolves a run's template, stage spec, or structure
- **THEN** it MUST NOT read or merge variables, and MUST NOT load or render prompts
