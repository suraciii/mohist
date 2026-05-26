using Mohist.Runner.Actions;

namespace Mohist.Runner;

public class ActionManager
{
    private readonly Dictionary<string, Func<IAction>> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ActionManager> _log;

    public ActionManager(IServiceProvider services, ILogger<ActionManager> log)
    {
        _log = log;
    }

    public void Register(string uses, Func<IAction> factory)
    {
        _actions[uses] = factory;
    }

    public IAction? Resolve(string? uses)
    {
        if (uses is null) return null;

        if (_actions.TryGetValue(uses, out var factory))
            return factory();

        _log.LogWarning("No action registered for '{Uses}', falling back to ProcessHandler", uses);
        return null;
    }

    public bool HasAction(string? uses) =>
        uses is not null && _actions.ContainsKey(uses);
}
