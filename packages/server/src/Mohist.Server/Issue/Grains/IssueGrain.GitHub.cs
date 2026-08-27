using Mohist.Server.Issue.Domain;

namespace Mohist.Server.Issue.Grains;

public partial class IssueGrain
{
    public async Task UpdateFromGitHubAsync(string title, string? body, string source)
    {
        EnsureIssue();
        _issue!.ReplaceContent(title, body, source, _timeProvider.GetUtcNow().UtcDateTime);
        await SaveIssueAsync();
    }
}
