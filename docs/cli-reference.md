# CLI Reference

`mo` is the Mohist command line for people and Agents. It is a small, stable
operational language. Commands express intent, help provides the exact syntax
for the current version, and Skills add decision rules that are specific to
Mohist.

## Product Contract

- **Predictable**: A resource and an action imply the command. Each capability
  has one canonical path.
- **Discoverable**: Start at `mo --help`, enter a command group, and then read
  leaf help. You do not need to read the complete manual first.
- **Context-efficient**: Each level provides only the information needed for
  the current decision. It does not repeat details that the next level owns.
- **Stable for automation**: Non-interactive calls never wait for input.
  Structured output contains only requested fields. Failure always has a
  nonzero exit code.
- **Recoverable errors**: Command syntax, argument, and field-selection errors
  return `2`. stderr shows usage for the nearest command. These errors do not
  contact the Server.
- **One surface for people and Agents**: Agents and people use the same
  commands, help, and errors. Mohist does not maintain a second Agent-only
  command surface.

## Usage

One operation usually needs only three levels of information:

```bash
mo --help
mo run --help
mo run retry --help
```

Each level answers one question:

- Root help: what capabilities exist? A task-grouped command index, one
  sentence for each group, and a few starting examples.
- Command-group help: which action applies? The resource boundary, action
  list, and commonly confused adjacent resources.
- Leaf help: how do I run this command correctly? Result, usage, arguments,
  state preconditions, JSON fields, and two or three examples.
- `mo help <topic>`: which rules are shared by multiple commands?
  Cross-cutting conventions such as `output`, `environment`, and `exit-codes`.
- Mohist Skill: what is the correct next action in this situation? Initial
  reads, recovery decisions, scenario Skill routing, and a small set of strict
  rules.

Help must not show internal service names, communication paths, source
locations, historical Issues, or migration aliases. It describes the current
product behavior of the command.

## Command Language

Commands have only two shapes:

```text literal
resource-command = mo <area> [<subarea>] <action> [target] [flags]
task-command     = mo <task> [target] [flags]
```

- `area` is the product object or task area that the user operates, such as
  `issue`, `run`, or `session`.
- `subarea` appears only when an object is owned by its area or has no clear
  meaning outside it, such as `issue comment`, `issue template`, or
  `routing rule`.
- `action` is a stable English verb such as `list`, `view`, or `retry`.
- `task` is a direct operation that does not need an artificial resource
  wrapper. The current tasks are `help`, `install`, `update`, and `info`.
- `target` uses the shortest stable identity when possible. `--project`
  expresses Project scope.
- A command may have at most one `subarea` level. Frequently operated resources
  that can be addressed independently use a root command, such as `session`.
- Each capability has one canonical path. Filters and convenient addressing use
  flags instead of duplicate synonymous commands.
- Root commands do not need to be nouns. `install`, `update`, and `info` express
  direct tasks more clearly than an artificial resource wrapper.

The target surface has one canonical verb set. Resource reads use `view`, not
parallel `show` or `get` commands. Resource changes use `edit`, not `update`.
Soft-delete recovery uses `restore`, not `unarchive`. `get`, `set`, and `unset`
are reserved for the key-value behaviors declared below. A resource does not
need all three commands for symmetry. User shell aliases are not part of the
`mo` contract.

### Verb Vocabulary

- `list`: returns a collection.
- `view`: returns the current state of one resource.
- `create`: creates an independent resource with a stable identity.
- `edit`: changes properties of an existing resource.
- `delete`: deletes permanently; the resource stops existing.
- `add` / `remove`: join or leave a collection without deleting the linked
  object.
- `archive` / `restore`: soft delete and recovery; preserves identity and
  history.
- `get` / `set` / `unset`: key-value configuration; provides only the
  key-value behavior defined for that resource, without adding commands for
  symmetry.
- Domain actions such as `start`, `approve`, and `retry` use Mohist
  state-transition language directly.

`retry`, `rerun`, `pause`, and `stop` are not synonyms:

- `retry` retries the current failed Task or Check. This manual retry restores
  the full automatic recovery budget.
- `rerun` executes the complete Run again from the start.
  `--from-stage <stage>` invalidates and executes only that Stage and all later
  results. Both forms apply only to a non-terminal Run.
- `pause` interrupts current advancement but preserves a recovery path.
  `resume` continues the same Run.
