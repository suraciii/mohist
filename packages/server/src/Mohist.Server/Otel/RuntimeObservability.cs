using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Mohist.Server.Otel;

public sealed class RuntimeObservability : IDisposable
{
    public const int MaxReasonLength = RuntimeValueRules.MaxReasonLength;
    public const long DefaultStorageBudgetBytes = RuntimeValueRules.StorageBudgetBytes;
    public const int RouteBucketCount = 301;
    public const int MaxRoutesPerBucket = 256;
    private const string OtherRoute = "other";
    public static readonly TimeSpan ProtectionWindow = TimeSpan.FromMinutes(5);
    public static readonly EventId StateTransitionEvent = new(470, "RuntimeObservabilityStateTransition");

    private readonly object _gate = new();
    private readonly bool _enabled;
    private readonly RuntimeEpoch _epoch;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RuntimeObservability>? _logger;
    private readonly Action<RuntimeStateTransition>? _transitionSink;
    private readonly Dictionary<DegradationSource, RuntimeDegradation> _activeDegradations = [];
    private readonly RouteBucket[] _routeBuckets = Enumerable.Range(0, RouteBucketCount)
        .Select(static _ => new RouteBucket())
        .ToArray();
    private readonly Meter _meter;
    private readonly Counter<long> _httpRequestCount;
    private readonly Histogram<double> _httpRequestDuration;
    private readonly Histogram<long> _httpRequestDatabaseCalls;
    private readonly Histogram<long> _httpRequestDownstreamCalls;
    private readonly Histogram<long> _pathCandidates;
    private readonly Histogram<long> _pathProcessed;
    private readonly Histogram<long> _pathTranscriptRecords;
    private readonly Counter<long> _spansReceived;
    private readonly Counter<long> _spansSaved;
    private readonly Counter<long> _spansRejected;
    private readonly Counter<long> _spansDropped;
    private readonly ObservableGauge<long> _storageUsage;
    private readonly ObservableGauge<long> _storageBudget;
    private readonly ObservableGauge<double> _storageGrowth;
    private readonly ObservableGauge<double> _processCpuUtilization;
    private readonly ObservableGauge<long> _processWorkingSet;
    private readonly ObservableGauge<long> _processGcHeap;
    private readonly long _storageBudgetBytes;
    private RuntimeProcessSnapshot _process = new(null, null, null);
    private RuntimeStorageSnapshot _storage;
    private long _receivedSpans;
    private long _savedSpans;
    private long _rejectedSpans;
    private long _droppedSpans;
    private RuntimeDegradation? _latestDegradation;
    private long _sequence;
    private bool _disposed;

    public RuntimeObservability(
        bool enabled,
        RuntimeEpoch epoch,
        TimeProvider timeProvider,
        ILogger<RuntimeObservability>? logger = null,
        IEnumerable<RuntimeDegradationSeed>? initialDegradations = null,
        long storageBudgetBytes = DefaultStorageBudgetBytes,
        Action<RuntimeStateTransition>? transitionSink = null)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegative(storageBudgetBytes);

        _enabled = enabled;
        _epoch = epoch;
        _timeProvider = timeProvider;
        _logger = logger;
        _transitionSink = transitionSink;
        _storageBudgetBytes = storageBudgetBytes;
        _storage = new RuntimeStorageSnapshot(null, storageBudgetBytes, null, null);
        _meter = RuntimeMetricCatalog.CreateMeter();

        _httpRequestCount = _meter.CreateCounter<long>(
            RuntimeMetricCatalog.HttpRequestCount,
            "{request}");
        _httpRequestDuration = _meter.CreateHistogram<double>(
            RuntimeMetricCatalog.HttpRequestDuration,
            "ms");
        _httpRequestDatabaseCalls = _meter.CreateHistogram<long>(
            RuntimeMetricCatalog.HttpRequestDatabaseCalls,
            "{call}");
        _httpRequestDownstreamCalls = _meter.CreateHistogram<long>(
            RuntimeMetricCatalog.HttpRequestDownstreamCalls,
            "{call}");
        _pathCandidates = _meter.CreateHistogram<long>(
            RuntimeMetricCatalog.PathCandidates,
            "{item}");
        _pathProcessed = _meter.CreateHistogram<long>(
            RuntimeMetricCatalog.PathProcessed,
            "{item}");
        _pathTranscriptRecords = _meter.CreateHistogram<long>(
            RuntimeMetricCatalog.PathTranscriptRecords,
            "{record}");
        _spansReceived = _meter.CreateCounter<long>(
            RuntimeMetricCatalog.SpansReceived,
            "{span}");
        _spansSaved = _meter.CreateCounter<long>(
            RuntimeMetricCatalog.SpansSaved,
            "{span}");
        _spansRejected = _meter.CreateCounter<long>(
            RuntimeMetricCatalog.SpansRejected,
            "{span}");
        _spansDropped = _meter.CreateCounter<long>(
            RuntimeMetricCatalog.SpansDropped,
            "{span}");

