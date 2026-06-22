## Context

The Mohist server's internal execution — inbound HTTP → SignalR hub dispatch → Orleans grain → EF Core query → outbound HttpClient — is invisible. Issue #219 (done) added an OTLP trace collector as a same-process server component under the `/otel/` prefix on port 4318, but the server itself emits no traces, so the collector only sees external apps. This change makes the server instrument itself.

Current state of the relevant code:

- `Program.cs` (36 lines) builds the host: loads `~/.mohist/config.jsonc` via `AddMohistConfigFile`, configures the Orleans silo, registers services via `AddMohistServerCore`, runs DB migration, maps API/web.
- `MohistServiceRegistration.ConfigureMohistServices` is the single DI wiring point. It registers `AddDbContextFactory<MohistDbContext>`, `AddSignalR`, `AddHttpClient<ISystemReadinessProbe, HttpSystemReadinessProbe>`, and binds options classes (`AgentJobOptions`, `AttachmentStorageOptions`, `WorkflowArtifactStorageOptions`) via `services.Configure<XOptions>(configuration.GetSection(XOptions.SectionName))`.
- `MohistSiloRegistration.ConfigureMohistSilo` configures the Orleans silo; note the silo has its **own DI container** separate from the host's (per the inline comment at line 31).
- The .NET configuration system already maps `MOHIST__Section__Key` environment variables onto `Mohist:Section:Key` (confirmed by the comment in `Program.cs:8`), so options binding gets env-var override for free.

Constraints driving the design:

- **.NET 11 / Orleans 10 target** (per `AGENTS.md`). SignalR ships a built-in `Microsoft.AspNetCore.SignalR.Server` ActivitySource on .NET 10+; Orleans ships a native ActivitySource. So three of the five sources (SignalR, Orleans, ASP.NET Core on .NET+) need no separate instrumentation package — only `AddSource("<name>")`. EF Core and HttpClient use the contrib instrumentation packages.
- **Same-process feedback risk**: #219's collector listens under `/otel/` on 4318. The server's default OTLP endpoint is that same address. The trace-send POST therefore arrives back at the server's own Kestrel as a normal inbound request and would, unguarded, create an inbound span that triggers another export — an unbounded feedback loop of noise.
- **Failure isolation is a hard requirement**: exporter failure must not throw, block, or regress. The OTel SDK's `BatchExportProcessor` already runs export on a background thread and the official instrumentations honor `SuppressInstrumentationScope` during export, but this must be verified, not assumed.

## Goals / Non-Goals

