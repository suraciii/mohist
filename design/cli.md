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
  Its implementation is the static Go binary under
  [`packages/go/mohist-cli/`](../packages/go/mohist-cli/); the reference contract
  is independent of the implementation language.
- The Server read model is the field authority. `ResourceOutputCatalog` is only
  its CLI projection.
- Help is local and side-effect free. Remote operations begin only after local
  parsing and validation. Project-scoped operations resolve a Project; global
  Runner operations do not.
- A renderer cannot change request parameters, resource selection, or state
  transitions.
- Slack reply construction is one typed mapping boundary. It validates the
  complete Connection anchor before HTTP, maps text, image, and file bodies
  through the same carrier, and never forwards legacy field names.
- When `MOHIST_MANAGER_MODE=1`, the CLI uses the existing Runner-provided
  credential broker. The broker supplies the Manager credential and the CLI
  marks the request for the Manager route; ordinary execution keeps the
  Connection credential and route.
- Manager status and management requests use the broker's management lease.
  The CLI never stores or receives the broker-issued bearer values.

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
- `runner`: Server-global Runner identity, presence, control, admission, Runtime,
  capacity, active-owner, drain, and next-action facts.
- `server`: connected Mohist Server status, health, and application logs.
- `service`: local lifecycle for a Server, Runner, or optional `mohist-slack`.
  `mo service status runner` reads the local service manager and does not use
  the remote Runner status resource.
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
  Workspace. Origin and Provisioning rules live in
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
- Operations flags are admitted only on leaves that consume them. Runner listing
  uses Project scope and has no `--scope` switch. Service `--lines`/`--follow`
  belong to logs, and `--unit-dir` belongs to uninstall. Dead-letter filters
  belong to list; OpenTelemetry filters belong to traces. Unsupported flags
  fail locally with exit 2 and usage for the addressed leaf.
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

Short flags are a closed allowlist owned by the reference and canonicalized to
their long spelling at the flag token before dispatch. Values are read from the
original argument slice and are never rewritten, so a value that looks like a
short flag stays byte-exact. A short flag outside the allowlist, or an
allowlisted short flag on a leaf that does not declare its long flag, is a local
usage error with `ExitUsage=2` that reaches no request. No abbreviation or alias
layer is added.

`mo skill install` accepts the existing `--claude` and `--hermes` install
targets, which select the `.claude/skills` and Hermes home skills directories
the install path already implements. Incompatible combinations such as
`--hermes` with `--claude` or `--path` fail locally before any write. Both flags
are valid only on `install`; every other skill action rejects them as a usage
error.

Project-scoped commands use one inherited `--project <name-or-id>` option and one
resolver. Name, ID, and current Project are input forms of one ProjectRef.
Mutually exclusive inputs such as body and body-file, or target and selector,
fail locally and cannot overwrite one another. Help, list, view, and local
validation never trigger a setup prompt.

Text carriers (Issue body, Issue comment, Epic description, Project
verification command, Project Prompt body, Workflow Definition, Agent prompt,
and Session follow-up text) share one resolver and identical file/stdin
semantics. The resolver returns the exact, untrimmed content of the chosen
carrier together with an explicit error. A successful read returns the bytes
as-is, including all trailing newlines; an empty file or empty stdin is a
successful read whose value is `""`. A read failure is a local usage error
with `ExitUsage=2`: the diagnostic names the originating flag and either the
quoted path or `-`/`stdin`. The carrier is resolved before Project-state
lookup and before any HTTP call, including command-specific pre-flight reads
such as the Issue GET that `issue edit --label` uses for label diffing.
stdin is consumed at most once per command so the resolved value can be
reused for pre-flight and mutation paths. Empty-text validation lives on the
command that owns the field and runs only after a successful read; a missing
file cannot be misclassified as a successful empty value. Read failures and
blank-required rejections share `ExitUsage=2` but use distinct diagnostics
(`could not read --<flag>` versus `--<flag> must not be blank`) so callers
can distinguish an unreadable carrier from a successful read that the
command's empty-text contract rejected.

Issue create applies one command-local envelope rule on top of the carrier
bytes. A leading YAML frontmatter block that opens and closes with `---` is
partitioned from the stored body; `recommended_workflow` and `risk` fill the
Issue metadata and `recommended_workflow_reason` is consumed but never sent to
the Server. Explicit `--workflow-profile`, `--risk`, and `--no-workflow`
override the envelope and emit a stderr note when they differ. Malformed
metadata or a missing closing delimiter warns on stderr and sends the exact
original body with no partial metadata, while a body without a leading `---`
stays byte-exact and silent. The rule is CLI-local and does not change the
Server DTO, the packaged Skill, or any other body carrier.
Structured string-map flags such as `--stage-models` and
`--stage-model-variants` accept a JSON object whose members are all strings,
with a `-file` twin that reads a file or `-` for stdin. Inline and file forms
of one field are mutually exclusive, and at most one stdin carrier is allowed
per command. Every member is validated before any Project-state lookup or HTTP
request: malformed JSON, a non-object root, and `null`, boolean, number, array,
or object members are local `ExitUsage=2` errors that name the originating
flag. A nil-able map tracks presence so an explicit empty object is sent while
an absent flag is omitted.

`issue edit` composes one atomic patch. A single pre-flight GET reads the
current labels only to merge them; the validated `key=value` and `-key` tokens
are applied to that snapshot, and the merged labels plus every other explicit
field are emitted by one shared builder into exactly one PATCH. The pre-read
never mutates, so the edit cannot diverge field by field from a non-label edit.
An `issue edit` with no editable field is a local `ExitUsage=2` error: it would
otherwise serialize an empty patch. `--project` and a `--json` field selection
do not count as editable fields, and bare `--json`/`--help` remain local
discovery with no request.

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

