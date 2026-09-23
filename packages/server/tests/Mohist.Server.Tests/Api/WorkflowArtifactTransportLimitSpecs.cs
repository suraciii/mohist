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
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Api;

/// <summary>
/// Transport-limit separation: only the directory upload routes raise the
/// request-body and form limits to the configured envelope bound. The
/// single-file routes keep the default Kestrel/form boundary, so the
/// directory envelope relaxation cannot silently widen regular file uploads.
/// </summary>
[Trait("level", "L1")]
public sealed class WorkflowArtifactTransportLimitSpecs
{
    private const long EnvelopeBytes = 4 * 1024;
    private const long SlackBytes = WorkflowArtifactDirectoryLimits.MultipartFramingSlackBytes;
    private const long BodyLimitBytes = EnvelopeBytes + SlackBytes;

    private const string WorkflowFileRoute =
        "/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads";
    private const string WorkflowDirectoryRoute =
        "/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-directory-uploads";
    private const string AgentFileRoute =
        "/api/agent-jobs/{agentJobId}/work/{workId}/artifact-uploads";
    private const string AgentDirectoryRoute =
        "/api/agent-jobs/{agentJobId}/work/{workId}/artifact-directory-uploads";

    [Fact]
    public void DirectoryRoutesCarryEnvelopeRequestSizeLimit_FileRoutesKeepDefaultBoundary()
    {
        using var host = BuildHost(mapProbe: false);

        var routes = ((IEndpointRouteBuilder)host).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is
                WorkflowFileRoute or WorkflowDirectoryRoute or AgentFileRoute or AgentDirectoryRoute)
            .ToList();

        Assert.Equal(4, routes.Count);

        var directoryRoutes = routes
            .Where(route => route.RoutePattern.RawText!.EndsWith(
                WorkflowArtifactUploadRoutes.DirectoryUploadRouteSuffix, StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, directoryRoutes.Count);
        foreach (var route in directoryRoutes)
        {
            var metadata = route.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
            Assert.NotNull(metadata);
            Assert.Equal(BodyLimitBytes, metadata!.MaxRequestBodySize);
        }

        var fileRoutes = routes
            .Where(route => route.RoutePattern.RawText!.EndsWith("artifact-uploads", StringComparison.Ordinal)
                && !route.RoutePattern.RawText!.EndsWith(
                    WorkflowArtifactUploadRoutes.DirectoryUploadRouteSuffix, StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, fileRoutes.Count);
        foreach (var route in fileRoutes)
        {
            Assert.Null(route.Metadata.GetMetadata<IRequestSizeLimitMetadata>());
        }

        // The form limit is raised per-request only on the directory routes, so
        // the process-global default still bounds single-file and attachment
        // multipart parsing.
        var formOptions = host.Services.GetRequiredService<IOptions<FormOptions>>().Value;
        Assert.Equal(FormOptions.DefaultMultipartBodyLengthLimit, formOptions.MultipartBodyLengthLimit);
    }

    [Fact]
    public async Task DirectoryFormLimit_AtEnvelopeLimit_IsAdmittedByFormLayer()
    {
        using var host = BuildHost(mapProbe: true);
        using var client = host.GetTestServer().CreateClient();

        using var form = BuildDirectoryMultipart(EnvelopeBytes);
        using var response = await client.PostAsync("/probe", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DirectoryFormLimit_OverTransportBound_IsRejectedBeforeFormBuffering()
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

        var app = builder.Build();
        app.MapWorkflowArtifactUploadRoutes();
        if (mapProbe)
        {
            app.MapPost("/probe", async (HttpRequest request) =>
            {
                // Exercise the production helper the directory route applies
                // before parsing its multipart body.
                WorkflowArtifactUploadRoutes.ApplyDirectoryFormLimit(request, BodyLimitBytes);
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
