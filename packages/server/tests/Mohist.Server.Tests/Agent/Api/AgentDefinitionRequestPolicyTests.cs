using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Xunit;

namespace Mohist.Server.Tests.Agent.Api;

[Trait("level", "L0")]
public sealed class AgentDefinitionRequestPolicyTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("\"two\"")]
    [InlineData("[]")]
    public void ValidateMaxConcurrentRuns_RejectsNonPositiveAndWrongTypeValues(string value)
    {
        using var document = JsonDocument.Parse($"{{\"maxConcurrentRuns\":{value}}}");

        var error = AgentDefinitionRoutes.ValidateMaxConcurrentRuns(document.RootElement);

        Assert.Equal("maxConcurrentRuns must be a positive integer or null.", error);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("12")]
    [InlineData("null")]
    public void ValidateMaxConcurrentRuns_AcceptsPositiveAndExplicitNullValues(string value)
    {
        using var document = JsonDocument.Parse($"{{\"maxConcurrentRuns\":{value}}}");

        Assert.Null(AgentDefinitionRoutes.ValidateMaxConcurrentRuns(document.RootElement));
    }

    [Fact]
    public void ValidateMaxConcurrentRuns_IgnoresAnOmittedField()
    {
        using var document = JsonDocument.Parse("{\"name\":\"reviewer\"}");

        Assert.Null(AgentDefinitionRoutes.ValidateMaxConcurrentRuns(document.RootElement));
    }

    [Fact]
    public async Task UpdateRequestBinder_ExcludesStatusFromMutableFields()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"status\":\"archived\",\"description\":\"still active\"}"));

        var request = await AgentUpdateRequest.BindAsync(context)
            ?? throw new InvalidOperationException("agent update binder returned null");

        Assert.DoesNotContain("status", request.Fields);
        Assert.Contains(nameof(AgentUpdateRequest.Description), request.Fields);
        Assert.Equal("still active", request.Description);
    }

    [Fact]
    public void PermissionVocabulary_AcceptsDeclaredTermsAndRejectsUnknownTerms()
    {
        using var valid = JsonDocument.Parse("{\"permissions\":[\"repo:read\",\"issue:write\"]}");
        using var invalid = JsonDocument.Parse("{\"permissions\":[\"shell:exec\"]}");

        Assert.Null(AgentPermissionVocabulary.Validate(valid.RootElement));
        var error = AgentPermissionVocabulary.Validate(invalid.RootElement);
        Assert.NotNull(error);
        Assert.Contains("shell:exec", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionVocabulary_RejectsNonStringEntries()
    {
        using var document = JsonDocument.Parse("{\"permissions\":[42]}");

        var error = AgentPermissionVocabulary.Validate(document.RootElement);

        Assert.NotNull(error);
        Assert.Contains("non-empty terms", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionVocabulary_RejectsEmptyEntries()
    {
        using var document = JsonDocument.Parse("{\"permissions\":[\"\"]}");

        var error = AgentPermissionVocabulary.Validate(document.RootElement);

        Assert.NotNull(error);
        Assert.Contains("non-empty terms", error, StringComparison.Ordinal);
    }
}
