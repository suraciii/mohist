using System.Collections.ObjectModel;

namespace Mohist.Server.Otel;

public enum RuntimeState
{
    Off,
    Healthy,
    Degraded,
}

public enum DegradationSource
{
    Collector,
    ProcessRead,
    StorageRead,
    StorageWrite,
    IngestProtection,
}

public enum RuntimeDegradationSource
{
    Collector,
    ProcessRead,
    StorageRead,
    StorageWrite,
    IngestProtection,
}

public enum IngestWriteResultKind
{
    NotAttempted,
    Committed,
    RolledBack,
    Cancelled,
}

public enum IngestResponseDisposition
{
    Success,
    PartialSuccess,
    RetryableFailure,
    Cancelled,
}

public enum IngestDisposition
{
    Success,
    PartialSuccess,
    RetryableFailure,
    Cancelled,
}

public static class RuntimeDegradationCodes
{
    public const string CollectorUnverified = "collector_unverified";
    public const string CollectorBindFailed = "collector_bind_failed";
    public const string ProcessReadFailed = "process_read_failed";
    public const string StorageUnverified = "storage_unverified";
    public const string StorageReadFailed = "storage_read_failed";
    public const string StorageWriteFailed = "storage_write_failed";
    public const string TelemetryRejected = "telemetry_rejected";
    public const string TelemetryDropped = "telemetry_dropped";

    public static string DefaultMessage(string code) => code switch
    {
        CollectorUnverified => "OTel collector readiness has not been verified",
        CollectorBindFailed => "OTel collector failed to bind",
        ProcessReadFailed => "Process resources could not be sampled",
        StorageUnverified => "OTel storage write readiness has not been verified",
        StorageReadFailed => "OTel storage metadata could not be read",
        StorageWriteFailed => "OTel storage write failed",
        TelemetryRejected => "Telemetry is being rejected",
        TelemetryDropped => "Telemetry is being dropped",
        _ => "Runtime observability is degraded",
    };

    public static string ForSource(DegradationSource source) => source switch
    {
        DegradationSource.Collector => CollectorBindFailed,
        DegradationSource.ProcessRead => ProcessReadFailed,
        DegradationSource.StorageRead => StorageReadFailed,
        DegradationSource.StorageWrite => StorageWriteFailed,
        DegradationSource.IngestProtection => TelemetryRejected,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static bool IsValidFor(DegradationSource source, string code) => source switch
    {
        DegradationSource.Collector => code is CollectorUnverified or CollectorBindFailed,
        DegradationSource.ProcessRead => code == ProcessReadFailed,
        DegradationSource.StorageRead => code == StorageReadFailed,
        DegradationSource.StorageWrite => code is StorageUnverified or StorageWriteFailed,
        DegradationSource.IngestProtection => code is TelemetryRejected or TelemetryDropped,
        _ => false,
    };
}

public sealed record RuntimeDegradationSeed
{
    public RuntimeDegradationSeed(
        DegradationSource source,
        string code,
        string? message = null,
        DateTimeOffset? at = null)
    {
        if (!RuntimeDegradationCodes.IsValidFor(source, code))
            throw new ArgumentException($"Reason code '{code}' is not valid for {source}.", nameof(code));

        Source = source;
        Code = code;
        Message = RuntimeValueRules.BoundedReason(code, message);
        At = at;
    }

    public RuntimeDegradationSeed(
        RuntimeDegradationSource source,
        string code,
        string? message = null,
        DateTimeOffset? at = null)
        : this((DegradationSource)source, code, message, at)
    {
    }

    public DegradationSource Source { get; }
    public string Code { get; }
    public string Message { get; }
    public DateTimeOffset? At { get; }

    public static RuntimeDegradationSeed CollectorUnverified(DateTimeOffset? at = null) =>
        new(DegradationSource.Collector, RuntimeDegradationCodes.CollectorUnverified, at: at);

