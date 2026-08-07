namespace Mohist.Server.Auth.Domain;

/// <summary>
/// Expiry discipline for personal access tokens: every PAT must expire —
/// default 90 days, hard cap of 1 year (the same discipline GitHub
/// fine-grained PATs follow). There is no way to issue a non-expiring PAT.
/// </summary>
public static class PatPolicy
{
    public const int DefaultTtlHours = 24 * 90;
    public const int MaxTtlHours = 24 * 365;

    public static DateTimeOffset ResolveExpiresAt(int? ttlHours, TimeProvider time)
    {
        var hours = ttlHours ?? DefaultTtlHours;
        if (hours is < 1 or > MaxTtlHours)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttlHours),
                hours,
                $"ttlHours must be between 1 and {MaxTtlHours}");
        }

        return time.GetUtcNow().AddHours(hours);
    }
}
