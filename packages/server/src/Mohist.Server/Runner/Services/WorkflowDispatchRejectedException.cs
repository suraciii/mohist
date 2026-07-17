namespace Mohist.Server.Runner.Services;

internal sealed class WorkflowDispatchRejectedException(string message) : Exception(message);
