### Requirement: Default recovery idempotency keys are unique per call
The Server SHALL generate a distinct idempotency key for every AgentSession reset or compact recovery call that omits an explicit idempotency key. Two distinct recovery operations of the same command kind SHALL NOT share a key, and the default SHALL NOT collapse to a fixed or sentinel value, including the former `"legacy"` constant.

#### Scenario: Two default-key operations do not share a key
- **WHEN** two compact (or reset) operations are each issued without an explicit idempotency key
- **THEN** each operation's reservation SHALL carry a different idempotency key, and neither key SHALL equal a fixed constant shared with the other

#### Scenario: No sentinel default value
- **WHEN** a reset or compact recovery call omits the idempotency key
- **THEN** the generated key SHALL be a freshly produced unique value, not the literal `"legacy"` nor any other constant reused across calls

### Requirement: Distinct operations are not falsely replayed
A completed recovery reservation MUST NOT be replayed to a later recovery call of the same command kind that did not supply a matching explicit idempotency key. A subsequent default-key call SHALL start a new operation rather than return the prior operation's result.

#### Scenario: Default-key call after a completed operation starts a new operation
- **WHEN** a compact operation has completed under a default-generated key and a second compact call is issued without an explicit idempotency key
- **THEN** the second call SHALL NOT return the completed outcome of the first and SHALL begin a new compact operation with a new operation id

#### Scenario: Default-key call after a completed operation produces its own effect
- **WHEN** a reset operation has completed under a default-generated key and a second reset call arrives without an explicit idempotency key
- **THEN** the second call SHALL be executed as a distinct operation and SHALL record its own recovery effect, rather than being swallowed as a replay of the prior reservation

### Requirement: Explicit-key idempotency contract is preserved
When a caller supplies an explicit idempotency key, the established idempotency contract SHALL remain unchanged: a repeat with the same key SHALL replay the same reservation and result, and a different explicit key SHALL join the same in-progress operation. An explicit key whose value is `"legacy"` SHALL be treated as an ordinary caller-supplied key with no special default semantics.

#### Scenario: Same explicit key replays the completed result
- **WHEN** a compact operation completes under the explicit key `k1` and the completed recovery is queried again with key `k1`
- **THEN** the Server SHALL return the completed result without starting a new operation

#### Scenario: Different explicit key joins the in-progress operation
- **WHEN** a reset operation is in progress under the explicit key `r1` and a second reset call arrives with the explicit key `r2`
- **THEN** the second call SHALL join the same in-progress reservation and replay its result when it completes

#### Scenario: Explicit `legacy` key is not treated as the default
- **WHEN** a caller supplies the explicit idempotency key `"legacy"`
- **THEN** it SHALL behave as an ordinary explicit key and SHALL NOT be normalized, merged with, or treated as equivalent to an omitted key