**Goals:**
- Wire the OpenTelemetry SDK into host startup so the five execution-chain segments emit one unbroken trace per request.
- Export traces via OTLP HTTP to a configurable endpoint (default the #219 collector).
- Eliminate the self-feedback loop on the `/otel/` path.
- Make the whole capability cleanly disableable with zero behavioral regression.
- Introduce no custom business spans, no logs/metrics, no sampling.

**Non-Goals:**
- Collect / store / query traces (#219's scope).
- Logs, metrics, or custom business spans.
- Sampling strategy (v1 exports full volume).
- Runner-side instrumentation.
- `HostedService` instrumentation (no standard automatic source).
- Trace visualization Web UI.

## Decisions

### Decision 1: New `OtelOptions` bound to `Mohist:Otel`, mirroring existing options classes

Add `Infrastructure/Config/OtelOptions.cs` following the `AgentJobOptions` / `AttachmentStorageOptions` pattern: a `SectionName` constant, sensible defaults, plain properties.

```
Mohist:Otel:Enabled   bool    default true
Mohist:Otel:Endpoint  string  default "http://localhost:4318/otel"
```

Bound via `services.Configure<OtelOptions>(configuration.GetSection(OtelOptions.SectionName))`. Env-var override (`MOHIST__Otel__Enabled`, `MOHIST__Otel__Endpoint`) works automatically through the existing configuration pipeline — no extra code.

The OTLP HTTP exporter appends `/v1/traces` to the configured endpoint, so `http://localhost:4318/otel` resolves to `http://localhost:4318/otel/v1/traces`, which matches #219's ingest route. No path surgery needed.

**Alternative considered:** env-var-only config (`OTEL_EXPORTER_OTLP_ENDPOINT`). Rejected — inconsistent with every other Mohist setting and would bypass the `~/.mohist/config.jsonc` + `MOHIST__*` contract users already rely on.

### Decision 2: Gate the entire SDK registration on the master switch

A new `AddMohistOpenTelemetry(IServiceCollection, IConfiguration)` extension (new file `Infrastructure/Hosting/MohistOpenTelemetryRegistration.cs`), called from `ConfigureMohistServices`. When `OtelOptions.Enabled == false`, it returns immediately **without calling `AddOpenTelemetry()`** — no `TracerProvider` is built, no `ActivitySource` is subscribed, no export pipeline exists. This is stronger than "subscribe but don't export": it guarantees the off-state is byte-for-byte equivalent to a server built without this capability (zero `Activity` creation overhead, zero background threads, zero HTTP attempts).

**Alternative considered:** always register, conditionally skip the exporter. Rejected — it leaves instrumentation sources active (creating `Activity` objects that are discarded) and conflicts with the spec's "produces no spans" requirement.

### Decision 3: One `WithTracing(...)` block subscribing to all five sources

Inside `AddOpenTelemetry().WithTracing(tp => ...)`, configure the resource and the five sources together so they share one provider, one export pipeline, and one `Activity.Current` flow:

- `tp.ConfigureResource(r => r.AddService("Mohist.Server"))` — attributes all server spans to the server so the collector can filter by service.
- `tp.AddAspNetCoreInstrumentation(o => o.Filter = ExcludeOtelPath)` — inbound HTTP, with the feedback filter (Decision 4).
- `tp.AddSource("Microsoft.AspNetCore.SignalR.Server")` — built-in .NET 10+ SignalR source. No package.
- `tp.AddSource("Orleans.Runtime")` — Orleans native source. No package. (Exact name to confirm in build — see Open Questions.)
- `tp.AddHttpClientInstrumentation()` — outbound HttpClient.
- `tp.AddEntityFrameworkCoreInstrumentation()` — EF SQL spans with SQL text.
- `tp.UseOtlpExporter(o => { o.Protocol = OtlpExportProtocol.HttpProtobuf; o.Endpoint = new Uri(options.Endpoint); })`.

**Trace continuity is automatic.** All five sources respect the ambient `Activity.Current`. ASP.NET Core opens the root activity; SignalR hub methods, Orleans grain calls, EF queries, and outbound HttpClient calls all inherit the active context and attach as children. No custom propagation code. This is why the five segments produce *one* trace, not five disconnected ones — it is a property of subscribing all five to a single provider, which is also why the issue bundles them.

**Alternative considered:** register the Orleans source in the silo's own DI container. Rejected — `ActivitySource` is process-global; subscribing from the host provider captures Orleans spans regardless of which container the silo uses, and registering OTel in the silo container would build a second `TracerProvider` and a second exporter (double export, double feedback risk).

### Decision 4: `/otel/` self-feedback guard via `AspNetCoreInstrumentationOptions.Filter`

```
o.Filter = httpContext =>
    !httpContext.Request.Path.StartsWithSegments("/otel");
```

This is the documented, supported exclusion API (confirmed in the opentelemetry-dotnet-contrib `AspNetCore` README). Returning `false` prevents telemetry collection for that request. The filter targets `/otel` as a path segment prefix so it covers `/otel/v1/traces` and any future collector sub-routes.

**On the outbound side:** the OTLP exporter's own HttpClient POST is the other half of the feedback path. The OpenTelemetry .NET SDK exports inside a `SuppressInstrumentationScope`, which the official HttpClient instrumentation honors — so the exporter's outbound call should not itself produce a span. This is relied upon as the primary mechanism but **must be verified empirically during build** (see Risks). If verification shows export-originated outbound spans leaking through, the fallback is `HttpClientInstrumentationOptions.FilterHttpRequestMessage` excluding requests to the configured OTLP endpoint:

```
o.FilterHttpRequestMessage = req =>
    !req.RequestUri?.IsBaseOf(new Uri(options.Endpoint)) ?? true;
```

### Decision 5: Rely on the SDK's non-throwing export path, with an explicit timeout

Exporter failure isolation comes from the SDK by design: `BatchExportProcessor` exports on a background thread; failures are swallowed/logged, never thrown into request paths. We rely on this and do not wrap it in custom try/catch (that would imply the SDK throws, which it does not). We do set a bounded export via the SDK's own options (exporter HTTP timeout + batch flush interval at defaults is fine for v1).

The non-fatal contract is enforced by a test (Decision 6), not by defensive code.

### Decision 6: Test with an in-memory exporter, not a live OTLP round-trip

Unit/integration tests must not depend on #219's collector being up. Swap the OTLP exporter for an in-memory capturing exporter (`AddProcessor` + a test `BaseExporter<Activity>` that records exported spans in a list) and assert:

- `OtelOptions` binding: defaults applied, env-var override precedence.
- A sample request that flows HTTP → hub → grain → EF → outbound HttpClient yields spans under one trace id with correct parent-child links.
- `/otel/` inbound produces no inbound span.
- `Enabled == false` → provider not built, zero spans captured.
- Endpoint-unreachable (point at a dead port) → no exception, request still served.

**Alternative considered:** spin a real collector stub. Rejected — brittle, couples test infra to OTLP wire format, and the SDK's export path is already trusted for transport; what we need to verify is *which spans* are produced, which an in-memory processor answers directly.

## Risks / Trade-offs

- **[Orleans ActivitySource name drift]** The exact Orleans ActivitySource name (`Orleans.Runtime` and possibly others) must be confirmed against the Orleans 10 package at build time. → *Mitigation:* in the build task, send one grain call through the instrumented host, dump captured source names, and add every Orleans-emitted source found. Listed in Open Questions.
- **[Exporter self-feedback on the outbound path]** If the SDK's `SuppressInstrumentationScope` does not fully cover the HttpClient instrumentation path used by the OTLP HTTP exporter, each export would emit one outbound span that itself gets exported — a slow unbounded loop. → *Mitigation:* verified during build; fallback `FilterHttpRequestMessage` (Decision 4) removes all doubt. Test asserts zero spans with the OTLP endpoint URI.
- **[SQL text in EF spans may include parameter values]** EF instrumentation captures the SQL text; depending on provider settings this can include interpolated parameter values. → *Mitigation:* Mohist is local-first, single-user, no PII sensitivity beyond the user's own data; acceptable for v1. Revisit if the server ever runs multi-tenant.
- **[Trace volume]** No sampling in v1 means every request emits a full chain of spans; on a busy local dev box this is negligible, but a runaway workflow could flood `otel.db`. → *Mitigation:* accepted per Non-Goals; sampling is a deliberate future-issue candidate, not a v1 concern.
- **[Same-process POST to a non-running collector]** If `#219`'s collector component is not started (or port 4318 is otherwise closed), every export attempt hits a refused connection. → *Mitigation:* exporter failure is non-fatal (Decision 5); server runs normally, traces are simply lost. The default endpoint can be overridden to an external collector.
- **[Test fixture picks up OTel automatically]** `ConfigureMohistServices` is shared with test fixtures (per its doc-comment). Adding `AddMohistOpenTelemetry` there means tests opt into tracing unless they disable it. → *Mitigation:* tests default `Enabled` from config; the in-memory exporter test sets it explicitly; unrelated existing tests set `Mohist:Otel:Enabled=false` (or omit the section so default applies) to stay unaffected. Verify no existing test regresses.

## Migration Plan

This is purely additive, behind a flag that defaults to **on**.

1. **Add NuGet references** to `Mohist.Server.csproj`: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.EntityFrameworkCore`.
2. **Add `OtelOptions`** and the `AddMohistOpenTelemetry` extension; wire one call into `ConfigureMohistServices`.
3. **No data migration, no schema change, no API contract change.** Existing endpoints, SignalR hubs, Orleans grains, and EF queries are untouched.
4. **Operational rollback (zero code change):** set `Mohist:Otel:Enabled = false` in `~/.mohist/config.jsonc`, or `MOHIST__Otel__Enabled=false` in the environment, and restart the server. The server then behaves identically to its pre-instrumentation state. Code-level rollback is simply reverting the commit; no state to clean up.

## Open Questions

- **Orleans ActivitySource names**: confirm the exact source name(s) emitted by Orleans 10 (expected `Orleans.Runtime`; there may be additional ones for persistence/streams). Resolve by capturing source names during the build task.
- **Default endpoint ↔ #219 ingest alignment**: confirm #219's collector accepts `POST /otel/v1/traces` (so the OTLP exporter appending `/v1/traces` to `http://localhost:4318/otel` lands correctly). Cross-check against #219's routing once both changes are on the same branch.
- **`service.name` value**: propose `Mohist.Server` for the resource attribute; confirm there is no project-wide service-naming convention to follow.
- **Outbound suppression verification**: confirm during build that no outbound HttpClient span appears for the OTLP POST; if any leak, apply the `FilterHttpRequestMessage` fallback from Decision 4.
