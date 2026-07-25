### Requirement: Ingestion stops accepting new traces when reclamation cannot keep up

When the storage budget cannot be reclaimed fast enough, the ingestion path SHALL stop accepting new Trace writes before the store grows without bound. Reclamation is treated as not keeping up when usage is at or above the high watermark and eviction has signalled that it cannot reduce usage below the low watermark — including the case where a truncating checkpoint is blocked by a long-running read transaction. The decision SHALL be evaluated on the write path using the current reclamation state, and SHALL apply per write rather than per request token or per process. As soon as reclamation succeeds in reducing usage below the low watermark again, ingestion SHALL resume accepting new Trace writes without a Server restart. The admission decision SHALL NOT block, busy-wait, or otherwise delay the request, the Workflow scheduler, or Runner communication; a rejected write SHALL be refused promptly.

#### Scenario: Reclamation cannot reduce usage below the low watermark

- **WHEN** usage is at or above the high watermark and eviction cannot reduce usage below the low watermark
- **THEN** the ingestion path SHALL stop accepting new Trace writes
- **AND** a write attempted in that state SHALL be refused promptly without blocking the request thread

#### Scenario: A long read transaction blocks reclamation

- **WHEN** a truncating checkpoint is blocked by a long-running read transaction so usage cannot be reduced below the low watermark
- **THEN** ingestion SHALL stop accepting new Trace writes for as long as reclamation is blocked
- **AND** ingestion SHALL resume as soon as the checkpoint completes and usage drops below the low watermark

#### Scenario: Reclamation recovers

- **WHEN** eviction reduces usage below the low watermark after a period of rejecting writes
- **THEN** ingestion SHALL resume accepting new Trace writes
- **AND** resumption SHALL NOT require a Server restart or manual intervention

#### Scenario: Admission does not block core execution

- **WHEN** a write is refused because reclamation cannot keep up
- **THEN** the refusal SHALL complete promptly
- **AND** it SHALL NOT block the request, the Workflow scheduler, or Runner communication

### Requirement: Rejected spans are reported via OTLP partial success

For a write that is refused because reclamation cannot keep up, every Span in that write that is not committed SHALL be reported to the sender as rejected using the OTLP `partial_success` response, carrying the count of rejected Spans, and the response SHALL instruct the sender not to retry that batch. Refused Spans SHALL be counted in the runtime rejection counter that already tracks intentionally-refused telemetry, so that rejected-volume reporting is consistent with the existing ingest outcome accounting. Spans that are refused for this reason SHALL NOT be counted as saved, dropped, or as a retryable storage failure.

#### Scenario: A write is refused while over budget

- **WHEN** an OTLP write containing N Spans arrives while reclamation cannot keep up
- **THEN** the response SHALL use OTLP `partial_success` to report the rejected Span count
- **AND** the response SHALL instruct the sender not to retry that batch
- **AND** the refused Spans SHALL be counted in the runtime rejected counter and not as saved or dropped

#### Scenario: A partial write is committed before the admission gate flips

- **WHEN** a write that was accepted and committed is followed by a write that is refused because the store has since become unreclaimable
- **THEN** the committed write's Spans SHALL be counted as saved
- **AND** only the later refused write's Spans SHALL be counted as rejected

### Requirement: Admission rejection is visible as a degradation reason

While ingestion is refusing writes because reclamation cannot keep up, the OTel status SHALL report `degraded` and SHALL expose a latest degradation reason that identifies storage-budget exhaustion as the cause, distinct from generic telemetry rejection, so an operator can tell from `mo otel status` or `/otel/api/status` why data is being refused. The status SHALL remain `off`/`healthy`/`degraded` as the only three top-level states; the over-budget reason is an additive cause within `degraded` and SHALL NOT change the three-state contract. When reclamation recovers and ingestion resumes, the over-budget degradation cause SHALL clear on the next observation, subject to the existing protection window, leaving `healthy` only when no unrelated degradation cause remains.

#### Scenario: An operator inspects status while writes are being refused

- **WHEN** ingestion is refusing writes because reclamation cannot keep up
- **THEN** `/otel/api/status` and `mo otel status` SHALL report `degraded`
- **AND** the latest degradation reason SHALL identify storage-budget exhaustion as the cause
- **AND** the three top-level states SHALL remain `off`, `healthy` and `degraded`

#### Scenario: Recovery clears the over-budget cause

- **WHEN** reclamation reduces usage below the low watermark and ingestion resumes after a period of over-budget rejection
- **THEN** the over-budget degradation cause SHALL clear on the next observation
- **AND** status SHALL recover to `healthy` only when no unrelated degradation cause remains
