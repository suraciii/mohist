using System.Text.Json;

namespace Mohist.Cli;

internal sealed record ManagedRuntimePayloadGcResult(
    int ScannedTransactions,
    int ReclaimedPayloadRoots,
    int SkippedTransactions);

/// <summary>
/// Removes only disposable payload trees from durable, terminal transaction
/// records. Pointer and state files are recovery evidence and are never owned
/// by this collector.
/// </summary>
internal sealed class ManagedRuntimeTransactionGarbageCollector
{
    private static readonly string[] DisposablePayloadNames = ["snapshot", "build", "candidate"];
    private readonly IFileSystem _files;
    private readonly TextWriter _err;

    public ManagedRuntimeTransactionGarbageCollector(IFileSystem files, TextWriter error)
    {
        _files = files;
        _err = error;
    }

    public ManagedRuntimePayloadGcResult Collect(string runtimeRoot, string currentTransactionId)
    {
        var empty = new ManagedRuntimePayloadGcResult(0, 0, 0);
        try
        {
            if (!_files.DirectoryExists(runtimeRoot) || _files.IsSymbolicLink(runtimeRoot))
                return empty;

            var active = ReadPointer(Path.Combine(runtimeRoot, "active.json").Replace('\\', '/'));
            var verified = ReadPointer(Path.Combine(runtimeRoot, "verified.json").Replace('\\', '/'));
            if (!active.Readable || !verified.Readable)
            {
                _err.WriteLine("Managed transaction payload cleanup skipped: runtime pointer state is unreadable.");
                return empty;
            }

            var protectedIds = new HashSet<string>(StringComparer.Ordinal)
            {
                currentTransactionId,
            };
            AddIfPresent(protectedIds, active.Value?.TransactionId);
            AddIfPresent(protectedIds, verified.Value?.TransactionId);

            var transactionsRoot = Path.Combine(runtimeRoot, "transactions").Replace('\\', '/');
            if (!_files.DirectoryExists(transactionsRoot) || _files.IsSymbolicLink(transactionsRoot))
                return empty;

            var transactionRoots = _files.EnumerateDirectories(transactionsRoot, SearchOption.TopDirectoryOnly).ToArray();
            var scanned = 0;
            var reclaimed = 0;
            var skipped = 0;
            foreach (var transactionRoot in transactionRoots)
            {
                scanned++;
                if (!TryGetTransactionId(transactionRoot, out var transactionId)
                    || protectedIds.Contains(transactionId)
                    || IsSymbolicLink(transactionRoot))
                {
                    skipped++;
                    continue;
                }

                var statePath = Path.Combine(transactionRoot, "state.json").Replace('\\', '/');
                if (!_files.Exists(statePath) || IsSymbolicLink(statePath))
                {
                    skipped++;
                    continue;
                }

                if (!TryReadState(statePath, out var state)
                    || !IsReclaimable(state.Status))
                {
                    skipped++;
                    continue;
                }

                foreach (var name in DisposablePayloadNames)
                {
                    var payloadRoot = Path.Combine(transactionRoot, name).Replace('\\', '/');
                    if (!_files.DirectoryExists(payloadRoot))
                        continue;
                    if (IsSymbolicLink(payloadRoot))
                    {
                        _err.WriteLine($"Managed transaction payload cleanup skipped symlink: {payloadRoot}");
                        continue;
                    }

                    try
                    {
                        _files.DeleteDirectoryForCleanup(payloadRoot);
                        reclaimed++;
                    }
                    catch (Exception ex)
                    {
                        _err.WriteLine($"Managed transaction payload cleanup failed for {payloadRoot}: {ex.Message}");
                    }
                }
            }

            return new ManagedRuntimePayloadGcResult(scanned, reclaimed, skipped);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Managed transaction payload cleanup skipped: {ex.Message}");
            return empty;
        }
    }

    private (bool Readable, RuntimeTargetSet? Value) ReadPointer(string path)
    {
        try
        {
            if (_files.IsSymbolicLink(path))
                return (false, null);
        }
        catch (FileNotFoundException)
        {
            // A normal absent pointer is allowed; a dangling link is reported
            // by filesystems that expose link attributes above.
        }
        catch (DirectoryNotFoundException)
        {
            return (false, null);
        }
        catch (IOException)
        {
            return (false, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, null);
        }

        if (!_files.Exists(path))
            return (true, null);

        try
        {
            var value = JsonSerializer.Deserialize<RuntimeTargetSet>(_files.ReadAllText(path), JsonOptions);
            return value is null ? (false, null) : (true, value);
        }
        catch (JsonException)
        {
            return (false, null);
        }
        catch (IOException)
        {
            return (false, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, null);
        }
    }

    private bool TryReadState(string path, out RuntimeTargetSet state)
    {
        try
        {
            state = JsonSerializer.Deserialize<RuntimeTargetSet>(_files.ReadAllText(path), JsonOptions)!;
            return state is not null;
        }
        catch (JsonException)
        {
            state = null!;
            return false;
        }
        catch (IOException)
        {
            state = null!;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            state = null!;
            return false;
        }
    }

    private bool IsSymbolicLink(string path)
    {
        try
        {
            return _files.IsSymbolicLink(path);
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryGetTransactionId(string path, out string transactionId)
    {
        transactionId = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return !string.IsNullOrWhiteSpace(transactionId);
    }

    private static bool IsReclaimable(string status) =>
        string.Equals(status, "verified", StringComparison.Ordinal)
        || string.Equals(status, "rolled-back", StringComparison.Ordinal);

    private static void AddIfPresent(HashSet<string> ids, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            ids.Add(value);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}
