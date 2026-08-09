using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public sealed class RoutingRuleStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly AgentQuerier _agentQuerier;
    private readonly TimeProvider _timeProvider;

    public RoutingRuleStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        AgentQuerier agentQuerier,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _agentQuerier = agentQuerier;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<RoutingRule>> ListAsync(string projectId, bool includeArchived = true, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.RoutingRules.AsNoTracking().Where(rule => rule.ProjectId == projectId);
        if (!includeArchived)
            query = query.Where(rule => rule.Status == RoutingRuleStatus.Active);
        var rows = await query.OrderBy(rule => rule.Position).ThenBy(rule => rule.Id).ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<RoutingRule?> GetAsync(string projectId, string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.RoutingRules.AsNoTracking().FirstOrDefaultAsync(rule => rule.ProjectId == projectId && rule.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<RoutingRule?> GetByIdempotencyKeyAsync(string projectId, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.RoutingRules.AsNoTracking()
            .FirstOrDefaultAsync(rule => rule.ProjectId == projectId && rule.IdempotencyKey == key, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<RoutingRule> CreateAsync(
        RoutingRule rule,
        string? beforeId = null,
        string? afterId = null,
        CancellationToken ct = default,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (idempotencyKey is { Length: > 256 })
            throw new RoutingRuleValidationException("Idempotency-Key must be 256 characters or fewer.", "idempotency_key_invalid");

        if (idempotencyKey is not null)
        {
            var existing = await GetByIdempotencyKeyAsync(rule.ProjectId, idempotencyKey, ct);
            if (existing is not null)
                return existing;
        }

        await ValidateAsync(rule.ProjectId, rule.Name, rule.Match, rule.AgentId, rule.ResponsePrompt, null, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rules = await LoadProjectRulesAsync(db, rule.ProjectId, ct);
        var now = _timeProvider.GetUtcNow();
        rule.Status = RoutingRuleStatus.Active;
        rule.CreatedAt = now;
        rule.UpdatedAt = now;
        rule.Position = InsertPosition(rules, beforeId, afterId);
        rule.IdempotencyKey = idempotencyKey;
        var newRow = ToRow(rule);
        db.RoutingRules.Add(newRow);
        Renumber(rules, newRow, beforeId, afterId);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsNameConflict(ex))
        {
            throw new RoutingRuleNameConflictException(rule.ProjectId, rule.Name);
        }
        catch (DbUpdateException ex) when (IsIdempotencyConflict(ex) && idempotencyKey is not null)
        {
            await transaction.RollbackAsync(ct);
            var existing = await db.RoutingRules.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.ProjectId == rule.ProjectId && candidate.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
                return ToDomain(existing);
            throw;
        }
        return rule;
    }

    public async Task<RoutingRule?> UpdateAsync(
        string projectId,
        string id,
        string? name,
        string? match,
        string? agentId,
        string? responsePrompt,
        bool? continueValue,
        IReadOnlySet<string> fields,
        CancellationToken ct = default)
    {
        var existing = await GetAsync(projectId, id, ct);
        if (existing is null) return null;
        var newName = fields.Contains(nameof(name)) ? name : existing.Name;
        var newMatch = fields.Contains(nameof(match)) ? match : existing.Match;
        var newAgentId = fields.Contains(nameof(agentId)) ? agentId : existing.AgentId;
        var newPrompt = fields.Contains(nameof(responsePrompt)) ? responsePrompt : existing.ResponsePrompt;
        var newContinue = fields.Contains("continue") ? continueValue : existing.Continue;
        await ValidateAsync(projectId, newName, newMatch, newAgentId, newPrompt, id, ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.RoutingRules.FirstOrDefaultAsync(rule => rule.ProjectId == projectId && rule.Id == id, ct);
        if (row is null) return null;
        if (row.Name == newName!.Trim()
            && row.Match == newMatch
            && row.AgentId == newAgentId
            && row.ResponsePrompt == newPrompt
            && row.Continue == (newContinue ?? false))
            return ToDomain(row);
        row.Name = newName!.Trim();
        row.Match = newMatch!;
        row.AgentId = newAgentId!;
        row.ResponsePrompt = newPrompt!;
        row.Continue = newContinue ?? false;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsNameConflict(ex))
        {
            throw new RoutingRuleNameConflictException(projectId, row.Name);
        }
        return ToDomain(row);
    }

    public async Task<RoutingRule?> DeleteAsync(string projectId, string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.RoutingRules.FirstOrDefaultAsync(rule => rule.ProjectId == projectId && rule.Id == id, ct);
        if (row is null) return null;
        var deleted = ToDomain(row);
        var rows = await LoadProjectRulesAsync(db, projectId, ct);
        rows.RemoveAll(candidate => candidate.Id == id);
        for (var position = 0; position < rows.Count; position++)
            rows[position].Position = position + 1;
        db.RoutingRules.Remove(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }

    public async Task<RoutingRule?> ArchiveAsync(string projectId, string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.RoutingRules.FirstOrDefaultAsync(rule => rule.ProjectId == projectId && rule.Id == id, ct);
        if (row is null) return null;
        if (row.Status == RoutingRuleStatus.Archived) return ToDomain(row);
        row.Status = RoutingRuleStatus.Archived;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    public async Task<RoutingRule?> MoveAsync(string projectId, string id, string? beforeId, string? afterId, CancellationToken ct = default)
    {
        if ((beforeId is null) == (afterId is null))
            throw new RoutingRuleMoveTargetException();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.RoutingRules.FirstOrDefaultAsync(rule => rule.ProjectId == projectId && rule.Id == id, ct);
        if (row is null) return null;
        var targetId = beforeId ?? afterId!;
        var target = await db.RoutingRules.FirstOrDefaultAsync(rule => rule.ProjectId == projectId && rule.Id == targetId, ct);
        if (target is null) throw new RoutingRuleMoveTargetNotFoundException(targetId);
        if (target.Id == row.Id) throw new RoutingRuleMoveTargetException();
        var rows = await LoadProjectRulesAsync(db, projectId, ct);
        rows.Remove(row);
        var targetIndex = rows.FindIndex(candidate => candidate.Id == target.Id);
        var insertIndex = beforeId is not null ? targetIndex : targetIndex + 1;
        rows.Insert(insertIndex, row);
        for (var index = 0; index < rows.Count; index++) rows[index].Position = index + 1;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToDomain(row);
    }

    private async Task ValidateAsync(string projectId, string? name, string? match, string? agentId, string? prompt, string? existingId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new RoutingRuleValidationException("name is required", "name_required");
        if (string.IsNullOrWhiteSpace(match)) throw new RoutingRuleValidationException("match is required", "match_required");
        var compiled = EventMatchExpression.Compile(match);
        if (!compiled.IsSuccess)
            throw new RoutingRuleMatchException(compiled.Diagnostic!);
        if (string.IsNullOrWhiteSpace(agentId)) throw new RoutingRuleValidationException("agent is required", "agent_required");
        var agent = await _agentQuerier.GetByIdAsync(projectId, agentId);
        if (agent is null) throw new RoutingRuleValidationException($"Agent '{agentId}' was not found in project '{projectId}'.", "agent_not_found");
        if (!string.Equals(agent.Status, AgentStatus.Active, StringComparison.Ordinal))
            throw new RoutingRuleValidationException($"Agent '{agentId}' is archived.", "agent_archived");
        if (string.IsNullOrWhiteSpace(prompt)) throw new RoutingRuleValidationException("responsePrompt cannot be blank", "response_prompt_blank");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var duplicate = await db.RoutingRules.AnyAsync(rule => rule.ProjectId == projectId && rule.Name == name.Trim() && rule.Id != existingId, ct);
        if (duplicate) throw new RoutingRuleNameConflictException(projectId, name.Trim());
    }

    private static async Task<List<RoutingRuleRow>> LoadProjectRulesAsync(MohistDbContext db, string projectId, CancellationToken ct) =>
        await db.RoutingRules.Where(rule => rule.ProjectId == projectId).OrderBy(rule => rule.Position).ThenBy(rule => rule.Id).ToListAsync(ct);

    private static int InsertPosition(List<RoutingRuleRow> rules, string? beforeId, string? afterId)
    {
        if (beforeId is not null)
        {
            var target = rules.FindIndex(rule => rule.Id == beforeId);
            if (target < 0) throw new RoutingRuleMoveTargetNotFoundException(beforeId);
            return target + 1;
        }
        if (afterId is not null)
        {
            var target = rules.FindIndex(rule => rule.Id == afterId);
            if (target < 0) throw new RoutingRuleMoveTargetNotFoundException(afterId);
            return target + 2;
        }
        return rules.Count + 1;
    }

    private static void Renumber(List<RoutingRuleRow> rules, RoutingRuleRow rule, string? beforeId, string? afterId)
    {
        var index = beforeId is not null
            ? rules.FindIndex(candidate => candidate.Id == beforeId)
            : afterId is not null ? rules.FindIndex(candidate => candidate.Id == afterId) + 1 : rules.Count;
        index = Math.Clamp(index, 0, rules.Count);
        rules.Insert(index, rule);
        for (var position = 0; position < rules.Count; position++) rules[position].Position = position + 1;
    }

    private static RoutingRule ToDomain(RoutingRuleRow row) => new()
    {
        Id = row.Id, ProjectId = row.ProjectId, Name = row.Name, Position = row.Position, Match = row.Match,
        AgentId = row.AgentId, ResponsePrompt = row.ResponsePrompt, Continue = row.Continue,
        Status = row.Status, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
        IdempotencyKey = row.IdempotencyKey,
    };

    private static RoutingRuleRow ToRow(RoutingRule rule) => new()
    {
        Id = rule.Id, ProjectId = rule.ProjectId, Name = rule.Name, Position = rule.Position, Match = rule.Match,
        AgentId = rule.AgentId, ResponsePrompt = rule.ResponsePrompt, Continue = rule.Continue,
        Status = rule.Status, CreatedAt = rule.CreatedAt, UpdatedAt = rule.UpdatedAt,
        IdempotencyKey = rule.IdempotencyKey,
    };

    private static bool IsNameConflict(DbUpdateException ex) => ex.InnerException is SqliteException sqlite
        && sqlite.SqliteErrorCode == 19
        && sqlite.Message.Contains("RoutingRules", StringComparison.OrdinalIgnoreCase)
        && !sqlite.Message.Contains("IdempotencyKey", StringComparison.OrdinalIgnoreCase);
    private static bool IsIdempotencyConflict(DbUpdateException ex) => ex.InnerException is SqliteException sqlite
        && sqlite.SqliteErrorCode == 19
        && sqlite.Message.Contains("IdempotencyKey", StringComparison.OrdinalIgnoreCase);
}

public sealed class RoutingRuleValidationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class RoutingRuleMatchException(MatchDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    public MatchDiagnostic Diagnostic { get; } = diagnostic;
}

public sealed class RoutingRuleNameConflictException(string projectId, string name) : Exception($"A routing rule named '{name}' already exists in project '{projectId}'.")
{
    public string ProjectId { get; } = projectId;
    public string Name { get; } = name;
}

public sealed class RoutingRuleMoveTargetException() : Exception("Exactly one of beforeId or afterId is required.");

public sealed class RoutingRuleMoveTargetNotFoundException(string targetId) : Exception($"Routing rule '{targetId}' was not found.")
{
    public string TargetId { get; } = targetId;
}
