namespace Mohist.Server.Workflow.Handlers;

public interface IHandlerRegistry
{
    ITaskHandler? Task(string? uses);
    ICheckHandler? Check(string? uses);
    ITaskLoader? TaskLoader(string? uses);
}

public class HandlerRegistry : IHandlerRegistry
{
    private readonly Dictionary<string, ITaskHandler> _tasks = [];
    private readonly Dictionary<string, ICheckHandler> _checks = [];
    private readonly Dictionary<string, ITaskLoader> _loaders = [];

    public void RegisterTask(string uses, ITaskHandler handler) => _tasks[uses] = handler;
    public void RegisterCheck(string uses, ICheckHandler handler) => _checks[uses] = handler;
    public void RegisterTaskLoader(string uses, ITaskLoader loader) => _loaders[uses] = loader;

    public ITaskHandler? Task(string? uses) => uses is not null ? _tasks.GetValueOrDefault(uses) : null;
    public ICheckHandler? Check(string? uses) => uses is not null ? _checks.GetValueOrDefault(uses) : null;
    public ITaskLoader? TaskLoader(string? uses) => uses is not null ? _loaders.GetValueOrDefault(uses) : null;
}
