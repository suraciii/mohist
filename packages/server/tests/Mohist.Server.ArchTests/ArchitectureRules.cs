using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Mohist.Server.Infrastructure.Data.Db;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using static ArchUnitNET.Fluent.Slices.SliceRuleDefinition;

namespace Mohist.Server.ArchTests;

[Trait(Traits.Sut.Name, Traits.Sut.Architecture)]
public class ArchitectureRules
{
    private static readonly ArchUnitNET.Domain.Architecture _architecture = new ArchLoader()
        .LoadAssemblies(
            System.Reflection.Assembly.Load("Mohist.Server"),
            System.Reflection.Assembly.Load("Mohist.Cli"))
        .Build();

    private static readonly IObjectProvider<IType> OrleansGeneratedTypes = Types()
        .That().ResideInNamespace("OrleansCodeGen", true)
        .As("Orleans Generated Types");

    // Layer definitions
    private static readonly IObjectProvider<IType> DomainLayer = Types()
        .That().ResideInNamespace("Mohist.Server.*.Domain", useRegularExpressions: true)
        .As("Domain Layer");

    private static readonly IObjectProvider<IType> ApiLayer = Types()
        .That().ResideInNamespace("Mohist.Server.Api")
        .As("API Layer");

    private static readonly IObjectProvider<IType> GrainLayer = Types()
        .That().ResideInNamespace("Mohist.Server.*.Grains", useRegularExpressions: true)
        .As("Grain Layer");

    private static readonly IObjectProvider<IType> ApplicationLayer = Types()
        .That().ResideInNamespace("Mohist.Server.*.(Grains|Services)", useRegularExpressions: true)
        .And().DoNotResideInNamespace("Mohist.Server.Infrastructure", useRegularExpressions: true)
        .As("Application Layer");

    private static readonly IObjectProvider<IType> GrainInterfaces = Interfaces()
        .That().ResideInNamespace("Mohist.Server.*.Grains", useRegularExpressions: true)
        .As("Grain Interfaces");

    private static readonly IObjectProvider<IType> DataLayer = Types()
        .That().ResideInNamespace("Mohist.Server.Infrastructure.Data", useRegularExpressions: true)
        .As("Data Layer");

    private static readonly IObjectProvider<IType> QuerierLayer = Types()
        .That().ResideInNamespace("Mohist.Server.*.Services", useRegularExpressions: true)
        .And().HaveNameEndingWith("Querier")
        .As("Querier Layer");

    private static readonly IObjectProvider<IType> OrleansTypes = Types()
        .That().ResideInNamespace("Orleans")
        .As("Orleans Types");

    // Rules

    [Fact]
    public void Domain_ShouldNotDependOnOrleans()
    {
        Types().That().Are(DomainLayer)
            .Should().NotDependOnAny(OrleansTypes)
            .Because("Domain layer must be independent of Orleans infrastructure")
            .Check(_architecture);
    }

    [Fact]
    public void Domain_ShouldNotDependOnStorage()
    {
        Types().That().Are(DomainLayer)
            .Should().NotDependOnAny(DataLayer)
            .Because("Domain layer must not depend on database implementation")
            .Check(_architecture);
    }

    [Fact]
    public void Api_ShouldNotDependOnStorage()
    {
        Types().That().Are(ApiLayer)
            .Should().NotDependOnAny(DataLayer)
            .Because("API layer should use grain for writes and query services for reads")
            .Check(_architecture);
    }

    [Fact]
    public void Queriers_ShouldNotDependOnGrainInterfaces()
    {
        Types().That().Are(QuerierLayer)
            .Should().NotDependOnAny(GrainInterfaces)
            .Because("queriers should read from EF directly, not through grain interfaces")
            .Check(_architecture);
    }

    [Fact]
    public void InfrastructureData_ShouldNotDependOnApplicationLayer()
    {
        // Cross-cutting event infrastructure (Events.Grains) is exempt:
        // the IEventDispatcherGrain poke is fired from the three event
        // producers (WorkflowRunStore, IssueStore, AgentSessionStore)
        // after commit, so Infrastructure.Data necessarily references the
        // grain interface. Events.Grains is a horizontal concern (event
        // delivery), not a feature-slice application service — the
        // dependency is unidirectional (poke only, no callback into the
        // stores) and is documented in openspec/changes/issue-362/design.md
        // (D5) as the intended wiring.
        var applicationLayerExcludingEventsGrains = Types()
            .That().Are(ApplicationLayer)
            .And().DoNotResideInNamespace("Mohist.Server.Events.Grains", useRegularExpressions: false)
            .As("Application Layer excluding Events.Grains");

        Types().That().Are(DataLayer)
            .Should().NotDependOnAny(applicationLayerExcludingEventsGrains)
            .Because("Infrastructure.Data is the persistence boundary and must not depend on application services, grains, or queriers; Events.Grains is the documented cross-cutting event delivery exception.")
            .Check(_architecture);
    }

