# CLI Design

`mo` is Mohist's operational language for people and Agents. It encodes domain intent as stable
commands and places the smallest accurate execution context at the current level.

This document defines domain ownership, design rationale, and implementation constraints for the
command language. [`docs/cli-reference.md`](../docs/cli-reference.md) is the sole authority for its
user-visible rules: syntax, verbs, flags, input channels, output, and error contracts. This document
does not repeat those rules. It retains only their construction rationale, implementation
mechanisms, and verification approach.

## Goals

- An Agent can discover and execute common operations accurately using only the Mohist Skill and
  `mo --help` from the current version.
- People and Agents use the same command surface, help, output, and errors. There is no parallel
  protocol.
- Commands can be derived from domain objects and actions. Each capability has one canonical path.
- Default output is readable by people, while structured output lets callers request only the
  fields needed for a decision.
- Non-interactive behavior is deterministic. Parameters, state, and failures provide the next step
  in one response.
- Help and the Skill stay small. Every sentence changes a choice, input, or recovery action.

`mo` does not:

- teach general shell, JSON, Git, or Agent reasoning in the Skill or help;
- put a complete product manual, internal interface, or implementation history in `--help`;
- invent resources without product meaning for the sake of syntactic regularity;
- become a generic pass-through client for arbitrary service interfaces;
- provide an Agent mode separate from commands used by people.

## Model

CLI navigation starts with user intent while respecting domain ownership. Top-level commands do
not mechanically mirror code modules or aggregates. An object merits a top-level area only when it
is independently addressable or users directly start operations from it.

| Area | Product or domain concept | Scope and boundary |
|---|---|---|
| `project` | Project | Root entry point for a Project Space; owns Prompts and Project Variables |
| `repo` | Repository | Named execution resource scoped to a Project; Issue only references it |
| `workspace` | Workspace | Persistent execution environment scoped to a Project; repository membership is its child resource |
| `issue` | Issue | Work item and its own lifecycle; `start` begins work |
| `epic` | Epic | Shares the bounded context with Issue but has an independent identity and lifecycle |
| `workflow` | WorkflowProfile | Project-scoped Workflow Definition entry point; does not represent one execution |
| `run` | WorkflowRun | One Workflow execution and its approval, recovery, and termination actions |
| `agent` | Mohist Agent | Reusable Agent scoped to a Project; AgentJob is a work child resource and Agent Connection is an external access child resource |
| `session` | AgentSession | Stable logical Session addressed uniformly regardless of origin |
| `activity` | AgentOps Activity feed | Project-scoped, cross-domain, read-only activity records |
| `runner` | Runner | Execution resource registered with Server, including presence and capacity |
| `server` | Mohist Server | Currently connected control-plane application, including status, health, and application logs |
| `service` | Managed Service | Locally managed Server, Runner, or optional `mohist-slack` process; not a domain context |
| `event` | Event delivery operations | Live envelope stream and dead-letter recovery; not a business-domain resource |
| `label` | Label definition | Project-scoped label vocabulary referenced by Issues and Epics |
| `routing` | Routing rule | Ordered Project-scoped event routing rules and dry-run evaluation |
| `notification` | Notification channel | Local outbound notification channel configuration; not a business-domain resource |
| `otel` | OpenTelemetry traces | Local trace storage and queries; an observability tool, not a business-domain resource |
| `skill` | Mohist Skill | Packaged Skill assets installed into a local Agent directory; not a business-domain resource |

Runtime adapters, Runtime Sessions, and the model catalog do not form top-level areas. Runtime is a
dimension of Agent configuration, Session binding, or Action selection. The model catalog supports
configuration through `agent model list --runtime`. A root-level `config` is not a product resource
either. Settings with different owners remain within explicit Project, Agent, Run, or local Service
boundaries and cannot be reaggregated through arbitrary key/value commands.

`run` is the CLI short name for WorkflowRun. `workflow` is the navigation name for
WorkflowProfile. The first sentence of group help must say that it manages Workflow Profiles so
users do not interpret it as WorkflowRun. A CLI short name introduces no new domain concept and
does not change the ownership defined by [`domain-analysis.md`](domain-analysis.md).

