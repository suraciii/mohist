# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: prior-review-repair
  Evidence: The previous review found that protobuf coverage used a synthetic payload and did not prove the real OpenTelemetry SDK `HttpProtobuf` exporter could reach the real `MapOtlpRoutes` collector route and persist to `otel.db`. The candidate now adds `RealSdkHttpProtobufExport_PersistsToOtelDb` in `packages/server/tests/Mohist.Server.Tests/Specs/Telemetry/OtlpRoutesIntegrationSpecs.cs`, which starts a socket-backed app using `MapOtlpRoutes`, exports an SDK span over OTLP HTTP/protobuf, and verifies a persisted `real-sdk-export` trace row. The parser was also repaired in `packages/server/src/Mohist.Server/Otel/OtlpProtobuf/OtlpProtobufTraceParser.cs` to parse current `scope_spans`, legacy `instrumentation_library_spans`, and real OTLP timestamp encodings.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~Otel|FullyQualifiedName~OtlpRoutesIntegrationSpecs" -p:SkipWebBuild=true --logger "console;verbosity=minimal"` passed: 116/116.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: prior-review-repair
  Evidence: The previous review found that SignalR coverage accepted synthetic or lifecycle-only activity instead of asserting a real dispatcher-emitted hub method span. The candidate removes the synthetic `EchoHubMethodActivity_CarriesMethodAndParentContext` test and updates `HubConnection_ProducesRealEchoHubMethodActivity` in `packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/Otel/OtelSignalRTracingSpecs.cs` to invoke a real SignalR client `Echo` call and assert exactly one captured SignalR activity has `rpc.method == "Echo"`, `rpc.system == "signalr"`, and non-default trace/span ids.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~Otel|FullyQualifiedName~OtlpRoutesIntegrationSpecs" -p:SkipWebBuild=true --logger "console;verbosity=minimal"` passed: 116/116.
  Status: resolved

## Blocking Items

- [ID: item-3]
  Severity: info
  Scope: none
  Evidence: No unresolved blocking issues were found in the post-repair candidate snapshot. The server tracing registration gates all SDK setup behind `Mohist:Otel:Enabled` in `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistOpenTelemetryRegistration.cs`, subscribes the required ASP.NET Core, SignalR, Orleans, EF Core, and HttpClient sources in one tracing pipeline, excludes `/otel`, filters exporter self-feedback, and configures OTLP HTTP/protobuf export to the resolved endpoint. The issue-228 tests cover inbound HTTP route/status/duration, `/otel` exclusion, endpoint default/config/env behavior, exporter failure isolation, source subscription, real SignalR `Echo`, Orleans source names, EF SQL text, outbound HttpClient spans, synthetic representative chain parentage, production HTTP/Orleans/EF continuity, real SDK OTLP payload parsing, and real SDK export persistence through `MapOtlpRoutes`.
  SuggestedAction: None.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~Otel|FullyQualifiedName~OtlpRoutesIntegrationSpecs" -p:SkipWebBuild=true --logger "console;verbosity=minimal"` passed: 116/116.
  Status: resolved

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/Otel/OtelSignalRTracingSpecs.cs
  Evidence: `HubConnection_ProducesRealEchoHubMethodActivity` now proves a real dispatcher-emitted `Echo` hub method span exists, but it does not directly assert the `Echo` span's parent span id against the specific carrying long-poll/inbound HTTP span. The broader representative chain test asserts parentage using a synthetic SignalR source activity, so adding a direct real-SignalR parentage assertion would make this criterion stronger.
  SuggestedAction: If SignalR parentage regresses or becomes important for diagnostics, add a stable assertion that correlates the real `Echo` activity to the carrying transport span without depending on fragile lifecycle timing.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: repository history relative to master
  Evidence: `git diff --name-only master...HEAD` shows many files unrelated to issue 228 because this workspace branch contains prior Mohist workflow/archive/product changes. Per the candidate boundary, issue-228 workflow artifacts are review context, and unrelated historical branch drift was not treated as part of this issue's product deliverable.
  SuggestedAction: Review unrelated branch drift in the workflow or issue that owns those changes, not as part of issue 228's OTel tracing candidate.
  Status: out-of-scope

<promise>PASS</promise>
