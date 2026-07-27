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
    /// Default retention age for traces in the built-in observation
    /// store. A trace whose latest Span activity is older than the
    /// retention age is deleted by the maintenance loop.
    /// </summary>
    public static readonly TimeSpan DefaultRetentionMaxAge = TimeSpan.FromHours(72);

    /// <summary>
    /// Default storage budget for the built-in observation store in
    /// bytes. Covers the combined <c>otel.db</c>, <c>-wal</c>, and
    /// <c>-shm</c> file sizes. The default carries over the
    /// pre-existing <see cref="RuntimeValueRules.StorageBudgetBytes"/>
    /// value (1 GiB) so the budget reported by
    /// <c>/otel/api/status</c> stays unchanged.
    /// </summary>
    public const long DefaultStorageBudgetBytes = RuntimeValueRules.StorageBudgetBytes;

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
    /// registered. Built-in observability is enabled unless explicitly
    /// disabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum age a Trace may remain in the observation store before the
    /// maintenance loop deletes it. Age is measured against a Trace's
    /// <c>end_time</c> (the latest Span time), so a Trace still receiving
    /// Spans is not aged out while it is growing. The default is the spec
    /// default of 72 hours; this is the only retention knob exposed to
    /// operators.
    /// </summary>
    public TimeSpan RetentionMaxAge { get; set; } = DefaultRetentionMaxAge;

    /// <summary>
    /// Hard storage budget in bytes for the observation store. Covers
    /// the combined <c>otel.db</c>, <c>-wal</c>, and <c>-shm</c> files.
    /// The maintenance loop evicts oldest complete Traces once usage
    /// crosses 90% of this budget and stops once it drops below 80%; if
    /// eviction cannot keep up, ingestion is closed via the
    /// <c>storage_budget_exhausted</c> degradation reason until
    /// reclamation recovers. The default matches the pre-existing
    /// <see cref="RuntimeValueRules.StorageBudgetBytes"/> value (1 GiB)
    /// so the budget reported by <c>/otel/api/status</c> stays unchanged.
    /// </summary>
    public long StorageBudgetBytes { get; set; } = DefaultStorageBudgetBytes;
}
