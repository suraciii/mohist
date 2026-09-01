# CLI Design

`mo` is Mohist's operational language for people and Agents. It expresses domain
intent as stable commands and uses the smallest accurate execution context at
each level.

[`docs/cli-reference.md`](../docs/cli-reference.md) is the sole authority for
user-visible syntax, verbs, flags, input channels, output, and error contracts.
This design records command ownership, construction rules, context boundaries,
and implementation constraints. It does not repeat the reference.

## Core Decisions

- People and Agents use the same command tree, help, output, and errors. There
  is no parallel Agent protocol.
- Each capability has one canonical command path. Navigation follows user intent
  and domain ownership, not code modules or aggregate names.
- Default output is readable by people. Callers select fields or a stream when
  they need structured output.
- Non-interactive behavior is deterministic. Parameters, state, and failures
  return the next action in one response.
- The Skill and help stay small. Every sentence changes a choice, input, or
  recovery action.
- The CLI is a constrained domain DSL. It is not a generic service client or a
  second product interface.
- `gh` is an interaction reference, not a compatibility target. Mohist adopts
  layered help, separate workflow and run ownership, field-selecting JSON, and a
  lightweight Skill entry point.

## System Boundary

The CLI translates user or Agent input into domain commands and renders semantic
results. It does not own domain state, create a second lifecycle, or infer state
from presentation.

- `docs/cli-reference.md` owns target product semantics and user-facing command
  behavior.
- The command tree is the executable syntax authority after implementation.
  Its implementation language migrates from C# to Go under
  [`decisions/cli-go.md`](decisions/cli-go.md); the reference contract is
  preserved across the migration.
- The Server read model is the field authority. `ResourceOutputCatalog` is only
  its CLI projection.
- Help is local and side-effect free. Remote operations begin only after local
  parsing, validation, and Project resolution.
- A renderer cannot change request parameters, resource selection, or state
  transitions.

## Command Model

Top-level commands represent independently addressable objects or operations
that users start directly:

- `project`: Project entry point. It owns Prompts, Project Variables, and default
  references.
- `repo`: Repository named by a Project and referenced by an Issue.
- `workspace`: persistent Project execution environment and repository members.
- `issue`: work item and its lifecycle. `start` begins work.
- `epic`: product goal with an independent identity and lifecycle.
- `workflow`: Workflow Profile collection. It does not represent one execution.
- `run`: one WorkflowRun and its Approval Point, recovery, and termination actions.
- `agent`: reusable Mohist Agent. AgentJob and Agent Connection are child
  resources.
- `session`: stable AgentSession, independent of origin.
- `activity`: read-only Project activity feed across domains.
- `runner`: Runner registration, presence, and capacity.
- `server`: connected Mohist Server status, health, and application logs.
- `service`: local lifecycle for a Server, Runner, or optional `mohist-slack`.
- `event`: event stream and dead-letter recovery operations.
- `label`: Project label vocabulary referenced by Issues and Epics.
- `routing`: ordered Project event-routing rules and dry-run evaluation.
- `notification`: local outbound notification-channel configuration.
- `otel`: local OpenTelemetry trace queries.
- `skill`: packaged Skill assets installed into a local Agent directory.

Runtime adapters, Runtime Sessions, and the model catalog are not product
resources. Runtime is a dimension of Agent configuration, Session binding, or
Action selection. Model discovery uses `agent model list --runtime`. There is
no root-level `config` area. Settings remain under their Project, Agent, Run,
or local Service owner.

`run` is the short CLI name for WorkflowRun. `workflow` is the navigation name
for Workflow Profile. Group help must state this distinction and link the two
areas. A short name introduces no new domain concept.

`workflow edit` changes a Profile for future WorkflowRuns. An active Run keeps
its bound Definition. `workflow edit --help` must state that fact and link to
`run --help`.

### Canonical Ownership

Every intent has one entry point. A cross-context relationship shows the Scope
that owns it instead of masquerading as a property of the referenced resource.

- `issue start` begins one work item and obtains its current WorkflowRun.
  `run approve`, `request-changes`, `retry`, `rerun`, `pause`, `resume`, and
  `stop` change that WorkflowRun.
- `project workflow set-default` changes the Project default Profile.
  `workflow` manages the Profile collection. `issue create/edit
  --workflow-profile` selects an Issue Profile, while
  `issue edit --inherit-workflow-profile` clears that selection. The two Issue
  flags are mutually exclusive. `project repo set-default` changes the default
  Repository.
- `agent launch` starts a Mohist Agent and returns an AgentJob, AgentSession,
  first SessionInput, and first AgentTurn. The Job owns initial launch
  arbitration. The Session owns the continuing conversation.
