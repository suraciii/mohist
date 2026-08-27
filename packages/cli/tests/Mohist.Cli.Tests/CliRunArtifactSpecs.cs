using System.Text;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliRunArtifactSpecs
{
    [Fact]
    public async Task List_RunId_ResolvesOwningIssueAndRendersHumanTable()
    {
        var (handler,http,output,error,fs,executor)=CreateArtifactFixture();
        var exit=await MohistCliCommands.RunAsync(http,["run","artifact","list","wr_1"],output,error,fs,executor);
        Assert.Equal(0,exit);
        Assert.Contains("artifact id",output.ToString());
        Assert.Contains("PLANS/PLAN.md",output.ToString());
        Assert.DoesNotContain("\"artifactId\"", output.ToString());
        Assert.Equal(3,handler.Requests.Count);
    }

    [Fact]
    public async Task List_JsonSelectionSupportsDiscoverySelectionAndInvalidFields()
    {
        var (_,http,output,error,fs,executor)=CreateArtifactFixture();
        Assert.Equal(0,await MohistCliCommands.RunAsync(http,["run","artifact","list","wr_1","--json"],output,error,fs,executor));
        Assert.Contains("artifactId",output.ToString());

        (var _,http,output,error,fs,executor)=CreateArtifactFixture();
        Assert.Equal(0,await MohistCliCommands.RunAsync(http,["run","artifact","list","wr_1","--json","artifactId,path"],output,error,fs,executor));
        Assert.Contains("\"artifactId\"",output.ToString());
        Assert.DoesNotContain("\"kind\"",output.ToString());

        (var _,http,output,error,fs,executor)=CreateArtifactFixture();
        Assert.Equal(2,await MohistCliCommands.RunAsync(http,["run","artifact","list","wr_1","--json","unknown"],output,error,fs,executor));
        Assert.Contains("Invalid --json field",error.ToString());
    }

    [Fact]
    public async Task Get_StreamsRecordedBytesExactlyThroughInjectedOutput()
    {
        var bytes = new byte[] { 0, 255, 10, 13, 42 };
        var (handler,http,output,error,fs,executor)=CliTestFactory.CreateSync(req => req.RequestUri?.PathAndQuery switch
        {
            "/api/workflow-runs/wr_1" => RecordingHttpHandler.Json(new { success=true,data=new { issueRef=new { projectId="proj_abc",number=42 } } }),
            "/api/projects/proj_abc/issues/42" => RecordingHttpHandler.Json(new { success=true,data=new { workflowRunId="wr_1" } }),
            "/api/projects/proj_abc/issues/42/workflow/artifacts/art-1/content" => new HttpResponseMessage(System.Net.HttpStatusCode.OK){ Content=new ByteArrayContent(bytes) },
            _ => null!,
        });
        await using var captured = new MemoryStream();
        var exit=await MohistCliCommands.RunAsync(
            http,["run","artifact","get","wr_1","art-1"],output,error,fs,executor,
            binaryOutput: async (stream, ct) => await stream.CopyToAsync(captured, ct));
        Assert.Equal(0,exit);
        Assert.Equal(bytes,captured.ToArray());
        Assert.Equal(string.Empty,output.ToString());
        Assert.Equal(3,handler.Requests.Count);
    }

    [Fact]
    public async Task List_RejectsHistoricalRunBinding()
    {
        var (handler,http,output,error,fs,executor)=CliTestFactory.CreateSync(req => req.RequestUri?.PathAndQuery switch
        {
            "/api/workflow-runs/wr_old" => RecordingHttpHandler.Json(new { success=true,data=new { issueRef=new { projectId="proj_abc",number=42 } } }),
            "/api/projects/proj_abc/issues/42" => RecordingHttpHandler.Json(new { success=true,data=new { workflowRunId="wr_new" } }),
            _ => null!,
        });
        var exit=await MohistCliCommands.RunAsync(http,["run","artifact","list","wr_old"],output,error,fs,executor);
        Assert.NotEqual(0,exit); Assert.Contains("no longer bound",error.ToString()); Assert.Equal(2,handler.Requests.Count);
    }

    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor) CreateArtifactFixture() =>
        CliTestFactory.CreateSync(req => req.RequestUri?.PathAndQuery switch
        {
            "/api/workflow-runs/wr_1" => RecordingHttpHandler.Json(new { success=true,data=new { issueRef=new { projectId="proj_abc",number=42 } } }),
            "/api/projects/proj_abc/issues/42" => RecordingHttpHandler.Json(new { success=true,data=new { workflowRunId="wr_1" } }),
            "/api/projects/proj_abc/issues/42/workflow/artifacts" => RecordingHttpHandler.Json(new { success=true,data=new[]{new { artifactId="art-1",path="PLANS/PLAN.md",kind="file",contentType="text/markdown",size=12,actionAttemptId="plan.1",recordedAt="2026-01-01T00:00:00Z" } } }),
            _ => null!,
        });
}
