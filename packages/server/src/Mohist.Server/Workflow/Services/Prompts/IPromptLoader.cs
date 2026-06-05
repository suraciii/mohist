using Mohist.Server.Workflow.Domain.Prompts;

namespace Mohist.Server.Workflow.Services.Prompts;

public interface IPromptLoader
{
    Dictionary<string, string> LoadAll();

    Dictionary<string, SystemTemplate> LoadAllTemplates() =>
        LoadAll().ToDictionary(
            kv => kv.Key,
            kv => new SystemTemplate(kv.Key, kv.Key, string.Empty, Array.Empty<string>(), null, kv.Value),
            StringComparer.Ordinal);
}