    public static RuntimeDegradationSeed CollectorBindFailed(string? message = null, DateTimeOffset? at = null) =>
        new(DegradationSource.Collector, RuntimeDegradationCodes.CollectorBindFailed, message, at);

    public static RuntimeDegradationSeed StorageUnverified(DateTimeOffset? at = null) =>
        new(DegradationSource.StorageWrite, RuntimeDegradationCodes.StorageUnverified, at: at);
}

public sealed record RuntimeDegradation
{
    internal RuntimeDegradation(
        DegradationSource source,
        string code,
        string message,
        DateTimeOffset at,
        long sequence,
        DateTimeOffset? expiresAt)
    {
        Source = source;
        Code = code;
        Message = message;
        At = at;
        Sequence = sequence;
        ExpiresAt = expiresAt;
    }

    public DegradationSource Source { get; }
    public string Code { get; }
    public string Message { get; }
    public DateTimeOffset At { get; }
    public long Sequence { get; }
    public DateTimeOffset? ExpiresAt { get; }
}

public sealed record RuntimeStateTransition(
    RuntimeState PreviousState,
    RuntimeState NewState,
    string ReasonCode,
    string Reason,
    DateTimeOffset At);

public sealed record ProcessSampleResult
{
    private ProcessSampleResult(
        bool succeeded,
        TimeSpan totalCpuTime,
        long workingSetBytes,
        long gcHeapBytes,
        int processorCount,
        double? cpuUtilization,
        string? failureReason)
    {
        IsSuccess = succeeded;
        TotalCpuTime = totalCpuTime;
        WorkingSetBytes = workingSetBytes;
        GcHeapBytes = gcHeapBytes;
        ProcessorCount = processorCount;
        CpuUtilization = cpuUtilization;
        FailureReason = failureReason;
    }

    public bool IsSuccess { get; }
    public bool Succeeded => IsSuccess;
    public TimeSpan TotalCpuTime { get; }
    public long TotalCpuTimeTicks => TotalCpuTime.Ticks;
    public long WorkingSetBytes { get; }
    public long GcHeapBytes { get; }
    public int ProcessorCount { get; }
    public double? CpuUtilization { get; }
    public string? FailureReason { get; }

    public static ProcessSampleResult Success(
        TimeSpan totalCpuTime,
        long workingSetBytes,
        long gcHeapBytes,
        int processorCount,
        double? cpuUtilization = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workingSetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(gcHeapBytes);
        if (processorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(processorCount));
        return new(
            true,
            totalCpuTime,
            workingSetBytes,
            gcHeapBytes,
            processorCount,
            RuntimeValueRules.BoundedRatio(cpuUtilization),
            null);
    }

    public static ProcessSampleResult Success(
        long totalCpuTimeTicks,
        long workingSetBytes,
        long gcHeapBytes,
        int processorCount,
        double? cpuUtilization = null) =>
        Success(
            TimeSpan.FromTicks(totalCpuTimeTicks),
            workingSetBytes,
            gcHeapBytes,
            processorCount,
            cpuUtilization);

    public static ProcessSampleResult Failure(string? reason = null) =>
        new(false, default, 0, 0, 0, null, reason);

    public static ProcessSampleResult Failed(string? reason = null) => Failure(reason);

    public static ProcessSampleResult Unavailable(string? reason = null) => Failure(reason);
}

public sealed record StorageProbeResult
{
    private StorageProbeResult(
        bool succeeded,
        long? usageBytes,
        double? growthBytesPerSecond,
        double? growthWindowSeconds,
        string? failureReason)
    {
        IsSuccess = succeeded;
        UsageBytes = usageBytes;
        GrowthBytesPerSecond = growthBytesPerSecond;
        GrowthWindowSeconds = growthWindowSeconds;
        FailureReason = failureReason;
    }

    public bool IsSuccess { get; }
    public bool Succeeded => IsSuccess;
    public long? UsageBytes { get; }
    public double? GrowthBytesPerSecond { get; }
    public double? GrowthWindowSeconds { get; }
    public string? FailureReason { get; }

