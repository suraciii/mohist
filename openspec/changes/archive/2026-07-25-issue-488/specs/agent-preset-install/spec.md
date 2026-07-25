### Requirement: Preset name resolution

`mo agent install` SHALL accept exactly one positional preset-name argument and resolve it against the built-in preset catalog shipped with the CLI. The catalog SHALL contain `supervisor` and no other preset at this change. An unrecognized preset name SHALL be rejected with a non-zero exit code, and the error output SHALL list the available preset name(s) so the user can correct the call.

#### Scenario: Unknown preset name is rejected and available presets are listed
- **WHEN** the user runs `mo agent install acme` and `acme` is not in the built-in catalog
- **THEN** the command exits non-zero and prints an error naming `acme` together with the available preset name(s), including `supervisor`

#### Scenario: Known preset name proceeds to installation
- **WHEN** the user runs `mo agent install supervisor` in an active project
- **THEN** the command resolves the `supervisor` preset and proceeds to create its resources in that project

### Requirement: Supervisor preset authoritative content

The `supervisor` preset SHALL install exactly three resources with fixed names and fixed match expressions, using prompt text shipped as CLI resources. The Agent `supervisor` SHALL carry only the shipped identity instructions — no AgentConfig, no Skills, no MaxConcurrentRuns override. RoutingRule `supervisor-approval` SHALL match `event.type == "com.mohist.workflow.stage.approval-requested"` and RoutingRule `supervisor-failure` SHALL match `event.type == "com.mohist.workflow.run.failed"`. The shipped response-prompt text SHALL be stored verbatim, including `{{event.*}}` placeholders; install SHALL NOT pre-render, substitute, or strip those placeholders (they are runtime syntax consumed by routing dispatch).

#### Scenario: Created resources carry the fixed names and match expressions
- **WHEN** `supervisor` is installed into a project that has no existing `supervisor` agent or matching rules
- **THEN** the project ends up with an Agent named `supervisor`, a RoutingRule `supervisor-approval` whose match is `event.type == "com.mohist.workflow.stage.approval-requested"`, and a RoutingRule `supervisor-failure` whose match is `event.type == "com.mohist.workflow.run.failed"`

#### Scenario: Event placeholders are preserved verbatim in the response prompt
- **WHEN** the `supervisor-approval` and `supervisor-failure` rules are created by install
- **THEN** their stored `responsePrompt` contains the `{{event.*}}` tokens exactly as authored in the shipped resource, with no substitution or removal performed by install

#### Scenario: Supervisor agent carries no execution overrides
- **WHEN** the `supervisor` Agent is created by install
- **THEN** it is created with the shipped identity instructions and no AgentConfig, Skills, or MaxConcurrentRuns value

### Requirement: Idempotent installation by name

Install SHALL process resources in fixed order — Agent `supervisor`, then `supervisor-approval`, then `supervisor-failure` — and each step SHALL be idempotent by name: if a resource with that name already exists, install SHALL skip creating it and reuse the existing one. Install SHALL NOT overwrite or patch an existing Agent's instructions or an existing rule's match, prompt, or Continue flag, so user edits to an already-installed preset are preserved. The outcome of every step (created, or skipped because it exists) SHALL be reported individually in the command output.

#### Scenario: Re-running install skips already-installed resources and preserves user edits
- **WHEN** `supervisor` has already been installed, the user then edited the `supervisor` agent's instructions, and the user re-runs `mo agent install supervisor`
- **THEN** no resource is recreated or overwritten, the edited instructions are unchanged, and the output reports each of the agent and the two rules as skipped because it already exists

#### Scenario: Partially pre-existing resources are filled in without disturbing the rest
- **WHEN** a project already has an Agent named `supervisor` and a `supervisor-approval` rule but no `supervisor-failure` rule
- **THEN** install skips the agent and the approval rule (reports exists/skipped), leaves them unmodified, and creates only the missing `supervisor-failure` rule

### Requirement: Rules appended at the routing-table tail

The two routing rules SHALL be created by appending to the end of the project's routing table in install order (`supervisor-approval` immediately before `supervisor-failure`), without specifying a before/after anchor. Install SHALL NOT move, reorder, or archive any pre-existing rule. The installed rules SHALL NOT set Continue, so each yields an exclusive response. User-authored rules already in the table therefore remain positioned above the supervisor rules and keep precedence.

#### Scenario: Supervisor rules land at the tail in install order, below existing rules
- **WHEN** the project already has one or more routing rules and `supervisor` is installed
- **THEN** `supervisor-approval` and `supervisor-failure` occupy the last two positions in that order, the pre-existing rules keep their relative order and positions above them, and no pre-existing rule is moved or archived

#### Scenario: Installed rules are exclusive
- **WHEN** a supervisor routing rule is created by install
- **THEN** its Continue flag is unset so a match produces an exclusive response rather than continuing evaluation

### Requirement: Check-only preflight warnings

Install SHALL perform best-effort preflight checks that verify the supervisor Agent can actually operate in the target workspace. A failing check SHALL NOT block installation and SHALL NOT be repaired by install; it SHALL only be surfaced as a warning. The checks SHALL cover at least: (a) whether the default repository workspace exposes the `mohist` skill stub (`.agents/skills/mohist`) the Agent relies on to discover the `mo` command surface, and (b) whether the owner retains the default notifications (approval requests, failures, completion) that make the supervisor's stop-hand escalation visible to a human. Each warning SHALL name the specific problem and the remediation the user must run themselves.

#### Scenario: Missing mohist skill stub warns but does not block install
- **WHEN** the default repository workspace does not contain the `mohist` skill stub and the user runs `mo agent install supervisor`
- **THEN** the agent and both rules are still created and the output includes a warning naming the missing stub and directing the user to run `mo skills install --path <repo>` themselves

#### Scenario: Default notifications disabled warns but does not block install
- **WHEN** the owner has the default approval/failure/completion notifications turned off and the user runs `mo agent install supervisor`
- **THEN** installation completes and the output includes a warning that, with notifications off, the owner can only discover a stopped or failed supervisor by actively checking rather than being notified
