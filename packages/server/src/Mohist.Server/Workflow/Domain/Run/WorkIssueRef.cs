using Orleans;

namespace Mohist.Server.Runner.Grains;

[GenerateSerializer]
public record WorkIssueRef(
    string ProjectId,
    int IssueNumber);
