using Mohist.Server.Infrastructure.Events.Matching;
using Xunit;

namespace Mohist.Server.UnitTests.Infrastructure.Events.Matching;

public sealed class EventMatchExpressionTests
{
    [Fact]
    public void AndBindsTighterThanOr()
    {
        var input = Input(type: "x");

        Assert.True(Compile("event.type == \"x\" || event.type == \"y\" && event.issue == \"1\"").Matches(input));
        Assert.True(Compile("event.type == \"x\" || (event.type == \"y\" && event.issue == \"1\")").Matches(input));
    }

    [Fact]
    public void ParenthesesOverridePrecedence()
    {
        Assert.False(Compile("(event.type == \"x\" || event.type == \"y\") && event.issue == \"1\"").Matches(Input(type: "x")));
    }

    [Fact]
    public void NotNegatesPresence()
    {
        Assert.True(Compile("!has(event.epic)").Matches(Input()));
    }

    [Theory]
    [InlineData("event.issue == 42")]
    [InlineData("event.issue == true")]
    [InlineData("event.issue == false")]
    [InlineData("event.issue == null")]
    public void NonStringLiteralsAreRejected(string source)
    {
        Assert.False(EventMatchExpression.Compile(source).IsSuccess);
    }

    [Fact]
    public void CoreAndExtensionAttributesMatch()
    {
        var input = Input(
            type: "com.mohist.issue.completed",
            source: "/mohist/projects/p/issues/42",
            subject: "subject",
            extensions: new Dictionary<string, string> { ["issue"] = "42" });

        Assert.True(Compile("event.type == \"com.mohist.issue.completed\"").Matches(input));
        Assert.True(Compile("event.source == \"/mohist/projects/p/issues/42\"").Matches(input));
        Assert.True(Compile("event.subject == \"subject\"").Matches(input));
        Assert.True(Compile("event.issue == \"42\"").Matches(input));
    }

    [Fact]
    public void MissingAttributesBehaveAsEmptyStrings()
    {
        var input = Input();

        Assert.True(Compile("event.epic == \"\"").Matches(input));
        Assert.False(Compile("event.epic.startsWith(\"7\")").Matches(input));
        Assert.False(Compile("event.stage in [\"plan\", \"build\"]").Matches(input));
    }

    [Fact]
    public void HasDistinguishesAbsentPresentAndPresentEmpty()
    {
        Assert.False(Compile("has(event.epic)").Matches(Input()));
        Assert.True(Compile("has(event.epic)").Matches(Input(extensions: new Dictionary<string, string> { ["epic"] = "7" })));
        Assert.True(Compile("has(event.subject) && event.subject == \"\"").Matches(Input(subject: string.Empty, subjectPresent: true)));
        Assert.False(Compile("has(event.subject) && event.subject == \"\"").Matches(Input(subjectPresent: false)));
    }

    [Fact]
    public void EqualityInequalityAndMembershipAreOrdinal()
    {
        var input = Input(type: "COM.MOHIST.ISSUE.COMPLETED", extensions: new Dictionary<string, string> { ["issue"] = "43" });

        Assert.False(Compile("event.type == \"com.mohist.issue.completed\"").Matches(input));
        Assert.True(Compile("event.type != \"com.mohist.issue.completed\"").Matches(input));
        Assert.True(Compile("event.issue in [\"42\", \"43\"]").Matches(input));
        Assert.False(Compile("event.issue in [\"42\", \"44\"]").Matches(input));
        Assert.False(Compile("event.issue in []").Matches(input));
    }

    [Fact]
    public void StringFunctionsAreOrdinal()
    {
        var input = Input(
            type: "com.mohist.workflow.stage.approval-requested",
            source: "/mohist/projects/p/issues/42");

        Assert.True(Compile("event.type.startsWith(\"com.mohist.workflow.\")").Matches(input));
        Assert.True(Compile("event.source.endsWith(\"/issues/42\")").Matches(input));
        Assert.True(Compile("event.type.contains(\"approval\")").Matches(input));
        Assert.False(Compile("event.type.contains(\"APPROVAL\")").Matches(input));
        Assert.True(Compile("event.type.matches(\"workflow.*approval\")").Matches(input));
    }

    [Fact]
    public void InvalidRegexReportsArgumentLocation()
    {
        var result = EventMatchExpression.Compile("event.type.matches(\"[\")");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(19, result.Diagnostic.Offset);
        Assert.Equal(1, result.Diagnostic.Line);
    }

    [Fact]
    public void RegexTimeoutIsANonMatch()
    {
        var expression = Compile("event.type.matches(\"^(a+)+$\")", TimeSpan.FromTicks(1));

        Assert.False(expression.Matches(Input(type: new string('a', 200) + "!")));
    }