    [Fact]
    public void DataStores_AreInInfrastructureData()
    {
        Classes().That().HaveNameEndingWith("Store")
            .And().ResideInNamespace("Mohist.Server.Infrastructure.Data", useRegularExpressions: true)
            .Should().Exist()
            .Because("database-backed stores should be in Infrastructure.Data namespace")
            .Check(_architecture);
    }

    [Fact]
    public void RowModels_AreInInfrastructureData()
    {
        Classes().That().ResideInNamespace("Mohist.Server", useRegularExpressions: true)
            .And().HaveNameEndingWith("Row")
            .Should().ResideInNamespace("Mohist.Server.Infrastructure.Data(\\..*)?", useRegularExpressions: true)
            .Because("EF row models are persistence data models and belong under Infrastructure.Data")
            .Check(_architecture);
    }

    [Fact]
    public void DbContexts_AreInInfrastructureData()
    {
        Classes().That().ResideInNamespace("Mohist.Server", useRegularExpressions: true)
            .And().AreAssignableTo(typeof(Microsoft.EntityFrameworkCore.DbContext))
            .Should().ResideInNamespace("Mohist.Server.Infrastructure.Data(\\..*)?", useRegularExpressions: true)
            .Because("database contexts are infrastructure data concerns")
            .Check(_architecture);
    }

    [Fact]
    public void Migrations_AreInInfrastructureData()
    {
        Classes().That().ResideInNamespace("Mohist.Server", useRegularExpressions: true)
            .And().AreAssignableTo(typeof(Microsoft.EntityFrameworkCore.Migrations.Migration))
            .Should().ResideInNamespace("Mohist.Server.Infrastructure.Data.Migrations", useRegularExpressions: true)
            .Because("EF migrations should live with database schema artifacts under Infrastructure.Data")
            .Check(_architecture);
    }

    [Fact]
    public void ModelSnapshots_AreInInfrastructureData()
    {
        Classes().That().ResideInNamespace("Mohist.Server", useRegularExpressions: true)
            .And().HaveNameEndingWith("ModelSnapshot")
            .Should().ResideInNamespace("Mohist.Server.Infrastructure.Data.Migrations", useRegularExpressions: true)
            .Because("EF model snapshots should live with database schema artifacts under Infrastructure.Data")
            .Check(_architecture);
    }

    [Fact]
    public void FeatureDirectories_ShouldOnlyContainDomainGrainsAndServices()
    {
        var sourceFiles = EmbeddedSources("ServerSources/");

        var featureRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            "Agent",
            "AgentOps",
            "Epic",
            "Issue",
            "Project",
            "Runner",
            "Sessions",
            "Workflow"
        };

        var allowedFeatureSegments = new HashSet<string>(StringComparer.Ordinal)
        {
            "Domain",
            "Grains",
            "Services"
        };

