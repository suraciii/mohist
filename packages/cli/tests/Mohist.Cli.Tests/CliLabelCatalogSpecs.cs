using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliLabelCatalogSpecs
{
    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateHarness(string? activeProjectId = "proj_abc")
    {
        var handler = new RecordingHttpHandler(async (req, _) =>
        {
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        if (activeProjectId is not null)
        {
            fs.AddFile(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
                $"{{\"activeProjectId\":\"{activeProjectId}\"}}");
        }
        return (handler, http, output, error, fs, new FakeCommandExecutor());
    }

    private static void SetCatalogListResponse(RecordingHttpHandler handler, params object[] definitions)
    {
        handler.SetResponder(async (req, _) =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (req.Method == HttpMethod.Get && path.EndsWith("/labels/catalog", StringComparison.Ordinal))
                return RecordingHttpHandler.Json(new { success = true, data = definitions });
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });
    }

    [Fact]
    public async Task LabelList_HitsCatalogEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        SetCatalogListResponse(handler,
            new { key = "refactor", description = "Technical refactoring", origin = "System" });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/projects/proj_abc/labels/catalog", req.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task LabelList_Table_ShowsKeyDescriptionOrigin()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        SetCatalogListResponse(handler,
            new { key = "refactor", description = "Technical refactoring that reduces complexity", origin = "System" },
            new { key = "module", description = "Classifies the subsystem", origin = "User", supportedValues = new[] { "auth", "ui" } });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "list", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("key", text);
        Assert.Contains("description", text);
        Assert.Contains("origin", text);
        Assert.Contains("refactor", text);
        Assert.Contains("module", text);
        Assert.Contains("System", text);
        Assert.Contains("User", text);
        Assert.Contains("[auth,ui]", text);
    }

    [Fact]
    public async Task LabelList_Json_ContainsFullData()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        SetCatalogListResponse(handler,
            new { key = "refactor", description = "Technical refactoring", origin = "System" },
            new { key = "module", description = "Classifies subsystem", origin = "User", supportedValues = new[] { "auth", "ui" } });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString().Trim();
        Assert.Contains("\"key\"", text);
        Assert.Contains("\"description\"", text);
        Assert.Contains("\"origin\"", text);
        Assert.Contains("\"supportedValues\"", text);
        Assert.Contains("\"refactor\"", text);
        Assert.Contains("\"module\"", text);
        Assert.Contains("\"System\"", text);
        Assert.Contains("\"User\"", text);
    }

    [Fact]
    public async Task LabelList_OnlySystemDefinitions_StillShowsRefactor()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        SetCatalogListResponse(handler,
            new { key = "refactor", description = "Technical refactoring that does not change observable behavior", origin = "System" });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "list", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("refactor", text);
        Assert.Contains("System", text);
    }

    [Fact]
    public async Task LabelAdd_CreatesDefinition()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        handler.SetResponder(async (req, _) =>
        {
            if (req.Method == HttpMethod.Post && (req.RequestUri?.AbsolutePath ?? "").EndsWith("/labels/catalog", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { key = "module", description = "Classifies the subsystem", origin = "User" },
                }, HttpStatusCode.Created);
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "add", "module", "--description", "Classifies the subsystem"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/projects/proj_abc/labels/catalog", req.RequestUri?.PathAndQuery);
        Assert.Contains("\"module\"", req.Body ?? "");
        Assert.Contains("Classifies the subsystem", req.Body ?? "");
    }

    [Fact]
    public async Task LabelAdd_WithSupportedValues()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        handler.SetResponder(async (req, _) =>
        {
            if (req.Method == HttpMethod.Post && (req.RequestUri?.AbsolutePath ?? "").EndsWith("/labels/catalog", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { key = "module", description = "Classifies", origin = "User", supportedValues = new[] { "auth", "ui" } },
                }, HttpStatusCode.Created);
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "add", "module", "--description", "Classifies", "--supported-values", "auth,ui"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Contains("auth", req.Body ?? "");
        Assert.Contains("ui", req.Body ?? "");
    }

    [Fact]
    public async Task LabelAdd_WithEmptySupportedValue_ExitsWithError()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "add", "module", "--description", "Classifies", "--supported-values", "auth,,ui"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("non-empty", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LabelAdd_InvalidKey_ExitsWithError()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "add", "Module", "--description", "Classifies"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.NotEqual("", error.ToString());
    }

    [Fact]
    public async Task LabelAdd_MissingDescription_ExitsWithError()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "add", "module", "--description", "   "], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.NotEqual("", error.ToString());
    }

    [Fact]
    public async Task LabelAdd_SystemKey_ReturnsError()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        handler.SetResponder(async (req, _) =>
        {
            if (req.Method == HttpMethod.Post && (req.RequestUri?.AbsolutePath ?? "").EndsWith("/labels/catalog", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.JsonError(
                    "Key 'refactor' is reserved as a system definition and cannot be created.",
                    "conflict",
                    HttpStatusCode.Conflict);
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "add", "refactor", "--description", "Custom refactor desc"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("refactor", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LabelRemove_RemovesDefinition()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        handler.SetResponder(async (req, _) =>
        {
            if (req.Method == HttpMethod.Delete && (req.RequestUri?.AbsolutePath ?? "").Contains("/labels/catalog/"))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "remove", "module"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Contains("/labels/catalog/module", req.RequestUri?.PathAndQuery ?? "");
    }

    [Fact]
    public async Task LabelRemove_MissingKey_Succeeds()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        handler.SetResponder(async (req, _) =>
        {
            if (req.Method == HttpMethod.Delete && (req.RequestUri?.AbsolutePath ?? "").Contains("/labels/catalog/"))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "remove", "nonexistent"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task LabelRemove_SystemKey_Fails()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        handler.SetResponder(async (req, _) =>
        {
            if (req.Method == HttpMethod.Delete && (req.RequestUri?.AbsolutePath ?? "").Contains("/labels/catalog/"))
            {
                return RecordingHttpHandler.JsonError(
                    "System definition 'refactor' is immutable and cannot be removed.",
                    "conflict",
                    HttpStatusCode.Conflict);
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "remove", "refactor"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("refactor", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LabelRemove_Alias_Rm_Works()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        handler.SetResponder(async (req, _) =>
        {
            if (req.Method == HttpMethod.Delete && (req.RequestUri?.AbsolutePath ?? "").Contains("/labels/catalog/"))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "rm", "module"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Contains("/labels/catalog/module", req.RequestUri?.PathAndQuery ?? "");
    }

    [Fact]
    public async Task LabelUpdate_DescriptionOnly_SendsPartialPatch()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        handler.SetResponder(async (req, _) =>
        {
            if (req.Method == HttpMethod.Patch && (req.RequestUri?.AbsolutePath ?? "").EndsWith("/labels/catalog/module", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { key = "module", description = "New desc", origin = "User" },
                });
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "update", "module", "--description", "New desc"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Patch, req.Method);
        Assert.Equal("/api/projects/proj_abc/labels/catalog/module", req.RequestUri?.PathAndQuery);
        var body = req.Body ?? "";
        Assert.Contains("\"description\"", body);
        Assert.Contains("New desc", body);
        Assert.DoesNotContain("supportedValues", body);
    }

    [Fact]
    public async Task LabelUpdate_SupportedValuesOnly_SendsPartialPatch()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        handler.SetResponder(async (req, _) =>
        {
            if (req.Method == HttpMethod.Patch && (req.RequestUri?.AbsolutePath ?? "").EndsWith("/labels/catalog/module", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { key = "module", description = "Unchanged", origin = "User", supportedValues = new[] { "auth", "ui", "persistence" } },
                });
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "update", "module", "--supported-values", "auth,ui,persistence"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Patch, req.Method);
        var body = req.Body ?? "";
        Assert.Contains("supportedValues", body);
        Assert.Contains("auth", body);
        Assert.Contains("ui", body);
        Assert.Contains("persistence", body);
        Assert.DoesNotContain("description", body);
    }

    [Fact]
    public async Task LabelUpdate_BothFields_SendsBothInBody()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        handler.SetResponder(async (req, _) =>
        {
            if (req.Method == HttpMethod.Patch && (req.RequestUri?.AbsolutePath ?? "").EndsWith("/labels/catalog/module", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { key = "module", description = "Updated", origin = "User", supportedValues = new[] { "auth", "ui" } },
                });
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "update", "module", "--description", "Updated", "--supported-values", "auth,ui"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Patch, req.Method);
        var body = req.Body ?? "";
        Assert.Contains("\"description\"", body);
        Assert.Contains("Updated", body);
        Assert.Contains("\"supportedValues\"", body);
        Assert.Contains("auth", body);
        Assert.Contains("ui", body);
    }

    [Fact]
    public async Task LabelUpdate_InvalidKey_ExitsWithErrorAndNoRequest()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "update", "Module", "--description", "Whatever"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.NotEqual("", error.ToString());
    }

    [Fact]
    public async Task LabelUpdate_EmptyDescription_ExitsWithErrorAndNoRequest()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "update", "module", "--description", "   "], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("description", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LabelUpdate_NoFieldsProvided_ExitsWithErrorAndNoRequest()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "update", "module"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--description", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--supported-values", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LabelUpdate_EmptySupportedValueEntry_ExitsWithErrorAndNoRequest()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "update", "module", "--supported-values", "auth,,ui"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("non-empty", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LabelUpdate_UnknownKey_404_SurfacesAsError()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        handler.SetResponder(async (req, _) =>
        {
            if (req.Method == HttpMethod.Patch && (req.RequestUri?.AbsolutePath ?? "").EndsWith("/labels/catalog/unknown", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.JsonError(
                    "Key 'unknown' not found in the project catalog.",
                    "not_found",
                    HttpStatusCode.NotFound);
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "update", "unknown", "--description", "Anything"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("unknown", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LabelUpdate_SystemKey_409_SurfacesAsError()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        handler.SetResponder(async (req, _) =>
        {
            if (req.Method == HttpMethod.Patch && (req.RequestUri?.AbsolutePath ?? "").EndsWith("/labels/catalog/refactor", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.JsonError(
                    "System definition 'refactor' is immutable and cannot be modified.",
                    "conflict",
                    HttpStatusCode.Conflict);
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "update", "refactor", "--description", "Trying to change"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("refactor", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
