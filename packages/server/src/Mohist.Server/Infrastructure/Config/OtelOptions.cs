namespace Mohist.Server.Infrastructure.Config;

/// <summary>
/// Configuration for the server's outbound OpenTelemetry tracing. Bound
/// from <c>Mohist:Otel</c> in <c>~/.mohist/config.jsonc</c>; overridable
/// via the standard <c>MOHIST__Otel__*</c> environment variables that
/// <see cref="MohistConfigurationExtensions.AddMohistConfigFile"/> plumbs
/// through <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
/// </summary>
/// <remarks>
/// When <see cref="Enabled"/> is <c>false</c>, the entire OpenTelemetry
/// SDK is skipped at host startup — no <c>TracerProvider</c> is built,
/// no <c>ActivitySource</c> is subscribed, no export pipeline exists.
/// This is a stronger guarantee than "registered but not exporting":
/// the server's request-handling path produces zero
/// <see cref="System.Diagnostics.Activity"/> objects from the OTel
/// pipeline, with zero background threads, zero HTTP attempts, and
/// zero behavioral delta compared to a build that omits this capability.
/// </remarks>
public sealed class OtelOptions
{
    public const string SectionName = "Mohist:Otel";

    /// <summary>
    /// Default endpoint targets the same-process OTLP/HTTP collector
    /// delivered by the server's collector component (issue #219). The
    /// OTLP HTTP exporter appends <c>/v1/traces</c> to this URL, so the
    /// configured value resolves to <c>http://localhost:4318/otel/v1/traces</c>.
    /// </summary>
    public const string DefaultEndpoint = "http://localhost:4318/otel";

    /// <summary>
    /// Master switch for the entire OpenTelemetry tracing capability.
    /// Defaults to <c>false</c> until the local collector's resource limits
    /// and degradation reporting are complete. Set to <c>true</c> in
    /// <c>~/.mohist/config.jsonc</c> or via <c>MOHIST__Otel__Enabled=true</c>
    /// to opt in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// OTLP HTTP endpoint the trace exporter posts to. Overridable via
    /// <c>MOHIST__Otel__Endpoint</c>. The OTLP HTTP exporter appends
    /// <c>/v1/traces</c> to this value.
    /// </summary>
    public string Endpoint { get; set; } = DefaultEndpoint;
}
