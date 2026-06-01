namespace Mohist.Server.Infrastructure.Events;

public interface IProjectScoped
{
    string? ProjectId { get; }
}
