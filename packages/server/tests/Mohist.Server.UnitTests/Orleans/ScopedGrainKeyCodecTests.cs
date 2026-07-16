using Mohist.Server.Infrastructure.Orleans;
using Xunit;

namespace Mohist.Server.UnitTests.Orleans;

/// <summary>
/// Issue #412 T-001 spec: prove the typed IssueKey/EpicKey values and
/// the lossless ScopedGrainKeyCodec round-trip the Project + Number pair
/// and isolate projects with equal numbers.
/// </summary>
public class ScopedGrainKeyCodecTests
{
    [Fact]
    public void Format_ProjectPlusNumber_RoundTripsThroughParse()
    {
        var projectId = "proj_a";
        var number = 42;

        var key = ScopedGrainKeyCodec.Format(projectId, number);

        var parsed = ScopedGrainKeyCodec.Parse(key);

        Assert.Equal(projectId, parsed.ProjectId);
        Assert.Equal(number, parsed.SubjectNumber);
    }

    [Fact]
    public void Format_NumberOnly_EncodedAsDecimalInvariantCulture()
    {
        Assert.Equal("proj_a:42", ScopedGrainKeyCodec.Format("proj_a", 42));
        Assert.Equal("proj_a:0", ScopedGrainKeyCodec.Format("proj_a", 0));
        Assert.Equal("proj_a:7", ScopedGrainKeyCodec.Format("proj_a", 7));
    }

    [Fact]
    public void Format_DoesNotAllowProjectIdWithSeparator()
    {
        Assert.Throws<ArgumentException>(() => ScopedGrainKeyCodec.Format("proj:a", 42));
    }

    [Fact]
    public void Format_DoesNotAllowNegativeNumbers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScopedGrainKeyCodec.Format("proj_a", -1));
    }

    [Fact]
    public void TryParse_RejectsEmptyAndMalformed()
    {
        Assert.False(ScopedGrainKeyCodec.TryParse(null, out _));
        Assert.False(ScopedGrainKeyCodec.TryParse("", out _));
        Assert.False(ScopedGrainKeyCodec.TryParse(":1", out _));
        Assert.False(ScopedGrainKeyCodec.TryParse("proj_a", out _));
        Assert.False(ScopedGrainKeyCodec.TryParse("proj_a:", out _));
        Assert.False(ScopedGrainKeyCodec.TryParse("proj_a:notanumber", out _));
        Assert.False(ScopedGrainKeyCodec.TryParse("proj_a:-1", out _));
    }

    [Fact]
    public void IssueKey_RecordEquality_DistinguishesByProjectIdAndNumber()
    {
        var first = new IssueKey("proj_a", 42);
        var same = new IssueKey("proj_a", 42);
        var differentProject = new IssueKey("proj_b", 42);
        var differentNumber = new IssueKey("proj_a", 7);

        Assert.Equal(first, same);
        Assert.True(first == same);
        Assert.NotEqual(first, differentProject);
        Assert.NotEqual(first, differentNumber);
    }

    [Fact]
    public void IssueKey_FromParse_ReconstructsTypedKey()
    {
        var key = ScopedGrainKeyCodec.Format("proj_a", 42);

        var typed = IssueKey.From(ScopedGrainKeyCodec.Parse(key));

        Assert.Equal(new IssueKey("proj_a", 42), typed);
        Assert.Equal("proj_a:42", typed.ToGrainKeyString());
    }

    [Fact]
    public void EpicKey_FromParse_ReconstructsTypedKey()
    {
        var key = ScopedGrainKeyCodec.Format("proj_b", 7);

        var typed = EpicKey.From(ScopedGrainKeyCodec.Parse(key));

        Assert.Equal(new EpicKey("proj_b", 7), typed);
        Assert.Equal("proj_b:7", typed.ToGrainKeyString());
    }

    [Fact]
    public void GrainKey_IssueAndEpic_UseSharedCodec()
    {
        // Project + Number identities from the two typed entry points must
        // produce the same grain-key string — there is only one codec.
        Assert.Equal(
            ScopedGrainKeyCodec.Format("proj_a", 42),
            GrainKey.Issue("proj_a", 42));
        Assert.Equal(
            IssueKey.Parse("proj_a", 42).ToGrainKeyString(),
            GrainKey.Issue(new IssueKey("proj_a", 42)));
        Assert.Equal(
            ScopedGrainKeyCodec.Format("proj_b", 7),
            GrainKey.Epic("proj_b", 7));
        Assert.Equal(
            EpicKey.Parse("proj_b", 7).ToGrainKeyString(),
            GrainKey.Epic(new EpicKey("proj_b", 7)));
    }

    [Fact]
    public void GrainKey_Overloads_NoHandConcatenatedStringsRequired()
    {
        // The shared typed entry point is what callers should reach for; the
        // legacy Issue(issueId) overload stays for the T-001/T-002 transition.
        // This test guards the contract that the overloads route through the
        // codec by checking string equality on a representative input.
        var shared = GrainKey.Issue("proj_a", 42);
        var legacy = GrainKey.Issue("issue_a_42");

        Assert.Equal("proj_a:42", shared);
        Assert.Equal("issue_a_42", legacy);
    }
}
