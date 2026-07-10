using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Mohist.Server.Infrastructure.Data.Db;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using static ArchUnitNET.Fluent.Slices.SliceRuleDefinition;

namespace Mohist.Server.ArchTests;

public class ArchitectureRules
{
    private const string RetiredSpecTestsNamespace = "Mohist.Server." + "SpecTests";

    private static readonly Regex TestSupportForbiddenSyntax = new(
        @"\[\s*(?:(?:global::)?Xunit\.)?(?:Fact|Theory|Trait|Collection|CollectionDefinition)(?:Attribute)?\b|\b(?:IAsyncLifetime|IClassFixture|ICollectionFixture)\b|\bWebApplicationFactory\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex TraitAttribute = new(
        @"\[\s*(?:(?:global::)?Xunit\.)?Trait(?:Attribute)?\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex DisabledParallelCollectionDefinition = new(
        @"\[\s*(?:(?:global::)?Xunit\.)?CollectionDefinition(?:Attribute)?\s*\(\s*""(?<name>[^""]+)""\s*,\s*DisableParallelization\s*=\s*true\s*\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex OtelTracingCollectionUse = new(
        @"\[\s*(?:(?:global::)?Xunit\.)?Collection(?:Attribute)?\s*\(\s*""OtelTracing""\s*\)",
        RegexOptions.CultureInvariant);

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
        Types().That().Are(DataLayer)
            .Should().NotDependOnAny(ApplicationLayer)
            .Because("Infrastructure.Data is the persistence boundary and must not depend on application services, grains, or queriers")
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
        var sourceRoot = RepositoryPaths.RequireDirectory(
            "packages", "server", "src", "Mohist.Server");

        var sourceFiles = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToArray();

