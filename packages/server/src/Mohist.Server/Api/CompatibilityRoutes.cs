using Microsoft.EntityFrameworkCore;
using Mohist.Server.Config.Domain;
using Mohist.Server.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Project.Grains;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Api;

public static class CompatibilityRoutes
{
    private const string ProjectRegistryKey = "project-registry";

    public static WebApplication MapCompatibilityRoutes(this WebApplication app)
    {
        app.MapGet("/api/system/info", (IConfiguration config) => ApiResults.Ok(new
        {
            version = typeof(CompatibilityRoutes).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            gitHash = Environment.GetEnvironmentVariable("MOHIST_GIT_HASH") ?? "",
            sourceHead = (string?)null,
            server = new
            {
                host = config["ASPNETCORE_URLS"] ?? config["Mohist:Host"] ?? "localhost",
                port = int.TryParse(config["Mohist:Port"], out var port) ? port : 3456,
                status = "running",
            },
            paths = new
            {
                db = config["Mohist:DbPath"] ?? Path.Combine(Home(), ".mohist", "mohist.db"),
                config = Path.Combine(Home(), ".mohist", "config.jsonc"),
                opencode = (string?)null,
                logs = Path.Combine(Home(), ".mohist", "logs"),
            },
        }));

        app.MapGet("/api/log-level", async (ConfigService svc) =>
        {
            var all = await svc.GetAllAsync();
            return ApiResults.Ok(new { level = all.GetValueOrDefault("logLevel", "INFO") });
        });

        app.MapPut("/api/log-level", async (LogLevelRequest req, ConfigService svc) =>
        {
            var level = string.IsNullOrWhiteSpace(req.Level) ? "INFO" : req.Level.ToUpperInvariant();
            await svc.SetAsync("logLevel", level);
            return ApiResults.Ok(new { level });
        });

        app.MapGet("/api/agent-runtime", async (ConfigService svc) => ApiResults.Ok(await GetAgentRuntimeAsync(svc)));

        app.MapPut("/api/agent-runtime", async (AgentRuntimeRequest req, ConfigService svc) =>
        {
            if (req.Timeout is not null) await svc.SetAsync("agentTimeout", req.Timeout.Value);
            if (req.StageTimeout is not null) await svc.SetAsync("stageTimeout", req.StageTimeout.Value);
            if (req.TaskTimeout is not null) await svc.SetAsync("taskTimeout", req.TaskTimeout.Value);
            if (req.MaxConcurrent is not null) await svc.SetAsync("maxConcurrentAgents", req.MaxConcurrent.Value);
            if (req.MaxGracePeriods is not null) await svc.SetAsync("maxGracePeriods", req.MaxGracePeriods.Value);
            if (req.PollInterval is not null) await svc.SetAsync("pollInterval", req.PollInterval.Value);
            return ApiResults.Ok(await GetAgentRuntimeAsync(svc));
        });

        app.MapPost("/api/settings/system/rebuild", () => ApiResults.Ok(new { success = true }));

        app.MapGet("/api/questions", () => ApiResults.Ok(Array.Empty<object>()));
        app.MapGet("/api/questions/{id}", (string id) => ApiResults.NotFound($"Question {id} not found"));
        app.MapPost("/api/questions/{id}/reply", (string id) => ApiResults.NotFound($"Question {id} not found"));
        app.MapPost("/api/questions/{id}/expire", (string id) => ApiResults.NotFound($"Question {id} not found"));

        app.MapPost("/api/issues/{number:int}/messages", async (int number, MessageRequest req, IGrainFactory grains, IEventStore events) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            var issue = await ResolveIssueAsync(pid, number, grains);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            if (!string.IsNullOrWhiteSpace(req.Message))
            {
                await events.AppendAsync(new EventInput(
                    ProjectId: pid,
                    IssueId: issue.Id,
                    IssueNumber: number,
                    Category: "issue",
                    Type: "issue_message_added",
                    Payload: new Dictionary<string, object?> { ["message"] = req.Message }));
            }
            return ApiResults.Ok(new { message = "Message recorded" });
        });

