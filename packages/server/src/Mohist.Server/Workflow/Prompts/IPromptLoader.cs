using Mohist.Server.Workflow.Prompts.Domain;

namespace Mohist.Server.Workflow.Prompts;

public interface IPromptLoader
{
    Dictionary<string, string> LoadAll();

    Dictionary<string, SystemTemplate> LoadAllTemplates() =>
        LoadAll().ToDictionary(
            kv => kv.Key,
            kv => new SystemTemplate(kv.Key, kv.Key, string.Empty, Array.Empty<string>(), null, kv.Value),
            StringComparer.Ordinal);
}
