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
    private static Dictionary<string, BuiltinTemplateEntry> Builtins() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["feature"] = new("Feature", "Product feature work", "## Section A\n\n<!-- guidance-a -->\n\n<placeholder-a>\n\n## Section B\n\nBody text"),
        ["bug"] = new("Bug", "Fix functional bugs", "## Symptom\n\n<!-- steps -->\n\n<repro>\n\n## Fix\n\nBody"),
        ["refactor"] = new("Refactor", "Internal quality", "## Motivation\n\n<!-- why -->\n\n<reason>"),
    };

    private const string FeatureSections = "## Section A\n\n<!-- guidance-a -->\n\n<placeholder-a>\n\n## Section B\n\nBody text";

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_WithoutProjectId_ReturnsThreeBuiltins()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list = registry.List();

        Assert.Equal(3, list.Count);
        Assert.Contains(list, t => t.Id == "feature" && t.Source == "builtin");
        Assert.Contains(list, t => t.Id == "bug" && t.Source == "builtin");
        Assert.Contains(list, t => t.Id == "refactor" && t.Source == "builtin");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_EntriesHaveNameAndDescriptionOnly()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list = registry.List();

        foreach (var entry in list)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
            Assert.Equal("builtin", entry.Source);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_EntriesAreSortedById()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list = registry.List();

        Assert.Equal(3, list.Count);
        Assert.Equal("bug", list[0].Id);
        Assert.Equal("feature", list[1].Id);
        Assert.Equal("refactor", list[2].Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Get_Feature_ReturnsFullSections()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var template = registry.Get("feature");

        Assert.Equal("feature", template.Id);
        Assert.Equal("Feature", template.Name);
        Assert.Equal("Product feature work", template.Description);
        Assert.Equal(2, template.Sections.Count);
        Assert.Equal("Section A", template.Sections[0].Title);
        Assert.Equal("guidance-a", template.Sections[0].Guidance);
        Assert.Equal("<placeholder-a>", template.Sections[0].Placeholder);
        Assert.Equal("Section B", template.Sections[1].Title);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Get_NullOrEmptyId_ReturnsDefaultFeature()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var template = registry.Get(null);
        Assert.Equal("feature", template.Id);

        var template2 = registry.Get("");
        Assert.Equal("feature", template2.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Get_Bug_ReturnsBugTemplate()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var template = registry.Get("bug");

        Assert.Equal("bug", template.Id);
        Assert.Equal("Bug", template.Name);
        Assert.Equal("Fix functional bugs", template.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Get_Refactor_ReturnsRefactorTemplate()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var template = registry.Get("refactor");

        Assert.Equal("refactor", template.Id);
        Assert.Equal("Refactor", template.Name);
        Assert.Equal("Internal quality", template.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Get_AliasMohistDefault_ReturnsFeature()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var alias = registry.Get("mohist/default");
        var canonical = registry.Get("feature");

        Assert.Equal(canonical.Id, alias.Id);
        Assert.Equal(canonical.Name, alias.Name);
        Assert.Equal(canonical.Description, alias.Description);
        Assert.Equal(canonical.Sections.Count, alias.Sections.Count);
        Assert.Equal(canonical.Sections[0].Title, alias.Sections[0].Title);
        Assert.Equal(canonical.Sections[0].Guidance, alias.Sections[0].Guidance);
        Assert.Equal(canonical.Sections[0].Placeholder, alias.Sections[0].Placeholder);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Exists_FeatureAndAlias_ReturnTrue()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        Assert.True(registry.Exists("feature"));
        Assert.True(registry.Exists("mohist/default"));
        Assert.True(registry.Exists("bug"));
        Assert.True(registry.Exists("refactor"));
        Assert.True(registry.Exists("Feature")); // case insensitive
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Exists_Nonexistent_ReturnsFalse()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        Assert.False(registry.Exists("nonexistent"));
        Assert.False(registry.Exists(null));
        Assert.False(registry.Exists(""));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Get_NonexistentTemplate_ThrowsKeyNotFoundException()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        Assert.Throws<KeyNotFoundException>(() => registry.Get("nonexistent"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void IsBuiltin_ReturnsTrueForBuiltinsAndAlias()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        Assert.True(registry.IsBuiltin("feature"));
        Assert.True(registry.IsBuiltin("mohist/default"));
        Assert.True(registry.IsBuiltin("bug"));
        Assert.False(registry.IsBuiltin("nonexistent"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_WithProjectId_ReturnsBuiltinsWhenNotDisabled()
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
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list = registry.List("project-1");

        Assert.Contains(list, t => t.Id == "feature");
        Assert.Contains(list, t => t.Id == "bug");
        Assert.Contains(list, t => t.Id == "refactor");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_WithProjectId_ExcludesAllBuiltinsWhenDisabled()
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
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list = registry.List("project-1");

        Assert.DoesNotContain(list, t => t.Id == "feature");
        Assert.DoesNotContain(list, t => t.Id == "bug");
        Assert.DoesNotContain(list, t => t.Id == "refactor");
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
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        Assert.Throws<KeyNotFoundException>(() => registry.Get("feature", "project-1"));
        Assert.Throws<KeyNotFoundException>(() => registry.Get("bug", "project-1"));
        Assert.Throws<KeyNotFoundException>(() => registry.Get("refactor", "project-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Default_Property_ReturnsFeature()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var info = registry.Default;

        Assert.Equal("feature", info.Id);
        Assert.Equal("Feature", info.Name);
        Assert.Equal("Product feature work", info.Description);
        Assert.Equal("builtin", info.Source);
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
                Name = "custom",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "custom",
                    Name = "Custom",
                    About = "Custom template",
                    Sections = new[]
                    {
                        new { Title = "S", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list = registry.List("project-1");

        Assert.Equal(4, list.Count);
        Assert.Contains(list, t => t.Id == "feature" && t.Source == "builtin");
        Assert.Contains(list, t => t.Id == "custom" && t.Source == "custom");
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
                    Sections = new[]
                    {
                        new { Title = "S", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

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
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list1 = registry.List("project-1");
        Assert.DoesNotContain(list1, t => t.Id == "feature");
        Assert.DoesNotContain(list1, t => t.Id == "bug");

        var list2 = registry.List("project-2");
        Assert.Contains(list2, t => t.Id == "feature");
        Assert.Contains(list2, t => t.Id == "bug");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void CustomTemplate_WithOnlyIdNameAndSections_IsAccepted()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "minimal",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "minimal",
                    Name = "Minimal",
                    Sections = new[]
                    {
                        new { Title = "S", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list = registry.List("project-1");
        Assert.Contains(list, t => t.Id == "minimal");

        var template = registry.Get("minimal", "project-1");
        Assert.Equal("minimal", template.Id);
        Assert.Equal("Minimal", template.Name);
        Assert.Equal(string.Empty, template.Description); // No About provided
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void CustomTemplate_LegacyAbout_MapsToDescription()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "legacy",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Id = "legacy",
                    Name = "Legacy",
                    About = "Old description",
                    IsDefault = false,
                    SuitableFor = new[] { "bug" },
                    Defaults = new { Risk = "high" },
                    Sections = new[]
                    {
                        new { Title = "S", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var template = registry.Get("legacy", "project-1");
        Assert.Equal("legacy", template.Id);
        Assert.Equal("Old description", template.Description);
        Assert.Single(template.Sections);
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
                    Sections = new[]
                    {
                        new { Title = "Section", Guidance = "", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list = registry.List("project-1");

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
                    Sections = new[]
                    {
                        new { Title = "Section", Guidance = "g", Placeholder = "" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

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
                    Sections = new[]
                    {
                        new { Title = "", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list = registry.List("project-1");

        Assert.DoesNotContain(list, t => t.Id == "invalid");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void List_InvalidTemplate_MissingRequiredFields_IsNotSurfaced()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "invalid",
                Template = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Name = "Invalid",
                    Sections = new[]
                    {
                        new { Title = "Section", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

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
                    Sections = new[]
                    {
                        new { Title = "S", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

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
                    Sections = Array.Empty<object>(),
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list = registry.List("project-1");

        Assert.DoesNotContain(list, t => t.Id == "invalid");
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
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list = registry.List("project-1");

        Assert.DoesNotContain(list, t => t.Id == "corrupt");
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
                    Sections = new[]
                    {
                        new { Title = "S", Guidance = "g", Placeholder = "p" },
                    },
                }),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var template = registry.Get("custom", "project-1");
        Assert.Equal("custom", template.Id);
        Assert.Equal("Custom", template.Name);
        Assert.Equal("Custom template", template.Description);
        Assert.Single(template.Sections);
    }
}