        var featureRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            "Agent",
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
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .Select(path => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
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
    // AgentOps is not yet listed — it has no code landing point today (its classes
    // still live under Sessions). Add it once issue #372 relocates them to
    // Mohist.Server.AgentOps.* and defines its allowed-direction set.
    private static readonly string[] DomainNamespaces =
        ["Agent", "Issue", "Workflow", "Project", "Runner", "Sessions"];

    private static readonly (string from, string to)[] AllowedDomainDependencies =
    [
        ("Agent", "Runner"),
        ("Agent", "Sessions"),
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
        for (int i = 0; i < DomainNamespaces.Length; i++)
        {
            for (int j = i + 1; j < DomainNamespaces.Length; j++)
            {
                var a = DomainNamespaces[i];
                var b = DomainNamespaces[j];

                if (AllowedDomainDependencies.Contains((a, b)) ||
                    AllowedDomainDependencies.Contains((b, a)))
                    continue;

                var aTypes = Types()
                    .That().ResideInNamespace($"Mohist.Server.{a}", useRegularExpressions: true)
                    .And().DoNotResideInNamespace("OrleansCodeGen", true)
                    .As($"{a}");

                var bTypes = Types()
                    .That().ResideInNamespace($"Mohist.Server.{b}", useRegularExpressions: true)
                    .And().DoNotResideInNamespace("OrleansCodeGen", true)
                    .As($"{b}");

                Types().That().Are(aTypes)
                    .Should().NotDependOnAny(bTypes)
                    .Check(_architecture);
            }
        }
    }

    [Fact]
    public void DomainInternalLayers_ShouldBeFreeOfCycles()
    {
        // Agent joined this list in issue-391 T-001: AgentGrain already
        // depended on AgentQuerier (Services) for its ToInfo projection,
        // and the shared IAgentLauncher (Services) introduced in T-001
        // legitimately depends on IAgentJobGrain / AgentJobInput (Grains)
        // to submit an AgentJob. The cycle is directional and accepted —
        // it does not block the manual launch + subscription dispatch
        // extraction. Tighten this to a Services → Grains-only flow if
        // Agent ever moves its projection path out of AgentGrain.
        var domainsWithKnownCycles = new HashSet<string> { "Issue", "Workflow", "Sessions", "Runner", "Agent" };

        foreach (var domain in DomainNamespaces)
        {
            if (domainsWithKnownCycles.Contains(domain))
                continue;

            Slices().Matching($"Mohist.Server.{domain}.(*)")
                .Should().BeFreeOfCycles()
                .Check(_architecture);
        }
    }

    [Fact(Skip = "Tech debt: Issue has internal cycles (Grains↔Services)")]
    public void IssueInternalLayers_ShouldBeFreeOfCycles()
    {
        Slices().Matching("Mohist.Server.Issue.(*)")
            .Should().BeFreeOfCycles()
            .Check(_architecture);
    }

    [Fact(Skip = "Tech debt: Workflow has internal cycles (Grains↔Services)")]
    public void WorkflowInternalLayers_ShouldBeFreeOfCycles()
    {
        Slices().Matching("Mohist.Server.Workflow.(*)")
            .Should().BeFreeOfCycles()
            .Check(_architecture);
    }

    [Fact(Skip = "Tech debt: Runner has internal cycles (Grains↔Services)")]
    public void RunnerInternalLayers_ShouldBeFreeOfCycles()
    {
        Slices().Matching("Mohist.Server.Runner.(*)")
            .Should().BeFreeOfCycles()
            .Check(_architecture);
    }

    /// <summary>
    /// Enforces the convention that all environment variable access goes through
    /// <c>System.IEnvironmentVariableProvider</c> (from the <c>EnvironmentAbstractions</c> NuGet package).
    /// </summary>
    /// <remarks>
    /// ArchUnitNET's call graph only tracks instance method dispatches and does not
    /// detect static method invocations on <c>System.Environment</c>. The primary
    /// enforcement is at compile time via the <c>EnvironmentAbstractions.BannedApiAnalyzer</c>
    /// Roslyn analyzer, which blocks any direct call to
    /// <c>System.Environment.GetEnvironmentVariable</c> /
    /// <c>System.Environment.SetEnvironmentVariable</c>. This archtest is a backstop:
    /// it verifies every production csproj references the analyzer so the compile-time
    /// enforcement actually runs.
    /// </remarks>
    [Fact]
    public void ProductionCode_ShouldNotCallSystemEnvironmentDirectly()
    {
        var productionProjects = new[]
        {
            ("Mohist.Server", RepositoryPaths.RequireFile("packages", "server", "src", "Mohist.Server", "Mohist.Server.csproj")),
            ("Mohist.Cli", RepositoryPaths.RequireFile("packages", "cli", "Mohist.Cli", "Mohist.Cli.csproj")),
        };

        var missing = new List<string>();
        foreach (var (name, csprojPath) in productionProjects)
        {
            var csproj = File.ReadAllText(csprojPath);
            if (!csproj.Contains("EnvironmentAbstractions.BannedApiAnalyzer", StringComparison.Ordinal))
            {
                missing.Add(name);
            }
        }

        Assert.True(
            missing.Count == 0,
            "These production csprojs must reference EnvironmentAbstractions.BannedApiAnalyzer to " +
            "enforce IEnvironmentVariableProvider usage at compile time: " + string.Join(", ", missing));
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
        var classRegex = new Regex(
            @"^\s*(?<access>public|internal|private|protected)?\s*(?:static\s+|sealed\s+|abstract\s+|partial\s+)*class\s+(?<name>\w+Specs)\b",
            RegexOptions.Multiline);

        var violations = new List<string>();
        foreach (var (specsRoot, _) in TestSpecRoots())
        {
            foreach (var path in Directory.EnumerateFiles(specsRoot, "*.cs", SearchOption.AllDirectories))
            {
                var src = File.ReadAllText(path);
                foreach (Match match in classRegex.Matches(src))
                {
                    if (match.Groups["access"].Value != "public")
                    {
                        violations.Add(
                            $"{Path.GetRelativePath(specsRoot, path)}: {match.Groups["name"].Value}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Spec classes must be public. Violations: " + string.Join(", ", violations));
    }

    /// <summary>
    /// Spec files in each test cave must use that cave's Specs namespace.
    /// </summary>
    [Fact]
    public void SpecNamespaces_MustBeUnderSpecs()
    {
        var namespaceRegex = new Regex(
            @"^\s*namespace\s+(?<name>[\w\.]+)\s*(?:;|\{)",
            RegexOptions.Multiline);

        var violations = new List<string>();
        foreach (var (specsRoot, namespacePrefix) in TestSpecRoots())
        {
            foreach (var path in Directory.EnumerateFiles(specsRoot, "*.cs", SearchOption.AllDirectories))
            {
                var src = File.ReadAllText(path);
                var match = namespaceRegex.Match(src);
                var relativePath = Path.GetRelativePath(specsRoot, path);
                if (!match.Success)
                {
                    violations.Add($"{relativePath}: no namespace");
                }
                else if (!match.Groups["name"].Value.StartsWith(namespacePrefix, StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}: {match.Groups["name"].Value}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Spec namespaces must match their test cave. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void TestProjects_MustNotReferenceOtherTestProjects()
    {
        var testProjects = new[]
        {
            RepositoryPaths.RequireFile("packages", "server", "tests", "Mohist.Server.UnitTests", "Mohist.Server.UnitTests.csproj"),
            RepositoryPaths.RequireFile("packages", "server", "tests", "Mohist.Server.ComponentSpecs", "Mohist.Server.ComponentSpecs.csproj"),
            RepositoryPaths.RequireFile("packages", "server", "tests", "Mohist.Server.IntegrationSpecs", "Mohist.Server.IntegrationSpecs.csproj"),
            RepositoryPaths.RequireFile("packages", "server", "tests", "Mohist.Server.ArchTests", "Mohist.Server.ArchTests.csproj"),
        };
        var testProjectPaths = new HashSet<string>(testProjects, StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var projectPath in testProjects)
        {
            var projectDirectory = Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException($"Project has no parent directory: {projectPath}");
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var document = XDocument.Load(projectPath);

            foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                    continue;

                var normalizedInclude = include
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                var referencedPath = Path.GetFullPath(Path.Combine(projectDirectory, normalizedInclude));
                if (testProjectPaths.Contains(referencedPath))
                    violations.Add($"{projectName} -> {Path.GetFileNameWithoutExtension(referencedPath)}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Test projects must not reference another test project. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void UnitTests_MustNotReferenceHostingPackages()
    {
        AssertProjectDoesNotReferencePackages(
            "Mohist.Server.UnitTests",
            "Microsoft.AspNetCore.Mvc.Testing",
            "Microsoft.Orleans.TestingHost");
    }

    [Fact]
    public void ComponentSpecs_MustNotReferenceMvcTesting()
    {
        AssertProjectDoesNotReferencePackages(
            "Mohist.Server.ComponentSpecs",
            "Microsoft.AspNetCore.Mvc.Testing");
    }

    [Fact]
    public void TestSupport_MustNotReferenceTestFrameworkPackages()
    {
        var projectPath = RepositoryPaths.RequireFile(
            "packages", "server", "tests", "Mohist.Server.TestSupport", "Mohist.Server.TestSupport.csproj");
        var document = XDocument.Load(projectPath);

        var violations = document.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include) && IsTestFrameworkPackage(include))
            .OrderBy(include => include, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Test support must not reference test framework packages. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void TestSupport_MustNotContainTestSyntax()
    {
        var supportRoot = RepositoryPaths.RequireDirectory(
            "packages", "server", "tests", "Mohist.Server.TestSupport");
        var violations = new List<string>();

        foreach (var sourcePath in ActiveTestFiles(supportRoot, "*.cs"))
        {
            var relativePath = Path.GetRelativePath(supportRoot, sourcePath);
            var match = TestSupportForbiddenSyntax.Match(File.ReadAllText(sourcePath));
            if (match.Success)
                violations.Add($"{relativePath}: {match.Value}");
        }

        Assert.True(
            violations.Count == 0,
            "Test support must not contain xUnit test syntax or test fixtures. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void TestSources_MustNotReferenceRetiredSpecTestsNamespace()
    {
        var testsRoot = RepositoryPaths.RequireDirectory("packages", "server", "tests");
        var violations = ActiveTestFiles(testsRoot, "*.cs")
            .Concat(ActiveTestFiles(testsRoot, "*.csproj"))
            .Where(path => File.ReadAllText(path).Contains(RetiredSpecTestsNamespace, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(testsRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"Active test sources must not reference {RetiredSpecTestsNamespace}. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void TestSources_MustNotUseTraits()
    {
        var testsRoot = RepositoryPaths.RequireDirectory("packages", "server", "tests");
        var violations = ActiveTestFiles(testsRoot, "*.cs")
            .Where(path => TraitAttribute.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(testsRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Test sources must not use xUnit traits. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void TestSources_MustNotContainCustomExecutionOrdering()
    {
        var testsRoot = RepositoryPaths.RequireDirectory("packages", "server", "tests");
        var collectionOrderer = "Collection" + "Orderer";
        var violations = ActiveTestFiles(testsRoot, "*.cs")
            .Where(path => File.ReadAllText(path).Contains(collectionOrderer, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(testsRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Test sources must not contain custom execution ordering. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void DisabledParallelCollections_MustOnlyProtectKnownProcessGlobals()
    {
        var testsRoot = RepositoryPaths.RequireDirectory("packages", "server", "tests");
        var violations = new List<string>();

        foreach (var sourcePath in ActiveTestFiles(testsRoot, "*.cs"))
        {
            var relativePath = Path.GetRelativePath(testsRoot, sourcePath);
            var projectDirectory = relativePath
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

            foreach (Match match in DisabledParallelCollectionDefinition.Matches(File.ReadAllText(sourcePath)))
            {
                var name = match.Groups["name"].Value;
                var isAllowed = name == "OtelTracing"
                    && projectDirectory is "Mohist.Server.UnitTests" or "Mohist.Server.ComponentSpecs" or "Mohist.Server.IntegrationSpecs";
                isAllowed |= name == "ConsoleOutput" && projectDirectory == "Mohist.Server.UnitTests";

                if (!isAllowed)
                    violations.Add($"{relativePath}: {name}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Disabled parallel collections must protect only known process globals. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void OtelTracing_UsesAssemblyLocalDisabledParallelDefinitions()
    {
        foreach (var projectName in new[]
                 {
                     "Mohist.Server.UnitTests",
                     "Mohist.Server.ComponentSpecs",
                     "Mohist.Server.IntegrationSpecs",
                 })
        {
            var projectRoot = RepositoryPaths.RequireDirectory("packages", "server", "tests", projectName);
            var sourceFiles = ActiveTestFiles(projectRoot, "*.cs").ToArray();
            if (!sourceFiles.Any(path => OtelTracingCollectionUse.IsMatch(File.ReadAllText(path))))
                continue;

            var hasDisabledDefinition = sourceFiles
                .SelectMany(path => DisabledParallelCollectionDefinition.Matches(File.ReadAllText(path)).Cast<Match>())
                .Any(match => match.Groups["name"].Value == "OtelTracing");

            Assert.True(
                hasDisabledDefinition,
                $"{projectName} uses OtelTracing but does not define it as a disabled-parallel collection.");
        }
    }

    private static bool IsTestFrameworkPackage(string packageId)
    {
        return packageId.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) ||
               packageId.Equals("xunit", StringComparison.OrdinalIgnoreCase) ||
               packageId.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase) ||
               packageId.EndsWith(".xunit", StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertProjectDoesNotReferencePackages(string projectName, params string[] forbiddenPackages)
    {
        var projectPath = RepositoryPaths.RequireFile(
            "packages", "server", "tests", projectName, $"{projectName}.csproj");
        var document = XDocument.Load(projectPath);
        var violations = document.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => include is not null && forbiddenPackages.Contains(include, StringComparer.OrdinalIgnoreCase))
            .OrderBy(include => include, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{projectName} must not reference hosting packages: " + string.Join(", ", violations));
    }

    private static IEnumerable<string> ActiveTestFiles(string root, string searchPattern)
    {
        return Directory.EnumerateFiles(root, searchPattern, SearchOption.AllDirectories)
            .Where(path =>
            {
                var relativePath = Path.GetRelativePath(root, path);
                return !relativePath
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is "bin" or "obj");
            });
    }

    private static IEnumerable<(string SpecsRoot, string NamespacePrefix)> TestSpecRoots()
    {
        yield return (
            RepositoryPaths.RequireDirectory("packages", "server", "tests", "Mohist.Server.ComponentSpecs", "Specs"),
            "Mohist.Server.ComponentSpecs.Specs");
        yield return (
            RepositoryPaths.RequireDirectory("packages", "server", "tests", "Mohist.Server.IntegrationSpecs", "Specs"),
            "Mohist.Server.IntegrationSpecs.Specs");
    }
}
