namespace Mohist.Server.Otel;

/// <summary>
/// Configuration for the built-in OTel trace collector. Bound from
/// <c>Mohist:Otel</c> in <c>~/.mohist/config.jsonc</c>.
/// </summary>
/// <remarks>
/// Schema is intentionally narrow (port, db path, on/off) — the collector
/// is a first-class Mohist Server component but its ingest and storage
/// contracts are fixed by the design and do not need user-tunable knobs
/// in v1.
/// </remarks>
public sealed class OtelOptions
{
    public const string SectionName = "Mohist:Otel";

    public const string DbPathEnvironmentVariable = "MOHIST_OTEL_DB_PATH";

    public const string MainDbPathEnvironmentVariable = "MOHIST_DB_PATH";

    /// <summary>
    /// Default OTLP HTTP ingestion port, per the OpenTelemetry spec.
    /// Exposed as a constant so non-DI consumers (test fixtures,
    /// middleware defaults) can reference it without instantiating
    /// <see cref="OtelOptions"/>.
    /// </summary>
    public const int DefaultPort = 4318;

    /// <summary>
    /// OTLP HTTP ingestion port. The default matches
    /// <see cref="DefaultPort"/> (the OpenTelemetry spec's conventional
    /// HTTP port).
    /// </summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Host interface to bind the OTLP port to. Defaults to
    /// <c>localhost</c>; set to <c>0.0.0.0</c> to listen on every
    /// interface (note: this exposes the ingest endpoint to the
    /// network without authentication).
    /// </summary>
    public string BindHost { get; set; } = "localhost";

    /// <summary>
    /// Absolute path to the <c>otel.db</c> SQLite file. When <c>null</c>,
    /// resolution falls back to <c>MOHIST_OTEL_DB_PATH</c>, otherwise
    /// <c>~/.mohist/otel.db</c>.
    /// </summary>
    public string? DbPath { get; set; }

    /// <summary>
    /// Master switch for the entire OTel subsystem. When <c>false</c> the
    /// OTLP port is not bound and <c>/otel/api/*</c> routes are not
    /// registered. Defaults to <c>false</c> until the collector's resource
    /// limits and degradation reporting are complete.
    /// </summary>
    public bool Enabled { get; set; }
}
