using Mohist.Server.Workflow.Domain.Definition;

namespace Mohist.Server.Workflow.Domain.Run;

public sealed record WorkflowRunProfile(string Id, WorkflowDefinition Definition);
