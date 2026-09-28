using Mohist.Server.Project.Domain;

namespace Mohist.Server.Issue.Services;

public interface IRepositorySourcePreflight
{
    Task<RepositorySourcePreflightResult> CheckAsync(string projectId, RepositoryInfo repository, CancellationToken cancellationToken = default);
}

public sealed record RepositorySourcePreflightResult(bool IsValid, string Code, string? Field, string Message, string? NextStep)
{
    public static RepositorySourcePreflightResult Valid() => new(true, string.Empty, null, string.Empty, null);
    public static RepositorySourcePreflightResult Invalid(string code, string field, string message, string nextStep) => new(false, code, field, message, nextStep);
}