- `workspace create`, `list`, `view`, and `close` manage Workspaces.
  `workspace repo add/remove` manage repository membership. `agent launch
  --workspace` is the explicit Workspace override. Without it, the entry point
  resolves the Workspace from Origin; CLI launch uses the Project's `cli-current`
  Workspace. Origin and Materialization rules live in
  [`workspaces.md`](workspaces.md).
- `slack install-agent`, `list`, `view`, `claim-owner`, `edit`,
  `transfer-owner`, `enable`, `disable`, and `remove-binding` manage one
  Agent's Slack access relationship. `permanent-delete` deletes its Agent App
  only when no active binding exists. These actions do not edit the Agent
  definition. Installation creates or recovers the Connection and Agent App.
- `session transcript`, `followup`, `compact`, `reset`, and `stop` operate on
  AgentSession. `stop` is the only end-work operation. With `--turn-id` it ends
  one frozen Turn. Without it, it starts the durable Session-rooted cascade.
  Membership and retry rules are defined in
  [`subagents.md#cascade-stop`](subagents.md#cascade-stop).
- `epic add/remove` expresses membership intent. Issue remains the write
  authority for current EpicNumber.
- `--issue`, `--run`, and `--agent` resolve or filter resources. They never
  transfer ownership of an action.

A subarea serves a subordinate resource without an independent operation entry
point, a catalog used by one area, or a relationship under its owner's Scope.
Examples are `issue comment`, `project workflow`, `project repo`, `agent job`,
`issue template`, `routing rule`, and `agent model`. AgentSession remains a top-
level area because it has a stable ID, independent lifecycle, and direct
operations.

## Command Construction

- Identify domain intent and its sole entry point before choosing a short,
  idiomatic command word.
- Use stable, usually singular English words such as `repo`, `run`, and `skill`.
  Do not mirror type names such as `repository`, `workflow-run`, or `skills`.
- Keep one action category consistent across areas. Shared implementation does
  not make different semantics one action.
- Use a flag only when variants share semantics, validation, and results.
  Different behavior remains a separate action.

Rejected choices remain concise decisions:

- Do not add direct task roots merely to avoid resource wrappers. Artificial
  paths such as `component install` and `system info` are less coherent than
  direct `install`, `update`, and `info` actions.
- Do not copy a complete command table into the Skill or add a machine-readable
  catalog. Runtime help and the command tree are the authorities.
- Do not wrap every result in `{ok,data,error}`. Successful output is the
  resource; failures use exit status and stderr.
- Do not make complete JSON the default. Human output is the default and callers
  explicitly select fields.
- Keep remote resource behavior under `runner` or `server`, and local process
  lifecycle under `service <action> <target>`. Do not merge their semantics.
- Keep `server logs` separate from `service logs server` because their sources,
  permissions, and failure results differ.
- Do not create `runtime` or root-level model commands. Runtime remains a
  configuration dimension and model discovery uses `agent model list --runtime`.
- Do not create root-level `config get/set`. Add typed settings under their
  owners.
- Keep Slack access under root-level `slack`. `setup` and `status` operate the
  Workspace installation; other actions manage Slack access resources.
- Use `slack install-agent <agent>`. `setup-agent` conflicts with Agent Readiness
  setup and `create` falsely implies that the Agent or Connection is created.
- Do not expose `rotate-credentials`. Credential rotation belongs to the one
  resumable installation path.
- Do not use `--agent-config <json>` as the public configuration surface. Typed
  flags such as `--runtime`, `--model`, `--variant`, `--skills`, and
  `--avatar-file` keep validation discoverable.
- Do not add one-off database audit commands. `otel` is the telemetry entry
  point; direct database reads remain a developer path.
- `mo otel query` uses the Server query capability. It does not read local trace
  storage directly because that would bypass query safeguards and remote-server
  boundaries.
- `run view --yaml` returns the complete Definition bound to the Run. A JSON-only
  view would hide a required resource source.
- Keep default references under `project`: use `project repo set-default` and
  `project workflow set-default`, not resource-local variants.

## Context and Help

An Agent uses progressive disclosure:

1. Mohist Skill selects a scenario, first read, dangerous action, or recovery.
2. Root or group help identifies the available capability and object boundary.
3. Leaf help makes one invocation executable.
4. The result or actionable error returns the facts needed for the next choice.

Each layer omits the next layer's details. Skill does not copy the command tree.
Root help does not contain leaf flags. Group help does not contain other group
manuals. Leaf help does not contain source paths, implementation interfaces, or
compatibility history. Results omit unrelated resource snapshots. Errors omit
internal call chains and vague generic advice.

Review help, Skills, and errors for six properties: authoritative, relevant,
sufficient, concise, executable, and current. Text must derive from the command
model, output fields, or domain state. It must omit no required input,
precondition, destructive consequence, or recovery action.

### Help Contract

Every help operation is local, fast, side-effect free, successful, and
independent of Server.

Root help uses this order:

1. Product description.
2. `USAGE`.
3. Work, Automation, Operations, and Tools groups with one result sentence per
   command.
4. Two or three discovery, reading, and recovery examples.
5. `mo help <topic>` and the documentation entry point.

Group help uses this order:

1. Area and Scope sentence.
2. `USAGE`.
3. One result sentence for every action.
4. `SEE ALSO` only for a genuine common ambiguity.

Leaf help uses this order:

1. Result sentence in product and domain language.
2. Valid `USAGE` forms.
3. Arguments and options, including required values, defaults, exclusions, and
   allowed values.
4. Preconditions, irreversible consequences, or distinctions that affect the
   choice.
5. `JSON FIELDS` for a resource result.
6. At most three executable examples.
7. Necessary `SEE ALSO` entries.

Help must not expose API routes, HTTP methods, DTOs, grains, handlers, source
paths, Issue numbers, migration stages, old commands, compatibility claims,
generic shell instruction, or unconstrained promotion. Common content used by
three or more groups belongs in `mo help output`, `environment`, or
`exit-codes`. Content used by one or two commands stays local.

### Skill Contract

The entry Skill contains only high-value decisions:

1. Scope and trigger.
2. First facts to read for an existing Issue or Run.
3. Scenario routing to skills such as explore, create-Issue, and create-Epic.
4. Hard distinctions such as `retry/rerun`, `pause/stop`, and `compact/reset`.
5. CLI handoff to leaf help and `--json` for required fields.

It does not copy lifecycle tables, common flags, startup instructions, removed
implementations, compatibility history, or details already expressed by leaf
help. Examples are few, canonical, and parseable. Additional hierarchy requires
a real scenario branch.

## Execution Contracts

### Syntax and Input

The command tree is the executable syntax authority after implementation. One
argument definition validates required values, mutual exclusions, defaults, and
allowed values. One field definition drives field selection, serialization, and
leaf help. Skill examples must parse against the same tree.

Project-scoped commands use one inherited `--project <name-or-id>` option and one
resolver. Name, ID, and current Project are input forms of one ProjectRef.
Mutually exclusive inputs such as body and body-file, or target and selector,
fail locally and cannot overwrite one another. Help, list, view, and local
validation never trigger a setup prompt.

### Output and Fields

A command computes a semantic result before selecting a renderer. The reference
owns human output, `--json`, NDJSON, and source-view rules. Implementation must
validate each JSON field locally before remote work and return the valid list on
an unknown field. A default table keeps only scanning and next-action columns.
Color follows terminal capability and `NO_COLOR`; redirected stderr has no
control sequence. Add a renderer only after three independent repeated use
cases cannot be served clearly by existing tools.

The Server DTO read model is the field authority. The CLI catalog must cover
all DTO JSON properties and must not add an unregistered field. Contract tests
must fail in both directions when a property is missing or a field is extra.
One declaration table records deliberate differences as `resource`, `field`,
and `reason`: `omit` hides a DTO property, and `local` identifies a CLI-only
value such as degraded output while Server is unavailable. Each TableShape maps
to a DTO type in the comparison tests. Reflection derives JSON names from the
Server assembly and serialization attributes. No runtime endpoint, shared
assembly, or manual property list replaces this check.

### Errors and Exit Status

The reference owns error format, stable codes, and exit status. A stable code
uses lowercase snake_case and names a product error, not an exception type. A
transport error distinguishes definitely not submitted from unknown submission.
The CLI never resends a state-changing request automatically and gives a retry
hint only when retry is confirmed safe.

## Non-Goals

- General shell, JSON, Git, and Agent reasoning instruction in Skill or help.
- A complete product manual, internal interface, or implementation history in
  `--help`.
- Resources without product meaning, arbitrary service pass-through, or a
  separate Agent command mode.
- A second command catalog, generic result envelope, alias system, or root
  `config` resource.

## Status

Project, Issue, and Run Variables command slices are delivered. Each Scope uses
`variable list`, `get`, `set`, and `unset`. Positional values store strings;
explicit JSON types use `--value-json <json>`. Effective Run reads remain
read-only.

The remaining implementation gaps are listed in
[`docs/cli-reference.md`](../docs/cli-reference.md#implementation-gaps). The
shared foundation covers field-selecting JSON, ProjectRef, stdout and stderr,
exit status, and non-interactive operation. Each domain slice must deliver its
leaf help and contract tests with the command tree and update its gap statement
when complete.

The Workflow Profile and Variables slices build on Definition and Variables
separation, attempt-context snapshots, and the authoritative validation chain.
