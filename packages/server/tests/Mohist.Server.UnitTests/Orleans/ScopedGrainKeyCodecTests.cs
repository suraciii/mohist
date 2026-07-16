using System.Reflection;
using Mohist.Server.Infrastructure.Orleans;
using Xunit;

namespace Mohist.Server.UnitTests.Orleans;

public class ScopedGrainKeyCodecTests
{
    [Fact]
    public void Format_RoundTripsLosslessly()
    {
        var projectId = "proj_a";
        var number = 42;

        var encoded = ScopedGrainKeyCodec.Format(projectId, number);

        Assert.True(ScopedGrainKeyCodec.TryParse(encoded, out var parsedProjectId, out var parsedNumber));
        Assert.Equal(projectId, parsedProjectId);
        Assert.Equal(number, parsedNumber);

        var roundTrip = ScopedGrainKeyCodec.Format(parsedProjectId, parsedNumber);
        Assert.Equal(encoded, roundTrip);
    }

    [Fact]
    public void Format_OutputLengthEqualsProjectIdPlusOnePlusDigitsOnly()
    {
        var encoded = ScopedGrainKeyCodec.Format("project-identifier", 1);

        Assert.Equal("project-identifier".Length + 1 + 1, encoded.Length);
    }

    [Fact]
    public void Format_ProducesNoNulPaddingForShortNumbers()
    {
        var encoded = ScopedGrainKeyCodec.Format("a", 1);
        Assert.Equal("a:1", encoded);
        Assert.Equal(3, encoded.Length);
    }

    [Fact]
    public void Format_HandlesLargestPositiveIntExactly()
    {
        var encoded = ScopedGrainKeyCodec.Format("p", int.MaxValue);

        Assert.Equal(12, encoded.Length);
        Assert.Equal("p:2147483647", encoded);
        Assert.DoesNotContain('\0', encoded);

        Assert.True(ScopedGrainKeyCodec.TryParse(encoded, out var projectId, out var number));
        Assert.Equal("p", projectId);
        Assert.Equal(int.MaxValue, number);
    }

    [Fact]
    public void Format_InvariantCultureNumberText()
    {
        var encoded = ScopedGrainKeyCodec.Format("proj", 1000000);
        Assert.Equal("proj:1000000", encoded);
    }

    [Fact]
    public void Format_RejectsBlankProjectId()
    {
        Assert.Throws<ArgumentException>(() => ScopedGrainKeyCodec.Format("", 1));
        Assert.Throws<ArgumentException>(() => ScopedGrainKeyCodec.Format("   ", 1));
    }

    [Fact]
    public void Format_RejectsProjectIdContainingSeparator()
    {
        Assert.Throws<ArgumentException>(() => ScopedGrainKeyCodec.Format("proj:a", 1));
        Assert.Throws<ArgumentException>(() => ScopedGrainKeyCodec.Format(":a", 1));
        Assert.Throws<ArgumentException>(() => ScopedGrainKeyCodec.Format("a:", 1));
    }

    [Fact]
    public void Format_RejectsNonPositiveNumber()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScopedGrainKeyCodec.Format("proj", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScopedGrainKeyCodec.Format("proj", -1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(":")]
    [InlineData("proj_a")]
    [InlineData("proj_a:")]
    [InlineData(":1")]
    [InlineData("proj_a:notanumber")]
    [InlineData("proj_a:0")]
    [InlineData("proj_a:-1")]
    [InlineData("proj_a:+1")]
    [InlineData("proj_a:01")]
    [InlineData("proj_a:007")]
    [InlineData("proj_a:1:2")]
    [InlineData("proj::a:1")]
    [InlineData("proj_a:1 ")]
    [InlineData("proj_a: 1")]
    [InlineData("proj_a:1\0")]
    public void TryParse_RejectsMalformedOrAmbiguousInput(string? grainKey)
    {
        Assert.False(ScopedGrainKeyCodec.TryParse(grainKey, out _, out _));
        if (!string.IsNullOrEmpty(grainKey))
        {
            Assert.Throws<FormatException>(() =>
                ScopedGrainKeyCodec.Parse(grainKey!, out _, out _));
        }
    }

    [Fact]
    public void CrossProject_SameNumber_ProducesUnequalStringsAndKeys()
    {
        var a = ScopedGrainKeyCodec.Format("proj_a", 42);
        var b = ScopedGrainKeyCodec.Format("proj_b", 42);

        Assert.NotEqual(a, b);

        Assert.True(ScopedGrainKeyCodec.TryParse(a, out var parsedAProject, out var parsedANumber));
        Assert.True(ScopedGrainKeyCodec.TryParse(b, out var parsedBProject, out var parsedBNumber));

        Assert.Equal("proj_a", parsedAProject);
        Assert.Equal("proj_b", parsedBProject);
        Assert.Equal(42, parsedANumber);
        Assert.Equal(42, parsedBNumber);
        Assert.NotEqual(parsedAProject, parsedBProject);
    }
}

public class IssueKeyTests
{
    [Fact]
    public void Construction_RejectsBlankProjectId()
    {
        Assert.Throws<ArgumentException>(() => new IssueKey("", 1));
        Assert.Throws<ArgumentException>(() => new IssueKey("   ", 1));
    }

    [Fact]
    public void Construction_RejectsProjectIdContainingSeparator()
    {
        Assert.Throws<ArgumentException>(() => new IssueKey("proj:a", 1));
        Assert.Throws<ArgumentException>(() => new IssueKey(":a", 1));
        Assert.Throws<ArgumentException>(() => new IssueKey("a:", 1));
    }

