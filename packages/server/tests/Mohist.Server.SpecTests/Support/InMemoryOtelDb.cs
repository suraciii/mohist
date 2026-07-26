using Microsoft.Data.Sqlite;
using Mohist.Server.Otel;

namespace Mohist.Server.SpecTests.Support;

public static class InMemoryOtelDb
{
    public static (OtelDb db, SqliteConnection keeper) Create()
    {
        var name = $"otel-inmem-{Guid.NewGuid():N}";
        var readWriteConnectionString =
            $"Data Source={name};Mode=Memory;Cache=Shared";
        var readOnlyConnectionString =
            $"Data Source={name};Mode=Memory;Cache=Shared";

        var keeper = new SqliteConnection(readWriteConnectionString);
        keeper.Open();

        // PRAGMA auto_vacuum must run before any schema object is
        // created; the OtelDb production path sets it for fresh
        // databases and we mirror that here so the in-memory test
        // factories exercise the same shape.
        using (var pragma = keeper.CreateCommand())
        {
            pragma.CommandText = "PRAGMA auto_vacuum=INCREMENTAL;";
            pragma.ExecuteNonQuery();
        }

        foreach (var ddl in new[]
        {
            "CREATE TABLE IF NOT EXISTS traces (trace_id TEXT PRIMARY KEY, service_name TEXT NOT NULL, start_time TEXT NOT NULL, end_time TEXT NOT NULL, span_count INTEGER NOT NULL DEFAULT 0);",
            "CREATE TABLE IF NOT EXISTS spans (trace_id TEXT NOT NULL, span_id TEXT NOT NULL, parent_span_id TEXT, name TEXT NOT NULL, kind INTEGER NOT NULL, start_time TEXT NOT NULL, end_time TEXT NOT NULL, attributes TEXT, status_code INTEGER NOT NULL DEFAULT 0, status_message TEXT, resource_attributes TEXT, PRIMARY KEY (trace_id, span_id));",
            "CREATE INDEX IF NOT EXISTS idx_traces_service_start ON traces(service_name, start_time DESC);",
            "CREATE INDEX IF NOT EXISTS idx_traces_start ON traces(start_time DESC);",
            "CREATE INDEX IF NOT EXISTS idx_traces_end ON traces(end_time);",
            "CREATE INDEX IF NOT EXISTS idx_spans_trace ON spans(trace_id);",
            "CREATE INDEX IF NOT EXISTS idx_spans_trace_start ON spans(trace_id, start_time);",
            "CREATE INDEX IF NOT EXISTS idx_spans_trace_end ON spans(trace_id, end_time);",
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
