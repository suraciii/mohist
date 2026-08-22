namespace Mohist.Server.Infrastructure.Events;

public sealed class EventDispatcherOptions
{
    public const string SectionName = "EventDispatcher";

    /// <summary>Number of concurrent dispatch workers. Zero disables
    /// background dispatch; only explicit drains run (test hosts).</summary>
    public int WorkerCount { get; set; } = 2;

    /// <summary>How long a stream lease survives without renewal before
    /// another worker may steal the stream.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound on dispatch latency after a lost wake signal;
    /// the slow poll is the correctness path.</summary>
    public TimeSpan SlowPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum streams returned by one discovery pass.</summary>
    public int MaxStreamsPerPass { get; set; } = 100;

    /// <summary>Maximum events read from one stream per drain pass.</summary>
    public int MaxEventsPerStreamPass { get; set; } = 200;

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public int PushQueueCapacity { get; set; } = 256;

    public TimeSpan PushDeliveryTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
