using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Mohist.Server.ArchTests;

public class ArchitectureRules
{
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

    private static readonly Regex TestProjectName = new(
        @"^Mohist\.(Server|Cli)\.(SpecTests|UnitTests|ArchTests|TestSupport)$",
        RegexOptions.CultureInvariant);

    private static readonly ArchUnitNET.Domain.Architecture _architecture = new ArchLoader()
        .LoadAssemblies(
            System.Reflection.Assembly.Load("Mohist.Server"),
            System.Reflection.Assembly.Load("Mohist.Cli"))
        .Build();

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
    public void Domain_ShouldNotDependOnApi()
    {
        Types().That().Are(DomainLayer)
            .Should().NotDependOnAny(ApiLayer)
            .Because("Domain layer must not depend on API layer")
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

    [Fact]
    public void TestProjects_MustNotReferenceOtherTestProjects()
    {
        var testProjects = TestProjectPaths().ToArray();
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
    public void TestSupport_MustNotReferenceTestFrameworkPackages()
    {
        var violations = TestSupportProjectPaths()
            .SelectMany(projectPath => XDocument.Load(projectPath)
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include) && IsTestFrameworkPackage(include))
                .Select(include => $"{Path.GetFileNameWithoutExtension(projectPath)}: {include}"))
            .OrderBy(violation => violation, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Test support must not reference test framework packages. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void TestSupport_MustNotContainTestSyntax()
    {
        var violations = new List<string>();

        foreach (var supportRoot in TestSupportRoots())
        {
            foreach (var sourcePath in ActiveTestFiles(supportRoot, "*.cs"))
            {
                var relativePath = Path.GetRelativePath(supportRoot, sourcePath);
                var match = TestSupportForbiddenSyntax.Match(File.ReadAllText(sourcePath));
                if (match.Success)
                    violations.Add($"{Path.GetFileName(supportRoot)}: {relativePath}: {match.Value}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Test support must not contain xUnit test syntax or test fixtures. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void TestProjects_UseTrackNames()
    {
        var violations = AllTestProjectPaths()
            .Select(path => Path.GetFileNameWithoutExtension(path) ?? path)
            .Where(name => !TestProjectName.IsMatch(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Test projects must use a SpecTests, UnitTests, ArchTests, or TestSupport name. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void TestSources_MustNotUseTraits()
    {
        var violations = TestRoots()
            .SelectMany(root => ActiveTestFiles(root.Path, "*.cs")
                .Where(path => TraitAttribute.IsMatch(File.ReadAllText(path)))
                .Select(path => $"{root.Name}: {Path.GetRelativePath(root.Path, path)}"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Test sources must not use xUnit traits. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void TestSources_MustNotContainCustomExecutionOrdering()
    {
        var collectionOrderer = "Collection" + "Orderer";
        var violations = TestRoots()
            .SelectMany(root => ActiveTestFiles(root.Path, "*.cs")
                .Where(path => File.ReadAllText(path).Contains(collectionOrderer, StringComparison.Ordinal))
                .Select(path => $"{root.Name}: {Path.GetRelativePath(root.Path, path)}"))
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
                    && projectDirectory is "Mohist.Server.UnitTests" or "Mohist.Server.SpecTests";
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
                     "Mohist.Server.SpecTests",
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

    private static IEnumerable<string> TestProjectPaths()
    {
        return AllTestProjectPaths()
            .Where(path => Path.GetFileNameWithoutExtension(path).EndsWith("Tests", StringComparison.Ordinal));
    }

    private static IEnumerable<string> TestSupportProjectPaths()
    {
        return AllTestProjectPaths()
            .Where(path => Path.GetFileNameWithoutExtension(path).EndsWith("TestSupport", StringComparison.Ordinal));
    }

    private static IEnumerable<string> TestSupportRoots()
    {
        return TestSupportProjectPaths()
            .Select(path => Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException($"Test support project has no parent directory: {path}"));
    }

    private static IEnumerable<string> AllTestProjectPaths()
    {
        return TestProjectRoots()
            .SelectMany(root => Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories));
    }

    private static IEnumerable<string> TestProjectRoots()
    {
        yield return RepositoryPaths.RequireDirectory("packages", "server", "tests");
        yield return RepositoryPaths.RequireDirectory("packages", "cli", "tests");
    }

    private static IEnumerable<(string Name, string Path)> TestRoots()
    {
        yield return ("server", RepositoryPaths.RequireDirectory("packages", "server", "tests"));
        yield return ("cli", RepositoryPaths.RequireDirectory("packages", "cli", "tests"));
    }
}
