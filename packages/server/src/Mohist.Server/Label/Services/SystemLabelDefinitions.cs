using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Label.Services;

public class SystemLabelDefinitions : ISingletonService
{
    public IReadOnlyList<LabelDefinition> All { get; }

    public SystemLabelDefinitions()
    {
        All = new List<LabelDefinition>
        {
            new(
                Key: "refactor",
                Description: "Technical refactoring: changing internal code or architecture to reduce complexity, improve comprehensibility, and lower the cost of future change — without changing observable behavior.",
                Origin: LabelOrigin.System)
        };
    }
}
