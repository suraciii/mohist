namespace Mohist.Server.Issue.Services;

/// <summary>
/// The uniform Insights selector vocabulary: exactly <c>7d</c>, <c>30d</c>,
/// <c>90d</c>. Owns the wire-string-to-day-count map so queriers and routes
/// share one validation surface; routes call <see cref="TryParse"/> on the
/// inbound <c>?range=</c> query parameter and either forward a day count to
/// the querier or return 400 for an unknown value.
/// <para>
/// The null/omitted path resolves to a <c>null</c> day count — per-endpoint
/// back-compat (omit ⇒ today's fixed literal) is the caller's responsibility,
/// not this type's. This is the load-bearing asymmetry the Insights M3 design
/// (D1) calls out: the range vocabulary is global, the fallback default is
/// per-endpoint.
/// </para>
/// </summary>
public static class MetricsRange
{
    public const string SevenDays = "7d";
    public const string ThirtyDays = "30d";
    public const string NinetyDays = "90d";

    public const int SevenDayCount = 7;
    public const int ThirtyDayCount = 30;
    public const int NinetyDayCount = 90;

    /// <summary>
    /// Parses the inbound wire string. Returns <c>true</c> when the value is
    /// one of the three accepted presets; <paramref name="dayCount"/> is set
    /// to the corresponding day count. Returns <c>false</c> for any other
    /// value (including whitespace and case-different forms like
    /// <c>"7D"</c>) — callers MUST return 400 in that case.
    /// </summary>
    public static bool TryParse(string? value, out int dayCount)
    {
        switch (value)
        {
            case SevenDays:
                dayCount = SevenDayCount;
                return true;
            case ThirtyDays:
                dayCount = ThirtyDayCount;
                return true;
            case NinetyDays:
                dayCount = NinetyDayCount;
                return true;
            default:
                dayCount = 0;
                return false;
        }
    }
}