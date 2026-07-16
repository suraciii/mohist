using Orleans;

namespace Mohist.Server.Workflow.Domain.Run;

[GenerateSerializer]
public record WorkIssueRef(
    string ProjectId,
    int IssueNumber);