    public static StorageProbeResult Success(
        long usageBytes,
        double? growthBytesPerSecond = null,
        double? growthWindowSeconds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usageBytes);
        return new(
            true,
            usageBytes,
            RuntimeValueRules.NonNegativeFinite(growthBytesPerSecond),
            RuntimeValueRules.NonNegativeFinite(growthWindowSeconds),
            null);
    }

    public static StorageProbeResult Failure(string? reason = null) =>
        new(false, null, null, null, reason);

    public static StorageProbeResult Failed(string? reason = null) => Failure(reason);

    public static StorageProbeResult Unavailable(string? reason = null) => Failure(reason);
}

public sealed record CollectorResult
{
    private CollectorResult(bool online, string? code, string? message)
    {
        IsOnline = online;
        FailureCode = code;
        FailureReason = message;
    }

    public bool IsOnline { get; }
    public bool IsBound => IsOnline;
    public string? FailureCode { get; }
    public string? FailureReason { get; }

    public static CollectorResult Online() => new(true, null, null);

    public static CollectorResult Bound() => Online();

    public static CollectorResult Unverified() =>
        new(false, RuntimeDegradationCodes.CollectorUnverified, null);

    public static CollectorResult BindFailed(string? reason = null) =>
        new(false, RuntimeDegradationCodes.CollectorBindFailed, reason);

    public static CollectorResult Failed(string? reason = null) => BindFailed(reason);
}

public readonly record struct RuntimeRequestFact(
    string? Route,
    string? Method,
    int StatusCode,
    double DurationMilliseconds,
    long DatabaseCalls,
    long DownstreamCalls);

public readonly record struct RequestFact(
    string? Route,
    string? Method,
    int StatusCode,
    double DurationMilliseconds,
    long DatabaseCalls,
    long DownstreamCalls)
{
    public RuntimeRequestFact ToRuntime() => new(
        Route,
        Method,
        StatusCode,
        DurationMilliseconds,
        DatabaseCalls,
        DownstreamCalls);
}

public readonly record struct RuntimeAgentPathFact(
    string? Path,
    long Candidates,
    long Processed,
    long TranscriptRecords);

public readonly record struct AgentPathFact(
    string? Path,
    long Candidates,
    long Processed,
    long TranscriptRecords)
{
    public RuntimeAgentPathFact ToRuntime() => new(
        Path,
        Candidates,
        Processed,
        TranscriptRecords);
}

public sealed record ClassifiedBatchTotals
{
    public ClassifiedBatchTotals(
        long parsedForWrite,
        long protectionRejected,
        long malformedDropped,
        long otherDropped)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(parsedForWrite);
        ArgumentOutOfRangeException.ThrowIfNegative(protectionRejected);
        ArgumentOutOfRangeException.ThrowIfNegative(malformedDropped);
        ArgumentOutOfRangeException.ThrowIfNegative(otherDropped);
        ParsedForWrite = parsedForWrite;
        ProtectionRejected = protectionRejected;
        MalformedDropped = malformedDropped;
        OtherDropped = otherDropped;
    }

    public long ParsedForWrite { get; }
    public long Parsed => ParsedForWrite;
    public long ProtectionRejected { get; }
    public long Rejected => ProtectionRejected;
    public long MalformedDropped { get; }
    public long OtherDropped { get; }
    public long Dropped => RuntimeValueRules.Add( MalformedDropped, OtherDropped);
    public long Received => RuntimeValueRules.Add(ParsedForWrite, ProtectionRejected);
    public long TotalAttempts => RuntimeValueRules.Add(Received, Dropped);

    public static ClassifiedBatchTotals Empty { get; } = new(0, 0, 0, 0);
}