        var allowedFeatureRootFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "AgentSessionReadModels.cs"
        };

        var violations = sourceFiles
            .Select(source => source.Path.Split('/'))
            .Where(parts => parts.Length >= 2 && featureRoots.Contains(parts[0]))
            .Where(parts => !(allowedFeatureSegments.Contains(parts[1])
                || (parts.Length == 2 && allowedFeatureRootFiles.Contains(parts[1]))))
            .Select(parts => string.Join("/", parts))
            .OrderBy(path => path)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Feature directories must only contain Domain, Grains, and Services. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void ServerSource_ShouldUseSharedJsonSerializerOptions()
    {
        var localConstructorOffenders = new List<string>();
        var localFieldOffenders = new List<string>();
        var fieldPattern = new System.Text.RegularExpressions.Regex(
            @"static\s+readonly\s+JsonSerializerOptions\s+\w+\s*=",
            System.Text.RegularExpressions.RegexOptions.None);

        foreach (var source in EmbeddedSources("ServerSources/")
                     .Where(source => !source.Path.Equals(
                         "Infrastructure/JSON.cs",
                         StringComparison.Ordinal)))
        {
            var lineNumber = 1;
            foreach (var line in source.Content.Split('\n'))
            {
                if (line.Contains("new JsonSerializerOptions(", StringComparison.Ordinal))
                    localConstructorOffenders.Add($"{source.Path}:{lineNumber}");

                if (fieldPattern.IsMatch(line)
                    && !line.Contains("JSON.Options", StringComparison.Ordinal)
                    && !line.Contains("JSON.Indented", StringComparison.Ordinal))
                {
                    localFieldOffenders.Add($"{source.Path}:{lineNumber}");
                }

                lineNumber++;
            }
        }

        Assert.True(
            localConstructorOffenders.Count == 0,
            "Found local JsonSerializerOptions construction outside the JSON facade: "
            + string.Join(", ", localConstructorOffenders));
        Assert.True(
            localFieldOffenders.Count == 0,
            "Found local static JsonSerializerOptions fields outside the JSON facade: "
            + string.Join(", ", localFieldOffenders));
    }

    [Fact]
    public void ProductionProjects_ShouldReferenceEnvironmentAnalyzer()
    {
        var missing = EmbeddedSources("ProductionProjects/")
            .Where(project => !project.Content.Contains(
                "EnvironmentAbstractions.BannedApiAnalyzer",
                StringComparison.Ordinal))
            .Select(project => project.Path)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Production projects must reference EnvironmentAbstractions.BannedApiAnalyzer: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void GrainImplementations_ShouldInheritFromGrain()
    {
        Classes().That().HaveNameEndingWith("Grain")
            .And().DoNotHaveNameStartingWith("I")
            .And().AreNot(OrleansGeneratedTypes)
            .And().DoNotResideInNamespace("OrleansCodeGen", true)
            .Should().BeAssignableTo(typeof(Orleans.Grain))
            .Because("Grain implementations must inherit from Orleans.Grain")
            .Check(_architecture);
    }

    [Fact]
    public void GrainInterfaces_ShouldStartWithI()
    {
        Interfaces().That().HaveNameEndingWith("Grain")
            .Should().HaveNameStartingWith("I")
            .Because("Grain interfaces should follow naming convention")
            .Check(_architecture);
    }

    [Fact]
    public void Domain_ShouldNotDependOnApi()
    {
        Types().That().Are(DomainLayer)
            .Should().NotDependOnAny(ApiLayer)
            .Because("Domain layer must not depend on API layer")
            .Check(_architecture);
    }

    [Fact]
    public void GrainInterfaces_ShouldBeInGrainsNamespace()
    {
        Interfaces().That().HaveNameEndingWith("Grain")
            .Should().ResideInNamespace("Mohist.Server.*.Grains", useRegularExpressions: true)
            .Because("Grain interfaces must be in Grains namespace")
            .Check(_architecture);
    }

    [Fact]
    public void Api_ShouldNotDependOnOrleans()
    {
        Types().That().Are(ApiLayer)
            .Should().NotDependOnAny(OrleansTypes)
            .Because("API layer should not depend on Orleans directly")
            .Check(_architecture);
    }

    [Fact]
    public void EfEntities_ShouldEndWithRow()
    {
        var dbSetProperties = typeof(Mohist.Server.Infrastructure.Data.Db.MohistDbContext)
            .GetProperties()
            .Where(p => p.PropertyType.IsGenericType &&
                        p.PropertyType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .ToList();

        Assert.All(dbSetProperties, entityType =>
        {
            Assert.True(
                entityType.Name.EndsWith("Row") || entityType.Name.EndsWith("Profile"),
                $"EF entity '{entityType.Name}' must end with 'Row' or 'Profile'. " +
                $"Entity type: {entityType.FullName}");
        });
    }

    // Domain namespaces participating in the cross-domain dependency check.
    // Epic is NOT listed as its own domain: per design/domain-analysis.md, Epic is
    // the "organization facet" of the Issue subdomain (same problem class, two
    // granularities), so Mohist.Server.Epic.* is measured as part of Issue, not as
    // a separate domain. Measuring it cross-domain would mis-flag subdomain-internal
    // coupling (Epic→IssueQuerier, IIssueGrain) as violations.
    private static readonly string[] DomainNamespaces =
        ["Agent", "AgentOps", "Issue", "Workflow", "Project", "Runner", "Sessions"];

    private static readonly (string from, string to)[] AllowedDomainDependencies =
    [
        ("Agent", "Runner"),
        ("Agent", "Sessions"),
        ("AgentOps", "Agent"),
        ("AgentOps", "Issue"),
        ("AgentOps", "Project"),
        ("AgentOps", "Runner"),
        ("AgentOps", "Sessions"),
        ("AgentOps", "Workflow"),
        ("Issue", "Workflow"),
        ("Issue", "Project"),
        ("Runner", "Sessions"),
        ("Runner", "Workflow"),
        ("Workflow", "Sessions"),
        // KNOWN DEBT — Project→Workflow is a config-data placement issue, not an
        // engine dependency: ProjectGrain/ProjectQuerier reference only the
        // ProjectWorkflowProfile config type (template selection + variables),
        // which design/workflow/boundaries/issue.md assigns to Issue/Project's own
        // config data but which currently lives under Workflow/Services. Long-term
        // fix: relocate the config type to Project. Tracked by that boundary doc's
        // "可选后续". Allowed here so the directional tightening (issue #368) is
        // not blocked on the relocation.
        ("Project", "Workflow"),
    ];

    [Fact]
    public void DomainModules_ShouldNotDependOnEachOther()
    {
        foreach (var from in DomainNamespaces)
        {
            foreach (var to in DomainNamespaces)
            {
                if (from == to || AllowedDomainDependencies.Contains((from, to))) continue;

                var fromTypes = Types()
                    .That().ResideInNamespace($@"Mohist\.Server\.{from}(\.|$)", useRegularExpressions: true)
                    .And().DoNotResideInNamespace("OrleansCodeGen", true)
                    .As($"{from}");

                var toTypes = Types()
                    .That().ResideInNamespace($@"Mohist\.Server\.{to}(\.|$)", useRegularExpressions: true)
                    .And().DoNotResideInNamespace("OrleansCodeGen", true)
                    .As($"{to}");

                Types().That().Are(fromTypes)
                    .Should().NotDependOnAny(toTypes)
                    .Check(_architecture);
            }
        }
    }


    /// <summary>
    /// Spec files in <c>Specs/</c> must end with <c>Specs</c> or
    /// <c>Collection</c> (or be <c>Index.md</c>). Prevents accidental
    /// mis-naming that breaks the "find the spec for SUT X" intuition.
    /// </summary>
    [Fact]
    public void SpecFiles_MustHaveSpecOrCollectionSuffix()
    {
        var violations = EmbeddedSources("SpecSources/")
            .Select(source => Path.GetFileNameWithoutExtension(source.Path)!)
            .Where(name => !name.EndsWith("Specs")
                        && !name.EndsWith("Collection")
                        && !name.EndsWith("Fixture")
                        && !name.EndsWith("Factory")
                        && !name.EndsWith("Hub")
                        && !name.EndsWith("Probe")
                        && !name.EndsWith("TestHost")
                        && !name.EndsWith("TestSupport")
                        && !name.Equals("Index", StringComparison.Ordinal))
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Spec files must end with 'Specs' or 'Collection'. Violations: " +
            string.Join(", ", violations));
    }

    /// <summary>
    /// Spec classes must be declared as <c>public</c> so xUnit can
    /// instantiate them. The rule parses each <c>*.cs</c> file for
    /// top-level class declarations named <c>*Specs</c> and verifies
    /// the <c>public</c> modifier is present.
    /// </summary>
    [Fact]
    public void SpecClasses_MustBePublic()
    {
        var classRegex = new System.Text.RegularExpressions.Regex(
            @"^\s*(?:(public|internal|private|protected)\s+)?(?:static\s+|sealed\s+|abstract\s+|partial\s+)*class\s+(\w+Specs)\b",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var violations = new List<string>();
        foreach (var source in EmbeddedSources("SpecSources/"))
        {
            foreach (System.Text.RegularExpressions.Match m in classRegex.Matches(source.Content))
            {
                var access = m.Groups[1].Success ? m.Groups[1].Value : "default";
                if (access != "public")
                {
                    violations.Add($"{source.Path}: {access} {m.Groups[2].Value}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Spec classes must be public. Violations: " + string.Join(", ", violations));
    }

    /// <summary>
    /// Spec files in <c>Specs/</c> must declare a namespace under
    /// <c>Mohist.Server.SpecTests.Specs</c>. Prevents accidentally placing
    /// test code outside the Specs sub-namespace, which would break
    /// test discovery and namespace-based filtering.
    /// </summary>
    [Fact]
    public void SpecNamespaces_MustBeUnderSpecs()
    {
        var namespaceRegex = new System.Text.RegularExpressions.Regex(
            @"^\s*namespace\s+([\w\.]+)\s*;",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var violations = new List<string>();
        foreach (var source in EmbeddedSources("SpecSources/"))
        {
            var m = namespaceRegex.Match(source.Content);
            if (!m.Success)
            {
                // No namespace declaration; skip (the existing test in
                // SkillsCliCollection.cs is such a file).
                continue;
            }
            var ns = m.Groups[1].Value;
            if (!ns.StartsWith("Mohist.Server.SpecTests.Specs", StringComparison.Ordinal))
            {
                violations.Add($"{source.Path}: {ns}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Spec namespaces must be under 'Mohist.Server.SpecTests.Specs'. Violations: " +
            string.Join(", ", violations));
    }

    private static IReadOnlyList<EmbeddedSource> EmbeddedSources(string prefix)
    {
        var assembly = typeof(ArchitectureRules).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return new EmbeddedSource(name[prefix.Length..], reader.ReadToEnd());
            })
            .ToArray();
    }

    private sealed record EmbeddedSource(string Path, string Content);
}