    [Fact]
    public void Construction_RejectsNonPositiveNumber()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IssueKey("proj", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IssueKey("proj", -1));
    }

    [Fact]
    public void Construction_HappyPath_StoresBothComponents()
    {
        var key = new IssueKey("proj_a", 42);

        Assert.Equal("proj_a", key.ProjectId);
        Assert.Equal(42, key.IssueNumber);
    }

    [Fact]
    public void Equality_CrossProjectSameNumber_IsUnequal()
    {
        var first = new IssueKey("proj_a", 42);
        var sameAsFirst = new IssueKey("proj_a", 42);
        var differentProject = new IssueKey("proj_b", 42);
        var differentNumber = new IssueKey("proj_a", 7);

        Assert.Equal(first, sameAsFirst);
        Assert.True(first == sameAsFirst);
        Assert.NotEqual(first, differentProject);
        Assert.NotEqual(first, differentNumber);
    }

    [Fact]
    public void ToGrainKeyString_MatchesFormat()
    {
        var key = new IssueKey("proj_a", 42);

        Assert.Equal(ScopedGrainKeyCodec.Format("proj_a", 42), key.ToGrainKeyString());
        Assert.Equal(key.ToGrainKeyString(), key.ToString());
    }

    [Fact]
    public void ToGrainKeyString_LargestPositiveIntExact()
    {
        var key = new IssueKey("p", int.MaxValue);

        Assert.Equal("p:2147483647", key.ToGrainKeyString());
        Assert.Equal(12, key.ToGrainKeyString().Length);
    }
}

public class EpicKeyTests
{
    [Fact]
    public void Construction_RejectsBlankProjectId()
    {
        Assert.Throws<ArgumentException>(() => new EpicKey("", 1));
        Assert.Throws<ArgumentException>(() => new EpicKey("   ", 1));
    }

    [Fact]
    public void Construction_RejectsProjectIdContainingSeparator()
    {
        Assert.Throws<ArgumentException>(() => new EpicKey("proj:a", 1));
        Assert.Throws<ArgumentException>(() => new EpicKey(":a", 1));
        Assert.Throws<ArgumentException>(() => new EpicKey("a:", 1));
    }

    [Fact]
    public void Construction_RejectsNonPositiveNumber()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EpicKey("proj", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EpicKey("proj", -1));
    }

    [Fact]
    public void Equality_CrossProjectSameNumber_IsUnequal()
    {
        var first = new EpicKey("proj_a", 7);
        var differentProject = new EpicKey("proj_b", 7);

        Assert.NotEqual(first, differentProject);
    }

    [Fact]
    public void ToGrainKeyString_MatchesFormat()
    {
        var key = new EpicKey("proj_a", 7);

        Assert.Equal(ScopedGrainKeyCodec.Format("proj_a", 7), key.ToGrainKeyString());
    }
}

public class GrainKeyTypedEntryPointTests
{
    [Fact]
    public void Issue_AcceptsIssueKey_DelegatesToCodec()
    {
        var key = new IssueKey("proj_a", 42);

        Assert.Equal(ScopedGrainKeyCodec.Format("proj_a", 42), GrainKey.Issue(key));
    }

    [Fact]
    public void Epic_AcceptsEpicKey_DelegatesToCodec()
    {
        var key = new EpicKey("proj_b", 7);

        Assert.Equal(ScopedGrainKeyCodec.Format("proj_b", 7), GrainKey.Epic(key));
    }

    [Fact]
    public void CrossProject_TypedEntries_AreUnequal()
    {
        var issueA = GrainKey.Issue(new IssueKey("proj_a", 42));
        var issueB = GrainKey.Issue(new IssueKey("proj_b", 42));

        Assert.NotEqual(issueA, issueB);

        var epicA = GrainKey.Epic(new EpicKey("proj_a", 7));
        var epicB = GrainKey.Epic(new EpicKey("proj_b", 7));

        Assert.NotEqual(epicA, epicB);
    }

    [Fact]
    public void Issue_DefaultStructValue_IsRejectedByCodec()
    {
        Assert.Throws<ArgumentException>(() => GrainKey.Issue(default(IssueKey)));
    }

    [Fact]
    public void Epic_DefaultStructValue_IsRejectedByCodec()
    {
        Assert.Throws<ArgumentException>(() => GrainKey.Epic(default(EpicKey)));
    }

    [Fact]
    public void GrainKey_DoesNotExposeScalarIssueOverload()
    {
        var issueMethods = typeof(GrainKey).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(GrainKey.Issue))
            .Select(m => string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)))
            .ToList();

        Assert.Contains(issueMethods, sig => sig.Contains("IssueKey"));

        var scalarOverloads = issueMethods
            .Where(sig => sig.Contains("String") && sig.Contains("Int32"))
            .ToList();
        Assert.Empty(scalarOverloads);
    }

    [Fact]
    public void GrainKey_DoesNotExposeScalarEpicOverload()
    {
        var epicMethods = typeof(GrainKey).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(GrainKey.Epic))
            .Select(m => string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)))
            .ToList();

        Assert.Contains(epicMethods, sig => sig.Contains("EpicKey"));

        var scalarOverloads = epicMethods
            .Where(sig => sig.Contains("String") && sig.Contains("Int32"))
            .ToList();
        Assert.Empty(scalarOverloads);
    }

    [Fact]
    public void LegacyIssue_TransitionSeam_IsInternal()
    {
        var randomIdOverload = typeof(GrainKey).GetMethod(
            "Issue",
            BindingFlags.NonPublic | BindingFlags.Static,
            new[] { typeof(string) });

        Assert.NotNull(randomIdOverload);
        Assert.False(randomIdOverload!.IsPublic);
    }
}
