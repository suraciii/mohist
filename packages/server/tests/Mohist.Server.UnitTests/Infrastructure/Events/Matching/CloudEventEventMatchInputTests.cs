using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;
using Xunit;

namespace Mohist.Server.UnitTests.Infrastructure.Events.Matching;

public sealed class CloudEventEventMatchInputTests
{
    [Fact]
    public void CoreFields_ResolveFromEnvelope()
    {
        var evt = new CloudEvent(
            id: "id-1",
            source: new Uri("/mohist/projects/p/issues/42", UriKind.Relative),
            type: "com.mohist.issue.completed",
            time: DateTimeOffset.UnixEpoch,
            data: null,
            subject: "42");

        var input = new CloudEventEventMatchInput(evt);

        Assert.Equal("com.mohist.issue.completed", input.GetValue("type"));
        Assert.Equal("/mohist/projects/p/issues/42", input.GetValue("source"));
        Assert.Equal("42", input.GetValue("subject"));
    }

    [Fact]
    public void Extensions_ResolveFromExtensionsDictionary()
    {
        var evt = new CloudEvent(
            id: "id-1",
            source: new Uri("/mohist/projects/p/issues/42", UriKind.Relative),
            type: "com.mohist.issue.completed",
            time: DateTimeOffset.UnixEpoch,
            data: null,
            subject: "42",
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["issue"] = "42",
                ["projectid"] = "proj_p",
            });

        var input = new CloudEventEventMatchInput(evt);

        Assert.Equal("42", input.GetValue("issue"));
        Assert.Equal("proj_p", input.GetValue("projectid"));
    }

    [Fact]
    public void MissingExtension_ResolvesToEmpty()
    {
        var evt = new CloudEvent(
            id: "id-1",
            source: new Uri("/mohist/projects/p/issues/42", UriKind.Relative),
            type: "com.mohist.issue.completed",
            time: DateTimeOffset.UnixEpoch,
            data: null,
            subject: "42");

        var input = new CloudEventEventMatchInput(evt);

        Assert.Equal(string.Empty, input.GetValue("epic"));
    }

    [Fact]
    public void NullSubject_ReadsAsEmptyAndAbsent()
    {
        var evt = new CloudEvent(
            id: "id-1",
            source: new Uri("/mohist/projects/p", UriKind.Relative),
            type: "com.mohist.project.reset",
            time: DateTimeOffset.UnixEpoch,
            data: null,
            subject: null);

        var input = new CloudEventEventMatchInput(evt);

        Assert.Equal(string.Empty, input.GetValue("subject"));
        Assert.False(input.Has("subject"));
    }

    [Fact]
    public void EmptySubject_IsPresentAndEmpty()
    {
        var evt = new CloudEvent(
            id: "id-1",
            source: new Uri("/mohist/projects/p", UriKind.Relative),
            type: "com.mohist.project.reset",
            time: DateTimeOffset.UnixEpoch,
            data: null,
            subject: string.Empty);

        var input = new CloudEventEventMatchInput(evt);

        Assert.True(input.Has("subject"));
        Assert.Equal(string.Empty, input.GetValue("subject"));
    }

    [Fact]
    public void Has_DistinguishesPresentButEmptyFromAbsent()
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["presentbutempty"] = string.Empty,
        };
        var evt = new CloudEvent(
            id: "id-1",
            source: new Uri("/mohist/projects/p", UriKind.Relative),
            type: "com.mohist.project.reset",
            time: DateTimeOffset.UnixEpoch,
            data: null,
            subject: null,
            extensions: extensions);

        var input = new CloudEventEventMatchInput(evt);

        Assert.True(input.Has("presentbutempty"));
        Assert.False(input.Has("absent"));
        Assert.Equal(string.Empty, input.GetValue("presentbutempty"));
    }

    [Fact]
    public void Matching_ThroughCompiledExpression_ChecksEnvelopeOnly()
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["issue"] = "42",
        };
        var evt = new CloudEvent(
            id: "id-1",
            source: new Uri("/mohist/projects/p/issues/42", UriKind.Relative),
            type: "com.mohist.issue.completed",
            time: DateTimeOffset.UnixEpoch,
            data: System.Text.Json.JsonSerializer.SerializeToElement(new { status = "ok" }),
            subject: "42",
            extensions: extensions);

        var expression = EventMatchExpression
            .Compile("event.type == \"com.mohist.issue.completed\" && event.issue == \"42\"");

        Assert.True(expression.IsSuccess);
        Assert.True(expression.Expression!.Matches(new CloudEventEventMatchInput(evt)));
    }

    [Fact]
    public void Matching_EnvelopeFieldsAreTheOnlyMatchInput()
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["issue"] = "99",
        };
        var evt = new CloudEvent(
            id: "id-1",
            source: new Uri("/mohist/projects/p/issues/99", UriKind.Relative),
            type: "com.mohist.issue.completed",
            time: DateTimeOffset.UnixEpoch,
            data: System.Text.Json.JsonSerializer.SerializeToElement(new { issue = "42" }),
            subject: "99",
            extensions: extensions);

        var envelopeExpression = EventMatchExpression.Compile("event.issue == \"42\"");
        Assert.True(envelopeExpression.IsSuccess);
        Assert.False(envelopeExpression.Expression!.Matches(new CloudEventEventMatchInput(evt)));

        var payloadReference = EventMatchExpression.Compile("event.data == \"x\"");
        Assert.False(payloadReference.IsSuccess);
    }
}