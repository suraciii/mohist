# Review Findings

## P1: Explicit opt-out still starts diagnostics work

`packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs:207` unconditionally registers `OtelDiagnosticsSampler`. Its `StartAsync` then unconditionally calls `SampleProcess()` at `packages/server/src/Mohist.Server/Otel/OtelDiagnosticsSampler.cs:116-121`, and its loop continues to sample process resources every 10 seconds at `packages/server/src/Mohist.Server/Otel/OtelDiagnosticsSampler.cs:143-151`. The `_enabled` guard only skips storage sampling and maintenance.

Consequently, `Mohist:Otel:Enabled=false` still creates and runs the diagnostics hosted service, reads process resource data, and mutates `RuntimeObservability`. This violates the issue/spec requirement that the explicit opt-out must not start diagnostics or maintenance work. Make the disabled path avoid registering/starting the sampler (or make `StartAsync` entirely inert when disabled), while preserving the status route so it can report `off`. Add a composition or hosted-service test that verifies the disabled configuration performs no diagnostics sampling or maintenance callbacks.

<promise>FAIL</promise>
