namespace Mohist.Server.Events.Grains;

public sealed class EventDispatcherOptions
{
    public const string SectionName = "EventDispatcher";

    public TimeSpan ReminderPeriod { get; set; } = TimeSpan.FromSeconds(1);

    public int BatchSize { get; set; } = 100;

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public int PushQueueCapacity { get; set; } = 256;

    public TimeSpan PushDeliveryTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
