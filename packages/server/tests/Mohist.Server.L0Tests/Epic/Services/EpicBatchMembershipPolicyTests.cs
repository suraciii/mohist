using Mohist.Server.Epic.Services;
using Mohist.Server.Issue.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Epic.Services;

public sealed class EpicBatchMembershipPolicyTests
{
    [Fact]
    public void Resolve_PreservesRequestOrderAndSendsEachKnownIssueOnce()
    {
        var request = EpicBatchMembershipPolicy.Resolve(
            [Issue(10), Issue(20)],
            [20, 999, 20, 10]);

        Assert.Equal(["20", "999", "20", "10"], request.RequestedIdentifiers);
        Assert.Equal([20, 10], request.ResolvedItems.Select(item => item.IssueNumber));
        Assert.Equal(0, request.ByIdentifier["999"].IssueNumber);
    }

    [Fact]
    public void Merge_LinkMapsUnknownAndDuplicateOutcomes()
    {
        var request = EpicBatchMembershipPolicy.Resolve([Issue(10)], [999, 10, 10]);

        var results = EpicBatchMembershipPolicy.Merge(
            request,
            [BatchMembershipOutcome.Linked("10", 10, 7, "Target")],
            isUnlink: false);

        Assert.Equal(["not-found", "linked", "already-linked"], results.Select(result => result.Status));
        Assert.Equal(["999", "10", "10"], results.Select(result => result.Identifier));
        Assert.Equal(7, results[2].OwningEpicNumber);
    }

    [Fact]
    public void Merge_LinkPreservesConflictForDuplicate()
    {
        var request = EpicBatchMembershipPolicy.Resolve([Issue(10)], [10, 10]);

        var results = EpicBatchMembershipPolicy.Merge(
            request,
            [BatchMembershipOutcome.Conflict("10", 10, 4, "Existing")],
            isUnlink: false);

        Assert.All(results, result => Assert.Equal("conflict", result.Status));
        Assert.All(results, result => Assert.Equal(4, result.OwningEpicNumber));
    }

    [Fact]
    public void Merge_UnlinkIsIdempotentForUnknownMissingAndDuplicateItems()
    {
        var request = EpicBatchMembershipPolicy.Resolve(
            [Issue(10), Issue(20)],
            [999, 10, 10, 20]);

        var results = EpicBatchMembershipPolicy.Merge(
            request,
            [BatchMembershipOutcome.Unlinked("10", 10)],
            isUnlink: true);

        Assert.Equal(
            ["was-not-a-member", "unlinked", "was-not-a-member", "was-not-a-member"],
            results.Select(result => result.Status));
        Assert.Equal(["999", "10", "10", "20"], results.Select(result => result.Identifier));
    }

    private static IssueReadModel Issue(int number) => new() { Number = number };
}
