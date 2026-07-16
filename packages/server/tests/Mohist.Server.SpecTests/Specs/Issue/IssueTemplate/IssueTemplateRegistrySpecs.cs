using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Services.IssueTemplates;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.IssueTemplate;

public sealed class FakeDbContextFactory : IDbContextFactory<MohistDbContext>
{
    private readonly SqliteConnection _connection;

    public FakeDbContextFactory(Action<MohistDbContext>? seed = null)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        MigratedSqliteTemplate.CopyTo(_connection);
        using var db = CreateDbContext();
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
    private const string FeatureSections = "## Section A\n\n<!-- guidance-a -->\n\n<placeholder-a>\n\n## Section B\n\nBody text";

    private static BuiltinTemplateEntry Builtin(string name, string description, string body) =>
        new(name, description, $"/templates/{name}.md", () => $"---\nname: {name}\ndescription: {description}\n---\n{body}");

    private static Dictionary<string, BuiltinTemplateEntry> Builtins() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["feature"] = Builtin("Feature", "Product feature work", FeatureSections),
        ["bug"] = Builtin("Bug", "Fix functional bugs", "## Symptom\n\n<!-- steps -->\n\n<repro>\n\n## Fix\n\nBody"),
        ["refactor"] = Builtin("Refactor", "Internal quality", "## Motivation\n\n<!-- why -->\n\n<reason>"),
    };

    private static string CustomTemplateJson(string id, string name, string description) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            Id = id,
            Name = name,
            About = description,
            Sections = new[]
            {
                new { Title = "S", Guidance = "g", Placeholder = "p" },
            },
        });

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

    [Fact]
    public void List_DoesNotReadBuiltInTemplateBodies()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/templates/feature.md"] = "---\nname: Feature\ndescription: Product feature work\n---\n## Body\n\n<feature>",
            ["/templates/bug.md"] = "---\nname: Bug\ndescription: Fix functional bugs\n---\n## Body\n\n<bug>",
        };
        var readers = new List<FrontmatterOnlyReader>();
        var fullReads = new List<string>();
        var loader = new IssueTemplateFileLoader(
            "/templates",
            (_, _) => files.Keys,
            path =>
            {
                var reader = new FrontmatterOnlyReader(files[path]);
                readers.Add(reader);
                return reader;
            },
            path =>
            {
                fullReads.Add(path);
                return files[path];
            });
        var registry = new IssueTemplateRegistry(new FakeDbContextFactory(), loader.Discover());

        var list = registry.List();

        Assert.Equal(2, list.Count);
        Assert.All(readers, reader => Assert.False(reader.BodyWasRead));
        Assert.Empty(fullReads);
    }

    [Fact]
    public void Discover_BuiltInWithUnterminatedFrontmatter_FailsBeforeScanningWholeBody()
    {
        var bodyLines = Enumerable.Range(0, 200)
            .Select(i => i == 150 ? "body-marker" : $"body-{i}");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/templates/feature.md"] = "---\nname: Feature\ndescription: Product feature work\n" + string.Join('\n', bodyLines),
        };
        var readers = new List<FrontmatterOnlyReader>();
        var loader = new IssueTemplateFileLoader(
            "/templates",
            (_, _) => files.Keys,
            path =>
            {
                var reader = new FrontmatterOnlyReader(files[path]);
                readers.Add(reader);
                return reader;
            },
            path => files[path]);

        var ex = Assert.Throws<InvalidOperationException>(() => loader.Discover());

        Assert.IsType<InvalidDataException>(ex.InnerException);
        var reader = Assert.Single(readers);
        Assert.False(reader.MarkerWasRead);
    }

    [Fact]
    public void Get_ReadsOnlyRequestedBuiltInTemplateBody()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/templates/feature.md"] = "---\nname: Feature\ndescription: Product feature work\n---\n## Feature Body\n\n<feature>",
            ["/templates/bug.md"] = "---\nname: Bug\ndescription: Fix functional bugs\n---\n## Bug Body\n\n<bug>",
        };
        var fullReads = new List<string>();
        var loader = new IssueTemplateFileLoader(
            "/templates",
            (_, _) => files.Keys,
            path => new StringReader(files[path]),
            path =>
            {
                fullReads.Add(path);
                return files[path];
            });
        var registry = new IssueTemplateRegistry(new FakeDbContextFactory(), loader.Discover());

        var template = registry.Get("bug");

        Assert.Equal("bug", template.Id);
        Assert.Contains("## Bug Body", template.Body);
        var path = Assert.Single(fullReads);
        Assert.Equal("/templates/bug.md", path);
    }

    [Fact]
    public void Get_Feature_ReturnsFullSections()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var template = registry.Get("feature");

        Assert.Equal("feature", template.Id);
        Assert.Equal("Feature", template.Name);
        Assert.Equal("Product feature work", template.Description);
        // Body is the raw markdown after frontmatter — contains both sections verbatim, including
        // the inline guidance comment (not parsed/stripped by the server).
        Assert.Contains("## Section A", template.Body);
        Assert.Contains("<!-- guidance-a -->", template.Body);
        Assert.Contains("<placeholder-a>", template.Body);
        Assert.Contains("## Section B", template.Body);
    }

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
        Assert.Equal(canonical.Body, alias.Body);
    }

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

    [Fact]
    public void Exists_Nonexistent_ReturnsFalse()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        Assert.False(registry.Exists("nonexistent"));
        Assert.False(registry.Exists(null));
        Assert.False(registry.Exists(""));
    }

    [Fact]
    public void Get_NonexistentTemplate_ThrowsKeyNotFoundException()
    {
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        Assert.Throws<KeyNotFoundException>(() => registry.Get("nonexistent"));
    }

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

    [Fact]
    public void DisabledBuiltIn_CanBeShadowedByProjectCustomTemplate()
    {
        var dbFactory = new FakeDbContextFactory(db =>
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = "project-1",
                DisableDefaultIssueTemplate = true,
            });
            db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
            {
                ProjectId = "project-1",
                Name = "feature",
                Template = CustomTemplateJson("feature", "Custom Feature", "Project feature template"),
            });
            db.SaveChanges();
        });
        var registry = new IssueTemplateRegistry(dbFactory, Builtins());

        var list = registry.List("project-1");
        var listed = Assert.Single(list, t => t.Id == "feature");
        Assert.Equal("custom", listed.Source);

        var template = registry.Get("feature", "project-1");
        Assert.Equal("feature", template.Id);
        Assert.Equal("Custom Feature", template.Name);
        Assert.Equal("Project feature template", template.Description);
        Assert.True(registry.Exists("feature", "project-1"));
    }

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
        Assert.Contains("## S", template.Body);
    }

    // Note: per-section field validation (Title/Guidance/Placeholder non-empty) was removed together
    // with IssueTemplateSection. The body is now a raw string composed from sections; an empty Title
    // or Placeholder no longer fails — it just yields a thinner body. Only structural problems
    // (missing Id/Name, id≠rowName, empty sections, corrupt JSON) still reject a custom template.

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

    [Fact]
    public void List_CustomTemplateWithEmptySections_StillSurfacesMetadata()
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

        var info = Assert.Single(list, t => t.Id == "invalid");
        Assert.Equal("Invalid", info.Name);
        Assert.Equal("custom", info.Source);
        Assert.False(registry.Exists("invalid", "project-1"));
        Assert.Throws<KeyNotFoundException>(() => registry.Get("invalid", "project-1"));
    }

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
        Assert.Contains("## S", template.Body);
    }

    private sealed class FrontmatterOnlyReader : StringReader
    {
        private int _delimiterCount;

        public FrontmatterOnlyReader(string value) : base(value)
        {
        }

        public bool BodyWasRead { get; private set; }
        public bool MarkerWasRead { get; private set; }

        public override string? ReadLine()
        {
            var line = base.ReadLine();
            if (line == "---")
                _delimiterCount++;
            else if (_delimiterCount >= 2 && line is not null)
                BodyWasRead = true;
            if (line == "body-marker")
                MarkerWasRead = true;
            return line;
        }
    }
}
