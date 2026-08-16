### Requirement: Ambiguous multi-Bot messages receive one signed interactive choice
When a human Slack message addresses at least two eligible enabled Mohist Bots in the same workspace, the Server MUST prevent any Bot from starting work immediately and MUST create one readable choice prompt for the stable Slack message identity. The prompt MUST contain one Server-generated signed action per eligible mentioned Bot, with a clear Bot label and the original conversation and thread placement.

#### Scenario: A channel message mentions two eligible Bots
- **WHEN** a human posts a channel message mentioning two or more enabled, identity-bound Mohist Bots in the same workspace
- **THEN** no Agent session, input, or turn starts, and Slack receives one choice prompt with one action for each eligible mentioned Bot

#### Scenario: A threaded message is ambiguous
- **WHEN** a human posts an ambiguous message in an existing Slack thread
- **THEN** the single choice prompt is delivered in that same thread and every candidate action retains the original thread context

#### Scenario: A message mentions an unrelated or ineligible Bot
- **WHEN** the mention list includes a Bot that is not an enabled identity-bound Mohist Connection in the workspace
- **THEN** that Bot does not appear as a selectable candidate and it does not receive or start work for the message

### Requirement: Selection actions are signed, actor-bound, and context-bound
Each multi-Bot selection action MUST be signed with the credential of the Connection that owns the prompt delivery and MUST bind the original workspace, conversation, message, optional thread, actor, candidate set, selected Connection, nonce, and expiry. The Server MUST verify the signature with constant-time comparison and MUST revalidate the receiving Connection, workspace, conversation, thread, actor, selected Connection access authorization, candidate eligibility, and action expiry before dispatching. Client-submitted candidate identifiers or message content MUST NOT override the durable prompt context.

#### Scenario: The original actor selects an eligible Bot
- **WHEN** the actor bound to an unexpired signed action selects a candidate that is in the persisted candidate set, remains eligible, and remains authorized for that actor
- **THEN** the Server accepts the selection for that original Slack message and dispatches only the selected Connection

#### Scenario: A selection value is tampered with
- **WHEN** a selection action value or its signed candidate data is modified
- **THEN** the Server reports an invalid selection, starts no work, and presents an explicit rejection in Slack

#### Scenario: A different actor selects a choice
- **WHEN** a Slack member other than the actor bound into the selection action selects a candidate
- **THEN** the Server reports unauthorized, starts no work, and presents an explicit rejection in Slack

#### Scenario: A selection is expired or from the wrong context
- **WHEN** the action is expired or its workspace, Connection, conversation, message, or thread does not match the prompt context
- **THEN** the Server reports stale or expired, starts no work, and presents an explicit rejection in Slack

### Requirement: A valid selection routes the original message to exactly one selected Connection
An accepted selection MUST dispatch the authoritative original message context, including its normalized text, attachments, sender, workspace, conversation, message identity, and thread identity, to the selected Connection. Root messages MUST use the selected Connection's normal channel launch path, and threaded messages MUST use the selected Connection's normal thread routing path when a matching session exists. The selected Connection MUST own subsequent status and reply delivery in the original conversation or thread.

#### Scenario: A candidate is selected for a root message
- **WHEN** a valid selection is accepted for an ambiguous root message
- **THEN** the selected Connection starts the original request in the original conversation and no unselected Connection creates a session, input, turn, or delivery

#### Scenario: A candidate is selected for a threaded message with an existing binding
- **WHEN** a valid selection is accepted for an ambiguous threaded message and the selected Connection has the matching thread session
- **THEN** the original request becomes a follow-up in that selected session and remains in the original thread, while every unselected Bot remains idle

#### Scenario: The selected candidate becomes unavailable before dispatch
- **WHEN** the candidate is disabled, unbound, removed, or no longer eligible after the prompt was created but before selection is committed
- **THEN** the Server reports unavailable or stale, starts no work for that candidate, and leaves the original message available for an explicit new request rather than silently choosing another Bot

### Requirement: Ambiguous prompt state records candidates and one selection outcome
The Server MUST persist one prompt record keyed by the stable Slack message identity `(workspace, conversation, message timestamp)`. The record MUST retain the original thread context, the eligible candidate set, the prompt delivery identity, and the selection outcome, including the selected Connection and resulting dispatch or terminal state when known. The record MUST retain a stable selection dispatch key and recovery-lease state once a winner is committed. The record MUST be sufficient to validate later actions without trusting redelivered Slack text.

#### Scenario: Concurrent Bot ingress handles one ambiguous message
- **WHEN** multiple mentioned Connections process the same Slack message concurrently
- **THEN** one durable prompt record wins, one prompt delivery is created, and the record contains the complete candidate set used for the actions

