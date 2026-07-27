# Review Findings

## P2: OTLP route enablement has no default or opt-out coverage

The acceptance criteria require focused coverage of the OTLP route for both default enablement and explicit opt-out. The added tests exercise options binding, OpenTelemetry SDK registration, the host listener plan, runtime status, and sampler registration, but none maps `OtlpRoutes` using an absent `Mohist:Otel:Enabled` setting or an explicit `false`. Existing OTLP integration specs configure the feature explicitly on at `packages/server/tests/Mohist.Server.SpecTests/Specs/Telemetry/OtlpRoutesWebApplicationFactory.cs:76-93`, so they cannot detect a regression in the newly changed default.

Add route-level coverage that starts the production mapping with no enablement setting and verifies `POST /otel/v1/traces` is mounted on the OTLP listener, plus an explicit-false case that verifies the route is absent while `/otel/api/status` remains available and reports `off`. This is needed to lock the `OtlpRoutes.MapOtlpRoutes` guard at `packages/server/src/Mohist.Server/Api/OtlpRoutes.cs:35-37` against the changed options default.

<promise>FAIL</promise>