- `stop` ends the Run permanently. Completed and stopped Runs cannot retry,
  rerun, or resume. Starting Issue work again creates a new WorkflowRun.

### Flag Conventions

Flag vocabulary is unique across the command surface. One term must not have
two meanings:

- Collection size always uses `--limit`. `service logs --lines` is the industry
  convention for a log tail and is the only exception.
- Resource reference flags are unique. Repositories always use `--repo`, and
  Projects always use `--project`. Synonyms must not exist in parallel.
- Argument definitions declare mutual exclusion, and leaf help must show it.
- Short flags use an allowlist. Only globally unique flags with a clear industry
  convention are permitted: `-l` for `--label`, `-p` for `--priority`, `-b` for
  `--body`, `-m` for `--message`, `-y` for `--yes`, `-f` for `--follow`, `-n`
  for `--lines`, and `-v` for `--verbose`. Leaf help must render them. A new
  short flag outside this allowlist must not be added.
- Default Project references, including the default repository and default
  Workflow Profile, are Project properties. The `project` area owns
  `project repo set-default` and `project workflow set-default`.

## Command Map

Root help groups commands by user task. It must not show one long, flat list.

- Work: `project`, `repo`, `workspace`, `issue`, `epic`, `label` manage Project
  space, execution environments, work items, and organization relationships.
- Automation: `workflow`, `run`, `agent`, `session`, `activity`, `routing`,
  `webhook` manage Workflow definitions and execution, Agent work,
  conversations, Project activity, and outbound webhook subscriptions.
- Operations: `runner`, `server`, `service`, `event`, `audit`, `github`,
  `slack`, `notification`, `otel`, `auth` manage execution resources, Server,
  integrations, event delivery, observability, and credentials.
- Tools: `help`, `skill`, `install`, `update`, `info` cover Help topics,
  Skills, installation, and maintenance.

### Core Command Groups

The command map includes only capabilities with defined product behavior.
Similar actions on adjacent resources are not enough reason to add a command.
A symmetric command without an independent scenario and semantics does not
enter the language.

- `project`: `list`, `view`, `create`, `use`, `delete`;
  `workflow set-default`; `workflow prompt get/set/clear/preview`;
  `repo set-default`; `variable list/get/set/unset`.
- `repo`: `list`, `create`, `edit`, `delete`.
- `workspace`: `list`, `view`, `create`, `close`; `repo add/remove`.
- `issue`: `list`, `view`, `create`, `edit`, `start`, `done`, `close`,
  `reopen`, `archive`, `restore`, `rebase`, `diff`, `commits`, `logs`,
  `events`; `comment create`; `prereq add/remove`; `template list/view`;
  `variable list/get/set/unset`; `watch list/add/remove`.
- `epic`: `list`, `view`, `create`, `edit`, `add`, `remove`, `start`, `pause`,
  `resume`, `done`, `close`, `reopen`.
- `label`: `list`, `create`, `edit`, `delete`.
- `workflow`: `list`, `view`, `create`, `edit`, `delete`, `validate`;
  `view --yaml` reads the raw Workflow Definition.
- `run`: `list`, `view`, `watch`, `approve`, `reject`, `retry`, `rerun`,
  `pause`, `resume`, `stop`; `view --yaml` reads the current Definition of the
  Profile bound to the Run; `feedback list/view`;
  `variable list/get/set/unset`, where `list/get --effective` reads merged
  values.
- `agent`: `list`, `view`, `start`, `create`, `edit`, `archive`, `restore`,
  `launch`, `spawn`, `install`; `job list/view`;
  `subscription list/create/edit/delete`; read-only `model list --runtime`.
- `session`: `list`, `tree`, `view`, `transcript`, `followup`, `compact`,
  `reset`, `stop`, `detach`; `schedule create/list/cancel`.
- `activity`: `list`.
- `routing`: `rule list/view/create/edit/archive/move`; `test` evaluates the
  complete routing table.

### Operations and Tool Command Groups

- `runner`: `list`, `view`, `status`, `revoke`.
- `audit`: `list`.
- `auth`: `login`, `logout`, `status`; `token list/create/revoke`.
- `server`: `status`, `health`, `info`, `logs`.
- `service`: `start`, `stop`, `restart`, `status`, `logs`, `uninstall`, with
  target `server`, `runner`, or `slack`.
- `event`: `tail`; `dead-letter list/redeliver`.
- `webhook`: `event-types`;
  `subscription list/view/create/edit/enable/disable/delete/rotate-secret/failures`.
