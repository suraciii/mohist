namespace Mohist.Server.Project.Domain;

public static class RepositoryPolicy
{
    public const string DefaultBaseBranch = "main";

    public sealed record ValidationError(string Code, string Message);

    public sealed record NormalizedRepository(
        string Name,
        string GitUrl,
        string BaseBranch,
        bool IsDefault);

    public enum TransitionKind
    {
        Add,
        Update,
        SetDefault,
        Remove,
    }

    public sealed record TransitionInput(
        string? Name,
        string? GitUrl = null,
        string? BaseBranch = null,
        bool? SetDefault = null);

    public sealed record TransitionRequest(
        TransitionKind Kind,
        string TargetName,
        TransitionInput Input);

    public sealed record TransitionResult(
        IReadOnlyList<NormalizedRepository> Repositories,
        RepositoryTransitionOutcome Outcome)
    {
        public static TransitionResult Mutated(IReadOnlyList<NormalizedRepository> repositories) =>
            new(repositories, RepositoryTransitionOutcome.Mutated);

        public static TransitionResult NoChange(IReadOnlyList<NormalizedRepository> repositories) =>
            new(repositories, RepositoryTransitionOutcome.Unchanged);
    }

    public enum RepositoryTransitionOutcome
    {
        Mutated,
        Unchanged,
    }

    public static bool TryNormalize(string? raw, out string value)
    {
        value = (raw ?? string.Empty).Trim();
        return !string.IsNullOrEmpty(value);
    }

    public static bool TryNormalizeBaseBranch(string? raw, out string value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = DefaultBaseBranch;
            return true;
        }

