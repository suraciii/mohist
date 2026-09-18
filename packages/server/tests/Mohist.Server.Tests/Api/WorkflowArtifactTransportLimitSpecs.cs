using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Api;

/// <summary>
/// Transport-limit alignment: the configured directory envelope limit is the
/// effective gate. The global form limit and both artifact upload routes'
/// request-size metadata are derived from <c>MaxEnvelopeBytes</c> plus a fixed
/// multipart-framing slack, so a multipart directory at the configured limit
/// is admitted by the form layer and one over the derived bound is rejected
/// before the whole body is buffered.
/// </summary>
[Trait("level", "L1")]
public sealed class WorkflowArtifactTransportLimitSpecs
{
    private const long EnvelopeBytes = 4 * 1024;
    private const long SlackBytes = WorkflowArtifactDirectoryLimits.MultipartFramingSlackBytes;
    private const long BodyLimitBytes = EnvelopeBytes + SlackBytes;

    [Fact]
    public void RequestSizeLimitMetadata_AndFormOptions_DeriveFromConfiguredEnvelopeLimit()
    {
        using var host = BuildHost(mapProbe: false);

        var routes = ((IEndpointRouteBuilder)host).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is
                "/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads"
                or "/api/agent-jobs/{agentJobId}/work/{workId}/artifact-uploads")
            .ToList();

        Assert.Equal(2, routes.Count);
        foreach (var route in routes)
        {
            var metadata = route.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
            Assert.NotNull(metadata);
            Assert.Equal(BodyLimitBytes, metadata!.MaxRequestBodySize);
        }

        var formOptions = host.Services.GetRequiredService<IOptions<FormOptions>>().Value;
        Assert.Equal(BodyLimitBytes, formOptions.MultipartBodyLengthLimit);
    }

    [Fact]
    public async Task MultipartDirectory_AtEnvelopeLimit_IsAdmittedByFormLayer()
    {
        using var host = BuildHost(mapProbe: true);
        using var client = host.GetTestServer().CreateClient();

        using var form = BuildDirectoryMultipart(EnvelopeBytes);
        using var response = await client.PostAsync("/probe", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MultipartDirectory_OverTransportBound_IsRejectedBeforeFormBuffering()
    {
        using var host = BuildHost(mapProbe: true);
        using var client = host.GetTestServer().CreateClient();

        // The envelope part already exceeds the derived body bound, so the
        // form reader rejects the request while streaming the body instead of
        // buffering it and letting the upload service reject it afterwards.
        using var form = BuildDirectoryMultipart(BodyLimitBytes + 1);
        using var response = await client.PostAsync("/probe", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("limit", body, StringComparison.OrdinalIgnoreCase);
    }

    private static WebApplication BuildHost(bool mapProbe)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.Configure<WorkflowArtifactStorageOptions>(options =>
            options.DirectoryLimits = new WorkflowArtifactDirectoryLimits
            {
                MaxEnvelopeBytes = EnvelopeBytes,
            });
        builder.Services.ConfigureWorkflowArtifactTransportLimits();

        var app = builder.Build();
        app.MapWorkflowArtifactUploadRoutes();
        if (mapProbe)
        {
            app.MapPost("/probe", async (HttpRequest request) =>
            {
                try
                {
                    var form = await request.ReadFormAsync();
                    return Results.Ok(new { files = form.Files.Count });
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });
        }

        app.Start();
        return app;
    }

    /// <summary>
    /// Builds a multipart directory form whose <c>content</c> part is exactly
    /// <paramref name="envelopeLength"/> bytes. The trailing space padding keeps
    /// the value sequence well-formed so the body is a realistic directory
    /// envelope at the requested size.
    /// </summary>
    private static MultipartFormDataContent BuildDirectoryMultipart(long envelopeLength)
    {
        var envelope = DirectoryEnvelopeTestData.Create(
            new DirectoryEnvelopeTestFile("a.md", Encoding.UTF8.GetBytes("alpha")));
        var padded = new byte[envelopeLength];
        Array.Copy(envelope, padded, Math.Min(envelope.Length, padded.Length));
        for (var index = envelope.Length; index < padded.Length; index++)
            padded[index] = (byte)' ';

        var form = new MultipartFormDataContent(
            "----mohist-transport-" + Guid.NewGuid().ToString("N"));
        form.Add(new StringContent("specs"), "path");
        form.Add(
            new StringContent(WorkflowArtifactDirectoryEnvelopeReader.ContentType),
            "contentType");
        form.Add(new StringContent("sha256:dir-envelope"), "contentHash");
        form.Add(new StringContent(padded.LongLength.ToString()), "size");
        var content = new ByteArrayContent(padded);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            WorkflowArtifactDirectoryEnvelopeReader.ContentType);
        form.Add(content, "content", "directory.ndjson");
        return form;
    }
}
