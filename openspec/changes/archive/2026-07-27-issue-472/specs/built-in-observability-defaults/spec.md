### Requirement: Built-in observability is enabled by default
When `Mohist:Otel:Enabled` is not configured, the Server SHALL enable its built-in OpenTelemetry tracing pipeline, local OTLP receiver, diagnostics sampling, and retention and storage-budget maintenance. The runtime observability status SHALL report `healthy` or `degraded`, never `off`, while this default is active.

#### Scenario: Server starts without an OTel enablement setting
- **WHEN** a Server starts with no `Mohist:Otel:Enabled` setting
- **THEN** it SHALL initialize the built-in observability pipeline and report a non-`off` observability status

### Requirement: Explicit opt-out disables all observability work
An explicit `Mohist:Otel:Enabled=false` setting SHALL disable the built-in OpenTelemetry tracing pipeline and OTLP receiver. The Server MUST NOT start diagnostics sampling or retention and storage-budget maintenance, and its observability status SHALL be `off`.

#### Scenario: Operator disables built-in observability
- **WHEN** a Server starts with `Mohist:Otel:Enabled=false`
- **THEN** it SHALL not bind the OTLP receiver or run observability background work, and `mo otel status` SHALL report `off`

### Requirement: Default deployment keeps OTLP local
The default OTLP receiver SHALL bind to `localhost` on port `4318`. The repository's Docker Compose deployment SHALL enable built-in observability without publishing port `4318`; it SHALL continue to publish only the Server API port by default.

#### Scenario: Operator starts the default Docker Compose deployment
- **WHEN** the operator runs the repository's unmodified Docker Compose configuration
- **THEN** built-in observability SHALL be enabled and the OTLP receiver SHALL not be exposed as a host-published port