        value = raw.Trim();
        return !string.IsNullOrEmpty(value);
    }

    public static List<ValidationError> Validate(IReadOnlyList<NormalizedRepository> repositories)
    {
        var errors = new List<ValidationError>();

        if (repositories is null || repositories.Count == 0)
        {
            errors.Add(new("repository_list_empty", "Project must declare at least one repository."));
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaults = 0;

        for (var i = 0; i < repositories.Count; i++)
        {
            var repo = repositories[i];
            var prefix = $"repositories[{i}]";

            if (string.IsNullOrWhiteSpace(repo.Name))
                errors.Add(new($"{prefix}.name", $"{prefix}.name must be a non-empty string."));
            else if (!seen.Add(repo.Name))
                errors.Add(new($"{prefix}.name", $"Duplicate repository name '{repo.Name}' (case-insensitive)."));

            if (string.IsNullOrWhiteSpace(repo.GitUrl))
                errors.Add(new($"{prefix}.gitUrl", $"{prefix}.gitUrl must be a non-empty string."));

            if (string.IsNullOrWhiteSpace(repo.BaseBranch))
                errors.Add(new($"{prefix}.baseBranch", $"{prefix}.baseBranch must be a non-empty string."));

            if (repo.IsDefault)
                defaults++;
        }

        if (defaults == 0)
            errors.Add(new("repository_default_missing", "Project must have exactly one default repository."));
        else if (defaults > 1)
            errors.Add(new("repository_default_multiple", "Project must have exactly one default repository."));

        return errors;
    }

    public static IReadOnlyList<NormalizedRepository> Normalize(
        IReadOnlyList<NormalizedRepository> repositories)
    {
        if (repositories is null || repositories.Count == 0)
            return [];

        var defaults = repositories.Where(r => r.IsDefault).ToList();
        string defaultName;

        if (defaults.Count == 1)
        {
            defaultName = defaults[0].Name;
        }
        else if (defaults.Count > 1)
        {
            defaultName = defaults[0].Name;
        }
        else
        {
            defaultName = repositories[0].Name;
        }

        return repositories
            .Select(r => new NormalizedRepository(
                r.Name,
                r.GitUrl,
                string.IsNullOrWhiteSpace(r.BaseBranch) ? DefaultBaseBranch : r.BaseBranch,
                string.Equals(r.Name, defaultName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public static NormalizedRepository CreateInitial(
        string name,
        string gitUrl,
        string? baseBranch)
    {
        return new NormalizedRepository(
            name.Trim(),
            gitUrl.Trim(),
            ResolveBaseBranch(baseBranch),
            IsDefault: true);
    }

    public sealed record Result<T>(T Value, IReadOnlyList<ValidationError> Errors)
    {
        public bool IsSuccess => Errors.Count == 0;
    }

    public static Result<NormalizedRepository> BuildAdd(
        TransitionInput input,
        IReadOnlyList<NormalizedRepository> current)
    {
        var errors = new List<ValidationError>();

        if (!TryNormalize(input.Name, out var name))
            errors.Add(new("name", "Repository name is required."));
        if (!TryNormalize(input.GitUrl, out var gitUrl))
            errors.Add(new("gitUrl", "gitUrl is required."));
        if (!TryNormalizeBaseBranch(input.BaseBranch, out var baseBranch))
            errors.Add(new("baseBranch", "baseBranch must be a non-empty string."));

        if (errors.Count > 0)
            return new(default!, errors);

        if (current.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new("name", $"Repository '{name}' already exists."));
            return new(default!, errors);
        }

        var setDefault = input.SetDefault ?? false;
        var anyCurrentDefault = current.Any(r => r.IsDefault);
        var isDefault = setDefault || !anyCurrentDefault;

        return new(new NormalizedRepository(
            name,
            gitUrl,
            baseBranch,
            isDefault),
            []);
    }

    public static Result<TransitionUpdate> BuildUpdate(
        string targetName,
        TransitionInput input,
        IReadOnlyList<NormalizedRepository> current)
    {
        if (string.IsNullOrWhiteSpace(targetName))
            return new(default!, new[] { new ValidationError("name", "Repository name is required.") });

        var repo = current.FirstOrDefault(r =>
            string.Equals(r.Name, targetName, StringComparison.OrdinalIgnoreCase));

        if (repo is null)
            return new(default!, new[] { new ValidationError("name", $"Repository '{targetName}' not found.") });

        var hasGitUrl = input.GitUrl is not null && !string.IsNullOrWhiteSpace(input.GitUrl);
        var hasBaseBranch = input.BaseBranch is not null;

        if (!hasGitUrl && !hasBaseBranch)
            return new(default!, new[] { new ValidationError("update", "Provide gitUrl and/or baseBranch to update.") });

        var baseBranch = repo.BaseBranch;
        if (hasBaseBranch && !TryNormalizeBaseBranch(input.BaseBranch, out baseBranch))
            return new(default!, new[] { new ValidationError("baseBranch", "baseBranch must be a non-empty string.") });

        var newGitUrl = hasGitUrl ? input.GitUrl!.Trim() : repo.GitUrl;

        return new(
            new TransitionUpdate(targetName, repo, repo with
            {
                GitUrl = newGitUrl,
                BaseBranch = baseBranch,
            }),
            []);
    }

    public sealed record TransitionUpdate(
        string TargetName,
        NormalizedRepository Previous,
        NormalizedRepository Next)
    {
        public bool Changed =>
            !string.Equals(Previous.GitUrl, Next.GitUrl, StringComparison.Ordinal)
            || !string.Equals(Previous.BaseBranch, Next.BaseBranch, StringComparison.Ordinal);
    }

    public static Result<(NormalizedRepository Previous, NormalizedRepository Next)> BuildSetDefault(
        string targetName,
        IReadOnlyList<NormalizedRepository> current)
    {
        if (string.IsNullOrWhiteSpace(targetName))
            return new(default!, new[] { new ValidationError("name", "Repository name is required.") });

        var match = current.FirstOrDefault(r =>
            string.Equals(r.Name, targetName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            return new(default!, new[] { new ValidationError("name", $"Repository '{targetName}' not found.") });

        if (match.IsDefault && current.Count(r => r.IsDefault) == 1)
            return new((match, match), []);

        var cleared = current
            .Select(r => r with { IsDefault = false })
            .ToArray();

        var target = match with { IsDefault = true };

        var next = cleared
            .Select(r => string.Equals(r.Name, match.Name, StringComparison.OrdinalIgnoreCase) ? target : r)
            .ToList();

        return new((match, target), []);
    }

    public static Result<NormalizedRepository> BuildRemove(
        string targetName,
        IReadOnlyList<NormalizedRepository> current)
    {
        if (string.IsNullOrWhiteSpace(targetName))
            return new(default!, new[] { new ValidationError("name", "Repository name is required.") });

        var match = current.FirstOrDefault(r =>
            string.Equals(r.Name, targetName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            return new(default!, new[] { new ValidationError("name", $"Repository '{targetName}' not found.") });

        if (match.IsDefault)
            return new(default!, new[]
            {
                new ValidationError(
                    "repository_default_deletion_conflict",
                    $"Repository '{match.Name}' is the default. Run 'mo repo set-default <other-name>' first."),
            });

        return new(match, []);
    }

    public static string ResolveBaseBranch(string? raw) =>
        TryNormalizeBaseBranch(raw, out var value) ? value : DefaultBaseBranch;
}
