using Orleans;

namespace Mohist.Server.Workspace.Domain;

[Serializable]
[GenerateSerializer]
public sealed class WorkspaceDomainException : InvalidOperationException
{
    public WorkspaceDomainException(string code, string message, string? hint = null)
        : base(message)
    {
        Code = code;
        Hint = hint;
    }

    [Id(0)]
    public string Code { get; }

    [Id(1)]
    public string? Hint { get; }
}