- `github`: `connect`; `edit` for feed policy and approver list.
- `notification`: `setup`.
- `slack`: `setup`, `status`, `install-agent`; `list`, `view`, `claim-owner`,
  `edit`, `transfer-owner`, `enable`, `disable`, `remove-binding`,
  `permanent-delete`; `message send`; `deliveries`, `resend-delivery`,
  `clear-gap`, `reconcile-create`, `reconcile-delete`.
- `otel`: `status`, `query <sql>`, `traces`; `query` runs through the Server
  and supports `--json <fields>` field selection.
- `skill`: `list`, `view`, `install`, `path`, `sync`.
- `help`: views shared rules such as `output`, `environment`, and
  `exit-codes`.
- `install`: installs `server`, `runner`, or `slack`.
- `update`: updates all components or one selected component.
- `info`: shows the local CLI, installation source, and effective environment.

Only leaf help contains the complete argument list. Root help and this command
map do not copy every flag.

### Agent Subscriptions

Agent subscription commands address an Agent by name or ID and use the same
`--project` scope as other Agent commands. They provide one CLI view of Agent
event-response configuration:

```text literal
mo agent subscription list <agent> [--project <project>]
mo agent subscription create <agent> --name <name> --match <expression> \
  --response-prompt <prompt> [--continue] [--idempotency-key <key>] [--project <project>]
mo agent subscription edit <agent> <subscription-id> [--name <name>] [--match <expression>] \
  [--response-prompt <prompt>] [--continue <true|false>] [--project <project>]
mo agent subscription delete <agent> <subscription-id> [--project <project>]
```

Create and edit return one subscription resource; its stable fields include
`id`, `name`, `match`, `responsePrompt`, `continue`, and `status`.
Subscription status is `active` or `archived`. Delete confirmation uses
`status: deleted`. A list also returns the collection `state` — one of
`configured`, `empty`, `unconfigured`, `unavailable`, or `no_connection` —
plus the Agent's `agentStatus`, `readiness`, and `connection`. Leaf help lists
the complete selectable field catalog.

An empty list is a successful result and does not mean the Agent is missing. A
request failure remains a failure and returns a nonzero exit code.

## Issue

`mo issue` manages the work itself: content, organization relationships, target
repository, Profile selection, and the Issue lifecycle such as Draft, Done,
Closed, and Archived. `mo issue start <number>` means "start this work." On
success, it creates and binds one WorkflowRun.

`issue create` and `issue edit` use the same typed flags for planning metadata:
`--priority`, `--risk low|medium|high`, `--label`, `--repo`, `--parent`, and
`--workflow-profile`. These fields are structured data and do not need to appear
in body frontmatter.

`issue list` supports filters for `--stage`, `--priority`, `--label`, `--repo`,
`--parent`, and `--epic`. With `--json` field selection, one call can compare
multiple Issues without an `issue view` call for each one.

Approval, recovery, pause, and termination change the WorkflowRun and therefore
exist only under `mo run`. Issue comments, prerequisites, templates, Variables,
diff, and commits remain under `mo issue` because they describe or support the
work.

## Workflow Profile

