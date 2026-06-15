using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class MohistExperimentIssueWorkflowProfile : MohistIssueWorkflowProfileBase
{
    public const string HardcodedDescription = """
        Exploratory workflow for spikes, prototypes, and proof-of-concept work.
        Suited for: exploring a new idea, validating a technical approach, building throwaway code, or running a time-boxed investigation.
        No deliverable artifacts are required: no proposal, no specs, no design document, no production merge. The working branch can be discarded when the experiment ends.
        Typical duration: 15-45 minutes of agent time, capped by the human who kicks off the run.
        Not suited for: shipping a real change (use mohist/default), or a simple bug fix (use quick-fix).
        """;

    public MohistExperimentIssueWorkflowProfile(
        IPromptLoader promptLoader,
        IDbContextFactory<MohistDbContext> dbFactory)
        : base(promptLoader, dbFactory)
    {
    }

    public override string Id => IssueWorkflowProfiles.ExperimentId;
    public override string DisplayName => "Mohist Experiment";
    public override string Description => HardcodedDescription;
    public override bool IsDefault => false;
}
