using System.Net;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateServerSpecs
{
    [Fact]
    public async Task UpdateServer_InstallsSourceHashUnderStableRootAndUsesAbsoluteServiceTarget()
    {
        var f = new UpdateTestFactory(root: "/home/test");
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(
                new ResponseSpec(HttpStatusCode.OK),
                new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
                new ResponseSpec(HttpStatusCode.OK)),
            unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateServerAsync("/clean", dryRun: false);

        Assert.Equal(0, exitCode);
        var versionRoot = "/home/test/.local/share/mohist/runtime/server/versions/abcdef0";
        Assert.True(f.Files.HasFile(Path.Combine(versionRoot, "mohist-build.json")));
        Assert.Equal(versionRoot, f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/server/current"));
        Assert.Equal(versionRoot, f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/server/verified"));

        var unit = f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service"));
        Assert.Contains("WorkingDirectory=/home/test/.local/share/mohist/runtime/server", unit);
        Assert.Contains("/home/test/.local/share/mohist/runtime/server/current/Mohist.Server.dll", unit);
        Assert.DoesNotContain("/clean", unit);
        Assert.Contains(f.Commands.ExecutedCommands, command =>
            command.FileName == "dotnet" && command.Args.Contains("publish"));
        Assert.Contains("Server runtime verification: current", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateServer_WhenRuntimeIdentityDiffers_RestoresVerifiedVersionBeforeReturningFailure()
    {
        var f = new UpdateTestFactory(root: "/home/test");
        var initial = f.BuildUpdater(
            new SequenceHttpHandler(
                new ResponseSpec(HttpStatusCode.OK),
                new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
                new ResponseSpec(HttpStatusCode.OK)),
            unitDir: UpdateTestFactory.UnitDir);
        Assert.Equal(0, await initial.UpdateServerAsync("/clean", dryRun: false));
        f.Runtime.FreezeServerIdentity();
        f.ClearOutput();

        const string candidate = "0123456789abcdef0123456789abcdef01234567";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(["rev-parse", "HEAD"]), 0, candidate + "\n", "");
        var stale = f.BuildUpdater(
            new SequenceHttpHandler(
                new ResponseSpec(HttpStatusCode.OK),
                new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
                new ResponseSpec(HttpStatusCode.OK)),
            unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await stale.UpdateServerAsync("/clean", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "/home/test/.local/share/mohist/runtime/server/versions/abcdef0",
            f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/server/current"));
        var error = f.Stderr.ToString();
        Assert.Contains($"expected {candidate}, actual abcdef0", error);
        Assert.Contains("Recovery: restored verified version abcdef0", error);
        Assert.DoesNotContain("Server runtime verification: current", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateServer_WithoutVerifiedVersion_StopsUnverifiedCandidateAndDoesNotReportSuccess()
    {
        var f = new UpdateTestFactory(root: "/home/test");
        const string source = "0123456789abcdef0123456789abcdef01234567";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(["rev-parse", "HEAD"]), 0, source + "\n", "");
        f.Runtime.SetServerIdentityOverride(
            "fedcba9876543210fedcba9876543210fedcba98",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(
                new ResponseSpec(HttpStatusCode.OK),
                new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
                new ResponseSpec(HttpStatusCode.OK)),
            unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateServerAsync("/clean", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Null(f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/server/current"));
        Assert.Contains(f.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl" && command.Args.SequenceEqual(["--user", "stop", "mohist.service"]));
        Assert.Contains("Recovery: no prior service target existed; stopped candidate service target", f.Stderr.ToString());
        Assert.DoesNotContain("Server runtime verification: current", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateServer_DryRunDoesNotResolveGitOrChangeServiceTarget()
    {
        var f = new UpdateTestFactory(root: "/home/test");
        var updater = f.BuildUpdater(unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateServerAsync("/clean", dryRun: true);

        Assert.Equal(0, exitCode);
        Assert.Empty(f.Commands.ExecutedCommands);
        Assert.Empty(f.Files.DirectoryLinks);
        Assert.Contains("dotnet publish", f.Stdout.ToString());
    }
}
