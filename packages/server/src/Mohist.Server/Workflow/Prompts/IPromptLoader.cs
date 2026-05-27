namespace Mohist.Server.Workflow.Prompts;

public interface IPromptLoader
{
    string Load(string name);
    Dictionary<string, string> LoadAll();
}
