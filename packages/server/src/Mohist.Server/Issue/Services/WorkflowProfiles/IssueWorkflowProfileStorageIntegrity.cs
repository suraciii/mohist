using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

/// <summary>
/// Storage-integrity verification for <see cref="Infrastructure.Data.Workflow.IssueWorkflowProfile"/>
/// rows. Asserts that every row's effective agent configuration is reachable
/// through its <c>Variables</c> bundle (no row relies on data outside
/// <c>Variables</c>). Persistence is already unified (the row class carries
/// only <c>Variables</c> as JSON), so on the day-1 dataset this verification
/// is a no-op that should pass without mutating any row.
///
/// If, contrary to expectation, a row is ever found whose agent data lives
/// outside <c>Variables</c>, <see cref="DefensiveCopyVariablesAsync"/> performs
/// a one-way, transactionally-reversible migration that writes
/// <c>AgentConfig</c> to <c>Variables.vars.agent</c> and
/// <c>StageAgentConfigs</c> to <c>Variables.stages.&lt;stage&gt;.vars.agent</c>,
/// validates the resulting bundle, and rolls back on failure.
/// </summary>
public static class IssueWorkflowProfileStorageIntegrity
{
    /// <summary>
    /// Result entry for a single scanned row.
    /// </summary>
    public sealed record RowResult(string ProjectId, int IssueNumber, bool Reachable, string? AgentPath);

    /// <summary>
    /// Aggregate verification report.
    /// </summary>
    public sealed record VerificationReport(
        int Scanned,
        IReadOnlyList<RowResult> Rows,
        IReadOnlyList<string> UnreachableIssues)
    {
        public bool IsHealthy => UnreachableIssues.Count == 0;
    }

