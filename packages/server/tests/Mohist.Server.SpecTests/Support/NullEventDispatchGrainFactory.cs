using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Support;

internal sealed class NullEventDispatchGrainFactory : IGrainFactory
{
    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
    {
        if (typeof(TGrainInterface) == typeof(IEventDispatcherGrain))
            return (TGrainInterface)(object)new NullEventDispatcherGrain();
        throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");
    }

    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix)
        => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix)
        => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
        => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
        => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

    TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
        => throw new NotSupportedException();

    void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
        => throw new NotSupportedException();

    IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
        => throw new NotSupportedException();

    IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey)
        => throw new NotSupportedException();

    IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey)
        => throw new NotSupportedException();

    IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
        => throw new NotSupportedException();

    IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
        => throw new NotSupportedException();

    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
        => throw new NotSupportedException();

    IAddressable IGrainFactory.GetGrain(GrainId grainId)
        => throw new NotSupportedException();

    IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
        => throw new NotSupportedException();

    IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix)
        => throw new NotSupportedException();

    IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey)
        => throw new NotSupportedException();
}

/// <summary>
/// Drop-in <see cref="IEventDispatcherGrain"/> reference whose
/// <see cref="DispatchNowAsync"/> returns <see cref="Task.CompletedTask"/>.
/// Lets the post-commit poke fire without an Orleans silo.
/// </summary>
internal sealed class NullEventDispatcherGrain : IGrainWithStringKey, IEventDispatcherGrain
{
    public Task DispatchNowAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default) =>
        Task.FromResult(new DeadLetterRedeliveryResult(false, false, 0, "null grain"));

    public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;

    public GrainId GrainId => default;
    public string Key => string.Empty;
}

