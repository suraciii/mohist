namespace Mohist.Server.Tests.Support;

/// <summary>
/// <c>[Trait]</c> attribute names and allowed values used to categorize
/// every spec test in the suite. See
/// <c>openspec/changes/test-organization-refactor/specs/test-categorization</c>
/// for the rationale.
/// </summary>
public static class Traits
{
    public const string Name = "Trait";

    /// <summary>
    /// <c>Speed</c> measures how long a single test instance takes to run.
    /// Used by <c>dotnet test --filter "Speed=..."</c> to target subsets.
    /// </summary>
    public static class Speed
    {
        public const string Name = "Speed";

        /// <summary>No I/O, no fixture, no DB, no grain. &lt; 50 ms typical.</summary>
        public const string Unit = "Unit";

        /// <summary>Talks to Orleans grains + EF SQLite in-memory via <c>WorkflowGrainFixture</c> or <c>BacklogFixture</c>.</summary>
        public const string Grain = "Grain";

        /// <summary>Goes through <c>WebApplicationFactory&lt;Program&gt;</c> + HTTP client.</summary>
        public const string Integration = "Integration";

        /// <summary>Talks to EF + Orleans DI container but skips <c>WebApplicationFactory</c> (uses <c>MohistDbFixture</c>).</summary>
        public const string Service = "Service";
    }

    /// <summary>
    /// <c>Sut</c> names the Bounded Context the test exercises. Mirrors the
    /// production directory layout under <c>packages/server/src/Mohist.Server/</c>.
    /// A test can carry multiple Sut traits when it exercises more than one context.
    /// </summary>
    public static class Sut
    {
        public const string Name = "Sut";

        public const string Workflow = "Workflow";
        public const string Issue = "Issue";
        public const string Project = "Project";
        public const string Epic = "Epic";
        public const string Runner = "Runner";
        public const string AgentSession = "AgentSession";
        public const string Skills = "Skills";
        public const string System = "System";
        public const string Api = "Api";
        public const string Architecture = "Architecture";
        public const string Foundation = "Foundation";
    }
}
