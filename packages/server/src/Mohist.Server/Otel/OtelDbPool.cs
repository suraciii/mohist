using Microsoft.Data.Sqlite;

namespace Mohist.Server.Otel;

/// <summary>
/// The connection-pool seam used by the startup recovery path. The
/// rebuild callback must drain every pooled SQLite connection before
/// deleting <c>otel.db</c> and its sidecars — otherwise an idle pooled
/// connection can re-open the deleted file from a transient state.
/// Production wires <see cref="SqliteOtelDbPool"/>, which calls
/// <see cref="SqliteConnection.ClearAllPools"/>; tests inject a fake
/// that records the call without touching the SQLite engine so they
/// can assert the rebuild path issues the clear without depending on
/// a real database lifecycle.
/// </summary>
public interface IOtelDbPool
{
    /// <summary>
    /// Releases every cached SQLite connection in the process so the
    /// rebuilt observation store cannot be re-opened against a stale
    /// handle. No-op when no pools exist.
    /// </summary>
    void ClearAll();
}

public sealed class SqliteOtelDbPool : IOtelDbPool
{
    public void ClearAll() => SqliteConnection.ClearAllPools();
}