        _storageUsage = _meter.CreateObservableGauge<long>(
            RuntimeMetricCatalog.StorageUsage,
            ReadStorageUsage,
            "By");
        _storageBudget = _meter.CreateObservableGauge<long>(
            RuntimeMetricCatalog.StorageBudget,
            ReadStorageBudget,
            "By");
        _storageGrowth = _meter.CreateObservableGauge<double>(
            RuntimeMetricCatalog.StorageGrowth,
            ReadStorageGrowth,
            "By/s");
        _processCpuUtilization = _meter.CreateObservableGauge<double>(
            RuntimeMetricCatalog.ProcessCpuUtilization,
            ReadProcessCpuUtilization,
            "1");
        _processWorkingSet = _meter.CreateObservableGauge<long>(
            RuntimeMetricCatalog.ProcessWorkingSet,
            ReadProcessWorkingSet,
            "By");
        _processGcHeap = _meter.CreateObservableGauge<long>(
            RuntimeMetricCatalog.ProcessGcHeap,
            ReadProcessGcHeap,
            "By");

        var seeds = initialDegradations is null
            ? DefaultSeeds()
            : initialDegradations.ToArray();
        if (_enabled)
        {
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                foreach (var seed in seeds)
                    SeedLocked(seed, now);
            }
        }
    }

    public RuntimeObservability(
        RuntimeEpoch epoch,
        bool enabled,
        TimeProvider timeProvider,
        ILogger<RuntimeObservability>? logger = null,
        IEnumerable<RuntimeDegradationSeed>? initialDegradations = null,
        long storageBudgetBytes = DefaultStorageBudgetBytes,
        Action<RuntimeStateTransition>? transitionSink = null)
        : this(
            enabled,
            epoch,
            timeProvider,
            logger,
            initialDegradations,
            storageBudgetBytes,
            transitionSink)
    {
    }

    public RuntimeObservability(
        bool enabled,
        DateTimeOffset since,
        TimeProvider timeProvider,
        ILogger<RuntimeObservability>? logger = null,
        IEnumerable<RuntimeDegradationSeed>? initialDegradations = null,
        long storageBudgetBytes = DefaultStorageBudgetBytes,
        Action<RuntimeStateTransition>? transitionSink = null)
        : this(
            enabled,
            new RuntimeEpoch(since),
            timeProvider,
            logger,
            initialDegradations,
            storageBudgetBytes,
            transitionSink)
    {
    }

    public RuntimeObservability(
        OtelOptions options,
        RuntimeEpoch epoch,
        TimeProvider timeProvider,
        ILogger<RuntimeObservability>? logger = null,
        IEnumerable<RuntimeDegradationSeed>? initialDegradations = null,
        long storageBudgetBytes = DefaultStorageBudgetBytes,
        Action<RuntimeStateTransition>? transitionSink = null)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).Enabled,
            epoch,
            timeProvider,
            logger,
            initialDegradations,
            storageBudgetBytes,
            transitionSink)
    {
    }

    public RuntimeEpoch Epoch => _epoch;
    public DateTimeOffset Since => _epoch.Since;
    public Meter Meter => _meter;
    public bool Enabled => _enabled;

    public RuntimeRequestFact CompleteRequest(RuntimeRequestFact fact)
    {
        var normalized = Normalize(fact);
        if (!_enabled)
            return normalized;

        var tags = new TagList
        {
            { "http.route", normalized.Route! },
            { "http.request.method", normalized.Method! },
            { "http.response.status_code", normalized.StatusCode },
        };
        lock (_gate)
        {
            if (_disposed)
                return normalized;
            _httpRequestCount.Add(1, tags);
            _httpRequestDuration.Record(normalized.DurationMilliseconds, tags);
            _httpRequestDatabaseCalls.Record(normalized.DatabaseCalls, tags);
            _httpRequestDownstreamCalls.Record(normalized.DownstreamCalls, tags);
            RecordRouteLocked(normalized, _timeProvider.GetUtcNow().ToUnixTimeSeconds());
        }
        return normalized;
    }

    public RuntimeRequestFact CompleteRequest(RequestFact fact) =>
        CompleteRequest(fact.ToRuntime());

    public RuntimeRequestFact CompleteRequest(
        string? route,
        string? method,
        int statusCode,
        double durationMilliseconds,
        long databaseCalls,
        long downstreamCalls) =>
        CompleteRequest(new RuntimeRequestFact(
            route,
            method,
            statusCode,
            durationMilliseconds,
            databaseCalls,
            downstreamCalls));

    public void RecordAgentPath(RuntimeAgentPathFact fact)
    {
        var path = NormalizeAgentPath(fact.Path);
        if (path is null || !_enabled)
            return;

        var candidates = RuntimeValueRules.NonNegative(fact.Candidates);
        var processed = RuntimeValueRules.NonNegative(fact.Processed);
        var transcriptRecords = RuntimeValueRules.NonNegative(fact.TranscriptRecords);
        var tags = new TagList { { "mohist.path", path } };
        lock (_gate)
        {
            if (_disposed)
                return;
            _pathCandidates.Record(candidates, tags);
            _pathProcessed.Record(processed, tags);
            _pathTranscriptRecords.Record(transcriptRecords, tags);
        }
    }

    public void RecordAgentPath(AgentPathFact fact) => RecordAgentPath(fact.ToRuntime());

    public void RecordAgentPath(
        string? path,
        long candidates,
        long processed,
        long transcriptRecords) =>
        RecordAgentPath(new RuntimeAgentPathFact(
            path,
            candidates,
            processed,
            transcriptRecords));

    public IngestOutcome RecordIngest(IngestOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        List<RuntimeStateTransition> transitions;
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            transitions = EvaluateAndApplyLocked(now, () =>
            {
                _receivedSpans = RuntimeValueRules.Add(_receivedSpans, outcome.Received);
                _savedSpans = RuntimeValueRules.Add(_savedSpans, outcome.Saved);
                _rejectedSpans = RuntimeValueRules.Add(_rejectedSpans, outcome.Rejected);
                _droppedSpans = RuntimeValueRules.Add(_droppedSpans, outcome.Dropped);

                RuntimeDegradation? activation = null;
                RuntimeDegradation? cleared = null;
                if (outcome.ClearsStorageWrite)
                    cleared = ClearLocked(DegradationSource.StorageWrite);
                if (outcome.ActivatesStorageWrite)
                {
                    activation = ActivateLocked(
                        DegradationSource.StorageWrite,
                        RuntimeDegradationCodes.StorageWriteFailed,
                        outcome.WriteResult.Reason,
                        now,
                        null);
                }
                if (outcome.ActivatesProtection)
                {
                    if (_activeDegradations.TryGetValue(DegradationSource.IngestProtection, out var existing)
                        && existing.Code == RuntimeDegradationCodes.StorageBudgetExhausted)
                    {
                        activation = ActivateLocked(
                            DegradationSource.IngestProtection,
                            RuntimeDegradationCodes.StorageBudgetExhausted,
                            existing.Message,
                            now,
                            now.Add(ProtectionWindow));
                    }
                    else
                    {
                        activation = ActivateLocked(
                            DegradationSource.IngestProtection,
                            outcome.ProtectionCode ?? RuntimeDegradationCodes.TelemetryRejected,
                            outcome.ProtectionReason,
                            now,
                            now.Add(ProtectionWindow));
                    }
                }
                return new MutationReason(activation, cleared);
            });

            if (_enabled && !_disposed)
            {
                AddCounter(_spansReceived, outcome.Received);
                AddCounter(_spansSaved, outcome.Saved);
                AddCounter(_spansRejected, outcome.Rejected);
                AddCounter(_spansDropped, outcome.Dropped);
            }
        }
        EmitTransitions(transitions);
        return outcome;
    }

    public void PublishProcess(ProcessSampleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        List<RuntimeStateTransition> transitions;
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            transitions = EvaluateAndApplyLocked(now, () =>
            {
                if (result.IsSuccess)
                {
                    _process = new RuntimeProcessSnapshot(
                        RuntimeValueRules.BoundedRatio(result.CpuUtilization),
                        Math.Max(0, result.WorkingSetBytes),
                        Math.Max(0, result.GcHeapBytes));
                    return new MutationReason(
                        null,
                        ClearLocked(DegradationSource.ProcessRead));
                }

                _process = new RuntimeProcessSnapshot(null, null, null);
                return new MutationReason(
                    ActivateLocked(
                        DegradationSource.ProcessRead,
                        RuntimeDegradationCodes.ProcessReadFailed,
                        result.FailureReason,
                        now,
                        null),
                    null);
            });
        }
        EmitTransitions(transitions);
    }

    public void PublishStorage(StorageProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        List<RuntimeStateTransition> transitions;
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            transitions = EvaluateAndApplyLocked(now, () =>
            {
                if (result.IsSuccess)
                {
                    _storage = new RuntimeStorageSnapshot(
                        result.UsageBytes,
                        _storageBudgetBytes,
                        result.GrowthBytesPerSecond,
                        result.GrowthWindowSeconds);
                    return new MutationReason(
                        null,
                        ClearLocked(DegradationSource.StorageRead));
                }

                _storage = new RuntimeStorageSnapshot(
                    null,
                    _storageBudgetBytes,
                    null,
                    null);
                return new MutationReason(
                    ActivateLocked(
                        DegradationSource.StorageRead,
                        RuntimeDegradationCodes.StorageReadFailed,
                        result.FailureReason,
                        now,
                        null),
                    null);
            });
        }
        EmitTransitions(transitions);
    }

    public void PublishCollector(CollectorResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        List<RuntimeStateTransition> transitions;
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            transitions = EvaluateAndApplyLocked(now, () =>
            {
                if (result.IsOnline)
                {
                    return new MutationReason(
                        null,
                        ClearLocked(DegradationSource.Collector));
                }

                var code = result.FailureCode;
                if (code is null || !RuntimeDegradationCodes.IsValidFor(DegradationSource.Collector, code))
                    code = RuntimeDegradationCodes.CollectorBindFailed;
                return new MutationReason(
                    ActivateLocked(
                        DegradationSource.Collector,
                        code,
                        result.FailureReason,
                        now,
                        null),
                    null);
            });
        }
        EmitTransitions(transitions);
    }

    /// <summary>
    /// Publishes or clears the <c>storage_budget_exhausted</c>
    /// degradation reason on the <c>IngestProtection</c> source under
    /// the same state lock as <see cref="RecordIngest"/>, so the more
    /// specific reason deterministically dominates
    /// <c>latest_degradation</c> while admission is closed and the
    /// off/healthy/degraded tri-state contract is preserved.
    /// </summary>
    public void PublishStorageBudgetExhausted(bool exhausted, DateTimeOffset? at = null, string? message = null)
    {
        List<RuntimeStateTransition> transitions;
        var now = at ?? _timeProvider.GetUtcNow();
        lock (_gate)
        {
            transitions = EvaluateAndApplyLocked(now, () =>
            {
                if (exhausted)
                {
                    return new MutationReason(
                        ActivateLocked(
                            DegradationSource.IngestProtection,
                            RuntimeDegradationCodes.StorageBudgetExhausted,
                            message,
                            now,
                            now.Add(ProtectionWindow)),
                        null);
                }

                if (_activeDegradations.TryGetValue(DegradationSource.IngestProtection, out var existing)
                    && existing.Code == RuntimeDegradationCodes.StorageBudgetExhausted)
                {
                    return new MutationReason(
                        null,
                        ClearLocked(DegradationSource.IngestProtection));
                }

                return MutationReason.None;
            });
        }
        EmitTransitions(transitions);
    }

    public RuntimeObservabilitySnapshot GetSnapshot()
    {
        List<RuntimeStateTransition> transitions;
        IReadOnlyList<RouteAggregate> routeCopy;
        RuntimeState status;
        bool collectorOnline;
        RuntimeStorageSnapshot storage;
        RuntimeTelemetrySnapshot telemetry;
        RuntimeProcessSnapshot process;
        RuntimeDegradation? latestDegradation;
        IReadOnlyDictionary<DegradationSource, RuntimeDegradation> activeDegradations;
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            transitions = EvaluateAndApplyLocked(now, static () => MutationReason.None);
            status = ProjectStateLocked();
            collectorOnline = _enabled && !_activeDegradations.ContainsKey(DegradationSource.Collector);
            storage = _storage;
            telemetry = new RuntimeTelemetrySnapshot(
                _receivedSpans,
                _savedSpans,
                _rejectedSpans,
                _droppedSpans);
            process = _process;
            latestDegradation = _latestDegradation;
            activeDegradations = RuntimeObservabilitySnapshot.CopyDegradations(_activeDegradations);
            routeCopy = CopyRoutesLocked(now);
        }
        var snapshot = new RuntimeObservabilitySnapshot(
            status,
            collectorOnline,
            _epoch.Since,
            storage,
            telemetry,
            process,
            latestDegradation,
            RankRoutes(routeCopy),
            activeDegradations);
        EmitTransitions(transitions);
        return snapshot;
    }

    public bool HasActiveDegradation(DegradationSource source)
    {
        lock (_gate)
            return _activeDegradations.ContainsKey(source);
    }

    public bool HasActiveDegradation(RuntimeDegradationSource source) =>
        HasActiveDegradation((DegradationSource)source);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _meter.Dispose();
    }

    public static string NormalizeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return "unmatched";

        var value = route.Trim();
        if (value.Length > 128 || value.Contains("?", StringComparison.Ordinal) || value.Contains("#", StringComparison.Ordinal))
            return "unmatched";
        if (value.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(value, UriKind.Absolute, out _))
            return "unmatched";

        var segments = value.Split('/', StringSplitOptions.None);
        if (segments.Length > 1 && segments[0].Length == 0)
        {
            for (var i = 1; i < segments.Length; i++)
            {
                if (segments[i].Length == 0)
                    continue;
                if (IsTemplateSegment(segments[i]))
                {
                    if (!IsSafeRouteSegment(segments[i]))
                        return "unmatched";
                    continue;
                }
                if (LooksLikeIdentity(segments[i]) || IsAfterIdentityParent(segments, i))
                    segments[i] = "{id}";
                else if (!IsSafeRouteSegment(segments[i]))
                    return "unmatched";
            }
            value = string.Join('/', segments);
        }
        else if (!IsSafeRouteSegment(value))
        {
            return "unmatched";
        }

        return value.Length is > 0 and <= 128 ? value : "unmatched";
    }

    public static string NormalizeMethod(string? method)
    {
        var value = method?.Trim().ToUpperInvariant();
        return value is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"
            ? value
            : "OTHER";
    }

    public static int NormalizeStatusCode(int statusCode) =>
        statusCode is >= 100 and <= 599 ? statusCode : 0;

    public static string? NormalizeAgentPath(string? path)
    {
        var value = path?.Trim().ToLowerInvariant();
        return value is "agent.status" or "agent.activity" ? value : null;
    }

    private RuntimeRequestFact Normalize(RuntimeRequestFact fact) =>
        new(
            NormalizeRoute(fact.Route),
            NormalizeMethod(fact.Method),
            NormalizeStatusCode(fact.StatusCode),
            NormalizeDuration(fact.DurationMilliseconds),
            RuntimeValueRules.NonNegative(fact.DatabaseCalls),
            RuntimeValueRules.NonNegative(fact.DownstreamCalls));

    private void RecordRouteLocked(RuntimeRequestFact fact, long second)
    {
        var bucket = _routeBuckets[BucketIndex(second)];
        if (bucket.Second != second)
        {
            bucket.Second = second;
            bucket.Routes.Clear();
            bucket.Other = null;
        }

        if (!bucket.Routes.TryGetValue(fact.Route!, out var aggregate))
        {
            if (bucket.Routes.Count >= MaxRoutesPerBucket)
            {
                bucket.Other ??= new RouteAggregateState();
                aggregate = bucket.Other;
            }
            else
            {
                aggregate = new RouteAggregateState();
                bucket.Routes.Add(fact.Route!, aggregate);
            }
        }

        aggregate.Add(fact);
    }

    private IReadOnlyList<RouteAggregate> CopyRoutesLocked(DateTimeOffset now)
    {
        var oldest = now - ProtectionWindow;
        var copy = new List<RouteAggregate>(RouteBucketCount * (MaxRoutesPerBucket + 1));
        foreach (var bucket in _routeBuckets)
        {
            if (bucket.Second is not { } second)
                continue;

            var start = DateTimeOffset.FromUnixTimeSeconds(second);
            if (start.AddSeconds(1) <= oldest || start > now)
                continue;

            foreach (var (route, aggregate) in bucket.Routes)
                copy.Add(new RouteAggregate(route, aggregate.Copy()));
            if (bucket.Other is not null)
                copy.Add(new RouteAggregate(OtherRoute, bucket.Other.Copy()));
        }
        return copy;
    }

    private static IReadOnlyList<RuntimeRouteSnapshot> RankRoutes(IReadOnlyList<RouteAggregate> values)
    {
        var merged = new Dictionary<string, RouteAggregateState>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!merged.TryGetValue(value.Route, out var aggregate))
            {
                aggregate = new RouteAggregateState();
                merged.Add(value.Route, aggregate);
            }
            aggregate.Merge(value.Values);
        }

        return merged
            .Select(static pair =>
            {
                var aggregate = pair.Value;
                var average = aggregate.TotalDuration / aggregate.RequestCount;
                return new RuntimeRouteSnapshot(
                    pair.Key,
                    aggregate.RequestCount,
                    average,
                    aggregate.MaxDuration,
                    (double)aggregate.DatabaseCalls / aggregate.RequestCount,
                    (double)aggregate.DownstreamCalls / aggregate.RequestCount);
            })
            .OrderByDescending(static route => route.DatabaseCallsPerRequest + route.DownstreamCallsPerRequest)
            .ThenByDescending(static route => route.AverageDurationMilliseconds)
            .ThenBy(static route => route.Route, StringComparer.Ordinal)
            .Take(10)
            .ToArray();
    }

    private static int BucketIndex(long second) => (int)((second % RouteBucketCount + RouteBucketCount) % RouteBucketCount);

    private static double NormalizeDuration(double duration)
    {
        if (double.IsNaN(duration) || double.IsInfinity(duration))
            return 0;
        return Math.Max(0, duration);
    }

    private static bool IsTemplateSegment(string value) =>
        value.StartsWith('{') && value.EndsWith('}') && value.Length > 2;

    private static bool IsSafeRouteSegment(string value)
    {
        if (value.StartsWith('{') && value.EndsWith('}') && value.Length > 2)
            return value[1..^1].All(char.IsLetterOrDigit);
        return value.All(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '~');
    }

    private static bool LooksLikeIdentity(string value)
    {
        if (value.All(char.IsDigit))
            return true;
        if (value.StartsWith("proj_", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("wr_", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("session_", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("trace_", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("span_", StringComparison.OrdinalIgnoreCase))
            return true;
        return Guid.TryParse(value, out _);
    }

    private static bool IsAfterIdentityParent(string[] segments, int index)
    {
        if (index == 0)
            return false;
        var parent = segments[index - 1];
        return parent is "projects" or "project" or "issues" or "issue" or "workflows" or "workflow-runs"
            or "sessions" or "session" or "agents" or "agent";
    }

    private IEnumerable<RuntimeDegradationSeed> DefaultSeeds() =>
    [
        RuntimeDegradationSeed.CollectorUnverified(),
        RuntimeDegradationSeed.StorageUnverified(),
    ];

    private void SeedLocked(RuntimeDegradationSeed seed, DateTimeOffset now)
    {
        if (_activeDegradations.ContainsKey(seed.Source))
            throw new ArgumentException($"Duplicate degradation source {seed.Source}.", nameof(seed));
        var at = seed.At ?? now;
        var degradation = new RuntimeDegradation(
            seed.Source,
            seed.Code,
            seed.Message,
            at,
            ++_sequence,
            null);
        _activeDegradations.Add(seed.Source, degradation);
        _latestDegradation = degradation;
    }

    private RuntimeDegradation ActivateLocked(
        DegradationSource source,
        string code,
        string? message,
        DateTimeOffset now,
        DateTimeOffset? expiresAt)
    {
        if (!RuntimeDegradationCodes.IsValidFor(source, code))
            code = RuntimeDegradationCodes.ForSource(source);
        var degradation = new RuntimeDegradation(
            source,
            code,
            RuntimeValueRules.BoundedReason(code, message),
            now,
            ++_sequence,
            expiresAt);
        _activeDegradations[source] = degradation;
        _latestDegradation = degradation;
        return degradation;
    }

    private RuntimeDegradation? ClearLocked(DegradationSource source)
    {
        if (!_activeDegradations.Remove(source, out var existing))
            return null;
        return existing;
    }

    private List<RuntimeStateTransition> EvaluateAndApplyLocked(
        DateTimeOffset now,
        Func<MutationReason> mutation)
    {
        var transitions = new List<RuntimeStateTransition>(2);
        var expired = ExpireProtectionLocked(now);
        if (expired is not null)
            AddTransitionLocked(
                transitions,
                expired.Value.PreviousState,
                ProjectStateLocked(),
                null,
                expired.Value.Cleared,
                now);

        var previous = ProjectStateLocked();
        var reason = mutation();
        var next = ProjectStateLocked();
        AddTransitionLocked(transitions, previous, next, reason.Activation, reason.Cleared, now);
        return transitions;
    }

    private ExpirationResult? ExpireProtectionLocked(DateTimeOffset now)
    {
        if (!_activeDegradations.TryGetValue(DegradationSource.IngestProtection, out var active)
            || !active.ExpiresAt.HasValue
            || active.ExpiresAt.Value > now)
            return null;

        var previous = ProjectStateLocked();
        _activeDegradations.Remove(DegradationSource.IngestProtection);
        return new ExpirationResult(previous, active);
    }

    private void AddTransitionLocked(
        List<RuntimeStateTransition> transitions,
        RuntimeState previous,
        RuntimeState next,
        RuntimeDegradation? activation = null,
        RuntimeDegradation? cleared = null,
        DateTimeOffset? at = null)
    {
        if (previous == next)
            return;
        if (previous == RuntimeState.Healthy && next == RuntimeState.Degraded)
        {
            var reason = activation ?? LatestActiveLocked() ?? _latestDegradation;
            if (reason is not null)
            {
                transitions.Add(new RuntimeStateTransition(
                    previous,
                    next,
                    reason.Code,
                    reason.Message,
                    reason.At));
            }
            return;
        }
        if (previous == RuntimeState.Degraded && next == RuntimeState.Healthy)
        {
            var reason = cleared ?? _latestDegradation;
            if (reason is not null)
            {
                transitions.Add(new RuntimeStateTransition(
                    previous,
                    next,
                    reason.Code,
                    reason.Message,
                    at ?? _timeProvider.GetUtcNow()));
            }
        }
    }

    private RuntimeDegradation? LatestActiveLocked() =>
        _activeDegradations.Values
            .OrderByDescending(static value => value.Sequence)
            .FirstOrDefault();

    private RuntimeState ProjectStateLocked() =>
        !_enabled
            ? RuntimeState.Off
            : _activeDegradations.Count == 0
                ? RuntimeState.Healthy
                : RuntimeState.Degraded;

    internal void ResetTelemetryCountersForTesting()
    {
        lock (_gate)
        {
            _receivedSpans = 0;
            _savedSpans = 0;
            _rejectedSpans = 0;
            _droppedSpans = 0;
        }
    }

    private bool IsDisposed()
    {
        lock (_gate)
            return _disposed;
    }

    private static void AddCounter(Counter<long> counter, long value)
    {
        if (value > 0)
            counter.Add(value);
    }

    private IEnumerable<Measurement<long>> ReadStorageUsage()
    {
        RuntimeStorageSnapshot storage;
        lock (_gate)
        {
            if (!_enabled || _disposed || !_storage.UsageBytes.HasValue)
                return [];
            storage = _storage;
        }
        return [new Measurement<long>(storage.UsageBytes!.Value)];
    }

    private IEnumerable<Measurement<long>> ReadStorageBudget()
    {
        lock (_gate)
        {
            if (!_enabled || _disposed)
                return [];
            return [new Measurement<long>(_storageBudgetBytes)];
        }
    }

    private IEnumerable<Measurement<double>> ReadStorageGrowth()
    {
        RuntimeStorageSnapshot storage;
        lock (_gate)
        {
            if (!_enabled || _disposed || ! _storage.GrowthBytesPerSecond.HasValue)
                return [];
            storage = _storage;
        }
        return [new Measurement<double>(storage.GrowthBytesPerSecond!.Value)];
    }

    private IEnumerable<Measurement<double>> ReadProcessCpuUtilization()
    {
        RuntimeProcessSnapshot process;
        lock (_gate)
        {
            if (!_enabled || _disposed || !_process.CpuUtilization.HasValue)
                return [];
            process = _process;
        }
        return [new Measurement<double>(process.CpuUtilization!.Value)];
    }

    private IEnumerable<Measurement<long>> ReadProcessWorkingSet()
    {
        RuntimeProcessSnapshot process;
        lock (_gate)
        {
            if (!_enabled || _disposed || !_process.WorkingSetBytes.HasValue)
                return [];
            process = _process;
        }
        return [new Measurement<long>(process.WorkingSetBytes!.Value)];
    }

    private IEnumerable<Measurement<long>> ReadProcessGcHeap()
    {
        RuntimeProcessSnapshot process;
        lock (_gate)
        {
            if (!_enabled || _disposed || !_process.GcHeapBytes.HasValue)
                return [];
            process = _process;
        }
        return [new Measurement<long>(process.GcHeapBytes!.Value)];
    }

    private void EmitTransitions(IEnumerable<RuntimeStateTransition> transitions)
    {
        foreach (var transition in transitions)
        {
            try
            {
                _logger?.LogInformation(
                    StateTransitionEvent,
                    "Runtime observability state transition {PreviousState} to {NewState}; reason {ReasonCode}: {Reason}",
                    transition.PreviousState,
                    transition.NewState,
                    transition.ReasonCode,
                    transition.Reason);
                _transitionSink?.Invoke(transition);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to emit runtime state transition");
            }
        }
    }

    private readonly record struct MutationReason(
        RuntimeDegradation? Activation,
        RuntimeDegradation? Cleared)
    {
        public static MutationReason None => new(null, null);
    }

    private readonly record struct ExpirationResult(
        RuntimeState PreviousState,
        RuntimeDegradation Cleared);

    private sealed class RouteBucket
    {
        public long? Second { get; set; }
        public Dictionary<string, RouteAggregateState> Routes { get; } = new(StringComparer.Ordinal);
        public RouteAggregateState? Other { get; set; }
    }

    private sealed class RouteAggregateState
    {
        public long RequestCount { get; private set; }
        public double TotalDuration { get; private set; }
        public double MaxDuration { get; private set; }
        public long DatabaseCalls { get; private set; }
        public long DownstreamCalls { get; private set; }

        public void Add(RuntimeRequestFact fact)
        {
            RequestCount = RuntimeValueRules.Add(RequestCount, 1);
            TotalDuration += fact.DurationMilliseconds;
            MaxDuration = Math.Max(MaxDuration, fact.DurationMilliseconds);
            DatabaseCalls = RuntimeValueRules.Add(DatabaseCalls, fact.DatabaseCalls);
            DownstreamCalls = RuntimeValueRules.Add(DownstreamCalls, fact.DownstreamCalls);
        }

        public void Merge(RouteAggregateState other)
        {
            RequestCount = RuntimeValueRules.Add(RequestCount, other.RequestCount);
            TotalDuration += other.TotalDuration;
            MaxDuration = Math.Max(MaxDuration, other.MaxDuration);
            DatabaseCalls = RuntimeValueRules.Add(DatabaseCalls, other.DatabaseCalls);
            DownstreamCalls = RuntimeValueRules.Add(DownstreamCalls, other.DownstreamCalls);
        }

        public RouteAggregateState Copy() => new()
        {
            RequestCount = RequestCount,
            TotalDuration = TotalDuration,
            MaxDuration = MaxDuration,
            DatabaseCalls = DatabaseCalls,
            DownstreamCalls = DownstreamCalls,
        };
    }

    private readonly record struct RouteAggregate(string Route, RouteAggregateState Values);
}
