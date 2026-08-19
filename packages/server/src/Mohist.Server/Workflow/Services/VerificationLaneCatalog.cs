namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Stable identity and order for the six built-in verification lanes that
/// replace the aggregate verify task in <c>mohist/local</c> and
/// <c>mohist/github-pr</c>. The order is the dispatch order; the build-stage
/// gate advances only when every lane has a durable <c>pass</c> outcome.
///
/// The catalog is a control-plane reference, not a profile definition: it
/// identifies the recognized lane tasks so the Server can classify reports,
/// project lane state, and apply ordered dispatch. The authoritative command
/// text, timeouts, and recovery declarations live in the bound definition
/// snapshot captured at <c>BindWorkflowRun</c> time, never in this catalog.
/// </summary>
public static class VerificationLaneCatalog
{
    public const string VerifyInstall = "verify-install";
    public const string VerifyDotnet = "verify-dotnet";
    public const string VerifyWebTypecheck = "verify-web-typecheck";
    public const string VerifyWebTests = "verify-web-tests";
    public const string VerifyRunnerTypecheck = "verify-runner-typecheck";
    public const string VerifyRunnerTests = "verify-runner-tests";

    public static readonly IReadOnlyList<string> LaneIds = new[]
    {
        VerifyInstall,
        VerifyDotnet,
        VerifyWebTypecheck,
        VerifyWebTests,
        VerifyRunnerTypecheck,
        VerifyRunnerTests,
    };

    public static int OrderOf(string laneId)
    {
        for (var i = 0; i < LaneIds.Count; i++)
        {
            if (string.Equals(LaneIds[i], laneId, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    public static bool IsKnownLane(string? laneId) =>
        laneId is not null && OrderOf(laneId) >= 0;
}