#### Scenario: A selected prompt is read after dispatch
- **WHEN** the Server reads the prompt record after a valid selection
- **THEN** it returns the same stable message identity, candidate set, selected winner, and dispatch outcome used to route the original message

### Requirement: Selection is single-winner and idempotent
The Server MUST commit at most one selection outcome for a prompt. A stable selection operation identity MUST be persisted before dispatch, with a `selection-dispatch-pending` state, so repeated Slack delivery, adapter failover, and concurrent clicks cannot create multiple sessions or inputs. A committed pending selection MUST be resumable by the fixed-key durable Server action-recovery reminder after the interaction request or process is lost. A repeated selection MUST converge on the recorded winner and outcome; a different candidate after a winner is committed MUST be rejected as already applied or stale.

#### Scenario: The same selection is redelivered
- **WHEN** Slack redelivers the same signed candidate action after the first selection was accepted
- **THEN** the Server reports already applied or replayed, creates no second session or input, and does not dispatch the original message again

#### Scenario: Two different candidates are selected concurrently
- **WHEN** valid actions for two different candidates arrive concurrently for the same ambiguous message
- **THEN** exactly one candidate is committed as the winner, only that Connection receives the original message, and the losing action reports already applied or stale with no side effect

#### Scenario: The prompt was claimed but its delivery was not persisted
- **WHEN** the prompt-creating request fails after durable claim but before its outbox delivery is visible
- **THEN** a retry for the same winning prompt identity recreates or converges on the one required prompt delivery without creating a second prompt record or changing the candidate set

#### Scenario: The selection winner was committed before dispatch
- **WHEN** the Server records the winner and `selection-dispatch-pending` before the selected Connection launch or follow-up dispatch, then the process dies
- **THEN** the fixed-key action-recovery reminder claims the prompt row after restart and resumes the same selection operation using its persisted dispatch key and source snapshot
- **AND** a selection redelivery may perform the same resume or observe the recovery lease, but no unselected or duplicate work starts

### Requirement: Selection outcomes replace the obsolete choice controls
After a selection is accepted or rejected, the Server MUST enqueue an idempotent update for the prompt message that uses Server-provided text and blocks. An accepted selection MUST identify the selected Bot and show that the original request was dispatched; the choice actions MUST no longer be active for that prompt. A stale, unauthorized, expired, unavailable, or replayed selection MUST produce a readable outcome without claiming that unselected work started.

#### Scenario: A Bot selection is accepted
- **WHEN** the Server commits a valid selection
- **THEN** Slack receives one update naming the selected Bot and acknowledging dispatch, and the prompt no longer offers the unselected choices

#### Scenario: A selection is rejected
- **WHEN** the Server rejects a selection because it is invalid, unauthorized, expired, stale, unavailable, or already applied
- **THEN** Slack receives an explicit readable outcome and no unselected Bot is represented as having started work

### Requirement: Interactive delivery has a readable text fallback
The choice prompt MUST include readable fallback text that names the candidates and instructs the user to address one Bot explicitly. If interactive Block Kit actions are unavailable, rejected, or cannot be delivered, the adapter MUST deliver the fallback text through the durable outbox without inventing a candidate or starting work.

#### Scenario: Slack cannot use interactive actions
- **WHEN** the prompt is delivered without usable Block Kit action support or the action-bearing delivery falls back
- **THEN** the user receives readable candidate and single-Bot instructions, and no selection is inferred from the fallback delivery

#### Scenario: The user follows the text fallback
- **WHEN** the user re-mentions one candidate Bot in a new Slack message after receiving the fallback
- **THEN** normal single-Bot routing handles that new message, while the original ambiguous message remains single-winner and is not dispatched again

### Requirement: The adapter forwards selection actions without interpreting authorization or routing
The Slack adapter MUST normalize multi-Bot selection `block_actions` events into the existing interaction envelope, preserve the signed action value and stable Slack identity, omit raw Slack payloads and credentials, acknowledge Slack before waiting for Server processing, and deliver Server-provided selection text and blocks through the existing outbox contract. Original message ingress is recorded in the provider inbox before prompt claim, but a button click uses the prompt row and selection operation as its separate durable interaction receipt; it MUST NOT be stopped by the source-message inbox's duplicate result. A replay MUST load or resume the selection operation and enqueue the stable prompt presentation reference. The adapter MUST NOT choose a Bot, validate the candidate, or dispatch work locally.

#### Scenario: Slack delivers a Bot choice click
- **WHEN** the Socket Mode adapter receives one candidate button click
- **THEN** it acknowledges promptly and forwards the actor, workspace, conversation, message, thread, action identifier, and signed value to the Server for authorization and routing

#### Scenario: The Server updates the choice prompt
- **WHEN** the adapter claims the Server's selection outcome delivery
- **THEN** it sends the exact Server-provided text and blocks to the original Slack message or thread and acknowledges the delivery idempotently
