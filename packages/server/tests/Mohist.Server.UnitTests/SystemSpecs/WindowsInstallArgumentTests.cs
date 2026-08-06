using Mohist.Cli;
using Mohist.Server.SpecTests.Support;
using Xunit;
using static Mohist.Server.TestSupport.SystemSpecs.WindowsInstallTestSupport;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class WindowsInstallArgumentTests
{
    [Fact]
    public void BuildCreateTaskArgs_ContainsDiscreteElements()
    {
        var args = WindowsScheduledTaskInstaller.BuildCreateTaskArgs(
            new WindowsScheduledTaskInstaller.TaskCreateSpec("Mohist_Server", @"C:\path\launcher.cmd"));

        Assert.Equal("/Create", args[0]);
        Assert.Equal("/SC", args[1]);
        Assert.Equal("ONLOGON", args[2]);
        Assert.Equal("/RL", args[3]);
        Assert.Equal("LIMITED", args[4]);
        Assert.Equal("/TN", args[5]);
        Assert.Equal("Mohist_Server", args[6]);
        Assert.Equal("/TR", args[7]);
        Assert.Equal(@"C:\path\launcher.cmd", args[8]);
        Assert.Equal("/F", args[9]);
    }

    [Fact]
    public void BuildRunArgs_ContainsDiscreteVerbAndTaskName()
    {
        var args = WindowsScheduledTaskInstaller.BuildRunArgs("Mohist_Server");

        Assert.Equal("/Run", args[0]);
        Assert.Equal("/TN", args[1]);
        Assert.Equal("Mohist_Server", args[2]);
    }

    [Fact]
    public void BuildEndArgs_ContainsDiscreteVerbAndTaskName()
    {
        var args = WindowsScheduledTaskInstaller.BuildEndArgs("Mohist_Runner");

        Assert.Equal("/End", args[0]);
        Assert.Equal("/TN", args[1]);
        Assert.Equal("Mohist_Runner", args[2]);
    }

    [Fact]
    public void BuildDeleteArgs_ContainsDiscreteVerbAndTaskNameAndForceFlag()
    {
        var args = WindowsScheduledTaskInstaller.BuildDeleteArgs("Mohist_Server");

        Assert.Equal("/Delete", args[0]);
        Assert.Equal("/TN", args[1]);
        Assert.Equal("Mohist_Server", args[2]);
        Assert.Equal("/F", args[3]);
    }

    [Fact]
    public void BuildQueryArgs_ContainsDiscreteVerbAndTaskName()
    {
        var args = WindowsScheduledTaskInstaller.BuildQueryArgs("Mohist_Runner");

        Assert.Equal("/Query", args[0]);
        Assert.Equal("/TN", args[1]);
        Assert.Equal("Mohist_Runner", args[2]);
    }

    [Fact]
    public void RenderServerLauncher_WithSpacePath_ContainsQuotedCd()
    {
        var installer = CreateInstaller(new FakeFileSystem(), new FakeCommandExecutor());
        var pathWithSpaces = @"C:\Users\Mohist User\repos\space repo";
        var body = installer.RenderServerLauncher(
            new WindowsScheduledTaskInstaller.ServerLauncherSpec(pathWithSpaces, "http://127.0.0.1:3456"));

        Assert.Contains("cd /d", body);
        Assert.Contains('"', body);
        Assert.Contains(pathWithSpaces, body);
    }

    [Fact]
    public void RenderRunnerLauncher_ContainsExpectedElements()
    {
        var installer = CreateInstaller(new FakeFileSystem(), new FakeCommandExecutor());
        var body = installer.RenderRunnerLauncher(
            new WindowsScheduledTaskInstaller.RunnerLauncherSpec(@"C:\repo", "http://127.0.0.1:3456", @"C:\runner"));

        Assert.Contains("cd /d", body);
        Assert.Contains("set \"SERVER_URL=http://127.0.0.1:3456\"", body);
        Assert.Contains("set \"RUNNER_ROOT=C:\\runner\"", body);
        Assert.Contains("node packages\\runner\\dist\\cli.js", body);
        Assert.Contains(@"%USERPROFILE%\.mohist\runner\out.log", body);
    }

    [Fact]
    public void RenderRunnerLauncher_WithNonDefaultServerUrl_PassesItThrough()
    {
        var installer = CreateInstaller(new FakeFileSystem(), new FakeCommandExecutor());
        var body = installer.RenderRunnerLauncher(
            new WindowsScheduledTaskInstaller.RunnerLauncherSpec(@"C:\repo", "http://example.com:9999", null));

        Assert.Contains("set \"SERVER_URL=http://example.com:9999\"", body);
        Assert.DoesNotContain("http://127.0.0.1:3456", body);
    }

    [Fact]
    public void RenderServerLauncher_WithNonDefaultListenUrl_PassesItThrough()
    {
        var installer = CreateInstaller(new FakeFileSystem(), new FakeCommandExecutor());
        var body = installer.RenderServerLauncher(
            new WindowsScheduledTaskInstaller.ServerLauncherSpec(@"C:\repo", "http://example.com:9999"));

        Assert.Contains("ASPNETCORE_URLS=http://example.com:9999", body);
        Assert.DoesNotContain("http://127.0.0.1:3456", body);
    }

    [Fact]
    public void RenderServerLauncher_WithoutListenUrl_OmitsAspnetcoreUrls()
    {
        var installer = CreateInstaller(new FakeFileSystem(), new FakeCommandExecutor());
        var body = installer.RenderServerLauncher(
            new WindowsScheduledTaskInstaller.ServerLauncherSpec(@"C:\repo", null));

        Assert.DoesNotContain("ASPNETCORE_URLS", body);
        Assert.Contains("dotnet run --project", body);
    }

    [Fact]
    public void QuoteForCmdBody_And_QuoteForSchtasksTr_ProduceDifferentOutputs_ForSamePath()
    {
        // The two helpers target different runtimes (cmd body vs. schtasks /TR
        // payload) and therefore apply different escape rules. A path that
        // contains a cmd metacharacter such as `&` exercises the difference:
        // QuoteForCmdBody caret-escapes the `&` for the .cmd body, while
        // QuoteForSchtasksTr leaves the `&` literal (cmd's quoting rules for
        // the /TR field are different from .cmd body rules).
        var path = @"C:\repo\bin&tools\launcher.cmd";
        var cmdBody = WindowsScheduledTaskInstaller.QuoteForCmdBody(path);
        var schtasksTr = WindowsScheduledTaskInstaller.QuoteForSchtasksTr(path);

        Assert.NotEqual(cmdBody, schtasksTr);
    }

    [Fact]
    public void QuoteForSchtasksTr_WithSpacePath_WrapsInDoubleQuotes()
    {
        var path = @"C:\Users\Mohist User\repos\space repo\launcher.cmd";
        var tr = WindowsScheduledTaskInstaller.QuoteForSchtasksTr(path);

        Assert.StartsWith("\"", tr);
        Assert.EndsWith("\"", tr);
        Assert.Contains("Mohist User", tr);
    }

    [Fact]
    public void QuoteForSchtasksTr_WithoutSpace_DoesNotWrapInDoubleQuotes()
    {
        var path = @"C:\repo\launcher.cmd";
        var tr = WindowsScheduledTaskInstaller.QuoteForSchtasksTr(path);

        Assert.Equal(path, tr);
    }

    [Fact]
    public void BuildCreateTaskArgs_WithSpaceLauncherPath_WrapsTrPayloadInDoubleQuotes()
    {
        var path = @"C:\Users\Mohist User\repos\space repo\launcher.cmd";
        var tr = WindowsScheduledTaskInstaller.QuoteForSchtasksTr(path);
        var args = WindowsScheduledTaskInstaller.BuildCreateTaskArgs(
            new WindowsScheduledTaskInstaller.TaskCreateSpec("Mohist_Server", tr));

        var trIndex = Array.IndexOf(args, "/TR");
        Assert.True(trIndex >= 0);
        var trPayload = args[trIndex + 1];
        Assert.StartsWith("\"", trPayload);
        Assert.EndsWith("\"", trPayload);
        Assert.Contains("Mohist User", trPayload);
    }

    [Theory]
    [InlineData("value with \r")]
    [InlineData("value with \n")]
    [InlineData("value with \0")]
    [InlineData("value with \" quote")]
    public void SanitizeForCmdAssignment_RejectsInjectionPayloads(string value)
    {
        Assert.Throws<ArgumentException>(() => WindowsScheduledTaskInstaller.SanitizeForCmdAssignment(value));
    }

    [Fact]
    public void SanitizeForCmdAssignment_AllowsSafeValues()
    {
        Assert.Equal("http://127.0.0.1:3456", WindowsScheduledTaskInstaller.SanitizeForCmdAssignment("http://127.0.0.1:3456"));
        Assert.Equal(@"C:\repo\runner", WindowsScheduledTaskInstaller.SanitizeForCmdAssignment(@"C:\repo\runner"));
    }

}
