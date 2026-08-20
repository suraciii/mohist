using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Label.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Label;

[Collection("MohistDb")]
public class LabelCatalogServiceSpecs
{
    private readonly IServiceProvider _services;

    public LabelCatalogServiceSpecs(MohistDbFixture fixture)
    {
        _services = fixture.Services;
    }

    private LabelCatalogService CreateService() =>
        _services.GetRequiredService<LabelCatalogService>();

    private MohistDbContext CreateDbContext()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        return factory.CreateDbContext();
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyUserDefinitionsForProject()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var createResult = await service.CreateAsync(projectId, "module",
            "Classifies the subsystem an issue touches", new[] { "auth", "ui" });
        Assert.Null(createResult.Error);
        Assert.NotNull(createResult.Definition);

        var definitions = await service.ListAsync(projectId);

        var module = Assert.Single(definitions, d => d.Key == "module");
        Assert.Equal("Classifies the subsystem an issue touches", module.Description);
        Assert.NotNull(module.SupportedValues);
        Assert.Contains("auth", module.SupportedValues);
        Assert.Contains("ui", module.SupportedValues);
    }

    [Fact]
    public async Task ListAsync_ReturnsEmptyForProjectWithNoDefinitions()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var definitions = await service.ListAsync(projectId);

        Assert.Empty(definitions);
    }

    [Fact]
    public async Task CreateAsync_WithValidData_PersistsAndReturnsDefinition()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var result = await service.CreateAsync(projectId, "module",
            "Classifies the subsystem an issue touches", new[] { "auth", "ui" });

        Assert.Null(result.Error);
        Assert.NotNull(result.Definition);
        Assert.Equal("module", result.Definition.Key);
        Assert.Equal("Classifies the subsystem an issue touches", result.Definition.Description);
        Assert.NotNull(result.Definition.SupportedValues);
        Assert.Equal(2, result.Definition.SupportedValues.Count);

        var definitions = await service.ListAsync(projectId);
        Assert.Contains(definitions, d => d.Key == "module");
    }

    [Fact]
    public async Task CreateAsync_WithExistingUserKey_RejectsDuplicate()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        await service.CreateAsync(projectId, "module", "First description");
        var result = await service.CreateAsync(projectId, "module", "Second description");

        Assert.NotNull(result.Error);
        Assert.Contains("already exists", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Definition);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidKey_RejectsWithError()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var result = await service.CreateAsync(projectId, "Module",
            "Classifies the subsystem");

        Assert.NotNull(result.Error);
        Assert.Contains("invalid", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Module", result.Error);
        Assert.Null(result.Definition);

        using var db = CreateDbContext();
        var rows = await db.LabelDefinitions.Where(r => r.ProjectId == projectId).ToListAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task CreateAsync_WithLeadingDashKey_RejectsWithError()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var result = await service.CreateAsync(projectId, "-mod",
            "Classifies the subsystem");

        Assert.NotNull(result.Error);
        Assert.Contains("invalid", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Definition);
    }

    [Fact]
    public async Task CreateAsync_WithEmptyDescription_RejectsWithError()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var result = await service.CreateAsync(projectId, "module", "   ");

        Assert.NotNull(result.Error);
        Assert.Contains("non-empty", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Definition);

        using var db = CreateDbContext();
        var rows = await db.LabelDefinitions.Where(r => r.ProjectId == projectId).ToListAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task CreateAsync_WithEmptySupportedValue_RejectsWithError()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var result = await service.CreateAsync(projectId, "module",
            "Classifies the subsystem", new[] { "auth", "", "ui" });

        Assert.NotNull(result.Error);
        Assert.Contains("non-empty", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Definition);

        using var db = CreateDbContext();
        var rows = await db.LabelDefinitions.Where(r => r.ProjectId == projectId).ToListAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task UpdateAsync_ExistingUserDefinition_UpdatesSuccessfully()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        await service.CreateAsync(projectId, "module", "Original description");
        var result = await service.UpdateAsync(projectId, "module",
            "Updated description", new[] { "data" });

        Assert.Null(result.Error);
        Assert.NotNull(result.Definition);
        Assert.Equal("Updated description", result.Definition.Description);
        Assert.NotNull(result.Definition.SupportedValues);
        Assert.Single(result.Definition.SupportedValues);
        Assert.Equal("data", result.Definition.SupportedValues[0]);
    }

    [Fact]
    public async Task UpdateAsync_MissingKey_ReturnsNotFound()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var result = await service.UpdateAsync(projectId, "nonexistent",
            "Some description");

        Assert.True(result.NotFound);
        Assert.NotNull(result.Error);
        Assert.Null(result.Definition);
    }

    [Fact]
    public async Task UpdateAsync_WithEmptySupportedValues_ClearsThem()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        await service.CreateAsync(projectId, "module", "Classifies the subsystem", new[] { "auth", "ui" });
        var result = await service.UpdateAsync(projectId, "module",
            "Classifies the subsystem", Array.Empty<string>());

        Assert.Null(result.Error);
        Assert.NotNull(result.Definition);
        Assert.Null(result.Definition.SupportedValues);
    }

    [Fact]
    public async Task DeleteAsync_ExistingUserKey_RemovesIt()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        await service.CreateAsync(projectId, "module", "Classifies the subsystem");
        var result = await service.DeleteAsync(projectId, "module");

        Assert.Null(result.Error);

        var definitions = await service.ListAsync(projectId);
        Assert.DoesNotContain(definitions, d => d.Key == "module");
    }

    [Fact]
    public async Task DeleteAsync_MissingKey_IsIdempotent()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var result = await service.DeleteAsync(projectId, "nonexistent");

        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Catalog_IsProjectScoped()
    {
        var service = CreateService();
        var projectA = $"proj-a-{Guid.NewGuid():N}";
        var projectB = $"proj-b-{Guid.NewGuid():N}";

        await service.CreateAsync(projectA, "module", "Project A module def");

        var catalogA = await service.ListAsync(projectA);
        var catalogB = await service.ListAsync(projectB);

        Assert.Contains(catalogA, d => d.Key == "module");
        Assert.DoesNotContain(catalogB, d => d.Key == "module");
    }

    [Fact]
    public async Task CatalogOperations_DoNotTouchIssueLabels()
    {
        var service = CreateService();
        var projectId = $"proj-{Guid.NewGuid():N}";
        using var db = CreateDbContext();

        var issueCountBefore = await db.Issues.CountAsync();
        var labelsBefore = await db.LabelDefinitions.CountAsync();

        await service.CreateAsync(projectId, "module", "Classifies the subsystem");
        await service.UpdateAsync(projectId, "module", "Updated subsystem");
        await service.DeleteAsync(projectId, "module");

        var issueCountAfter = await db.Issues.CountAsync();
        var labelsAfter = await db.LabelDefinitions.CountAsync();

        Assert.Equal(issueCountBefore, issueCountAfter);
        Assert.Equal(labelsBefore, labelsAfter);
    }
}
