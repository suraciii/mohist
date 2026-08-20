using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.DirectApi;

internal static class DirectApiCredentialTestSupport
{
    internal static async Task<string> CreatePatAsync(
        MohistIntegrationFixture fixture,
        string namePrefix,
        IReadOnlyList<string> projectIds,
        string scope = "operator")
    {
        if (!Scope.TryParse(scope, out var parsedScope))
            throw new ArgumentException($"Unknown PAT scope '{scope}'.", nameof(scope));

        await using var serviceScope = fixture.Services.CreateAsyncScope();
        var result = await serviceScope.ServiceProvider.GetRequiredService<ICredentialStore>().CreatePatAsync(
            principalId: "direct-api-specs",
            name: $"{namePrefix}-{Guid.NewGuid():N}",
            scopes: [parsedScope],
            expiresAt: fixture.TimeProvider.GetUtcNow().AddDays(1),
            directApiProjectGrant: DirectApiProjectGrant.Explicit(projectIds));
        if (result.Status != PatCreateStatus.Created || string.IsNullOrWhiteSpace(result.Token))
            throw new InvalidOperationException($"Could not create Direct API test PAT: {result.Status}.");
        return result.Token;
    }
}
