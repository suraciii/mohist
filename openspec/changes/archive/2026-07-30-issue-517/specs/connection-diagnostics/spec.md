### Requirement: A unified diagnostic surfaces one most-important state, reason, and next action

The Connection diagnostic SHALL compute a single most-important state, its concrete reason, and exactly one next action from the independent facts: Setup progress, service liveness (heartbeat freshness), credential validity, Owner availability, identity drift, and Agent Readiness. The diagnostic SHALL always point to one actionable next step, not a list of undifferentiated raw fields, and SHALL NOT collapse the independent facts into a single covering `Connected` value.

#### Scenario: Setup incomplete surfaces the install next step
- **WHEN** a Connection has not completed setup (credentials missing, service offline, or verification failed)
- **THEN** the diagnostic surfaces the specific incomplete step as the most-important state with its reason and the single next action needed to advance

#### Scenario: Credentials invalid surfaces re-verification
- **WHEN** a Connection's stored credentials fail verification
- **THEN** the diagnostic surfaces credential failure as the most-important state with the concrete failure reason and credential rotation as the single next action

#### Scenario: Agent needs setup surfaces Agent configuration
- **WHEN** the Slack side is healthy and complete but the bound Agent lacks required runtime configuration
- **THEN** the diagnostic surfaces Agent Needs setup as the most-important state with Agent configuration as the next action, while the Connection health remains independently reported

#### Scenario: All clear surfaces a healthy operating state
- **WHEN** setup is complete, the service is online, credentials are valid, the Owner is available, there is no identity drift, and the Agent is ready
- **THEN** the diagnostic surfaces a healthy operating state with no blocking next action

### Requirement: Diagnostics distinguish six actionable states

The diagnostic SHALL distinguish at least these six states, each with a different and actionable next action: setup incomplete, service offline, credentials invalid, Owner unavailable, identity drift, and Agent Needs setup. No two distinct underlying conditions SHALL produce an indistinguishable diagnostic; the operator SHALL be able to tell which boundary is blocked from the summary alone.

#### Scenario: Service offline is distinct from credentials invalid
- **WHEN** the adapter heartbeat is stale and credentials were previously valid
- **THEN** the diagnostic surfaces service offline with a start-service next action, distinct from a credentials-invalid diagnostic

#### Scenario: Owner unavailable is distinct from setup incomplete
- **WHEN** setup is complete but the bound Owner has left the workspace
- **THEN** the diagnostic surfaces Owner unavailable with a transfer next action, distinct from a setup-incomplete diagnostic

### Requirement: Diagnostics do not introduce a covering Connected state

The diagnostic SHALL preserve the independence of Setup progress, Desired state, Connection health, and Agent Readiness. It SHALL NOT derive or present a single `Connected` or `Disconnected` value that hides which specific boundary is blocked. A Connection MAY be healthy while its Agent needs setup, and an Agent MAY be ready while the Slack side is temporarily unreachable.

#### Scenario: Healthy Connection with unconfigured Agent
- **WHEN** a Connection has completed setup and Slack is reachable but the bound Agent lacks runtime configuration
- **THEN** the diagnostic reports healthy setup and health while separately surfacing the Agent-needs-setup state, without a single covering status

### Requirement: Owner availability is probed and surfaced

The diagnostic SHALL probe whether the bound Owner is currently a regular, non-deactivated, non-guest member of the workspace and SHALL surface Owner unavailable when the Owner has left, been deactivated, or been downgraded. The probe SHALL use the bound workspace identity, not display-name matching.

#### Scenario: Active Owner reported as available
- **WHEN** the bound Owner is a current regular member of the workspace
- **THEN** the diagnostic reports the Owner as available

#### Scenario: Departed Owner reported as unavailable
- **WHEN** the bound Owner has been deactivated, has left, or has been downgraded to guest or restricted
- **THEN** the diagnostic reports Owner unavailable with a transfer action as the next step

### Requirement: Identity drift is detected and shown honestly without auto-rewrite

The diagnostic SHALL detect when the Slack-side App or Bot name or icon differs from the name or icon recorded on the Connection at the last verification, and when the Bot presentation name differs from the bound Agent's name. Drift SHALL be surfaced honestly as a diagnostic fact with the concrete differing values shown. Mohist SHALL NOT automatically modify the Slack App name, icon, or Bot profile, and SHALL NOT silently overwrite the Connection's recorded identity to match.

#### Scenario: Name drift surfaced
- **WHEN** the Slack-side Bot display name differs from the Connection's recorded BotName or the bound Agent's name
- **THEN** the diagnostic surfaces the identity-drift state showing the difference, and does not modify the Slack side or silently overwrite the Connection record

#### Scenario: Avatar drift surfaced
- **WHEN** the Slack-side Bot icon URL captured at verification differs from the Connection's recorded AvatarHash
- **THEN** the diagnostic surfaces the avatar drift showing both values, and does not modify the Slack side

### Requirement: The Web presents the diagnostic summary

The Web UI SHALL present the Connection diagnostic summary (most-important state, reason, single next action) as the primary view, with the underlying independent facts available as detail. The summary SHALL let the operator understand what is blocked and what to do next without reading raw fields.

#### Scenario: Web shows the single next action
- **WHEN** an operator opens a Connection in the Web UI
- **THEN** the summary area highlights the current most-important state, its reason, and the single next action, with setup progress, health, Owner availability, identity drift, and Agent readiness available as supporting detail

### Requirement: The CLI presents the diagnostic summary

The CLI `view` and `list` commands SHALL consume the diagnostic summary and present the most-important state, reason, and single next action as the primary output, rather than dumping raw Connection fields for the operator to interpret.

#### Scenario: CLI view shows the next action
- **WHEN** an operator runs `mo agent connection view <id>`
- **THEN** the output highlights the current most-important state, its reason, and the single next action

#### Scenario: CLI list shows per-connection next actions
- **WHEN** an operator runs `mo agent connection list`
- **THEN** each Connection row shows its most-important state and single next action so the operator can triage at a glance