`workflow edit` modifies a Profile resource rather than configuration only for future Runs. The
product [Workflow Profile specification](../docs/workflow-profiles.md#select-a-profile) defines
Profile ID binding and when Definition and Variables changes take effect. CLI does not copy a
second set of lifecycle rules. `workflow edit --help` must state that the operation can affect an
active Run and link to `run --help` to distinguish a Profile from an execution.

### Canonical Ownership

Every domain intent has one canonical entry point. The area represents the main object or
relationship the user is expressing. The aggregate that persists a field is an implementation
detail and does not mechanically choose command navigation. A relationship across contexts must
show the Scope that owns the relationship in its path. It cannot masquerade as a property of the
referenced resource:

- `issue start` begins one work item and obtains its current WorkflowRun. It is an Issue action.
- `run approve/reject/retry/rerun/pause/resume/stop` changes WorkflowRun and is not duplicated under
  `issue`.
- `project workflow set-default` changes the Project's default Profile reference. `workflow`
  manages only the Profile collection. `issue create/edit --workflow-profile` changes an Issue's
  explicit selection and `issue edit --inherit-workflow-profile` clears it; the two flags are
  mutually exclusive. The default repository follows the same rule and changes through
  `project repo set-default`.
- `agent launch` starts a Mohist Agent and returns an AgentJob, AgentSession, first SessionInput, and
  first AgentTurn. Job arbitrates the initial launch execution and Session carries the ongoing
  conversation. Neither claims the other's state or result.
- `workspace create/list/view/close` manages Workspace entities, while
  `workspace repo add/remove` manages repository membership. Session has one explicit Workspace
  override entry point: `agent launch --workspace`. Without that override, the entry point resolves
  the Workspace from its Origin; CLI launch uses the Project's `cli-current` Workspace. This is a
  binding decision, not permission to pass a Runner directory. Origin resolution and
  materialization are authoritative in [`workspace.md`](workspace.md). Issue, Slack, and Web do not
  duplicate Workspace commands under `issue` or `session`. The Workspace field in
  `session list --workspace` and `session view` is read-only.
- `slack install-agent/list/view/claim-owner/edit/transfer-owner/enable/disable/remove-binding`
  manages the Slack access relationship of one Agent. `permanent-delete` permanently deletes its
  Agent App only when no active binding exists. These actions do not modify the Agent definition.
  `install-agent` is the domain action that installs an existing Agent into a Slack workspace and
  creates or recovers the Connection and Agent App. Access actions live directly under root-level
  `slack`; there is no generic connection subgroup.
- `session transcript/followup/compact/reset/cancel/stop` reads or changes AgentSession. `cancel`
  deterministically cancels one specified queued Turn. `stop` creates the durable cascade rooted at
  the selected Session; it is not a public single-Turn Runtime command. Membership and retry are
  authoritative in [`subagents.md#cascade-stop`](subagents.md#cascade-stop). Paths are not
  duplicated by Issue origin and Agent origin.
- `epic add/remove` expresses the user intent of Epic membership. Issue remains the sole write
  authority for current EpicNumber, and CLI does not expose cross-aggregate coordination.
- `--issue`, `--run`, and `--agent` are resolution or filtering conditions. They do not transfer
  ownership of an action.

A subarea represents a subordinate resource without an independent operation entry point, such as
`issue comment`, `project workflow prompt`, or `agent job`; a narrow catalog used by one area, such
as `issue template`, `routing rule`, or `agent model`; or a relationship under its owner's Scope,
such as `project workflow` or `project repo`. AgentSession has a stable ID, independent lifecycle,
and frequent direct operations, so it must remain the top-level `session` area.

## Command Language

[`docs/cli-reference.md`](../docs/cli-reference.md) is the sole authority for user-visible command
shape, verb vocabulary, flag vocabulary, and input channels. This section retains only the design
decisions used to construct a command:

- First identify domain intent and its sole entry point, then choose the shortest idiomatic command
  word. This is a constrained command DSL, not a requirement that every sentence have the same
  syntactic appearance.
- Areas use short, stable, usually singular English words such as `repo`, `run`, and `skill`. They
  do not mirror type names as `repository`, `workflow-run`, or `skills`.
- The same action belongs to the same action category in every area. Different semantics cannot
  share a word merely because their implementations are reused.
- Variants can become flags only when they share semantics, validation, and results. Different
  behavior remains a different action.

### Reference Baseline

`gh` is an interaction-design reference, not a compatibility target. `mo` adopts four proven
shapes: layered [root, group, and leaf help](https://cli.github.com/manual/gh), separate
[workflow](https://cli.github.com/manual/gh_workflow) and
[run](https://cli.github.com/manual/gh_run) ownership,
[field-selecting JSON](https://cli.github.com/manual/gh_help_formatting), and a lightweight
[Skill entry point](https://cli.github.com/manual/gh_skill).

`mo` does not copy `gh api`, built-in `--jq`, a template renderer, or an alias system. Those features
address GitHub's scope and compatibility requirements. Mohist adds similar capabilities only after
its own repeated use cases appear.

### Main Trade-Offs

| Option | Result | Decision |
|---|---|---|
| Allow only domain nouns at root and wrap every task as a resource | The syntax looks regular but creates artificial levels such as `component install` and `system info` | Reject. Keep direct `install`, `update`, and `info` task entry points |
| Maintain the full command table in the Skill or add a machine-readable command catalog | The initial read looks complete but duplicates the running version, becomes stale, and consumes substantial context | Reject. The Skill makes decisions and runtime help discovers syntax |
| Wrap every result in a generic `{ok,data,error}` envelope | The transport shape is uniform but adds fields without information and forces human output around the same model | Reject. Successful output is the resource itself; failures use exit status and stderr |
| Dump complete JSON by default and make the Agent filter it | Implementation is simple but every call pays in irrelevant fields and tokens | Reject. Default to a human view; automation explicitly selects JSON fields |
| Put both remote resource behavior and local process lifecycle under `runner` or `server` | There are fewer root commands, but one action changes target, permission, and failure semantics by area and machine state | Reject. Remote objects remain under `runner` and `server`; local lifecycle is uniformly `service <action> <target>` |
| Merge application logs and local service logs as `server logs --source` | The table is shorter, but connection, permission, stream, and failure results differ | Reject. Keep the distinct `server logs` and `service logs server` behaviors |
| Create `runtime list/view/model list` for execution backends | It looks symmetric but promotes an internal Runner adapter and configuration catalog into a nonexistent product resource | Reject. Runtime remains a configuration dimension; model discovery is `agent model list --runtime` |
| Keep root-level `config get/set` | Adding settings is easy but hides different Project, Agent, Workflow, and local Service owners | Reject. Add only typed settings under the resource that owns them |
| Choose the command surface for Slack access | A generic provider subgroup anticipated multiple future providers, but Slack is the only provider and the abstraction hides binding, permission, and lifecycle behind a generic noun | Use root-level `slack`: `setup` and `status` orchestrate workspace installation, while other actions manage Slack access resources. Keep no compatibility command. Reassess when a second provider exists |
| Name the action that adds an existing Agent to Slack | `setup-agent` conflicts with Agent Readiness setup and gives workspace-level `slack setup` a second subject; `create` incorrectly suggests creating an Agent or Connection | Use `slack install-agent <agent>`. The subject is an existing Agent and the result is a recoverable Slack installation. App creation, authorization, credentials, and binding remain internal steps |
| Keep `rotate-credentials` for credential rotation | It overlaps the resumable credential step in `install-agent`; once Connection no longer owns credentials, the command has no target | Reject. Rerun `setup` or `install-agent` and explicitly provide credentials again. One installation record has one path |
| Use `--agent-config <json>` as the long-term Agent configuration surface | Implementation is short but pushes schema, mutual exclusion, and errors onto users and Agents | Reject. Public CLI uses typed flags such as `--runtime`, `--model`, `--variant`, `--skills`, and `--avatar-file` |
| Add storage or database audit commands for table size, freelist, and row counts | They cover internal Server audits, but those are development operations. Architectural prohibitions constrain domain operations and do not justify expanding the command surface for a one-off audit | Reject. `otel` is the telemetry entry point; direct database reads for an internal Server audit are a valid developer path |
| Let `mo otel query` read local trace storage directly | Queries survive Server outage, but they bypass the API and query safeguards, couple the storage schema, have no query budget or size limit, and silently read local data when CLI points at a remote Server | Reject. `query` uses the Server query capability. Direct database access during Server failure is a developer path |
| Give `run view` only `--json` | There is one less source view, but callers cannot inspect the Definition that will govern later Stages | Reject. `run view --yaml` resolves the current Definition of the Profile ID bound to the Run and parallels `workflow view --yaml` as a resource-content view; it is not historical evidence |
| Put `set-default` under the resource being made default as `repo set-default` or `workflow set-default` | The path is shorter, but a default reference is Project state and two conventions are less derivable than one | Reject. Use `project repo set-default` and `project workflow set-default` consistently |

## Context Architecture

An Agent obtains context in this order:

```text diagram
Mohist Skill -> root/group help -> leaf help -> result or actionable error
```

Each layer makes one decision:

| Source | Sole responsibility | Must not contain |
|---|---|---|
| Mohist Skill | Select a scenario, first read, dangerous action, or nearby recovery action | Complete command tree, common flags, or implementation startup commands |
| Root help | Establish the product capability map | Leaf flags or complete state semantics |
| Group help | Explain the object boundary and choose an action | Reference manuals for other groups |
| Leaf help | Make one invocation executable without guessing | Source code, interface paths, or compatibility history |
| Result | Return facts needed by this operation | Complete snapshots of unrelated resources |
| Error | Explain this failure and a deterministic next step | Internal call chains or vague generic advice |

### Context Quality

Review all help, Skills, and error text on six dimensions:

| Dimension | Criterion |
|---|---|
| Authoritative | Derived from the current command model, output field definition, or domain state instead of guessed from a prose copy |
| Relevant | Answers only the choice required at the current layer |
| Sufficient | Omits no required parameter, precondition, destructive consequence, or recovery action |
| Concise | Removes sentences that do not change the next action and avoids repeating another authoritative layer |
| Executable | Examples parse under the current command tree and hints can be run directly or completed |
| Current | Help matches the binary version and the Skill does not freeze a volatile flag list |

## Syntax Authority

[`docs/cli-reference.md`](../docs/cli-reference.md) specifies the target product semantics and
command surface. Once implemented, the C# `System.CommandLine` tree is the sole executable syntax
authority for that version:

- `mo --help`, group help, and leaf help are generated from the command tree.
- One argument definition validates required values, mutual exclusion, defaults, and allowed values.
- One field definition drives selection, serialization, and leaf help for JSON fields in each
  resource result.
- Command examples in the Skill must pass parsing by the same command tree.
- Do not add a separate `mo command list/get` catalog. It would copy the command tree and expand the
  synchronization surface.

When the spec precedes implementation, only the product document Status records the gap. A change
that completes a migration must update the command tree, generated help, example tests, and gap
statement together. Two authorities must not persist.

## Help Contract

Every `--help` operation is local, fast, side-effect free, successful, and independent of Server.

### Root Help

Use this fixed order:

1. One product description.
2. `USAGE`.
3. Commands grouped under Work, Automation, Operations, and Tools, with a one-sentence result for
   each command.
4. Two or three examples covering discovery, reading, and recovery.
5. `mo help <topic>` and the documentation entry point.

Root help is an index. It neither displays every shared flag nor expands subcommands.

### Group Help

Use this fixed order:

1. One sentence identifying the area and its Scope.
2. `USAGE`.
3. An action list with a one-sentence result for every action.
4. `SEE ALSO` only for a genuine common ambiguity.

For example, `workflow --help` must state that it manages Workflow Profiles and link to
`run --help`. `run --help` must state that it manages WorkflowRun and can resolve one through a Run
ID or `--issue`.

### Leaf Help

Use this fixed order:

1. One precise result sentence in product and domain language.
2. One or more valid `USAGE` forms.
3. Arguments and options, including required values, defaults, mutual exclusion, and allowed values.
4. State preconditions, irreversible consequences, or distinctions from nearby actions only when
   they affect the choice.
5. `JSON FIELDS` for a resource result.
6. At most three independently executable `EXAMPLES`.
7. Necessary `SEE ALSO` entries.

Leaf help must not contain:

- an API route, HTTP method, DTO, grain, handler, class, or source path;
- an Issue number, migration stage, old command, or compatibility statement;
- generic shell instruction or common Agent-operating knowledge;
- promotional text without a behavioral constraint.

Content shared by three or more command groups and not self-explanatory from argument definitions
moves to `mo help output`, `mo help environment`, or `mo help exit-codes`. A rule used by only one
or two commands remains in leaf help to avoid a premature help topic abstraction.

## Skill Contract

The Mohist Skill uses progressive disclosure. The entry Skill contains only high-value decisions
and loads a sibling Skill when a scenario needs detail.

The entry Skill body has a fixed structure:

1. Scope: when to use the Mohist Skill.
2. First read: which current facts to read first for an existing Issue or Run.
3. Scenario routing: when to load explore, create-Issue, create-Epic, and other Skills.
4. Hard decisions: distinctions that generic CLI cannot derive, including `retry/rerun`,
   `pause/stop`, and `compact/reset`.
5. CLI handoff: use leaf help to confirm exact flags, then request only required fields through
   `--json`.

The entry Skill does not copy:

- a complete Issue or Epic lifecycle table;
- every read-only helper or common flag;
- Server, Runner, test, or source startup instructions;
- removed implementations or compatibility history;
- parameter details already expressed accurately by leaf help.

The Skill frontmatter description only determines triggering. Body examples must be few, canonical,
and parseable. Complex scenarios belong in sibling Skills or references. Do not add another file
hierarchy level unless scenario routing has a real branch.

## Input and Scope

[`docs/cli-reference.md`](../docs/cli-reference.md) is the sole authority for product rules around
Project resolution order and interaction. Implementation adds only three constraints:

- Every Project-scoped command reuses one inherited `--project <name-or-id>` option and the same
  resolver. Resolution must be unique. Name, ID, and current Project are input forms for the same
  ProjectRef, not distinct handler paths.
- Mutually exclusive inputs such as body and body-file or target and selector fail locally. One
  cannot silently overwrite the other.
- Help, list, view, and local validation never trigger a setup prompt.

## Output Contract

A command produces a semantic result before selecting a renderer. TTY detection and output format
cannot change the request, resource selection, or state transition.
[`docs/cli-reference.md`](../docs/cli-reference.md) is the sole authority for user-visible output
rules, including the human view, field selection with `--json`, field discovery with bare `--json`,
the NDJSON stream, and source views. Implementation adds:

- Validate each `--json` field locally before a remote operation. An unknown field returns the
  valid field list and a usage error without making a remote request.
- A default table retains only columns needed for scanning and choosing the next action.
- Color follows terminal capability and `NO_COLOR`; redirected stderr contains no control sequence.
- Adopt a new renderer only after at least three independent, repeated use cases that external
  tools cannot solve clearly.

## Field Contract

The Server read model, represented by API response DTOs, is the sole authority for fields of each
resource. The CLI field catalog, `ResourceOutputCatalog`, is a projection of the DTO rather than a
second fact. It must cover every JSON property of the DTO. Only two explicitly registered
differences are allowed:

- **Coverage is bidirectional.** If the catalog omits a DTO property, `--json` rejects a valid
  field and forces callers to bypass CLI for the API. If the catalog lists a property absent from
  the DTO, CLI silently renders a null column. Both are contract failures that must fail tests.
- **Differences are explicit.** One declaration table registers each difference as resource,
  field, and reason. Omit means a DTO property intentionally hidden from the catalog. Local means
  a catalog property absent from the DTO and synthesized locally by CLI, such as degraded output
  when Server is unavailable. A new DTO property or catalog field fails tests until registered.
  This converts the discipline to update CLI with a DTO into a mechanical check. Each declaration
  is a deliberate review decision rather than a fallback for omissions.
- **Mappings are explicit.** Contract comparison tests contain the `TableShape -> DTO type` map.
  Adding a resource without a mapping fails, preventing a new shape from silently escaping the
  contract.
- **Mechanism.** Tests reflect DTO types from the Server assembly, derive property names from JSON
  serialization naming policy and `[JsonPropertyName]`, and compare the set with the CLI field
  catalog for each resource. There is no runtime endpoint, shared assembly, or manual property list.

## Errors and Exit Status

[`docs/cli-reference.md`](../docs/cli-reference.md) is the sole authority for the user-visible error
format, stable error codes, and exit status. Implementation adds:

- A stable code uses lowercase snake_case and represents a product error on which a caller can
  branch. It does not represent an internal exception type.
- A transport error distinguishes definitely not submitted from submission result unknown. CLI
  does not automatically resend a state-changing request and provides a retry hint only when retry
  is confirmed safe.

## Managed Runtime Updates

`mo install server`, `mo install runner`, `mo update server`, and `mo update runner` share one
local deployment contract. It covers only managed Server and Runner installations; it does not
change CLI, Slack, authentication, or other components.

Each Server or Runner installation/update follows this fact chain:

```text
UpdateSource --repo-root -> InstalledArtifact <component>/<source-hash>
  -> ServiceTarget (absolute, stable) -> RuntimeIdentity <source-hash>
```

- `--repo-root` is the sole source authority for that invocation. The command resolves one source
  hash there and carries it through build, installation, and runtime verification. It never infers
  the source revision from the current directory, an existing unit `WorkingDirectory`, or another
  checkout.
- Build outputs are installed at the stable versioned path
  `~/.local/share/mohist/runtime/<component>/versions/<source-hash>/`. The Server publish output,
  plus Runner `dist` and required Node dependencies, belong to that installed version and do not
  read a Git worktree at runtime.
- Managed unit `WorkingDirectory` and `ExecStart` are absolute paths beneath the installed runtime
  root and point at the active version. They contain neither relative `packages/...` paths nor a
  `--repo-root` path. Updates switch installed versions; they never turn an arbitrary worktree into
  a service runtime directory.
- A Server installation carries a source-hash manifest; a Runner installation carries and verifies
  `dist/build-info.json`. Server reads identity from its installed manifest and Runner reports its
  build hash through its existing connection. Both are compared with the resolved source hash.
- The candidate version is fully built and installed, then activated, restarted, and verified. It
  becomes verified and may report success only after its runtime identity equals the expected source
  hash.
- Missing or mismatched identity, service startup failure, and readiness failure all fail the
  operation. When a previously verified version exists, CLI restores and restarts it; the error
  reports expected hash, actual hash when available, and the recovery action. A first installation
  with no verified version stops the unverified candidate and explicitly reports that nothing could
  be restored.

This boundary keeps source build facts, installed versions, service targets, and runtime reports
separate: a successful build or systemd restart alone does not constitute a successful update.

## Reliability Checks

CLI spec tests verify the public contract without a real Server, process, Git repository, network,
or wall clock:

- Every capability in the command tree has one canonical path, and a group has no synonymous
  action.
- `run variable` reads and writes Run Variables. Effective reads remain read-only. Tests prove that
  a later attempt uses an updated value while an accepted attempt keeps its own context snapshot.
- Project, Issue, and Run `variable` commands share dotted key paths and `--stage`. Positional
  `set <key> <value>` always stores a string. Boolean, number, object, and array values require the
  mutually exclusive `--value-json <json>` input. `--json` remains output field selection and is
  never overloaded as an input value.
- `agent launch` returns Job, Session, first Input, and Turn IDs. The AgentJob read model and
  AgentSession commands never claim each other's state or result. `session followup` returns the new
  Input ID and the Turn ID when known.
- Agent create and edit use typed flags for Profile, Runtime, Model, Variant, Skills, and concurrency
  limit. Commands expose no arbitrary Agent configuration JSON and display Readiness gaps provided
  by Server.
- `slack setup` and `slack install-agent` each have one canonical path. Both resume idempotently from
  durable progress, complete App creation and configuration automatically, and pause only for Slack
  installation confirmation or local credential input. A rerun revalidates stored credentials and
  returns to the credential step if they are invalid. Explicitly resupplying credentials to a ready
  record rotates them; there is no separate rotate command. `view` always displays current facts
  and the next action. An Owner is established through an explicit claim. The command surface does
  not copy Agent configuration, expose a token, or make users orchestrate underlying create and
  configure operations.
- `runner` and `server` commands do not call a local service manager. `service` commands neither
  depend on Project nor represent local process status as Runner resource state.
- Server and Runner install/update contract tests use one scoped fake source of truth for source
  hash, filesystem state, service target, and runtime identity. They assert versioned installation
  paths, absolute unit targets, identity verification, and rollback without real systemd,
  processes, network, or Git; sleeps, polling, and retries do not mask identity failures.
- `otel` queries use the Server query capability. CLI neither opens trace storage files directly
  nor resolves a local storage path.
- Activity list, Event tail, and dead-letter recovery preserve different results for a durable read
  model, live stream, and delivery recovery. A source or mode flag cannot merge them.
- The target command tree has no top-level `runtime` or generic `config`.
- Root, group, and leaf help satisfy their structures, and `--help` invokes no remote dependency.
- Command examples in documentation, Skills, and help parse through the real command tree.
- JSON fields declared by help exactly match the field selector, and a selected object contains no
  additional fields.
- stdout contains only results and stderr only diagnostics. JSON and NDJSON contain no ANSI or
  progress text.
- There is no prompt path for non-TTY input or when `MOHIST_PROMPT_DISABLED=1`.
- Mutually exclusive target and selector or body and body-file inputs fail locally with no remote
  call.
- Every error path exits nonzero and contains a stable code. A hint command also parses through the
  command tree.
- Help prose checks reject API routes, HTTP methods, grains, handlers, source paths, historical
  Issues, and migration aliases.
- Each resource has one field catalog. `list`, `view`, and mutations that return that resource share
  field names and semantics. The catalog covers every user-visible field in the read model and has
  no fallback default field set.
- Contract comparison tests pass between field catalogs and Server read model DTOs as defined under
  Field Contract: catalog equals DTO JSON property set minus explicit exemptions, with the reverse
  direction enforced as well.
- Bare `--json` field discovery occurs before other argument validation and makes no remote request.
- Short flags all belong to an allowlist, use globally unique letters, and render in the
  corresponding leaf help.
- Help option descriptions are non-empty and correctly spelled. `USAGE` headings and `--json`
  descriptions are consistent across all leaves, and mutual exclusion is visible.

Do not use a full-page snapshot as the only test. Structural tests lock required sections and
semantics. A small number of golden tests cover output that is genuinely part of the public layout
contract.

## Status

The Project, Issue, and Run Variables command slices are delivered. All three Scopes use
`variable list/get/set/unset`; a positional value always stores a string, explicit JSON types enter
only through `--value-json <json>`, and effective reads for Run remain read-only.

The main gaps between the current implementation and target design are recorded under
[`docs/cli-reference.md`](../docs/cli-reference.md#implementation-gaps). Delivery first establishes
shared contracts for field-selecting JSON, a consistent ProjectRef, stdout and stderr, exit status,
and non-interactive operation. Domain slices can proceed in parallel on that foundation. Each slice
delivers its own leaf help and contract tests so the command tree, help, and tests remain internally
consistent at every point. Do not publish one command surface and use a Skill to explain another.

The WorkflowProfile and Variables slices must build on the existing separation between Definition
and Variables, attempt context snapshots, and the authoritative validation chain.
