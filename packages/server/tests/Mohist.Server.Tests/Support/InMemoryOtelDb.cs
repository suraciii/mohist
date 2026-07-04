using Microsoft.Data.Sqlite;
using Mohist.Server.Otel;

namespace Mohist.Server.Tests.Support;

/// <summary>
/// Builds an <see cref="OtelDb"/> backed by an in-memory shared-cache SQLite
/// database, so OTel specs never touch a real <c>otel.db</c> file
/// (design/testing.md hard-constraint 1). The returned <see cref="OtelDb"/>
/// shares a single in-memory database across its read-write and read-only
/// connections; the caller must keep the returned <see cref="SqliteConnection"/>
/// (the keeper) open for the lifetime of the <see cref="OtelDb"/> — disposing
/// the keeper destroys the in-memory database.
/// </summary>
/// <remarks>
/// The read-only contract is <strong>not</strong> physically enforced against
/// an in-memory shared-cache database (SQLite's <c>Mode=ReadOnly</c> open flag
/// is a no-op there). That is acceptable for tests: <see cref="TraceQuerier"/>
/// only issues <c>SELECT</c> statements, and the physical read-only guard is a
/// production CLI safety constraint, not a behavior under test.
/// </remarks>
public static class InMemoryOtelDb
{
    /// <summary>
    /// Creates an <see cref="OtelDb"/> backed by a fresh in-memory database and
    /// returns the keeper connection that must remain open while the
    /// <see cref="OtelDb"/> is in use. Initializing the schema eagerly means
    /// read-only connections see a fully-formed database without needing the
    /// file-bootstrap path.
    /// </summary>
    public static (OtelDb db, SqliteConnection keeper) Create()
    {
        var name = $"otel-inmem-{Guid.NewGuid():N}";
        var readWriteConnectionString =
            $"Data Source={name};Mode=Memory;Cache=Shared";
        var readOnlyConnectionString =
            $"Data Source={name};Mode=Memory;Cache=Shared";

        var keeper = new SqliteConnection(readWriteConnectionString);
        keeper.Open();

        // Eagerly create the schema so read-only connections opened before any
        // write see the tables (mirrors the file bootstrap in
        // OtelDb.OpenReadOnlyConnection, but without touching the filesystem).
        foreach (var ddl in new[]
        {
            "CREATE TABLE IF NOT EXISTS traces (trace_id TEXT PRIMARY KEY, service_name TEXT NOT NULL, start_time TEXT NOT NULL, end_time TEXT NOT NULL, span_count INTEGER NOT NULL DEFAULT 0);",
            "CREATE TABLE IF NOT EXISTS spans (trace_id TEXT NOT NULL, span_id TEXT NOT NULL, parent_span_id TEXT, name TEXT NOT NULL, kind INTEGER NOT NULL, start_time TEXT NOT NULL, end_time TEXT NOT NULL, attributes TEXT, status_code INTEGER NOT NULL DEFAULT 0, status_message TEXT, resource_attributes TEXT, PRIMARY KEY (trace_id, span_id));",
            "CREATE INDEX IF NOT EXISTS idx_traces_service_start ON traces(service_name, start_time DESC);",
            "CREATE INDEX IF NOT EXISTS idx_traces_start ON traces(start_time DESC);",
            "CREATE INDEX IF NOT EXISTS idx_spans_trace ON spans(trace_id);",
        })
        {
            using var cmd = keeper.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }

        var db = new OtelDb(readWriteConnectionString, readOnlyConnectionString);
        return (db, keeper);
    }
}
