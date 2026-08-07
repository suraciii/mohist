using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

/// <summary>
/// Issue-327 T-002 / design D4: locks in the consolidated
/// <see cref="AgentSessionContextRefs.TryBuild"/> parser — the single
/// construction site for the four-label context-reference envelope
/// (issue-number / epic-number / repository / workspace-name) with the
/// null-when-all-empty invariant previously duplicated in
/// <see cref="AgentSessionQuerier.BuildAgentSessionListContextRefs"/>
/// and
/// <see cref="AgentSessionQuerier.BuildGenericSessionSummaryContextRefs"/>.
/// Round-trips both wrapper call sites to prove the wire-format DTOs
/// (<see cref="AgentSessionListContextRefsDto"/>,
/// <see cref="GenericAgentSessionSummaryContextRefsDto"/>) are
/// byte-identical to the pre-consolidation values.
/// </summary>
public sealed class AgentSessionContextRefsTests
{
    [Fact]
    public void TryBuild_AllLabelsPopulated_ReturnsAllFields()
    {
        var record = BuildRecord(issueNumber: "42", epic: "7", repo: "owner/repo", workspace: "/work/agent");

        var result = AgentSessionContextRefs.TryBuild(record);

        Assert.NotNull(result);
        Assert.Equal(42, result!.Value.IssueNumber);
        Assert.Equal(7, result.Value.EpicNumber);
        Assert.Equal("owner/repo", result.Value.Repository);
        Assert.Equal("/work/agent", result.Value.WorkspaceName);
    }

    [Fact]
    public void TryBuild_IssueNumberNonNumeric_IssueNumberIsNull()
    {
        var record = BuildRecord(issueNumber: "not-a-number", epic: "7", repo: "owner/repo", workspace: "/work");

        var result = AgentSessionContextRefs.TryBuild(record);

        Assert.NotNull(result);
        Assert.Null(result!.Value.IssueNumber);
        Assert.Equal(7, result.Value.EpicNumber);
        Assert.Equal("owner/repo", result.Value.Repository);
        Assert.Equal("/work", result.Value.WorkspaceName);
    }

    [Fact]
    public void TryBuild_EpicOnly_ReturnsEpicAndOmitsOther()
    {
        var record = BuildRecord(epic: "9");

        var result = AgentSessionContextRefs.TryBuild(record);

        Assert.NotNull(result);
        Assert.Null(result!.Value.IssueNumber);
        Assert.Equal(9, result.Value.EpicNumber);
        Assert.Null(result.Value.Repository);
        Assert.Null(result.Value.WorkspaceName);
    }

    [Fact]
    public void TryBuild_AllLabelsAbsent_ReturnsNull()
    {
        var record = BuildRecord();

        var result = AgentSessionContextRefs.TryBuild(record);

        Assert.Null(result);
    }

    [Fact]
    public void TryBuild_AllLabelsBlankOrWhitespace_ReturnsNull()
    {
        var record = BuildRecord(issueNumber: "  ", epic: "", repo: null, workspace: "\t");

        var result = AgentSessionContextRefs.TryBuild(record);

        Assert.Null(result);
    }

    [Fact]
    public void TryBuild_NonPositiveEpicNumber_OmitsEpicContext()
    {
        var record = BuildRecord(epic: "0", repo: "owner/repo");

        var result = AgentSessionContextRefs.TryBuild(record);

        Assert.NotNull(result);
        Assert.Null(result!.Value.EpicNumber);
        Assert.Equal("owner/repo", result.Value.Repository);
    }

    [Fact]
    public void TryBuild_LabelsOnlyOnMetadata_FallbackResolvesFields()
    {
        var record = BuildRecordOnMetadata(issueNumber: "11", epic: "3", repo: "owner/metarepo", workspace: "/work/meta");

        var result = AgentSessionContextRefs.TryBuild(record);

        Assert.NotNull(result);
        Assert.Equal(11, result!.Value.IssueNumber);
        Assert.Equal(3, result.Value.EpicNumber);
        Assert.Equal("owner/metarepo", result.Value.Repository);
        Assert.Equal("/work/meta", result.Value.WorkspaceName);
    }

    [Fact]
    public void TryBuild_NoLabelsAnywhere_ReturnsNull()
    {
        var record = new AgentSessionRecord(
            new AgentSessionRow(),
            new AgentSession { Id = "s_meta", Runtime = new AgentSessionRuntime("r", null) },
            new Dictionary<string, string>(StringComparer.Ordinal));

        var result = AgentSessionContextRefs.TryBuild(record);

        Assert.Null(result);
    }

