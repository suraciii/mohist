using Mohist.Cli;
using Xunit;

namespace Mohist.Server.UnitTests.Project.Api;

public class NoActiveProjectMessageSourceAlignmentTests
{
    [Fact]
    public void MohistCliCommands_IsTheSingleSourceOfTruthForTheDiagnostic()
    {
        var source = File.ReadAllText(GetCliCommandsPath());
        Assert.Contains("internal const string NoActiveProjectMessage", source);
    }

    [Fact]
    public void MohistCliApi_ReferencesTheHelperForTheDiagnostic()
    {
        var source = File.ReadAllText(GetCliApiPath());
        var occurrences = CountOccurrences(source, "Run 'mo project use <name-or-id>' or pass --project <name-or-id>");
        Assert.Equal(0, occurrences);
        Assert.Contains("MohistCliCommands.NoActiveProjectMessage", source);
    }

    [Fact]
    public void IssueCommandsFile_ReferencesTheHelperForTheDiagnostic()
    {
        var source = File.ReadAllText(GetIssueCommandsPath());
        var occurrences = CountOccurrences(source, "Run 'mo project use <name-or-id>' or pass --project <name-or-id>");
        Assert.Equal(0, occurrences);
        Assert.Contains("MohistCliCommands.NoActiveProjectMessage", source);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
            return 0;
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string GetCliCommandsPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", "..", "cli", "Mohist.Cli", "MohistCliCommands.cs"));
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"Could not find MohistCliCommands.cs at {candidate}");
        }
        return candidate;
    }

    private static string GetCliApiPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", "..", "cli", "Mohist.Cli", "MohistCliApi.cs"));
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"Could not find MohistCliApi.cs at {candidate}");
        }
        return candidate;
    }

    private static string GetIssueCommandsPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", "..", "cli", "Mohist.Cli", "MohistCliCommands.Issue.cs"));
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"Could not find MohistCliCommands.Issue.cs at {candidate}");
        }
        return candidate;
    }
}
