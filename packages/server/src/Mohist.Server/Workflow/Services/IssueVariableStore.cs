using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Services;

public class IssueVariableStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public IssueVariableStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<VariableBundle> GetVariablesAsync(string projectId, int issueNumber)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IssueNumber == issueNumber);
        return row is null ? VariableBundle.Empty : VariableBundle.FromJson(row.Variables);
    }

    public async Task<VariableBundle> SetVariablesAsync(string projectId, int issueNumber, VariableBundle bundle)
    {
        VariableBundleShapeValidator.Validate(bundle);
        RejectNamedAgentRuntime(bundle);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IssueNumber == issueNumber);

        if (row is null)
        {
            row = new IssueWorkflowProfile
            {
                ProjectId = projectId,
                IssueNumber = issueNumber,
                Variables = bundle.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.IssueWorkflowProfiles.Add(row);
        }
        else
        {
            row.Variables = bundle.ToJson();
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return bundle;
    }

    public async Task<VariableBundle> PatchVariablesAsync(string projectId, int issueNumber, VariableBundle patch)
    {
        VariableBundleShapeValidator.Validate(patch);
        RejectNamedAgentRuntime(patch);
        var current = await GetVariablesAsync(projectId, issueNumber);
        var merged = VariableBundle.Patch(current, patch);
        return await SetVariablesAsync(projectId, issueNumber, merged);
    }

    private static void RejectNamedAgentRuntime(VariableBundle bundle)
    {
        if (ContainsRuntime(bundle.Vars)
            || bundle.Stages?.Values.Any(stage => ContainsRuntime(stage.Vars)) == true)
        {
            throw new ArgumentException(
                "vars.agent.runtime is not supported for Issue configuration; configure runtime on the Agent definition.");
        }
    }

    private static bool ContainsRuntime(JsonElement? vars) =>
        vars is { ValueKind: JsonValueKind.Object }
        && vars.Value.TryGetProperty("agent", out var agent)
        && agent.ValueKind == JsonValueKind.Object
        && agent.TryGetProperty("runtime", out _);
}