        app.MapPost("/api/issues/{number:int}/comments", async (int number, CommentRequest req, IGrainFactory grains, IDbContextFactory<MohistDbContext> dbFactory, IEventBus eventBus) =>
        {
            if (string.IsNullOrWhiteSpace(req.Body)) return ApiResults.BadRequest("body is required");
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            var issue = await ResolveIssueAsync(pid, number, grains);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            await using var db = await dbFactory.CreateDbContextAsync();
            var comment = new IssueCommentEntry
            {
                Id = $"comment_{Guid.NewGuid():N}",
                ProjectId = pid,
                IssueId = issue.Id,
                IssueNumber = number,
                Body = req.Body,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.IssueComments.Add(comment);
            await db.SaveChangesAsync();
            var dto = IssueQueryService.ToCommentDto(comment);
            eventBus.Emit("comment_added", new { issueId = issue.Id, projectId = pid, commentId = comment.Id, body = comment.Body, createdAt = dto.CreatedAt });
            return ApiResults.Ok(dto);
        });

        app.MapDelete("/api/issues/{number:int}/comments/{commentId}", async (int number, string commentId, IGrainFactory grains, IDbContextFactory<MohistDbContext> dbFactory) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            await using var db = await dbFactory.CreateDbContextAsync();
            var comment = await db.IssueComments.FirstOrDefaultAsync(c => c.ProjectId == pid && c.IssueNumber == number && c.Id == commentId);
            if (comment is null) return ApiResults.NotFound($"Comment {commentId} not found");
            db.IssueComments.Remove(comment);
            await db.SaveChangesAsync();
            return ApiResults.Ok(new { message = "Comment deleted" });
        });

        app.MapPost("/api/issues/{number:int}/prerequisites", async (int number, PrerequisiteRequest req, IGrainFactory grains, IDbContextFactory<MohistDbContext> dbFactory, IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            var issue = await ResolveIssueAsync(pid, number, grains);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");
            var prereq = await issuesQuery.GetDomainAsync(pid, req.PrerequisiteNumber);
            if (prereq is null) return ApiResults.NotFound($"Issue #{req.PrerequisiteNumber} not found");
            if (req.PrerequisiteNumber == number) return ApiResults.Conflict("Issue cannot depend on itself", "circular_prerequisite");

            await using var db = await dbFactory.CreateDbContextAsync();
            var exists = await db.IssuePrerequisites.AnyAsync(p => p.ProjectId == pid && p.IssueNumber == number && p.PrerequisiteNumber == req.PrerequisiteNumber);
            if (!exists)
            {
                db.IssuePrerequisites.Add(new IssuePrerequisiteEntry { ProjectId = pid, IssueNumber = number, PrerequisiteNumber = req.PrerequisiteNumber });
                await db.SaveChangesAsync();
            }
            var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
            var project = await registry.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");
            return ApiResults.Ok(new { issue = await issuesQuery.GetAsync(pid, number, project), message = "Prerequisite added" });
        });

        app.MapDelete("/api/issues/{number:int}/prerequisites/{prerequisiteNumber:int}", async (int number, int prerequisiteNumber, IGrainFactory grains, IDbContextFactory<MohistDbContext> dbFactory, IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            await using var db = await dbFactory.CreateDbContextAsync();
            var row = await db.IssuePrerequisites.FindAsync(pid, number, prerequisiteNumber);
            if (row is not null)
            {
                db.IssuePrerequisites.Remove(row);
                await db.SaveChangesAsync();
            }
            var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
            var project = await registry.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");
            return ApiResults.Ok(new { issue = await issuesQuery.GetAsync(pid, number, project), message = "Prerequisite removed" });
        });

        return app;
    }

    private static async Task<object> GetAgentRuntimeAsync(ConfigService svc)
    {
        var cfg = await svc.GetConfigAsync();
        return new
        {
            timeout = Value(cfg, "agentTimeout", 600),
            stageTimeout = Value(cfg, "stageTimeout", 3600),
            taskTimeout = Value(cfg, "taskTimeout", 600),
            maxConcurrent = Value(cfg, "maxConcurrentAgents", 3),
            maxGracePeriods = Value(cfg, "maxGracePeriods", 3),
            pollInterval = Value(cfg, "pollInterval", 5000),
        };
    }

    private static int Value(Dictionary<string, object?> cfg, string key, int fallback) =>
        cfg.TryGetValue(key, out var value) && value is int n ? n : fallback;

    private static string Home() => Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static async Task<string?> ResolveProjectIdAsync(string? projectId, IGrainFactory grains)
    {
        if (!string.IsNullOrWhiteSpace(projectId)) return projectId;
        var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
        return (await registry.GetCurrentAsync())?.Id;
    }

    private static async Task<IssueInfo?> ResolveIssueAsync(string projectId, int number, IGrainFactory grains)
    {
        try
        {
            return await grains.GetGrain<IIssueGrain>($"{projectId}:{number}").GetInfoAsync();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

public record LogLevelRequest(string Level);
public record AgentRuntimeRequest(int? Timeout, int? StageTimeout, int? TaskTimeout, int? MaxConcurrent, int? MaxGracePeriods, int? PollInterval);
public record MessageRequest(string? Message);
public record CommentRequest(string? Body);
public record PrerequisiteRequest(int PrerequisiteNumber);