public sealed record IngestBatchTotals
{
    public IngestBatchTotals(
        long parsedForWrite,
        long protectionRejected,
        long malformedDropped,
        long otherDropped)
        : this(new ClassifiedBatchTotals(
            parsedForWrite,
            protectionRejected,
            malformedDropped,
            otherDropped))
    {
    }

    public IngestBatchTotals(ClassifiedBatchTotals totals)
    {
        Totals = totals ?? throw new ArgumentNullException(nameof(totals));
    }

    public ClassifiedBatchTotals Totals { get; }
    public long ParsedForWrite => Totals.ParsedForWrite;
    public long ProtectionRejected => Totals.ProtectionRejected;
    public long MalformedDropped => Totals.MalformedDropped;
    public long OtherDropped => Totals.OtherDropped;
}

public sealed record IngestWriteResult
{
    private IngestWriteResult(
        IngestWriteResultKind kind,
        bool enteredProductionWritePath,
        string? reason)
    {
        Kind = kind;
        EnteredProductionWritePath = enteredProductionWritePath;
        Reason = kind == IngestWriteResultKind.RolledBack
            ? RuntimeValueRules.BoundedReason(RuntimeDegradationCodes.StorageWriteFailed, reason)
            : null;
    }

    public IngestWriteResultKind Kind { get; }
    public bool EnteredProductionWritePath { get; }
    public bool EnteredWritePath => EnteredProductionWritePath;
    public string? Reason { get; }
    public bool IsRetryable => Kind == IngestWriteResultKind.RolledBack;

    public static IngestWriteResult NotAttempted() =>
        new(IngestWriteResultKind.NotAttempted, false, null);

    public static IngestWriteResult Committed(bool enteredProductionWritePath = true) =>
        new(IngestWriteResultKind.Committed, enteredProductionWritePath, null);

    public static IngestWriteResult Succeeded(bool enteredProductionWritePath = true) =>
        Committed(enteredProductionWritePath);

    public static IngestWriteResult RolledBack(string? reason = null) =>
        new(IngestWriteResultKind.RolledBack, true, reason);

    public static IngestWriteResult Failed(string? reason = null) => RolledBack(reason);

    public static IngestWriteResult Cancelled() =>
        new(IngestWriteResultKind.Cancelled, true, null);
}

public sealed record IngestOutcome
{
    internal IngestOutcome(
        ClassifiedBatchTotals classification,
        IngestWriteResult writeResult,
        IngestResponseDisposition responseDisposition,
        long received,
        long saved,
        long rejected,
        long dropped,
        bool clearsStorageWrite,
        bool activatesStorageWrite,
        bool activatesProtection,
        string? protectionCode,
        string? protectionReason)
    {
        Classification = classification;
        WriteResult = writeResult;
        ResponseDisposition = responseDisposition;
        Received = received;
        Saved = saved;
        Rejected = rejected;
        Dropped = dropped;
        ClearsStorageWrite = clearsStorageWrite;
        ActivatesStorageWrite = activatesStorageWrite;
        ActivatesProtection = activatesProtection;
        ProtectionCode = protectionCode;
        ProtectionReason = protectionReason;
    }

    public ClassifiedBatchTotals Classification { get; }
    public IngestWriteResult WriteResult { get; }
    public IngestWriteResultKind WriteKind => WriteResult.Kind;
    public IngestResponseDisposition ResponseDisposition { get; }
    public IngestDisposition Disposition => (IngestDisposition)ResponseDisposition;
    public long Received { get; }
    public long Saved { get; }
    public long Rejected { get; }
    public long Dropped { get; }
    public bool ClearsStorageWrite { get; }
    public bool ActivatesStorageWrite { get; }
    public bool ActivatesProtection { get; }
    public bool RefreshesProtection => ActivatesProtection;
    public string? ProtectionCode { get; }
    public string? ProtectionReason { get; }
    public bool IsRetryable => ResponseDisposition == IngestResponseDisposition.RetryableFailure;
    public bool IsCancelled => ResponseDisposition == IngestResponseDisposition.Cancelled;
}