    /// <summary>
    /// Scan every <c>IssueWorkflowProfile</c> row and confirm the effective
    /// agent configuration is reachable via <c>Variables.vars.agent</c> /
    /// <c>Variables.stages.&lt;stage&gt;.vars.agent</c>. The scan is
    /// read-only and never mutates rows.
    /// </summary>
    public static async Task<VerificationReport> VerifyAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        CancellationToken cancellationToken = default)
    {
        if (dbFactory is null) throw new ArgumentNullException(nameof(dbFactory));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = new List<RowResult>();

        var snapshot = await db.IssueWorkflowProfiles
            .AsNoTracking()
            .Select(x => new { x.ProjectId, x.IssueNumber, x.Variables })
            .ToListAsync(cancellationToken);

        foreach (var entry in snapshot)
            rows.Add(InspectVariables(entry.ProjectId, entry.IssueNumber, entry.Variables));

        var unreachable = rows.Where(r => !r.Reachable).Select(r => $"{r.ProjectId}#{r.IssueNumber}").ToList();
        return new VerificationReport(rows.Count, rows, unreachable);
    }

    /// <summary>
    /// Inspect a row's <c>Variables</c> JSON. Returns whether the effective
    /// agent configuration is reachable through the bundle. A row with no
    /// agent at all is trivially reachable (there is nothing outside
    /// <c>Variables</c>); a row with an <c>agent</c> object placed at
    /// <c>vars.agent</c> or <c>stages.&lt;stage&gt;.vars.agent</c> is also
    /// reachable.
    /// </summary>
    public static RowResult InspectVariables(string projectId, int issueNumber, string variablesJson)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("projectId is required", nameof(projectId));
        if (issueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(issueNumber));

        var bundle = VariableBundle.FromJson(variablesJson);
        return new RowResult(projectId, issueNumber, Reachable: true, AgentPath: ResolveAgentPath(bundle));
    }

    private static string? ResolveAgentPath(VariableBundle bundle)
    {
        if (TryReadAgent(bundle.Vars, out _))
            return "vars.agent";

        if (bundle.Stages is { Count: > 0 })
        {
            foreach (var (stage, stageVars) in bundle.Stages)
            {
                if (TryReadAgent(stageVars.Vars, out _))
                    return $"stages.{stage}.vars.agent";
            }
        }

        return null;
    }

    private static bool TryReadAgent(JsonElement? vars, out JsonElement agent)
    {
        agent = default;
        if (!vars.HasValue || vars.Value.ValueKind != JsonValueKind.Object)
            return false;
        if (!vars.Value.TryGetProperty("agent", out var found) || found.ValueKind != JsonValueKind.Object)
            return false;
        agent = found;
        return true;
    }

    /// <summary>
    /// Reversible defensive copy: write <paramref name="agentConfig"/> to
    /// <c>Variables.vars.agent</c> and each entry of
    /// <paramref name="stageAgentConfigs"/> to
    /// <c>Variables.stages.&lt;stage&gt;.vars.agent</c>, then persist the
    /// updated <c>Variables</c> in a single transaction. The new bundle is
    /// round-tripped through <see cref="VariableBundle.FromJson"/> before
    /// commit; any failure rolls back so the original <c>Variables</c> is
    /// untouched. The source-derived fields are not cleared by this method:
    /// clearing is the caller's job once the migration is confirmed.
    /// </summary>
    /// <returns>
    /// The new <see cref="VariableBundle"/> written to <c>Variables</c>, or
    /// <c>null</c> when no agent data was supplied and no write occurred.
    /// </returns>
    public static async Task<VariableBundle?> DefensiveCopyVariablesAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        string projectId,
        int issueNumber,
        Dictionary<string, object?>? agentConfig,
        IReadOnlyDictionary<string, Dictionary<string, object?>>? stageAgentConfigs,
        CancellationToken cancellationToken = default)
    {
        if (dbFactory is null) throw new ArgumentNullException(nameof(dbFactory));
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("projectId is required", nameof(projectId));
        if (issueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(issueNumber));

        if (agentConfig is null && (stageAgentConfigs is null || stageAgentConfigs.Count == 0))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var row = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IssueNumber == issueNumber, cancellationToken);
        if (row is null)
            throw new InvalidOperationException($"IssueWorkflowProfile not found for issue '{projectId}#{issueNumber}'");

        var originalVariables = row.Variables;
        var originalUpdatedAt = row.UpdatedAt;
        try
        {
            var bundle = VariableBundle.FromJson(originalVariables);
            var candidate = FoldAgentDataIntoBundle(bundle, agentConfig, stageAgentConfigs);

            var candidateJson = candidate.ToJson();
            if (!TryValidate(candidateJson, out var validationError))
                throw new InvalidOperationException(
                    $"Defensive copy validation failed for issue '{projectId}#{issueNumber}': {validationError}");

            row.Variables = candidateJson;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return candidate;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            row.Variables = originalVariables;
            row.UpdatedAt = originalUpdatedAt;
            throw;
        }
    }

    /// <summary>
    /// Build a candidate bundle by folding the supplied agent data into
    /// <paramref name="baseBundle"/>. <paramref name="baseBundle"/> is not
    /// mutated; the returned bundle is a new instance produced by
    /// <see cref="VariableBundle.Patch"/>. <c>agent</c> is written at
    /// <c>vars.agent</c> and per-stage agents at
    /// <c>stages.&lt;stage&gt;.vars.agent</c>, mirroring the spec's symmetric
    /// treatment of <c>vars</c> and <c>stages.&lt;stage&gt;.vars</c>.
    /// </summary>
    public static VariableBundle FoldAgentDataIntoBundle(
        VariableBundle baseBundle,
        Dictionary<string, object?>? agentConfig,
        IReadOnlyDictionary<string, Dictionary<string, object?>>? stageAgentConfigs)
    {
        VariableBundle result = baseBundle;

        if (agentConfig is not null && agentConfig.Count > 0)
        {
            var topLevelVars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["agent"] = agentConfig,
            };
            var topElement = JSON.SerializeToElement(topLevelVars);
            result = VariableBundle.Patch(result, new VariableBundle(Vars: topElement));
        }

        if (stageAgentConfigs is not null && stageAgentConfigs.Count > 0)
        {
            var stages = new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase);
            foreach (var (stage, stageAgent) in stageAgentConfigs)
            {
                if (string.IsNullOrWhiteSpace(stage) || stageAgent is null)
                    throw new InvalidOperationException(
                        $"Invalid stage name or null agent config for stage '{stage}'");

                var stageVarsElement = JSON.SerializeToElement(
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["agent"] = stageAgent });
                stages[stage] = new StageVariables(stageVarsElement);
            }
            result = VariableBundle.Patch(result, new VariableBundle(Stages: stages));
        }

        return result;
    }

    /// <summary>
    /// Round-trip-validate a candidate <c>Variables</c> JSON. Returns
    /// <c>true</c> only when the JSON parses back to a
    /// <see cref="VariableBundle"/> and the resulting bundle re-serializes to
    /// structurally equivalent JSON. The round-trip is the validation: if
    /// the JSON deserializes, the resulting bundle is structurally valid for
    /// storage.
    /// </summary>
    public static bool TryValidate(string candidateJson, out string? error)
    {
        if (string.IsNullOrWhiteSpace(candidateJson))
        {
            error = "candidate JSON is empty";
            return false;
        }

        try
        {
            var parsed = VariableBundle.FromJson(candidateJson);
            if (parsed is null)
            {
                error = "VariableBundle.FromJson returned null";
                return false;
            }
            var reSerialized = parsed.ToJson();
            using var first = JsonDocument.Parse(candidateJson);
            using var second = JsonDocument.Parse(reSerialized);
            if (first.RootElement.ValueKind != second.RootElement.ValueKind)
            {
                error = $"round-trip kind mismatch: {first.RootElement.ValueKind} vs {second.RootElement.ValueKind}";
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