    [Theory]
    [InlineData("event.data == \"x\"")]
    [InlineData("event.data.status == \"failed\"")]
    public void EventDataIsRejected(string source)
    {
        var result = EventMatchExpression.Compile(source);

        Assert.False(result.IsSuccess);
        Assert.Contains("event.data", result.Diagnostic!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedEvaluationIsStable()
    {
        var expression = Compile("event.type.startsWith(\"com.mohist.\") && event.issue == \"42\"");
        var input = Input(type: "com.mohist.issue.completed", extensions: new Dictionary<string, string> { ["issue"] = "42" });

        for (var index = 0; index < 1000; index++)
            Assert.True(expression.Matches(input));
    }

    [Fact]
    public void CompiledExpressionEvaluatesManyInputs()
    {
        var expression = Compile("event.issue == \"42\"");

        Assert.True(expression.Matches(Input(extensions: new Dictionary<string, string> { ["issue"] = "42" })));
        Assert.False(expression.Matches(Input(extensions: new Dictionary<string, string> { ["issue"] = "43" })));
    }

    [Fact]
    public void SyntaxErrorReportsOffsetLineAndColumn()
    {
        var result = EventMatchExpression.Compile("event.type == \"x\" &&\n(event.issue == \"42\"");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Diagnostic);
        Assert.True(result.Diagnostic.Offset > 0);
        Assert.Equal(2, result.Diagnostic.Line);
        Assert.True(result.Diagnostic.Column > 0);
    }

    [Fact]
    public void RuntimeFailureIsRecordedAndDoesNotPropagate()
    {
        var sink = new RecordingFailureSink();
        var result = EventMatchExpression.Compile("event.type == \"x\"", failureSink: sink);
        var expression = Assert.IsType<EventMatchExpression>(result.Expression);

        Assert.False(expression.Matches(new ThrowingInput()));
        var failure = Assert.Single(sink.Failures);
        Assert.Equal("event.type == \"x\"", failure.Source);
        Assert.IsType<InvalidOperationException>(failure.Exception);
    }

    [Fact]
    public void FailureSinkFailureDoesNotPropagate()
    {
        var expression = Assert.IsType<EventMatchExpression>(
            EventMatchExpression.Compile("event.type == \"x\"", failureSink: new ThrowingFailureSink()).Expression);

        Assert.False(expression.Matches(new ThrowingInput()));
    }

    private static EventMatchExpression Compile(string source, TimeSpan? timeout = null)
    {
        var result = EventMatchExpression.Compile(source, timeout);
        Assert.True(result.IsSuccess, result.Diagnostic?.Message);
        return Assert.IsType<EventMatchExpression>(result.Expression);
    }

    private static DictionaryInput Input(
        string? type = null,
        string? source = null,
        string? subject = null,
        bool subjectPresent = false,
        IReadOnlyDictionary<string, string>? extensions = null) =>
        new(type, source, subject, subjectPresent, extensions);

    private sealed class DictionaryInput : EventMatchInput
    {
        private readonly IReadOnlyDictionary<string, string> _values;
        private readonly HashSet<string> _present;

        public DictionaryInput(
            string? type,
            string? source,
            string? subject,
            bool subjectPresent,
            IReadOnlyDictionary<string, string>? extensions)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            _present = new HashSet<string>(StringComparer.Ordinal);
            AddCore("type", type, type is not null);
            AddCore("source", source, source is not null);
            AddCore("subject", subject, subjectPresent || subject is not null);
            if (extensions is not null)
            {
                foreach (var extension in extensions)
                {
                    values[extension.Key] = extension.Value;
                    _present.Add(extension.Key);
                }
            }
            _values = values;

            void AddCore(string name, string? value, bool present)
            {
                if (!present)
                    return;
                values[name] = value ?? string.Empty;
                _present.Add(name);
            }
        }

        public string GetValue(string attribute) => _values.GetValueOrDefault(attribute, string.Empty);

        public bool Has(string attribute) => _present.Contains(attribute);
    }

    private sealed class ThrowingInput : EventMatchInput
    {
        public string GetValue(string attribute) => throw new InvalidOperationException("failed");

        public bool Has(string attribute) => throw new InvalidOperationException("failed");
    }

    private sealed class RecordingFailureSink : IEventMatchFailureSink
    {
        public List<(string Source, Exception Exception)> Failures { get; } = [];

        public void Record(string source, Exception exception) => Failures.Add((source, exception));
    }

    private sealed class ThrowingFailureSink : IEventMatchFailureSink
    {
        public void Record(string source, Exception exception) => throw new InvalidOperationException("sink failed");
    }
}