public sealed class IngestOutcomeBuilder
{
    private readonly ClassifiedBatchTotals _classification;
    private readonly IngestWriteResult _writeResult;

    public IngestOutcomeBuilder(
        ClassifiedBatchTotals classification,
        IngestWriteResult writeResult)
    {
        _classification = classification ?? throw new ArgumentNullException(nameof(classification));
        _writeResult = writeResult ?? throw new ArgumentNullException(nameof(writeResult));
    }

    public IngestOutcomeBuilder(IngestBatchTotals classification, IngestWriteResult writeResult)
        : this((classification ?? throw new ArgumentNullException(nameof(classification))).Totals, writeResult)
    {
    }

    public IngestOutcome Build() => Build(_classification, _writeResult);

    public static IngestOutcome Build(
        ClassifiedBatchTotals classification,
        IngestWriteResult writeResult)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(writeResult);

        var received = classification.Received;
        var saved = 0L;
        var rejected = 0L;
        var dropped = 0L;
        var clearsStorageWrite = false;
        var activatesStorageWrite = false;
        var activatesProtection = false;
        string? protectionCode = null;
        string? protectionReason = null;
        IngestResponseDisposition disposition;

        switch (writeResult.Kind)
        {
            case IngestWriteResultKind.NotAttempted:
                if (classification.ParsedForWrite != 0)
                    throw new ArgumentException(
                        "A not-attempted batch cannot contain parsed-for-write attempts.",
                        nameof(writeResult));
                rejected = classification.ProtectionRejected;
                dropped = classification.Dropped;
                activatesProtection = rejected != 0 || dropped != 0;
                disposition = activatesProtection
                    ? IngestResponseDisposition.PartialSuccess
                    : IngestResponseDisposition.Success;
                break;
            case IngestWriteResultKind.Committed:
                saved = classification.ParsedForWrite;
                rejected = classification.ProtectionRejected;
                dropped = classification.Dropped;
                clearsStorageWrite = writeResult.EnteredProductionWritePath
                    && classification.ParsedForWrite != 0;
                activatesProtection = rejected != 0 || dropped != 0;
                disposition = activatesProtection
                    ? IngestResponseDisposition.PartialSuccess
                    : IngestResponseDisposition.Success;
                break;
            case IngestWriteResultKind.RolledBack:
                activatesStorageWrite = true;
                disposition = IngestResponseDisposition.RetryableFailure;
                break;
            case IngestWriteResultKind.Cancelled:
                disposition = IngestResponseDisposition.Cancelled;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(writeResult));
        }

        if (activatesProtection)
        {
            protectionCode = dropped != 0
                ? RuntimeDegradationCodes.TelemetryDropped
                : RuntimeDegradationCodes.TelemetryRejected;
            protectionReason = RuntimeDegradationCodes.DefaultMessage(protectionCode);
        }

        return new IngestOutcome(
            classification,
            writeResult,
            disposition,
            received,
            saved,
            rejected,
            dropped,
            clearsStorageWrite,
            activatesStorageWrite,
            activatesProtection,
            protectionCode,
            protectionReason);
    }

    public static IngestOutcome Create(
        ClassifiedBatchTotals classification,
        IngestWriteResult writeResult) => Build(classification, writeResult);
}

public sealed record RuntimeTelemetrySnapshot(
    long ReceivedSpans,
    long SavedSpans,
    long RejectedSpans,
    long DroppedSpans);

public sealed record RuntimeProcessSnapshot(
    double? CpuUtilization,
    long? WorkingSetBytes,
    long? GcHeapBytes);

public sealed record RuntimeStorageSnapshot(
    long? UsageBytes,
    long BudgetBytes,
    double? GrowthBytesPerSecond,
    double? GrowthWindowSeconds);

