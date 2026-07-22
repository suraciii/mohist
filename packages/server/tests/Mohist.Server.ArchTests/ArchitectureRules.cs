using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using Mohist.Server.Infrastructure.Data.Db;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using static ArchUnitNET.Fluent.Slices.SliceRuleDefinition;

namespace Mohist.Server.ArchTests;

public class ArchitectureRules
{
    private static readonly ArchUnitNET.Domain.Architecture _architecture = new ArchLoader()
        .LoadAssemblies(
            System.Reflection.Assembly.Load("Mohist.Server"),
            System.Reflection.Assembly.Load("Mohist.Cli"))
        .Build();

    private static readonly IObjectProvider<IType> OrleansGeneratedTypes = Types()
        .That().ResideInNamespaceMatching("OrleansCodeGen")
        .As("Orleans Generated Types");

    // Layer definitions
    private static readonly IObjectProvider<IType> DomainLayer = Types()
        .That().ResideInNamespaceMatching("Mohist.Server.*.Domain")
        .As("Domain Layer");

    private static readonly IObjectProvider<IType> ApiLayer = Types()
        .That().ResideInNamespace("Mohist.Server.Api")
        .As("API Layer");

    private static readonly IObjectProvider<IType> GrainLayer = Types()
        .That().ResideInNamespaceMatching("Mohist.Server.*.Grains")
        .As("Grain Layer");

    private static readonly IObjectProvider<IType> ApplicationLayer = Types()
        .That().ResideInNamespaceMatching("Mohist.Server.*.(Grains|Services)")
        .And().DoNotResideInNamespaceMatching("Mohist.Server.Infrastructure")
        .As("Application Layer");

    private static readonly IObjectProvider<IType> GrainInterfaces = Interfaces()
        .That().ResideInNamespaceMatching("Mohist.Server.*.Grains")
        .As("Grain Interfaces");

    private static readonly IObjectProvider<IType> DataLayer = Types()
        .That().ResideInNamespaceMatching("Mohist.Server.Infrastructure.Data")
        .As("Data Layer");