`mo workflow` manages Project-scoped Workflow Profiles. A WorkflowRun binds a
Profile ID when it starts; it does not copy the Definition. Changing the Issue
selection or Project default affects only future Runs. Editing the bound Profile
affects an active Run. See
[Workflow Profile: Select a Profile](workflow-profiles.md#select-a-profile) for
the complete timing rules. `workflow edit --help` must state the effect on
active Runs.

The Profile collection belongs to Workflow. The Project default and explicit
Issue selection are references to a Profile and do not belong to the Profile.
A Project uses `mo project workflow set-default <profile>`. An Issue uses
`--workflow-profile <profile>` during `create` or `edit`.
`issue edit --inherit-workflow-profile` clears the explicit selection and
restores the Project default. It is mutually exclusive with
`--workflow-profile`. `mo workflow` does not copy these selection actions and
does not use a `default` or `none` sentinel that can conflict with a valid
Profile ID.

The division between `workflow` and `run` follows the GitHub CLI mental model of
a Workflow Definition and a Run execution, but it uses Mohist's own
WorkflowProfile and WorkflowRun semantics.

`mo workflow create` and `mo workflow edit` accept a Workflow Definition through
`--file <path>`. `--file -` reads from stdin.
`mo workflow validate --file <path>` validates optional Profile metadata and the
Workflow Definition locally. It checks the restricted Agent Action binding
syntax but does not resolve a Project override, inspect Action availability, or
connect to the Server.

## WorkflowRun

`mo run` views and controls one WorkflowRun. An Issue number can address the
current Run conveniently, but this does not duplicate control commands under
Issue.

A command that needs one Run accepts exactly one of these targets:

```bash
mo run retry wr_abc123
mo run retry --issue 42
```

The positional argument is a WorkflowRun ID. `--issue` resolves the current Run
bound to that Issue. Callers must provide exactly one. An Issue number is unique
within a Project and can be used with `--project`.

`mo run view --yaml` reads the current Definition of the Profile ID bound to the
Run and materializes Agent references with that Run's bound concrete Action. It
is not a historical Definition snapshot: editing other Profile structure can
change later Stages, so this view can also change during the Run. Changing the
Project's Profile Agent Action override does not change this view for an active
Run. The option is mutually exclusive with `--json`. The JSON view exposes the
nullable concrete `agentAction` bound to the Run and its derived `agentRuntime`
so clients can select the matching model catalog without rereading Profile YAML.

Project, Issue, and WorkflowRun each own one set of Variables. All three scopes
use the same `variable list/get/set/unset` key-value language.
`run variable list/get --effective` reads the merged Project -> Issue -> Run
result and accepts `--stage` for a selected Stage. When any scope changes, an
accepted attempt retains its input. A Task that has not started, a manual retry,
or a recovery continuation uses the latest Variables when it starts.

Variable commands use the same dotted key path as `${{ vars.* }}`.
`--stage <stage>` limits the operation to Stage Variables in that scope. Without
it, the command operates on Workflow-wide Variables. A positional value for
`set` is stored as a string. To store a boolean, number, object, or array, use
the mutually exclusive `--value-json <json>` instead. An Agent therefore does
not need to guess whether shell text is converted automatically:

```bash
mo project variable set agent.model provider-a/model-a
mo issue variable set 42 review.strict --value-json true --stage check
mo issue variable unset 42 review.strict --stage check
mo run variable get --issue 42 agent.model --effective --stage check
```

`list` and `get` read values stored by the selected scope. Only Run supports
`--effective` because the merged result is a read-only WorkflowRun fact. `set`
must receive exactly one of a positional value or `--value-json`.

## Workspace

A Workspace is a persistent Project execution environment. See
[Workspace](workspaces.md) for product semantics. The CLI creates, changes
repository membership for, and archives Workspaces with source `manual`. It can
read all Workspaces. Issue, Slack, Web, and CLI entry points create and archive
their default Workspaces automatically.

- `mo workspace list [--status active|archived] [--origin issue|slack|web|cli|manual]`
  lists Workspaces in the current Project.
- `mo workspace view <name>` reads the Origin, repository membership, bound
  Sessions, materialization location, and status.
- `mo workspace create <name> [--repo <repo>...]` creates a `manual` Workspace.
  Its name must be unique within the Project.
- `mo workspace repo add <name> <repo>` and
  `mo workspace repo remove <name> <repo>` change repository membership. Mohist
  rejects the change while the Workspace has an active bound Session.
- `mo workspace close <name>` archives the Workspace. When an active Session is
  bound, Mohist rejects the operation and gives the next action. A Workspace
  with source `issue` must not be closed manually; an Issue terminal state
  archives it automatically.
- `mo agent launch <agent> --workspace <name>` binds the new Session to an
  existing Workspace. Without `--workspace`, it binds the current Project's
  default Workspace and creates `cli-current` when necessary. JSON and human
  output return the actual Workspace, target, Session, and Turn identities.
  The returned `WorkspaceId` is the stable Workspace Name within the current
  Project, not another global Workspace entity ID. `origin` identifies the real
  entry point, such as `cli`, `web`, or an event-routing source.
- `mo session list --workspace <name>` lists Sessions bound to the Workspace.
  `mo session view` output contains Workspace fields.

```bash
mo workspace create payment-refactor --repo server --repo web
mo agent launch coder --workspace payment-refactor
mo agent launch reviewer --workspace payment-refactor
mo workspace view payment-refactor
mo workspace close payment-refactor
```

## Agent, AgentJob, and Session

An `agent` is a Mohist Agent with a stable identity in one Project. An AgentJob
is the first execution from one Agent launch. It answers whether that launch
finished and what result it produced. An AgentSession is an independently
addressable, continuing conversation. It contains messages, context, and usage.
The CLI must not use Session state as the AgentJob result. It must not interpret
Job completion as a closed conversation or a delivered user goal.

- `mo agent start --prompt <task>` is the default task-first path when you
  have work but do not need a preconfigured Agent. It creates the Agent and
  launches its first AgentJob, AgentSession, SessionInput, and AgentTurn in one
  accepted request. Use `--prompt-file` instead of `--prompt`, and optionally
  pass `--attach`, `--name`, `--runtime`, `--model`, `--variant`, `--issue`,
  `--epic`, `--repo`, and `--workspace`. A Project default execution
  configuration supplies omitted execution hints; without one, pass the
  execution hints explicitly. `--runtime` accepts `opencode` or `pi`, and
  `--model` uses `provider/model`. The command sends the CLI launch origin and
  returns the Agent, Job, Session, Input, Turn, Workspace, status, and canonical
  Session, transcript, Job, and observation URLs. In table mode, an omitted
  `--idempotency-key` is generated and printed before the request. Retry a lost
  response with that same key; accepted replays return the original identities
  and do not start another launch. Raw JSON mode prints the complete Server
  response; task-first field subsets are not a separate output contract.
- `mo agent launch <agent>` remains the definition-first path. It creates an
  AgentJob, AgentSession, first SessionInput, and first AgentTurn from an
  existing Agent profile. It returns stable Agent, Job, Session, Input, Turn,
  Workspace, and target identities, plus the canonical Session URL, transcript
  URL, and composite observation URL. `--workspace <name>` binds the new
  Session to an existing Workspace. Without it, the command binds the current
  Project default Workspace. See [Workspace](#workspace). The command accepts
  `--idempotency-key`. When omitted, it prints a generated key before the
  request. After a lost response, the caller must retry with that key.
- `mo agent create/edit` remains the deliberate definition-first configuration
  path. It configures an Agent with typed `--runtime`, `--model`,
  `--variant`, `--reasoning-effort`, `--skills`, and
  `--max-concurrent-runs` flags. `--avatar-file`
  supplies the avatar. Mutually exclusive `--instructions` or
  `--instructions-file` supplies Instructions. `--runtime` accepts only
  `opencode` or `pi`. `--skills` must contain at least one nonempty Skill name.
  An empty string does not clear Skills; `edit` must use `--clear-skills`
  explicitly. `--avatar-file` reads a UTF-8 avatar URL or data URI. The caller
  does not need to construct Agent config JSON. The `--agent-config`
  surface has no generic Agent-config JSON input.
  `edit` clears fields with `--clear-runtime`, `--clear-model`,
  `--clear-variant`, `--clear-reasoning-effort`, `--clear-avatar`,
  `--clear-skills`, and `--clear-max-concurrent-runs`. Set and clear options are
  mutually exclusive.
  `mo agent view` shows unified Readiness, configuration gaps, and current
  execution availability. The concurrency limit constrains launch and
  follow-up immediately but does not stop an execution that is already running.
- `mo agent install <name>` installs a built-in Agent preset, such as
  `supervisor`, which contains a supervising Agent and routing rules for
  Approval and failure. The operation is idempotent and does not overwrite
  existing content. It produces a normal Agent and RoutingRule.
- `mo agent job list <agent>` and `mo agent job view <job-id>` read work state
  and results.
- `mo agent model list --runtime <runtime>` reads the models available for Agent
  and Issue configuration. Runtime is a configuration dimension, not an
  independent command resource.
- `mo session list --agent <agent>` lists Sessions started by that Agent.
- `mo session list --issue <number>` lists Sessions created by that Issue's
  Workflow.
- `mo session list --run <run-id>` lists Sessions for that Run.
- `mo session list --workspace <name>` lists Sessions bound to that Workspace.
- `mo session schedule create <session-id> --at <time> --text <text> [--idempotency-key <key>]`
  schedules input for the Session. `--at` accepts only an absolute RFC 3339 time
  with a time-zone offset, and it must be later than the current time.
  `--idempotency-key` may be omitted. When omitted, the command prints a
  generated key before the request, as follow-up does. To deduplicate retries
  across requests, the caller must reuse the same key. A different key creates
  a new schedule. `mo session schedule list` lists the Session's schedules.
  `mo session schedule cancel <session-id> <schedule-id>` cancels a schedule
  that has not been delivered. Cancellation has no effect after delivery and
  does not affect the delivered input. See
  [Subagents and Session Trees](subagents.md) for the scheduled-input contract.
- Subsequent read, follow-up, compact, reset, and stop operations use
  the stable Session ID. Stop requires `--idempotency-key`. With `--turn-id`,
  it ends that one Turn: a queued Turn ends locally without contacting the
  Runtime, and an executing Turn is ended only after Runtime confirmation.
  Without `--turn-id`, it requests a durable cascade over the attached session
  subtree. Follow-up returns a new Input ID. It also returns a Turn ID when the
  Input has joined the current Turn or a new Turn; otherwise, read the Session
  later to find the assignment.

Source is only a filter and convenient lookup condition. It does not create
duplicate `mo issue session` and `mo agent session` capabilities.
`session stop` with `--turn-id` creates a durable operation over that one
frozen Turn; without it, the operation covers the subtree attached to the
selected root Session. It ends queued Turns locally and requests Runtime stop
only for executing Turns in that fixed scope; ended Turns are recorded
cancelled, and Sessions remain available for later continuation. Retry an
unconfirmed request with the same idempotency key to recover the same operation
instead of selecting the tree again. See
[Subagents and Session Trees](subagents.md#lifecycle).

## Slack

`mo slack` manages Slack integration: the binding between one Mohist Agent and
a bot identity in one Slack workspace, and installation of the workspace-level
Mohist App.

- `mo slack setup [--workspace-team <team-id>] [--configuration-token-file <path>] [--credentials-file <path>]`
  installs the workspace-level Mohist App in Slack and connects local Socket
  Mode. It creates or restores one workspace installation record, creates and
  configures the App, and guides the user through Slack installation and
  App-level token generation. On first installation, Configuration token
  validation determines the workspace. Use the flag when multiple workspaces
  are connected.
- `mo slack install-agent <agent> [--workspace-team <team-id>] [--credentials-file <path>]`
  installs an existing Mohist Agent in Slack. It creates or restores the Agent
  integration and dedicated Agent App. It guides App configuration,
  installation, identity and credential validation, connection startup, and
  Owner claim. The workspace flag may be omitted when only one workspace is
  connected. If the Agent already has an installation record in that workspace,
  the command continues it instead of creating a second App.
- `setup` and `install-agent` are idempotent, resumable guides. When a step must
  be completed in Slack, the command returns the installation link, exact page,
  and same continuation command. A rerun continues from confirmed steps and
  validates saved credentials again. Invalid credentials return the guide to
  the credential step. Supplying credentials again for a ready record rotates
  them. New credentials must still belong to the original workspace, App, and
  Bot. Non-interactive mode never waits for input. If a required file for the
  current step is missing, it exits nonzero and returns the continuation
  command.
- A Configuration Token file contains only
  `{ "configurationToken": "...", "configurationRefreshToken": "..." }`.
  A runtime credential file contains only
  `{ "botToken": "...", "appToken": "..." }`. Both must be regular,
  non-symlink files owned by the current user and readable and writable only by
  that user. Interactive mode uses hidden input. The command line does not
  accept token literals. The CLI reads only the fields needed for the current
  step. Mohist encrypts them after validation and never includes them in output,
  errors, JSON, or logs.
- `mo slack status` shows the current Mohist App, Agent integrations, local
  connection state, and one next action. Missing provisioning credentials point
  to `setup`. An incomplete Agent installation points to the same
  `install-agent` command.
- `mo slack message send --conversation <conversation-id> [--reply-to <thread-root-ts>] --text <body> [--image <url> | --file <path>]`
  lets an Agent speak in Slack. It renders Markdown body text as native Slack
  formatting, including bold, inline code, code blocks, lists, and quotes.
  Tables and headings degrade to readable plain text. `--image` embeds a public
  image URL. `--file` uploads a local image of at most 10 MB. The options are
  mutually exclusive. `--text -` reads the body from stdin and preserves line
  breaks. `--text` may be omitted when an image is attached.
- `mo slack claim-owner <id>` generates and displays a setup claim, expiration,
  and Slack direct-message step only after identity verification. A second call
  invalidates the old claim immediately.
- `mo slack view <id>` always returns setup progress, status, and one next
  action. The command process may exit; installation and claim do not depend on
  it remaining alive.
- `mo slack list <agent>` reads all integrations for that Agent.
  `view/claim-owner/edit/transfer-owner/enable/disable <id>` manages one
  integration. `edit --access-policy allowlist` atomically replaces non-Owner
  members through repeatable `--allow-member <slack-member-id>`. A new one-time
  claim transfers the Owner.
- `disable` is recoverable and preserves the Agent and all execution history.
  `remove-binding` removes the Connection but preserves Agent App management
  facts. `permanent-delete --yes` permanently deletes the Agent App when no
  active binding exists. The last two operations do not delete the Agent,
  AgentJob, or AgentSession.
- When the result of an external App write is unknown, `reconcile-create` or
  `reconcile-delete` checks the original operation without replaying it blindly.
  Delivery diagnostics use `deliveries`, `resend-delivery`, and `clear-gap`.
  These recovery commands do not replace `install-agent` as the normal
  installation path.

The integration owns only the external identity, permissions, and connection
state. `agent edit` still changes Agent configuration. The normal path for
mounting and changing an integration is a conversation with the Mohist App in
Slack. CLI and Web operate the same integration record. See [Slack](slack.md)
for the complete product semantics.

## GitHub

`mo github connect owner/repo [--feed-mode start|backlog] [--approver <login> ...]`
connects a GitHub repository to the current Project. It matches a registered
repository by address, creates the connection and an inbound signing secret,
and then prints the GitHub configuration checklist: webhook address, content
type, secret, and event subscriptions. After configuration, GitHub actions such
as applying a label, closing an Issue, submitting a pull request review, or
completing a check suite enter Mohist event routing in real time. Output
describes GitHub App or PAT identity configuration; this version does not
require it.

See [GitHub](github.md) for the complete product semantics.

## Activity, Event, and Local Services

`activity` is a Project-scoped, read-only activity record. It answers what
recently happened to an Issue, WorkflowRun, or AgentSession. `event tail` is a
real-time Event envelope stream that starts when the subscription is created.
`event dead-letter` is a delivery recovery operation. These three capabilities
do not share read semantics and must not be combined into one command with a
mode or source flag.

`runner` represents only Server-registered execution resources and their
presence, capacity, and state. `server` represents only the connected Mohist
Server application. Use `mo service <action> <server|runner|slack>` to start,
stop, or read logs from a managed local process. `slack` is the optional
`mohist-slack` integration service, not an integration resource managed by
`mo slack`. Therefore, `server logs` returns application logs, while
`service logs server` returns local service-manager logs. A `--source` flag must
not switch between these behaviors.

The CLI does not provide a generic root `config`. Each resource manages its own
Project Variables, Prompts, Agent configuration, and other product settings.
Local installation or service settings gain typed commands only for a defined
product scenario. The CLI must not expose an arbitrary key-value passthrough.

## Project Scope

Project-scoped commands use one resolution sequence:

1. Use the Project from an explicit `--project <name-or-id>`.
2. Otherwise, use the Project selected by the current directory or local
   configuration.
3. If resolution is not unique, fail and explain how to pass `--project` or
   select the current Project.

The command surface has only `--project`, not a parallel `--project-id`. The
same argument resolves a Project name or ID.

## Input and Interaction

- Short text uses `--body`, `--message`, or another argument declared by the
  command.
- Long text and structured values use a file flag with the same name as the
  short-text flag, such as `--body-file`, `--prompt-file`, `--text-file`, or
  `--stage-models-file`. Pass `-` to read from stdin.
- Complete documents such as a Workflow Definition use `--file <path>`. Pass
  `-` to read from stdin.
- Files and stdin have only two channels: `--<name>-file` and `--file`. The
  `@<file>` form must not be accepted.
- In a TTY, some install, setup, and create commands may prompt when optional
  input is missing.
- Outside a TTY, commands never prompt. Missing required input fails
  immediately with an executable hint.
- `MOHIST_PROMPT_DISABLED=1` disables prompts in every environment so Agents,
  scripts, and CI get deterministic behavior.
- Interactive use confirms permanent deletion and unrecoverable control
  actions. Automation explicitly confirms with `--yes` when leaf help declares
  it.

There is no universal `--dry-run`. Only a command that can produce a complete,
truthful preview declares preview support.

## Output

Default output is for people. A list is a compact table, one resource is a
concise detail view, and a successful state change is one line.

Commands that return resources support field selection:

```bash
mo issue list --json number,title,status
mo run view --issue 42 --json id,status,currentStage
```

- `--json <fields>` returns only requested fields. Field order has no semantic
  effect.
- There is no generic `-o` or `--output`. A command has only the default human
  view and explicit field selection as normal output paths.
- One resource is one JSON object. A collection is one JSON array. Mohist does
  not add an `{ ok, data, error }` wrapper.
- Bare `--json` lists selectable fields for the command and exits. An Agent does
  not need to guess field names. Field discovery occurs before other argument
  validation, so required arguments are not needed first.
- JSON fields are part of the command contract. Leaf help lists fields supported
  by the current version.
- Each resource has one field catalog. `list`, `view`, and mutations that return
  that resource share field names and semantics. The catalog contains all
  user-visible fields owned by that resource. For example, Issue contains
  `number`, `title`, `status`, `stage`, `priority`, `risk`, `labels`,
  `repository`, `prereq`, `epic`, `workflowRunId`, `createdAt`, and `updatedAt`.
  It must not contain placeholder fields that the resource does not own.
- A mutation that returns a resource also accepts `--json` with the same fields
  as its corresponding `view`.
- Continuous events and logs use NDJSON with one JSON object per line. An
  unbounded stream is not wrapped in an array.
- Normal results use stdout only. Errors, hints, confirmations, and progress use
  stderr only.
- Human output may improve presentation. Scripts and Agents depend only on JSON
  or NDJSON.

The initial command surface does not include built-in `--jq`, `--template`, or
a generic YAML renderer. An Agent can request the smallest JSON field set and
use existing shell tools. The CLI grows only when repeated needs cannot be
solved through field selection.

`mo workflow view <profile> --yaml` is an explicit resource-specific view. A
Workflow Definition is itself a YAML artifact. `--yaml` is mutually exclusive
with `--json` and does not imply YAML output for other resources.

## Errors and Exit Codes

An error must let an Agent correct the next call directly and remain readable
to a person:

```text literal
error: issue 42 has no active workflow run [run_not_found]
hint: start it with `mo issue start 42`
```

- The first line identifies the failed object, cause, and stable error code.
- `hint:` appears only when a clear recovery action exists. It provides an
  executable command or missing argument.
- An argument error also shows relevant usage, not the complete root help.
- An unknown area or action is a usage error. It returns `2`, shows only the
  nearest relevant usage, and must not fall back to root help and exit `0`.
- A domain error from the service retains its specific cause. It must not become
  a generic "request failed" error.
- Output does not include a stack trace or internal communication detail by
  default.

Exit codes are small and stable:

- `0`: success.
- `1`: operation failure, disallowed state, or unavailable service.
- `2`: command or argument usage error.
- `130`: user interruption.

`--json` does not change errors to another envelope. The caller always uses the
exit code for success or failure and reads the same diagnostic from stderr.

## Common Invocations

```bash
# Read only the fields needed for the decision.
mo issue list --json number,title,status

# Start an Issue, then use its number to view the current Run.
mo issue start 42
mo run view --issue 42

# Retry the failure point, or execute again from the Build Stage.
mo run retry --issue 42
mo run rerun --issue 42 --from-stage build

# Find a Session, then read its transcript.
mo session list --issue 42 --json id,name,status
mo session transcript session_abc123

# Submit long content through stdin.
mo issue comment create 42 --body-file -

# Change Variables for the current Run. A later attempt uses the new value.
mo run variable set --issue 42 agent.model provider-a/model-a
mo run variable get --issue 42 agent.model --effective --stage check

# Distinguish a remote Runner resource from the local Runner service.
mo runner status
mo service status runner

# Validate a local Workflow Definition without connecting to the Server.
mo workflow validate --file workflow.yaml
```

## Role of the Skill

The Mohist Skill is a short decision guide, not a second CLI reference. It
contains only:

- Which current facts to read before acting on an existing Issue.
- When to use `retry`, `rerun`, `pause`, `stop`, or `reset`.
- When to enter an explore, create-Issue, or create-Epic scenario Skill.
- Mohist state constraints that generic CLI knowledge cannot infer.
- A final reminder to read current leaf help for exact flags and request only
  needed JSON fields.

The Skill must not repeat the complete command map, generic flags, output
formats, or installation details. After a CLI update, the Agent reads help
generated by the current binary instead of a stale copy.

## Implementation Gaps

- `agent restore` is not implemented.
- The `github` command group declares `connect` and `edit` for connection
  changes. The current implementation uses `update` instead of `edit`.

Implementation source: `packages/cli/`.
