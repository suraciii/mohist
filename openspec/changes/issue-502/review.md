## Findings

### P1: The blocked-source gauge is never exported

`EventDispatcherService` creates `mohist.server.event_dispatcher.blocked_sources` on its own meter at `packages/server/src/Mohist.Server/Infrastructure/Events/EventDispatcherService.cs:59`, but the production OpenTelemetry registration only invokes `WithTracing` at `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistOpenTelemetryRegistration.cs:44` and never configures a metrics provider, subscribes to the dispatcher meter, or adds a metrics OTLP exporter. Consequently, the new `MeterListener` unit test can observe the gauge in-process, but no configured observability backend can receive it. This fails the issue requirement that operators can identify FIFO stalls through this metric. Configure the metrics pipeline to subscribe to the dispatcher meter and export the metric, with an integration-level assertion that the configured pipeline receives it.

## Verification

- `git diff --check origin/master...HEAD`
- Server unit, server spec, and architecture suites pass (1538, 3278, and 35 tests respectively).

<promise>FAIL</promise>
