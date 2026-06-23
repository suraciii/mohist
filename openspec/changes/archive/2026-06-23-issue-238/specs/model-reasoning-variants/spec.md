## ADDED Requirements

### Requirement: Requested model id is composed in slash-separated form

The runner SHALL compose the model identifier delivered to an opencode coder session as `provider/model/variant` when a reasoning variant is configured for the requested model, and as `provider/model` when no variant is configured. The variant SHALL be appended as the final slash-separated path segment only when a non-empty variant is present. The runner SHALL NOT compose the id using a `model:variant` (colon) separator or any format other than slash-separated path segments, because opencode's `parseModelSelection` splits solely on `/`.

#### Scenario: Variant configured composes three-segment id

- **WHEN** the requested model is `zhipuai-coding-plan/glm-5.2` and the configured variant is `max`
- **THEN** the composed model id delivered to opencode SHALL be `zhipuai-coding-plan/glm-5.2/max`

#### Scenario: No variant configured composes bare two-segment id

- **WHEN** the requested model is `minimax-coding-plan/MiniMax-M3` and no variant is configured
- **THEN** the composed model id delivered to opencode SHALL be `minimax-coding-plan/MiniMax-M3` with no trailing slash or variant segment

#### Scenario: Empty variant is treated as absent

- **WHEN** the requested model has a variant value that is empty or whitespace-only
- **THEN** the runner SHALL compose the bare `provider/model` id as if no variant were configured

### Requirement: Requested model is applied via unstable_setSessionModel

The runner SHALL apply the requested model to an opencode coder session by issuing exactly one `unstable_setSessionModel({ sessionId, modelId })` call with the composed id, before the first prompt is sent on that session. The runner SHALL NOT use `setSessionConfigOption` (or any config-option mechanism) to set the model, because opencode does not implement that method and the call is silently swallowed, leaving the session on opencode's default model.

#### Scenario: Configured model is applied before the first prompt

- **WHEN** a requested model is resolved for a session that is about to receive its first prompt
- **THEN** the runner SHALL issue exactly one `unstable_setSessionModel` call with the composed model id
- **AND** that call SHALL be ordered before the `prompt` call on the same session

#### Scenario: No model configured uses the provider default

- **WHEN** no requested model is resolved for the session
- **THEN** the runner SHALL NOT issue `unstable_setSessionModel`
- **AND** opencode's session-default model SHALL be used

#### Scenario: setSessionConfigOption is never used for model application

- **WHEN** the runner applies a requested model to an opencode coder session
- **THEN** no `setSessionConfigOption` call with `configId: "model"` SHALL be issued for model application
- **AND** the `set_session_config` liveness activity classification SHALL NOT be emitted for model application

### Requirement: Variant delivery is best-effort and never fails the run

Delivery of the composed model id SHALL be best-effort. If `unstable_setSessionModel` rejects (for example because the variant is no longer present in the model's variant map after a server-side configuration change), the runner SHALL log a warning, record `variantDelivered: false` in the diagnostic context, and continue the run against opencode's session-default model. The rejected variant MUST NOT be attributed as the run failure reason, and the run SHALL remain eligible to complete successfully.

#### Scenario: Rejected variant does not fail the run

- **WHEN** `unstable_setSessionModel` rejects while applying a composed id that carries a variant
- **THEN** the runner SHALL NOT fail the task or session because of the rejection
- **AND** the run SHALL continue against opencode's session-default model
- **AND** the diagnostic context SHALL record `variantDelivered: false`

#### Scenario: Accepted variant records successful delivery

- **WHEN** `unstable_setSessionModel` resolves while applying a composed id that carries a variant
- **THEN** the diagnostic context SHALL record `variantDelivered: true`
- **AND** the variant SHALL be observable on the opencode session's subsequent prompt

#### Scenario: Bare model delivery records successful delivery

- **WHEN** `unstable_setSessionModel` resolves while applying a composed id with no variant segment
- **THEN** the diagnostic context SHALL record `variantDelivered: true`
- **AND** no variant SHALL be forwarded to the opencode session prompt

### Requirement: Model diagnostic context reports variant delivery

The runner's model diagnostic context SHALL include a `variantDelivered: boolean` reflecting whether the composed id (including any variant) was accepted by `unstable_setSessionModel`. When a variant is configured for the requested model, the diagnostic context SHALL also include `requestedVariant` carrying the configured variant string, so provider-side reasoning effort can be correlated with delivery outcome. The existing `requestedModel` and `requestedModelSource` fields SHALL be preserved.

#### Scenario: Delivered variant is reflected in diagnostics

- **WHEN** a run is configured with model `zhipuai-coding-plan/glm-5.2` and variant `max`, and `unstable_setSessionModel` accepts the composed id
- **THEN** the diagnostic context SHALL contain `requestedVariant: "max"` and `variantDelivered: true`

#### Scenario: Failed variant delivery is reflected in diagnostics

- **WHEN** a run is configured with a variant and `unstable_setSessionModel` rejects the composed id
- **THEN** the diagnostic context SHALL contain `requestedVariant` set to the configured variant and `variantDelivered: false`

#### Scenario: Absent variant omits variant-specific diagnostics

- **WHEN** a run is configured with a model and no variant
- **THEN** the diagnostic context SHALL contain `variantDelivered: true` after a successful `unstable_setSessionModel` call
- **AND** `requestedVariant` SHALL be absent or null

### Requirement: Session reuse keys on the composed model id

Session reuse comparisons SHALL key on the full composed model id (`provider/model/variant` or `provider/model`), so that the same model configured with a different variant starts a fresh opencode session. Reusing a session across variants is forbidden because opencode stores the parsed variant on the session and forwards it to every prompt; a reused session would silently deliver the previous variant.

#### Scenario: Same model and same variant may reuse the session

- **WHEN** a cached session was established with composed id `zhipuai-coding-plan/glm-5.2/max`
- **AND** the new request resolves to the same composed id `zhipuai-coding-plan/glm-5.2/max`
- **THEN** the runner MAY reuse the cached session without a new `newSession` call

#### Scenario: Same model with a different variant starts a fresh session

- **WHEN** a cached session was established with composed id `zhipuai-coding-plan/glm-5.2/max`
- **AND** the new request resolves to `zhipuai-coding-plan/glm-5.2/high`
- **THEN** the runner SHALL start a fresh opencode session
- **AND** SHALL NOT reuse or resume the cached session

#### Scenario: Bare model reuse keys on provider/model

- **WHEN** a cached session was established with composed id `minimax-coding-plan/MiniMax-M3` (no variant)
- **AND** the new request resolves to the same bare id
- **THEN** the runner MAY reuse the cached session
- **AND** switching to a request that adds a variant SHALL start a fresh session