    private static readonly IObjectProvider<IType> QuerierLayer = Types()
        .That().ResideInNamespaceMatching("Mohist.Server.*.Services")
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
            .And().DoNotResideInNamespace("Mohist.Server.Events.Grains")
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
            .And().ResideInNamespaceMatching("Mohist.Server.Infrastructure.Data")
            .Should().Exist()
            .Because("database-backed stores should be in Infrastructure.Data namespace")
            .Check(_architecture);
    }

    [Fact]
    public void RowModels_AreInInfrastructureData()
    {
        Classes().That().ResideInNamespaceMatching("Mohist.Server")
            .And().HaveNameEndingWith("Row")
            .Should().ResideInNamespaceMatching("Mohist.Server.Infrastructure.Data(\\..*)?")
            .Because("EF row models are persistence data models and belong under Infrastructure.Data")
            .Check(_architecture);
    }

    [Fact]
    public void DbContexts_AreInInfrastructureData()
    {
        Classes().That().ResideInNamespaceMatching("Mohist.Server")
            .And().AreAssignableTo(typeof(Microsoft.EntityFrameworkCore.DbContext))
            .Should().ResideInNamespaceMatching("Mohist.Server.Infrastructure.Data(\\..*)?")
            .Because("database contexts are infrastructure data concerns")
            .Check(_architecture);
    }

    [Fact]
    public void Migrations_AreInInfrastructureData()
    {
        Classes().That().ResideInNamespaceMatching("Mohist.Server")
            .And().AreAssignableTo(typeof(Microsoft.EntityFrameworkCore.Migrations.Migration))
            .Should().ResideInNamespaceMatching("Mohist.Server.Infrastructure.Data.Migrations")
            .Because("EF migrations should live with database schema artifacts under Infrastructure.Data")
            .Check(_architecture);
    }

    [Fact]
    public void ModelSnapshots_AreInInfrastructureData()
    {
        Classes().That().ResideInNamespaceMatching("Mohist.Server")
            .And().HaveNameEndingWith("ModelSnapshot")
            .Should().ResideInNamespaceMatching("Mohist.Server.Infrastructure.Data.Migrations")
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

    // issue-432 T-001: Mohist.Workflow.Definition is the authoritative
    // validator library shared by the server save path, the offline CLI, and
    // CI. The CLI references it; if it pulls Orleans or ASP.NET in transitively,
    // the offline validator stops being offline. This arch test asserts the
    // project itself does not declare any such PackageReference.
    [Fact]
    public void MohistWorkflowDefinition_ShouldNotReferenceOrleansOrAspNet()
    {
        var project = EmbeddedSources("ProductionProjects/")
            .Single(source => source.Path.Equals(
                "Mohist.Workflow.Definition.csproj",
                StringComparison.Ordinal));

        var bannedSubstrings = new[]
        {
            "Microsoft.Orleans",
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
        };

        var violations = bannedSubstrings
            .Where(substring => project.Content.Contains(substring, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Mohist.Workflow.Definition must not reference any of: "
            + string.Join(", ", bannedSubstrings)
            + ". Found: "
            + string.Join(", ", violations));
    }

    [Fact]
    public void GrainImplementations_ShouldInheritFromGrain()
    {
        Classes().That().HaveNameEndingWith("Grain")
            .And().DoNotHaveNameStartingWith("I")
            .And().AreNot(OrleansGeneratedTypes)
            .And().DoNotResideInNamespaceMatching("OrleansCodeGen")
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
            .Should().ResideInNamespaceMatching("Mohist.Server.*.Grains")
            .Because("Grain interfaces must be in Grains namespace")
            .Check(_architecture);
    }

    // issue-417 T-005: the narrow binding-participant interfaces
    // (IIssueBindingParticipant, IProjectBindingParticipant) are the
    // contract the coordinator uses to invoke idempotent commands on
    // the Issue / Project grains. Only the coordinator may depend on
    // them; any other production grain / route / service that needs to
    // create an issue, reassign its repository, reopen it, or remove a
    // Project repository must go through IIssueRepositoryCoordinatorGrain
    // instead. Without this guard, a well-intentioned contributor
    // could re-introduce a direct IssueGrain.CreateAsync call and
    // silently break the orphan-binding invariant.
    [Fact]
    public void BindingParticipantInterfaces_OnlyConsumedByCoordinator()
    {
        var participantInterfaces = Interfaces()
            .That().HaveNameEndingWith("BindingParticipant")
            .Or().HaveNameEndingWith("BindingTarget")
            .And().ResideInNamespaceMatching("Mohist.Server.*.Grains.Coordinator")
            .As("IssueRepositoryBindingParticipantInterfaces");

        var coordinatorGrain = Classes()
            .That().HaveNameEndingWith("CoordinatorGrain")
            .And().ResideInNamespaceMatching("Mohist.Server.*.Grains.Coordinator")
            .As("IssueRepositoryCoordinatorGrain");

        Classes().That().AreNot(coordinatorGrain)
            .And().DoNotHaveNameEndingWith("BindingParticipantProxy")
            .And().DoNotHaveName("IssueGrain")
            .And().DoNotResideInNamespaceMatching("OrleansCodeGen")
            .Should().NotDependOnAny(participantInterfaces)
            .Because("only the IssueRepositoryCoordinatorGrain and its binding-participant proxies may depend on the narrow binding-participant interfaces; production routes / services / other grains must call the coordinator instead")
            .Check(_architecture);
    }

    [Fact]
    public void IssueGrain_DoesNotExposeDirectRepositoryBindingCommands()
    {
        var names = typeof(Mohist.Server.Issue.Grains.IIssueGrain)
            .GetMethods()
            .Select(method => method.Name);

        Assert.DoesNotContain("ReopenAsync", names);
        Assert.DoesNotContain("ReopenWithTargetCheckAsync", names);
        Assert.DoesNotContain("ChangeRepositoryAsync", names);
        Assert.DoesNotContain("RecordRepositoryCommandReceiptAsync", names);
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
                    .That().ResideInNamespaceMatching($@"Mohist\.Server\.{from}(\.|$)")
                    .And().DoNotResideInNamespaceMatching("OrleansCodeGen")
                    .As($"{from}");

                var toTypes = Types()
                    .That().ResideInNamespaceMatching($@"Mohist\.Server\.{to}(\.|$)")
                    .And().DoNotResideInNamespaceMatching("OrleansCodeGen")
                    .As($"{to}");

                Types().That().Are(fromTypes)
                    .Should().NotDependOnAny(toTypes)
                    .Check(_architecture);
            }
        }
    }


    [Fact]
    public void CSharpTestFiles_MustStayWithinSizeRatchet()
    {
        const int absoluteBudgetBytes = 24_000;
        var sources = EmbeddedSources("TestSources/");
        Assert.NotEmpty(sources);

        var requiredRoots = new[]
        {
            "Mohist.Server.SpecTests/",
            "Mohist.Server.UnitTests/",
            "Mohist.Server.ArchTests/",
        };
        foreach (var root in requiredRoots)
        {
            Assert.Contains(sources, source => source.Path.StartsWith(root, StringComparison.Ordinal));
        }

        var baseline = ReadTestFileSizeBaseline();
        var sourceByPath = sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var source in sources.Where(source => source.ByteLength > absoluteBudgetBytes))
        {
            if (!baseline.TryGetValue(source.Path, out var allowedBytes))
            {
                violations.Add($"{source.Path} is {source.ByteLength} bytes and has no baseline allowance");
                continue;
            }

            if (source.ByteLength > allowedBytes)
                violations.Add($"{source.Path} grew from its {allowedBytes}-byte allowance to {source.ByteLength} bytes");
        }

        foreach (var (path, allowedBytes) in baseline)
        {
            if (allowedBytes <= absoluteBudgetBytes)
            {
                violations.Add($"{path} has an invalid {allowedBytes}-byte baseline allowance");
                continue;
            }

            if (!sourceByPath.TryGetValue(path, out var source))
            {
                violations.Add($"{path} is in the baseline but its source file is missing");
                continue;
            }

            if (source.ByteLength <= absoluteBudgetBytes)
                violations.Add($"{path} is now {source.ByteLength} bytes and must be removed from the baseline");
        }

        Assert.True(
            violations.Count == 0,
            "C# test files must stay within the 24,000-byte ratchet. Violations: "
            + string.Join("; ", violations.OrderBy(violation => violation, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Spec files in <c>Specs/</c> must end with <c>Specs</c> or
    /// <c>Collection</c> (or be <c>Index.md</c>). Prevents accidental
    /// mis-naming that breaks the "find the spec for SUT X" intuition.
    /// </summary>
    [Fact]
    public void SpecFiles_MustHaveSpecOrCollectionSuffix()
    {
        var violations = EmbeddedSources("TestSources/Mohist.Server.SpecTests/Specs/")
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
        foreach (var source in EmbeddedSources("TestSources/Mohist.Server.SpecTests/Specs/"))
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
        foreach (var source in EmbeddedSources("TestSources/Mohist.Server.SpecTests/Specs/"))
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

    private static IReadOnlyDictionary<string, int> ReadTestFileSizeBaseline()
    {
        const string resourceName = "SpecFileSizeBaseline.json";
        var assembly = typeof(ArchitectureRules).Assembly;
        Assert.Contains(resourceName, assembly.GetManifestResourceNames());

        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        var baseline = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(stream);
        Assert.NotNull(baseline);
        Assert.NotEmpty(baseline);
        return baseline;
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
                var byteLength = checked((int)stream.Length);
                using var reader = new StreamReader(stream);
                return new EmbeddedSource(name[prefix.Length..], reader.ReadToEnd(), byteLength);
            })
            .ToArray();
    }

    private sealed record EmbeddedSource(string Path, string Content, int ByteLength);
}
