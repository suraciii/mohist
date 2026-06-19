using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Services.IssueTemplates;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.IssueTemplate;

public sealed class FakeDbContextFactory : IDbContextFactory<MohistDbContext>
{
    private readonly SqliteConnection _connection;

    public FakeDbContextFactory(Action<MohistDbContext>? seed = null)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
        seed?.Invoke(db);
    }

    public MohistDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(_connection).Options;
        return new MohistDbContext(options);
    }

    public void Dispose() => _connection.Dispose();
}

public class IssueTemplateRegistrySpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_WithoutProjectId_ReturnsDefaultTemplate()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List();

        var defaultTemplate = Assert.Single(list);
        Assert.Equal("mohist/default", defaultTemplate.Id);
        Assert.Equal("Mohist Default", defaultTemplate.Name);
        Assert.True(defaultTemplate.IsDefault);
        Assert.Equal("builtin", defaultTemplate.Source);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void DefaultTemplate_SectionsAreInExactOrder()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory);
        var template = registry.Get("mohist/default");

        Assert.Equal(5, template.Sections.Count);
        Assert.Equal("User Voice", template.Sections[0].Title);
        Assert.Equal("Product Shape", template.Sections[1].Title);
        Assert.Equal("Domain Model", template.Sections[2].Title);
        Assert.Equal("Acceptance Criteria", template.Sections[3].Title);
        Assert.Equal("Non-Goals", template.Sections[4].Title);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void DefaultTemplate_EachSectionHasNonEmptyGuidanceAndPlaceholder()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory);
        var template = registry.Get("mohist/default");

        foreach (var section in template.Sections)
        {
            Assert.False(string.IsNullOrWhiteSpace(section.Guidance),
                $"Section '{section.Title}' has empty guidance");
            Assert.False(string.IsNullOrWhiteSpace(section.Placeholder),
                $"Section '{section.Title}' has empty placeholder");
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void DefaultTemplate_SuitableForContainsValues()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory);
        var template = registry.Get("mohist/default");

        Assert.NotEmpty(template.SuitableFor);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Matches_UsesSharedSuitableForSemantics()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "custom",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "custom",
                    Name = "Custom",
                    About = "Custom template",
                    IsDefault = false,
                    SuitableFor = new[] { "Bug Reports" },
                    Defaults = new { },
                    Sections = new[]
                    {
                        new { Title = "S", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        Assert.True(registry.Matches("custom", "bug reports", "project-1"));
        Assert.False(registry.Matches("custom", "product requirements", "project-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_WithProjectId_ReturnsDefaultWhenNotDisabled()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = "project-1",
                DisableDefaultIssueTemplate = false,
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List("project-1");

        Assert.Contains(list, t => t.Id == "mohist/default");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_WithProjectId_ExcludesDefaultWhenDisabled()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = "project-1",
                DisableDefaultIssueTemplate = true,
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List("project-1");

        Assert.DoesNotContain(list, t => t.Id == "mohist/default");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_WithProjectId_MergesBuiltinAndCustomTemplates()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "bug-report",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "bug-report",
                    Name = "Bug Report",
                    About = "For reporting bugs",
                    IsDefault = false,
                    SuitableFor = new[] { "bug reports" },
                    Defaults = new { Risk = "high" },
                    Sections = new[]
                    {
                        new { Title = "Reproduction", Guidance = "Steps to reproduce", Placeholder = "1. ..." },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List("project-1");

        Assert.Equal(2, list.Count);
        Assert.Contains(list, t => t.Id == "mohist/default" && t.Source == "builtin");
        Assert.Contains(list, t => t.Id == "bug-report" && t.Source == "custom");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_DefaultsOrderedFirst()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "custom",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "custom",
                    Name = "Custom",
                    About = "Custom template",
                    IsDefault = false,
                    SuitableFor = new[] { "custom" },
                    Defaults = new { },
                    Sections = new[]
                    {
                        new { Title = "Section 1", Guidance = "Guidance", Placeholder = "Placeholder" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List("project-1");

        Assert.Equal(2, list.Count);
        Assert.Equal("mohist/default", list[0].Id); // Default first
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Get_DefaultTemplate_ReturnsFullSections()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory);

        var template = registry.Get("mohist/default");

        Assert.NotNull(template);
        Assert.Equal("mohist/default", template.Id);
        Assert.Equal(5, template.Sections.Count);
        foreach (var section in template.Sections)
        {
            Assert.False(string.IsNullOrWhiteSpace(section.Title));
            Assert.False(string.IsNullOrWhiteSpace(section.Guidance));
            Assert.False(string.IsNullOrWhiteSpace(section.Placeholder));
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Get_NullOrEmptyId_ReturnsDefault()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory);

        var template = registry.Get(null);
        Assert.Equal("mohist/default", template.Id);

        var template2 = registry.Get("");
        Assert.Equal("mohist/default", template2.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Get_DisabledDefault_ThrowsKeyNotFoundException()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = "project-1",
                DisableDefaultIssueTemplate = true,
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        Assert.Throws<KeyNotFoundException>(() => registry.Get("mohist/default", "project-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Get_NonexistentTemplate_ThrowsKeyNotFoundException()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory);

        Assert.Throws<KeyNotFoundException>(() => registry.Get("nonexistent"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Exists_DefaultTemplate_ReturnsTrue()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory);

        Assert.True(registry.Exists("mohist/default"));
        Assert.True(registry.Exists("Mohist/Default")); // Case insensitive
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Exists_NonexistentTemplate_ReturnsFalse()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory);

        Assert.False(registry.Exists("nonexistent"));
        Assert.False(registry.Exists(null));
        Assert.False(registry.Exists(""));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Default_Property_ReturnsDefaultTemplateInfo()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory);

        var info = registry.Default;

        Assert.Equal("mohist/default", info.Id);
        Assert.True(info.IsDefault);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_CustomTemplateIsValid_IsSurfaced()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "custom",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "custom",
                    Name = "Custom Template",
                    About = "For custom workflows",
                    IsDefault = false,
                    SuitableFor = new[] { "custom" },
                    Defaults = new { Risk = "medium", Workflow = "custom-workflow" },
                    Sections = new[]
                    {
                        new { Title = "Overview", Guidance = "Write an overview", Placeholder = "<overview>" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List("project-1");

        Assert.Contains(list, t => t.Id == "custom");
        var custom = list.Single(t => t.Id == "custom");
        Assert.Equal("Custom Template", custom.Name);
        Assert.False(custom.IsDefault);
        Assert.Equal("custom", custom.Source);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_InvalidTemplate_MissingSectionGuidance_IsNotSurfaced()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "invalid",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "invalid",
                    Name = "Invalid",
                    About = "Invalid template",
                    IsDefault = false,
                    SuitableFor = new[] { "x" },
                    Defaults = new { },
                    Sections = new[]
                    {
                        new { Title = "Section", Guidance = "", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List("project-1");

        // Invalid template should not appear; only the default
        Assert.DoesNotContain(list, t => t.Id == "invalid");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_InvalidTemplate_MissingSectionPlaceholder_IsNotSurfaced()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "invalid",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "invalid",
                    Name = "Invalid",
                    About = "Missing placeholder",
                    IsDefault = false,
                    SuitableFor = new[] { "x" },
                    Defaults = new { },
                    Sections = new[]
                    {
                        new { Title = "Section", Guidance = "g", Placeholder = "" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List("project-1");

        Assert.DoesNotContain(list, t => t.Id == "invalid");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_InvalidTemplate_MissingSectionTitle_IsNotSurfaced()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "invalid",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "invalid",
                    Name = "Invalid",
                    About = "Missing title",
                    IsDefault = false,
                    SuitableFor = new[] { "x" },
                    Defaults = new { },
                    Sections = new[]
                    {
                        new { Title = "", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List("project-1");

        Assert.DoesNotContain(list, t => t.Id == "invalid");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_InvalidTemplate_MissingRequiredFrontmatterField_IsNotSurfaced()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "invalid",
                // Missing Id field
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Name = "Invalid",
                    About = "Missing id",
                    IsDefault = false,
                    SuitableFor = new[] { "x" },
                    Defaults = new { },
                    Sections = new[]
                    {
                        new { Title = "Section", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List("project-1");

        Assert.DoesNotContain(list, t => t.Id == "invalid");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_InvalidTemplate_RowNameAndIdMismatch_IsNotSurfaced()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "bug-report",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "other-id",
                    Name = "Bug Report",
                    About = "Mismatched id",
                    IsDefault = false,
                    SuitableFor = new[] { "bug" },
                    Defaults = new { },
                    Sections = new[]
                    {
                        new { Title = "S", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List("project-1");

        Assert.DoesNotContain(list, t => t.Id == "other-id");
        Assert.False(registry.Exists("bug-report", "project-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_InvalidTemplate_EmptySections_IsNotSurfaced()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "invalid",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "invalid",
                    Name = "Invalid",
                    About = "No sections",
                    IsDefault = false,
                    SuitableFor = new[] { "x" },
                    Defaults = new { },
                    Sections = Array.Empty<object>(),
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List("project-1");

        Assert.DoesNotContain(list, t => t.Id == "invalid");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void CustomTemplates_AreProjectPrivate()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-A",
                Name = "custom",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "custom",
                    Name = "Custom",
                    About = "Custom template",
                    IsDefault = false,
                    SuitableFor = new[] { "custom" },
                    Defaults = new { },
                    Sections = new[]
                    {
                        new { Title = "S", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var listA = registry.List("project-A");
        Assert.Contains(listA, t => t.Id == "custom");

        var listB = registry.List("project-B");
        Assert.DoesNotContain(listB, t => t.Id == "custom");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void DisableDefault_AffectsOnlySpecifiedProject()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = "project-1",
                DisableDefaultIssueTemplate = true,
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list1 = registry.List("project-1");
        Assert.DoesNotContain(list1, t => t.Id == "mohist/default");

        var list2 = registry.List("project-2");
        Assert.Contains(list2, t => t.Id == "mohist/default");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void DefaultTemplate_Defaults_HasCorrectRiskAndWorkflow()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory);
        var template = registry.Get("mohist/default");

        Assert.Equal("medium", template.Defaults.Risk);
        Assert.Equal("mohist/default", template.Defaults.Workflow);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Get_CustomTemplate_ReturnsFullTemplate()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "custom",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "custom",
                    Name = "Custom",
                    About = "Custom template",
                    IsDefault = false,
                    SuitableFor = new[] { "custom" },
                    Defaults = new { Risk = "low", Workflow = "custom-wf", Labels = new Dictionary<string, string> { ["type"] = "bug" } },
                    Sections = new[]
                    {
                        new { Title = "S", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var template = registry.Get("custom", "project-1");
        Assert.Equal("custom", template.Id);
        Assert.Equal("low", template.Defaults.Risk);
        Assert.Equal("custom-wf", template.Defaults.Workflow);
        Assert.NotNull(template.Defaults.Labels);
        Assert.Contains("type", template.Defaults.Labels!.Keys);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_WithCorruptTemplate_IsNotSurfaced()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "corrupt",
                Template = "not valid json {{{",
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory);

        var list = registry.List("project-1");

        Assert.DoesNotContain(list, t => t.Id == "corrupt");
    }
}
