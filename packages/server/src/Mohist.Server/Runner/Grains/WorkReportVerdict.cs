namespace Mohist.Server.Runner.Grains;

/// <summary>
/// Decidable owner arbitration for a structurally valid terminal report.
/// </summary>
[GenerateSerializer]
public enum WorkReportVerdict
{
    Accepted,
    Refused,
    Outstanding,
}
