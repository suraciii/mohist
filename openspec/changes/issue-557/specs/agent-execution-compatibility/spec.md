### Requirement: Runtime capability information describes effort support

Each registered runtime SHALL publish known capability information that distinguishes supported models, supported reasoning-effort values, and runtime-specific variants. Capability information SHALL identify whether the catalog is complete enough to make a compatibility decision. A missing or incomplete catalog SHALL mean that compatibility is unconfirmed, not that the requested model or effort is incompatible. Direct, routed, and Agent Connection launches SHALL consume the same per-Runner facts.

#### Scenario: A runtime publishes model effort capabilities

- **WHEN** a Runner registers a runtime catalog containing a model and its supported reasoning-effort values
- **THEN** the server SHALL retain the model-to-effort relationship for that runtime
- **AND** runtime-specific variants SHALL remain a separate capability dimension

#### Scenario: An incomplete catalog does not fabricate incompatibility

- **WHEN** a runtime reports an incomplete catalog or no capability catalog
- **THEN** the server SHALL mark the requested compatibility as temporarily unconfirmed
- **AND** it SHALL not report a known model/effort incompatibility solely because the catalog is incomplete

#### Scenario: Pi publishes effort levels separately from variants

- **WHEN** a Pi Runner registers a model with SDK `thinkingLevels`
- **THEN** the server SHALL retain those levels as canonical `ReasoningEfforts` for that model
- **AND** the registration SHALL not copy the levels into runtime-specific `Variants`

#### Scenario: OpenCode without an independent effort source remains unconfirmed

- **WHEN** an OpenCode Runner cannot distinguish native reasoning effort from provider variants
- **THEN** its catalog SHALL be marked incomplete
- **AND** readiness SHALL report temporary uncertainty rather than claiming support or incompatibility from variant names

### Requirement: Readiness distinguishes configuration and compatibility failures

Before a new Agent execution is admitted, readiness SHALL evaluate the complete requested tuple of runtime, model, reasoning effort, and runtime-specific variant against known capability information. Readiness for direct launches, routed launches, and Agent Connections SHALL use the same evaluator. Readiness SHALL distinguish at least missing configuration, invalid or unknown effort, runtime effort unsupported, model-and-effort incompatible, and temporarily unconfirmed capability states. Each non-ready state SHALL include a stable reason and an actionable next action.

#### Scenario: Required configuration is missing

- **WHEN** an Agent has no model or has no resolved reasoning effort
- **THEN** readiness SHALL return a missing-configuration conclusion
- **AND** the result SHALL identify the missing field without classifying it as model incompatibility

#### Scenario: The runtime does not support reasoning effort

- **WHEN** the selected runtime has no support for the requested reasoning-effort capability
- **THEN** readiness SHALL return an unsupported-effort conclusion
- **AND** the result SHALL identify the runtime as the unsupported capability owner

#### Scenario: The model does not support the requested effort

- **WHEN** the selected runtime supports reasoning effort but its known catalog excludes the requested effort for the selected model
- **THEN** readiness SHALL return a model-and-effort-incompatible conclusion
- **AND** the result SHALL identify both the selected model and requested effort

#### Scenario: Capability is temporarily unconfirmed

- **WHEN** the selected runtime or model catalog is temporarily unavailable or incomplete
- **THEN** readiness SHALL return an unknown or temporarily-unconfirmed conclusion
- **AND** it SHALL not report the tuple as a permanent configuration error

### Requirement: Compatibility evaluation does not probe model availability

Readiness and Agent list/detail availability evaluation SHALL use only known, registered capability facts and local configuration validation. It SHALL NOT start a provider process, make a provider request, or perform an active model-availability probe to decide whether a tuple is compatible.

#### Scenario: A readiness request uses the registered catalog only

- **WHEN** a caller requests readiness for an Agent with a configured model and reasoning effort
- **THEN** the result SHALL be derived from configuration and registered capability data
- **AND** no provider or model-availability probe SHALL be required to produce the result

#### Scenario: Agent Connection readiness uses the shared evaluator

- **WHEN** a saved Agent is bound to an Agent Connection and the Connection list, detail, or launch path requests readiness
- **THEN** the result SHALL include the same execution projection and compatibility gap codes as a direct Agent launch
- **AND** the Connection path SHALL not parse `variant` as reasoning effort or call a separate readiness derivation

### Requirement: Temporary unavailability preserves the requested tuple

When a valid Agent tuple cannot currently execute because the selected runtime, model, or effort capability is temporarily unavailable, the system SHALL represent the execution as waiting or retryable with a temporary-unavailability reason. It SHALL preserve the exact requested runtime, model, reasoning effort, and variant and SHALL not substitute another model, effort, runtime, or provider. A confirmed before-execution temporary failure reported after admission SHALL return the Job to the pending or retryable state using the existing recovery/admission signals. An inconclusive Runner loss SHALL remain nonterminal until reconciliation establishes whether execution was accepted; it SHALL not become a terminal configuration failure or trigger an unverified replay.

#### Scenario: No eligible runtime is temporarily online

- **WHEN** an Agent has a known compatible tuple but no eligible Runner or runtime is currently ready
- **THEN** the execution SHALL remain queued or waiting with a temporary-unavailability reason
- **AND** recovery SHALL retry the same tuple when capability becomes available
- **AND** no fallback tuple SHALL be created

#### Scenario: A post-admission temporary failure retries the frozen tuple

- **WHEN** an admitted Job reports `runtime-unavailable`, `model-unavailable`, or `effort-unavailable` before the selected runtime accepts the prompt
- **THEN** the Job SHALL become pending or retryable with a temporary-unavailability reason
- **AND** the next admission SHALL reuse the persisted runtime, model, reasoning effort, and variant
- **AND** no fallback tuple or second provider selection SHALL be created
