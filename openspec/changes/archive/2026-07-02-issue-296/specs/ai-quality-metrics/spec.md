## ADDED Requirements

### Requirement: AI-quality surface exposes a per-bucket first-time-right series over the trailing window

The project AI-quality surface SHALL expose a per-time-bucket first-time-right (FTR) series across the trailing window the existing single-point aggregation already evaluates. For each time bucket in the window, the series SHALL provide the FTR rate evaluated over only the issues shipped (reached `Done`) whose ship time falls within that bucket: the count of first-time-right shipped-in-bucket issues divided by the count of all shipped-in-bucket issues. The series SHALL be computed purely from the already-recorded per-check repair counts and the already-recorded ship event (`IssueWorkCompleted`) that the existing single-point first-time-right aggregation already uses — reusing the existing first-time-right classification verbatim — and SHALL NOT introduce any new event, state collection, or workflow-domain write. A bucket that contains no shipped issues SHALL yield the defined empty result (distinguishable from a genuine FTR rate of one or zero), evaluated independently per bucket.

#### Scenario: Per-bucket FTR rate equals first-time-right shipped over all shipped in that bucket

- **WHEN** the AI-quality surface returns the per-bucket FTR series for a project whose shipped issues span the trailing window
- **THEN** each bucket's FTR rate SHALL be the count of first-time-right issues shipped within that bucket divided by the count of all issues shipped within that bucket
- **AND** every bucket's numerator and denominator SHALL count only issues that reached `Done` within that bucket

#### Scenario: A bucket with no shipped issues yields the empty result, independent of other buckets

- **WHEN** one time bucket contains no issues that reached `Done` while another bucket contains several
- **THEN** the empty bucket SHALL yield the defined empty result
- **AND** the empty bucket SHALL NOT be reported as an FTR rate of zero or one
- **AND** the non-empty bucket SHALL report its computed FTR rate from its own samples

#### Scenario: Per-bucket FTR series reuses the existing first-time-right classification and introduces no new data collection

- **WHEN** the per-bucket FTR series is computed and returned
- **THEN** the series SHALL be derived from the already-recorded per-check repair counts and the already-recorded ship event
- **AND** the first-time-right classification within each bucket SHALL match the existing whole-lifecycle first-time-right classification
- **AND** no new event, state collection, or workflow-domain write SHALL be introduced to support the series

### Requirement: AI-quality surface exposes a per-bucket rework series over the trailing window

The project AI-quality surface SHALL expose a per-time-bucket rework series across the same trailing window, on the same percentage scale as the FTR series. For each time bucket, the series SHALL provide the rework rate evaluated over only the issues shipped whose ship time falls within that bucket: the count of shipped-in-bucket issues that were reworked at any stage they entered divided by the count of all shipped-in-bucket issues. An issue SHALL count as reworked at any stage when at least one stage it entered is reworked per the existing per-stage-rework classification (at least one check in that stage has a repair count greater than zero); an issue reworked at multiple stages SHALL count once in the bucket numerator. The series SHALL be computed purely from the already-recorded per-check repair counts and the already-recorded ship event, reusing the existing per-stage-rework classification, and SHALL NOT introduce any new event, state collection, or workflow-domain write. A bucket that contains no shipped issues SHALL yield the defined empty result, evaluated independently per bucket.

#### Scenario: Per-bucket rework rate equals reworked-at-any-stage shipped over all shipped in that bucket

- **WHEN** the AI-quality surface returns the per-bucket rework series for a project whose shipped issues span the trailing window
- **THEN** each bucket's rework rate SHALL be the count of shipped-in-bucket issues reworked at any stage they entered divided by the count of all issues shipped within that bucket
- **AND** the denominator SHALL count only issues that reached `Done` within that bucket

#### Scenario: An issue reworked at multiple stages counts once in the bucket numerator

- **WHEN** a shipped issue in a bucket was reworked at both the `plan` stage and the `check` stage
- **THEN** the issue SHALL contribute exactly one to that bucket's rework numerator
- **AND** the issue SHALL NOT be double-counted across stages

#### Scenario: A bucket with no shipped issues yields the empty result

- **WHEN** a time bucket contains no issues that reached `Done`
- **THEN** the rework series SHALL yield the defined empty result for that bucket
- **AND** the bucket SHALL NOT be reported as a rework rate of zero or one

#### Scenario: Per-bucket rework series reuses the existing per-stage-rework classification

- **WHEN** the per-bucket rework series is computed and returned
- **THEN** the reworked-at-any-stage classification within each bucket SHALL be derived from the existing per-stage-rework classification
- **AND** no new event, state collection, or workflow-domain write SHALL be introduced to support the series

### Requirement: Per-bucket series are co-located with the AI-quality surface and leave existing contracts unchanged

The per-bucket FTR series and per-bucket rework series SHALL be exposed co-located with the existing project AI-quality aggregation (the existing 7-day / 30-day single-point first-time-right rate and per-stage rework rates), so a dashboard can read the trend in the same surface it already reads for the single-point quality summary. Introducing the per-bucket series SHALL NOT alter, remove, or re-shape the existing 7-day / 30-day single-point aggregate contract or the existing zero-sample empty result; the per-bucket series are strictly additive. The per-bucket series SHALL be anchored on ship time (reached `Done`): an issue contributes to a bucket if and only if it reached `Done` within that bucket, and issues that have not reached `Done` SHALL NOT contribute to any bucket. The zero-sample bucket cases SHALL be returned as `200` with the defined empty result per bucket, not as an error. The surface SHALL return `404` for an unknown project, consistent with the existing endpoint.

#### Scenario: Per-bucket series are readable alongside the existing single-point aggregation

- **WHEN** a client requests the project AI-quality surface for a project that has shipped issues
- **THEN** the surface SHALL return the per-bucket FTR series and per-bucket rework series alongside the existing single-point aggregation
- **AND** the existing 7-day / 30-day single-point first-time-right rate and per-stage rework rates SHALL remain available and unchanged

#### Scenario: Existing single-point contract is preserved

- **WHEN** the per-bucket series are added to the AI-quality surface
- **THEN** the existing 7-day / 30-day single-point aggregate contract and the existing zero-sample empty result SHALL retain their existing semantics and shape
- **AND** no existing field of the single-point aggregation SHALL be altered or removed

#### Scenario: Per-bucket membership is anchored on ship time; non-shipped issues are excluded from every bucket

- **WHEN** the per-bucket series are evaluated for a project that has both shipped and in-flight issues
- **THEN** an issue SHALL contribute to a bucket if and only if it reached `Done` within that bucket
- **AND** in-flight issues SHALL NOT contribute to any bucket's numerator or denominator

#### Scenario: Zero-sample bucket returns 200 with the empty result, not an error

- **WHEN** a client requests the per-bucket series for a project where one or more buckets contain no shipped issues
- **THEN** the surface SHALL return `200` with the defined empty result for each empty bucket
- **AND** the response SHALL NOT report a numeric zero or one rate for an empty bucket

#### Scenario: Unknown project returns not found

- **WHEN** a client requests the per-bucket series for a project reference that does not resolve to a known project
- **THEN** the surface SHALL return `404`
