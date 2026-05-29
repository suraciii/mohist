namespace Mohist.Server.Workflow.Prompts;

public interface IPromptLoader
{
    Dictionary<string, string> LoadAll();
}