public sealed record RuntimeRouteSnapshot(
    string Route,
    long RequestCount,
    double AverageDurationMilliseconds,
    double MaxDurationMilliseconds,
    double DatabaseCallsPerRequest,
    double DownstreamCallsPerRequest);

public sealed record RuntimeObservabilitySnapshot
{
    public RuntimeObservabilitySnapshot(
        RuntimeState status,
        bool collectorOnline,
        DateTimeOffset since,
        RuntimeStorageSnapshot storage,
        RuntimeTelemetrySnapshot telemetry,
        RuntimeProcessSnapshot process,
        RuntimeDegradation? latestDegradation,
        IReadOnlyList<RuntimeRouteSnapshot> routes,
        IReadOnlyDictionary<DegradationSource, RuntimeDegradation> activeDegradations)
    {
        Status = status;
        CollectorOnline = collectorOnline;
        Since = since;
        Storage = storage ?? throw new ArgumentNullException(nameof(storage));
        Telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        Process = process ?? throw new ArgumentNullException(nameof(process));
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(activeDegradations);
        LatestDegradation = latestDegradation;
        Routes = Array.AsReadOnly(routes.ToArray());
        ActiveDegradations = CopyDegradations(activeDegradations);
    }

    public RuntimeState Status { get; }
    public string StatusName => Status switch
    {
        RuntimeState.Off => "off",
        RuntimeState.Healthy => "healthy",
        RuntimeState.Degraded => "degraded",
        _ => "degraded",
    };
    public string State => StatusName;
    public bool CollectorOnline { get; }
    public DateTimeOffset Since { get; }
    public RuntimeStorageSnapshot Storage { get; }
    public RuntimeTelemetrySnapshot Telemetry { get; }
    public RuntimeProcessSnapshot Process { get; }
    public RuntimeDegradation? LatestDegradation { get; }
    public IReadOnlyList<RuntimeRouteSnapshot> Routes { get; }
    public IReadOnlyDictionary<DegradationSource, RuntimeDegradation> ActiveDegradations { get; }
    public long ReceivedSpans => Telemetry.ReceivedSpans;
    public long SavedSpans => Telemetry.SavedSpans;
    public long RejectedSpans => Telemetry.RejectedSpans;
    public long DroppedSpans => Telemetry.DroppedSpans;
    public long? UsageBytes => Storage.UsageBytes;
    public long BudgetBytes => Storage.BudgetBytes;
    public double? GrowthBytesPerSecond => Storage.GrowthBytesPerSecond;
    public double? GrowthWindowSeconds => Storage.GrowthWindowSeconds;
    public double? CpuUtilization => Process.CpuUtilization;
    public long? WorkingSetBytes => Process.WorkingSetBytes;
    public long? GcHeapBytes => Process.GcHeapBytes;

    internal static IReadOnlyDictionary<DegradationSource, RuntimeDegradation> CopyDegradations(
        IEnumerable<KeyValuePair<DegradationSource, RuntimeDegradation>> values)
    {
        return new ReadOnlyDictionary<DegradationSource, RuntimeDegradation>(
            values.ToDictionary(static pair => pair.Key, static pair => pair.Value));
    }
}

internal static class RuntimeValueRules
{
    public const int MaxReasonLength = 256;
    public const long StorageBudgetBytes = 1_073_741_824;

    public static string BoundedReason(string code, string? message)
    {
        var value = string.IsNullOrWhiteSpace(message)
            ? RuntimeDegradationCodes.DefaultMessage(code)
            : message.Trim();
        return value.Length <= MaxReasonLength ? value : value[..MaxReasonLength];
    }

    public static double? BoundedRatio(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            return null;
        return Math.Clamp(value.Value, 0, 1);
    }

    public static double? NonNegativeFinite(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            return null;
        return Math.Max(0, value.Value);
    }

    public static long Add(long left, long right)
    {
        if (left < 0 || right < 0)
            throw new ArgumentOutOfRangeException();
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    public static long NonNegative(long value) => Math.Max(0, value);
}
