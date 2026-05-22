using System.Text.Json;

namespace Mohist.Server.Workflow.Domain.Run;

public class StageCheck
{
    public string Name { get; }
    public string Title { get; }
    public string? Uses { get; }
    public Dictionary<string, JsonElement?>? WithInput { get; }
    public CheckRunStatus Status { get; private set; } = CheckRunStatus.Pending;
    public string? Message { get; set; }
    public JsonElement? Output { get; set; }

    public StageCheck(string name, string title, string? uses = null, Dictionary<string, JsonElement?>? withInput = null)
    {
        Name = name;
        Title = title;
        Uses = uses;
        WithInput = withInput;
    }

    public void Reset()
    {
        Status = CheckRunStatus.Pending;
        Message = null;
        Output = null;
    }

    public void Pass() => Status = CheckRunStatus.Passed;
    public void Fail() => Status = CheckRunStatus.Failed;
}