    [Fact]
    public void TryBuild_RoundTripsThroughListDto_WireFormatIdentical()
    {
        var record = BuildRecord(issueNumber: "42", epic: "7", repo: "owner/repo", workspace: "/work/agent");

        var refs = AgentSessionContextRefs.TryBuild(record);
        var dto = refs is null
            ? null
            : new AgentSessionListContextRefsDto(refs.Value.IssueNumber, refs.Value.EpicNumber, refs.Value.Repository, refs.Value.WorkspaceName, refs.Value.WorkspacePath);

        Assert.NotNull(dto);
        Assert.Equal(42, dto!.IssueNumber);
        Assert.Equal(7, dto.EpicNumber);
        Assert.Equal("owner/repo", dto.Repository);
        Assert.Equal("/work/agent", dto.WorkspaceName);
    }

    [Fact]
    public void TryBuild_RoundTripsThroughGenericSummaryDto_WireFormatIdentical()
    {
        var record = BuildRecord(issueNumber: "42", epic: "7", repo: "owner/repo", workspace: "/work/agent");

        var refs = AgentSessionContextRefs.TryBuild(record);
        var dto = refs is null
            ? null
            : new GenericAgentSessionSummaryContextRefsDto(refs.Value.IssueNumber, refs.Value.EpicNumber, refs.Value.Repository, refs.Value.WorkspaceName, refs.Value.WorkspacePath);

        Assert.NotNull(dto);
        Assert.Equal(42, dto!.IssueNumber);
        Assert.Equal(7, dto.EpicNumber);
        Assert.Equal("owner/repo", dto.Repository);
        Assert.Equal("/work/agent", dto.WorkspaceName);
    }

    [Fact]
    public void TryBuild_NullResultAtBothCallSites_EnvelopeIsNull()
    {
        var record = BuildRecord();

        var listRefs = AgentSessionContextRefs.TryBuild(record);
        var listDto = listRefs is null
            ? null
            : new AgentSessionListContextRefsDto(listRefs.Value.IssueNumber, listRefs.Value.EpicNumber, listRefs.Value.Repository, listRefs.Value.WorkspaceName, listRefs.Value.WorkspacePath);

        var summaryRefs = AgentSessionContextRefs.TryBuild(record);
        var summaryDto = summaryRefs is null
            ? null
            : new GenericAgentSessionSummaryContextRefsDto(summaryRefs.Value.IssueNumber, summaryRefs.Value.EpicNumber, summaryRefs.Value.Repository, summaryRefs.Value.WorkspaceName, summaryRefs.Value.WorkspacePath);

        Assert.Null(listDto);
        Assert.Null(summaryDto);
    }

    private static AgentSessionRecord BuildRecord(
        string? issueNumber = null,
        string? epic = null,
        string? repo = null,
        string? workspace = null)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (issueNumber is not null) labels[GenericAgentSessionMetadata.IssueNumber] = issueNumber;
        if (epic is not null) labels[GenericAgentSessionMetadata.EpicNumber] = epic;
        if (repo is not null) labels[GenericAgentSessionMetadata.Repository] = repo;
        if (workspace is not null) labels[GenericAgentSessionMetadata.WorkspaceName] = workspace;
        return new AgentSessionRecord(
            new AgentSessionRow(),
            new AgentSession { Id = "s_test", Runtime = new AgentSessionRuntime("r", null) },
            labels);
    }

    private static AgentSessionRecord BuildRecordOnMetadata(
        string? issueNumber,
        string? epic,
        string? repo,
        string? workspace)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (issueNumber is not null) labels[GenericAgentSessionMetadata.IssueNumber] = issueNumber;
        if (epic is not null) labels[GenericAgentSessionMetadata.EpicNumber] = epic;
        if (repo is not null) labels[GenericAgentSessionMetadata.Repository] = repo;
        if (workspace is not null) labels[GenericAgentSessionMetadata.WorkspaceName] = workspace;
        return new AgentSessionRecord(
            new AgentSessionRow(),
            new AgentSession
            {
                Id = "s_meta",
                Runtime = new AgentSessionRuntime("r", null),
                Metadata = new AgentSessionMetadata(labels, null),
            },
            new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
