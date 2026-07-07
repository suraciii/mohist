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
        var sourceRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "Mohist.Server"));

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
        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src"));

        var productionProjects = new[]
        {
            ("Mohist.Server", Path.Combine(repoRoot, "Mohist.Server", "Mohist.Server.csproj")),
            ("Mohist.Cli",    Path.Combine(repoRoot, "..", "..", "cli", "Mohist.Cli", "Mohist.Cli.csproj")),
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

    private const int SpecFileSizeBudgetBytes = 24_000;

    /// <summary>
    /// Spec files in <c>Specs/</c> must end with <c>Specs</c> or
    /// <c>Collection</c> (or be <c>Index.md</c>). Prevents accidental
    /// mis-naming that breaks the "find the spec for SUT X" intuition.
    /// </summary>
    [Fact]
    public void SpecFiles_MustHaveSpecOrCollectionSuffix()
    {
        var specsRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "Specs"));
        if (!Directory.Exists(specsRoot))
            return; // Specs dir not present (fresh checkout?); rule is vacuous.

        var specFiles = Directory.EnumerateFiles(
            specsRoot, "*.cs", SearchOption.AllDirectories);

        var violations = specFiles
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .Where(name => !name.EndsWith("Specs")
                        && !name.EndsWith("Collection")
                        && !name.Equals("Index", StringComparison.Ordinal))
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Spec files must end with 'Specs' or 'Collection'. Violations: " +
            string.Join(", ", violations));
    }

    /// <summary>
    /// Spec files must stay under ~24 KB so contributors notice when a
    /// file has grown past a comfortable reading size. Larger files
    /// should be split by lifecycle phase, behavior scenario, or
    /// SUT-prefix grouping.
    /// </summary>
    [Fact]
    public void SpecFiles_MustStayBellowSizeBudget()
    {
        var specsRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "Specs"));
        if (!Directory.Exists(specsRoot))
            return;

        var tooBig = Directory.EnumerateFiles(
            specsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => new FileInfo(p).Length > SpecFileSizeBudgetBytes)
            .Select(p => Path.GetRelativePath(specsRoot, p))
            .OrderBy(p => p)
            .ToList();

        Assert.True(
            tooBig.Count == 0,
            $"Spec files must stay under {SpecFileSizeBudgetBytes / 1000} KB. " +
            "Too big: " + string.Join(", ", tooBig));
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
        var specsRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "Specs"));
        if (!Directory.Exists(specsRoot))
            return;

        var classRegex = new System.Text.RegularExpressions.Regex(
            @"^\s*(?:public\s+)?(internal|private|protected)?\s*(?:static\s+|sealed\s+|abstract\s+|partial\s+)*class\s+(\w+Specs)\b",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(specsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var src = File.ReadAllText(path);
            foreach (System.Text.RegularExpressions.Match m in classRegex.Matches(src))
            {
                var access = m.Groups[1].Value;
                if (!string.IsNullOrEmpty(access) && access != "public")
                {
                    violations.Add($"{Path.GetRelativePath(specsRoot, path)}: {access} {m.Groups[2].Value}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Spec classes must be public. Violations: " + string.Join(", ", violations));
    }

    /// <summary>
    /// Spec files in <c>Specs/</c> must declare a namespace under
    /// <c>Mohist.Server.Tests.Specs</c>. Prevents accidentally placing
    /// test code outside the Specs sub-namespace, which would break
    /// test discovery and namespace-based filtering.
    /// </summary>
    [Fact]
    public void SpecNamespaces_MustBeUnderSpecs()
    {
        var specsRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "Specs"));
        if (!Directory.Exists(specsRoot))
            return;

        var namespaceRegex = new System.Text.RegularExpressions.Regex(
            @"^\s*namespace\s+([\w\.]+)\s*;",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(specsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var src = File.ReadAllText(path);
            var m = namespaceRegex.Match(src);
            if (!m.Success)
            {
                // No namespace declaration; skip (the existing test in
                // SkillsCliCollection.cs is such a file).
                continue;
            }
            var ns = m.Groups[1].Value;
            if (!ns.StartsWith("Mohist.Server.Tests.Specs", StringComparison.Ordinal))
            {
                violations.Add($"{Path.GetRelativePath(specsRoot, path)}: {ns}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Spec namespaces must be under 'Mohist.Server.Tests.Specs'. Violations: " +
            string.Join(", ", violations));
    }
}