### Managed Runtime Updates

`mo update server` and `mo update runner` replace installed managed services;
they are not source-tree build shortcuts. `mo update` updates Server and Runner
together from one captured source revision. CLI and optional Slack adapter
updates keep their independent `mo update cli` and `mo update slack`
transactions.

Managed runtime update requires an existing verified runtime target and refuses
to adopt a source-bound service unit implicitly. A missing or incomplete
`active.json` or `verified.json` fails before build or service mutation.

The repository root selected by `--repo-root`, or discovered from the current
directory, is the sole source authority. Before building, the CLI requires a
clean Git worktree, captures the exact commit and tree identities, and creates
a read-only archive plus a separate writable build workspace. Every requested
runtime is completely built and its payload is hashed before any installed
service changes. A source identity change or dirty worktree observed after
staging rejects the candidate.

Each installed runtime is an immutable release under the per-user Mohist
runtime root. Its identity is the versioned `RuntimeIdentity` v1 contract:
`schemaVersion = 1` plus nine always-emitted keys with one fixed meaning each,
identical for Server and Runner.

| Field | Type | Meaning |
| --- | --- | --- |
| `schemaVersion` | integer | Contract version. Exactly `1`; readers reject any other non-null value. |
| `component` | string | `server` or `runner`. The managed component this identity describes. |
| `sourceRevision` | non-empty string | Git commit the release was built from. The single source authority for the release. |
| `buildGitHash` | non-empty string | Git revision embedded in the built artifact. Equals `sourceRevision` for managed builds; kept distinct so a pinned or vendored build cannot masquerade as the source revision. |
| `treeHash` | non-empty string | Hash of the source tree the release was built from. |
| `artifactDigest` | non-empty string | Content digest of the release payload. Metadata files are excluded from the digest. |
| `releaseId` | non-empty string | Stable identifier of the immutable release. |
| `generation` | integer > 0 | Monotonic managed runtime generation for the update transaction. |
| `runnerId` | string | Runner identity. Non-empty for `runner`; present and empty for `server`. |

All nine keys are always emitted for both components, so the manifest key set
is stable and directly testable. Two identities are equal only when all nine
fields are byte-equal, with `runnerId` compared only for `runner`. Identities
written by one update must agree on `sourceRevision`, `treeHash`, and
`generation`; `component`, `artifactDigest`, `releaseId`, `buildGitHash`, and
`runnerId` are per-component. `version` and `builtAt` are documented display
metadata, not identity; they never satisfy a missing identity field.

The canonical identity is written to three release manifest files:

- `runtime-identity.json` — the file named by `MOHIST_RUNTIME_IDENTITY_PATH` /
  `MOHIST_RUNTIME_IDENTITY_FILE`.
- `release.json` — `{ "identity": <canonical>, ... }` plus source roots.
- `dist/build-info.json` (Runner only) — canonical identity plus `builtAt`.

A managed identity source that is present but fails the schema is malformed: an
unknown `schemaVersion`, a wrong JSON type, an invalid `component`, a
`generation` of zero or less, or any missing required field. Readers reject a
malformed source and never fall back to the source checkout identity. A missing
managed identity source yields a partial or null identity; that is tolerated
for external Runner registration but is a hard failure for managed deployment
preflight.

During the bounded migration only, a payload **without** `schemaVersion` is
legacy v0: `buildGitHash = buildGitHash ?? gitHash`, then
`sourceRevision = sourceRevision ?? buildGitHash ?? gitHash`, with the other
fields read when present. `gitHash` is accepted only as a read input at the
file and health boundary and is never emitted by a writer. A payload **with**
any other `schemaVersion` is malformed, not legacy. The v0 fallback is removed
in the release immediately after the release that first writes and verifies
`schemaVersion: 1` on every supported installation; at that point managed
preflight requires `schemaVersion: 1` on the active and verified release
manifest and the `gitHash` and v0 read paths are deleted.

The service uses an absolute entry point inside that release; success never
depends on the source checkout remaining present or writable.

Activation is one serialized transaction. Before changing a service, the CLI
captures its exact unit contents and active/enabled state. It changes only the
managed working directory, entry point, and runtime-identity reference while
retaining tracked entry-point arguments, service environment, credentials,
Runner identity, Server address, and other operator-owned directives. The
captured unit fragment and its effective target must match the verified runtime
before build. Systemd drop-ins are outside the update target and remain
untouched. Unit replacement is atomic.

After activation, Server must answer its health check with the candidate
identity. Runner must reconnect with the candidate source revision, artifact
digest, generation, and the same Runner identity. Process liveness alone is not
readiness. Before Runner activation, an identity-bound interrupt closes new
work admission; if existing work is still active, the update cancels its own
interrupt and exits without restarting the service. A failed activation or
verification restores the captured unit and service state, then verifies the
restored runtime. Success is emitted only after every requested component is
verified.

One per-user lock serializes managed service installs and runtime updates. Before
a new mutation, the CLI reads any pending transaction from its validated runtime
directory. A transaction durably marked verified is completed only after the
candidate runtimes and pointers are reverified. Every earlier or ambiguous state
is rolled back from the private unit and pointer snapshots, including cancelling
its Runner interrupt and verifying the restored runtimes. Recovery is idempotent;
if identity, snapshots, service state, or readiness cannot be proven, the pending
marker remains and the new mutation fails closed. Dry-run performs local
validation and reports the selected source and components but does not build,
write, stop, start, recover, or contact a service.

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
