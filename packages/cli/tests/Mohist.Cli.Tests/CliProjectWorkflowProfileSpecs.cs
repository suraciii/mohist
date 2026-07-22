using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliProjectWorkflowProfileSpecs
{
    private static object SampleProfile(string id = "mohist/local", string displayName = "Mohist Local Workflow", string description = "Standard pipeline.") => new
    {
        id,
        displayName,
        description,
    };

    private static object SampleTemplateInfo(string templateId = "mohist/local") => new
    {
        id = templateId,
    };

    [Fact]
    public async Task ProfileList_DescribedWithProject_SendsProjectQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-profiles?project=proj_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { SampleProfile() },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list", "--described", "--project", "proj_abc"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-profiles?project=proj_abc", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("mohist/local", stdout);
    }

    [Fact]
    public async Task ProfileList_DescribedWithProjectIdAlias_SendsProjectQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-profiles?project=proj_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { SampleProfile() },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list", "--described", "--project", "proj_abc"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-profiles?project=proj_abc", getReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ProfileList_DescribedWithActiveProject_SendsProjectQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-profiles?project=proj_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        SampleProfile("mohist/local", "Mohist Local Workflow", "Standard pipeline."),
                        SampleProfile("mohist/github-pr", "GitHub PR Workflow", "PR pipeline."),
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list", "--described"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-profiles?project=proj_abc", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("mohist/local", stdout);
        Assert.Contains("mohist/github-pr", stdout);
        Assert.Contains("Standard pipeline.", stdout);
        Assert.Contains("PR pipeline.", stdout);
        Assert.DoesNotContain("Suitable for", stdout);
        Assert.DoesNotContain("(not specified)", stdout);
    }

    [Fact]
    public async Task ProfileList_Described_RendersIdDisplayNameAndDescriptionWithoutSuitableForLine()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-profiles?project=proj_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { SampleProfile() },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list", "--described", "--project", "proj_abc"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("mohist/local", stdout);
        Assert.Contains("Mohist Local Workflow", stdout);
        Assert.Contains("Standard pipeline.", stdout);
        Assert.DoesNotContain("Suitable for", stdout);
        Assert.DoesNotContain("(not specified)", stdout);
    }

    [Fact]
    public async Task ProfileList_DescribedNoProjectResolvable_FallsBackToUnfilteredWithStderrNote()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-profiles")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { SampleProfile() },
                });
            }
            return null!;
        }, activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list", "--described"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-profiles", getReq.RequestUri?.PathAndQuery);
        var stderr = error.ToString();
        Assert.Contains("degraded", stderr);
    }

    [Fact]
    public async Task ProfileList_DescribedWithConflictingProjectFlags_ReturnsOneAndDoesNotRequestDiscovery()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list", "--described", "--project", "proj_a", "--project-id", "proj_b"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--project-id is not supported", error.ToString());
        Assert.DoesNotContain("degraded", error.ToString());
    }

    [Fact]
    public async Task ProfileList_PlainWithProject_SendsProjectQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-templates/system?project=proj_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        SampleTemplateInfo("mohist/local"),
                        SampleTemplateInfo("mohist/github-pr"),
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list", "--project", "proj_abc"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-templates/system?project=proj_abc", getReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ProfileList_PlainJson_UsesSharedOutputOption()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-templates/system?project=proj_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { SampleTemplateInfo("mohist/local") },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list", "--project", "proj_abc", "--json", "id"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-templates/system?project=proj_abc", getReq.RequestUri?.PathAndQuery);
        Assert.Contains("mohist/local", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ProfileList_BareJson_DiscoversFieldsWithoutCallingApi()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list", "--json"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Equal(
            "[\"id\",\"name\",\"displayName\",\"description\",\"isDefault\"]",
            output.ToString().Trim());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ProfileList_PlainWithActiveProject_SendsProjectQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-templates/system?project=proj_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { SampleTemplateInfo("mohist/local") },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-templates/system?project=proj_abc", getReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ProfileList_PlainWithConflictingProjectFlags_ReturnsOneAndDoesNotRequestDiscovery()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list", "--project", "proj_a", "--project-id", "proj_b"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--project-id is not supported", error.ToString());
    }

    [Fact]
    public async Task ProfileList_DescribedWithMissingProject_ReturnsNotFoundAndPrintsError()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-profiles?project=missing-project")
            {
                return RecordingHttpHandler.JsonError(
                    "Project 'missing-project' not found",
                    "project_not_found",
                    HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list", "--described", "--project", "missing-project"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        var getReq = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-profiles?project=missing-project", getReq.RequestUri?.PathAndQuery);
        Assert.Contains("Project 'missing-project' not found", error.ToString());
        Assert.Contains("project_not_found", error.ToString());
        Assert.DoesNotContain("degraded", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task ProfileList_HelpMentionsDescribedOptionAndOmitsSuitableForWording()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "list", "--help"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--described", stdout);
        Assert.DoesNotContain("suitable_for", stdout);
    }

    [Fact]
    public async Task ProfileEnable_PostsProfileIdToEnableEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/enable")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "enable", "mohist/github-pr"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/workflow-profile/enable", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("mohist/github-pr", body["profileId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ProfileDisable_PostsProfileIdToDisableEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/disable")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "disable", "mohist/github-pr"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/workflow-profile/disable", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("mohist/github-pr", body["profileId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ProfileEnable_MissingProfileId_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "enable"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("<profile-id> is required", error.ToString());
    }

    [Fact]
    public async Task ProfileDisable_MissingProfileId_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "disable"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("<profile-id> is required", error.ToString());
    }

    [Fact]
    public async Task ProfileEnable_HonorsProjectIdFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_xyz/workflow-profile/enable")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "enable", "mohist/local", "--project", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/workflow-profile/enable", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ProfileDisable_HonorsProjectFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_xyz/workflow-profile/disable")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "disable", "mohist/local", "--project", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/workflow-profile/disable", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ProfileEnable_UnknownProfileServerError_SurfacesCodeAndMessage()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(
                    new { success = false, error = "Profile 'mohist/ghost' not found", code = "unknown_workflow_profile" },
                    HttpStatusCode.BadRequest);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "enable", "mohist/ghost"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        var stderr = error.ToString();
        Assert.Contains("Profile 'mohist/ghost' not found", stderr);
        Assert.Contains("unknown_workflow_profile", stderr);
    }

    [Fact]
    public async Task ProfileDisable_UnknownProfileServerError_SurfacesCodeAndMessage()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(
                    new { success = false, error = "Profile 'mohist/ghost' not found", code = "unknown_workflow_profile" },
                    HttpStatusCode.BadRequest);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "disable", "mohist/ghost"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        var stderr = error.ToString();
        Assert.Contains("Profile 'mohist/ghost' not found", stderr);
        Assert.Contains("unknown_workflow_profile", stderr);
    }

    [Fact]
    public async Task ProfileDisable_LastEnabledProfileServerError_SurfacesCodeAndMessage()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(
                    new
                    {
                        success = false,
                        error = "Cannot disable the last enabled profile for the project",
                        code = "last_enabled_workflow_profile",
                    },
                    HttpStatusCode.BadRequest);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "disable", "mohist/local"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        var stderr = error.ToString();
        Assert.Contains("Cannot disable the last enabled profile", stderr);
        Assert.Contains("last_enabled_workflow_profile", stderr);
    }

    [Fact]
    public async Task ProfileEnable_NoProjectResolvable_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "enable", "mohist/local"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("mo project use", error.ToString());
    }

    [Fact]
    public async Task ProfileEnable_ConflictingProjectFlags_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "enable", "mohist/local", "--project", "proj_a", "--project-id", "proj_b"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--project-id is not supported", error.ToString());
    }

    [Fact]
    public async Task Profile_HelpAdvertisesEnableAndDisableSubcommands()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "workflow", "profile", "--help"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("enable", stdout);
        Assert.Contains("disable", stdout);
    }
}
