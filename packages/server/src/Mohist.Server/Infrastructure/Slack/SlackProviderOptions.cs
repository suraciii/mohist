using Microsoft.Extensions.Configuration;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Tunable knobs for the Slack provider reliability layer. Tests
/// construct this directly with the values they care about; production
/// binds the "SlackProvider" section of configuration. Defaults reflect
/// "small but meaningful": enough capacity to absorb Slack's redelivery
/// window without becoming an unbounded queue, tight enough backoff that
/// an offline adapter surfaces Delivery uncertain quickly.
/// </summary>
public sealed class SlackProviderOptions
{
    public const string SectionName = "SlackProvider";

    public int InboxCapacityPerConnection { get; set; } = 256;

    public int OutboxCapacityPerConnection { get; set; } = 256;

    public TimeSpan OutboxReminderPeriod { get; set; } = TimeSpan.FromSeconds(1);

    public int OutboxMaxAttempts { get; set; } = 5;

    public TimeSpan OutboxBaseBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan OutboxMaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a Claimed row may sit without an adapter ack before the
    /// dispatcher flips it to Delivery uncertain. Honoring this bound
    /// means a crashed or stuck adapter surfaces its stuck deliveries as
    /// honest uncertainty instead of leaving them invisible.
    /// </summary>
    public TimeSpan OutboxClaimTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a DeliveryUncertain row may sit before the dispatcher
    /// dead-letters it. Long enough for an operator to inspect and
    /// resend, short enough that the dead-letter table does not grow
    /// without bound.
    /// </summary>
    public TimeSpan OutboxUncertainTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public int DispatcherBatchSize { get; set; } = 100;

    /// <summary>
    /// Character budget for the rendered thread-history background on a
    /// first-mention launch. When the bounded range exceeds the budget
    /// the oldest whole messages are dropped first; a stable marker
    /// surfaces in both the Slack acceptance reply and the agent input.
    /// Approximately a few paragraphs of plaintext; the budget is
    /// deterministic and avoids a tokenizer dependency for v1.
    /// </summary>
    public int StartupContextCharacterBudget { get; set; } = 8_000;

    /// <summary>
    /// Maximum number of <c>conversations.replies</c> pages fetched for
    /// one first-mention launch. Bounds cost/latency on huge threads
    /// independently of the character budget. Pages are 200 messages
    /// each, so the default reads at most 2000 messages.
    /// </summary>
    public int StartupContextPaginationDepthCap { get; set; } = 10;

    /// <summary>
    /// Conservative Slack event retention window. When the adapter
    /// reconnects after an outage of at least this duration, the
    /// Connection's <c>OfflineGapAt</c> is stamped so the diagnostic
    /// surface can warn that Slack may have discarded events from the
    /// outage window. The default is short on purpose: Slack's Socket
    /// Mode redelivery is brief unless Delayed Events is enabled, and
    /// only outages plausibly past the window warrant the notice.
    /// Operators tune this to match their retention posture.
    /// </summary>
    public TimeSpan SlackEventRetentionWindow { get; set; } = TimeSpan.FromMinutes(30);

    public void Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        if (!section.Exists())
            return;

        InboxCapacityPerConnection = section.GetValue(nameof(InboxCapacityPerConnection), InboxCapacityPerConnection);
        OutboxCapacityPerConnection = section.GetValue(nameof(OutboxCapacityPerConnection), OutboxCapacityPerConnection);
        OutboxMaxAttempts = section.GetValue(nameof(OutboxMaxAttempts), OutboxMaxAttempts);
        OutboxBaseBackoff = section.GetValue(nameof(OutboxBaseBackoff), OutboxBaseBackoff);
        OutboxMaxBackoff = section.GetValue(nameof(OutboxMaxBackoff), OutboxMaxBackoff);
        OutboxClaimTimeout = section.GetValue(nameof(OutboxClaimTimeout), OutboxClaimTimeout);
        OutboxUncertainTimeout = section.GetValue(nameof(OutboxUncertainTimeout), OutboxUncertainTimeout);
        DispatcherBatchSize = section.GetValue(nameof(DispatcherBatchSize), DispatcherBatchSize);
        OutboxReminderPeriod = section.GetValue(nameof(OutboxReminderPeriod), OutboxReminderPeriod);
        StartupContextCharacterBudget = section.GetValue(nameof(StartupContextCharacterBudget), StartupContextCharacterBudget);
        StartupContextPaginationDepthCap = section.GetValue(nameof(StartupContextPaginationDepthCap), StartupContextPaginationDepthCap);
        SlackEventRetentionWindow = section.GetValue(nameof(SlackEventRetentionWindow), SlackEventRetentionWindow);
    }
}
