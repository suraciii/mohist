using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Server.Infrastructure.Events;

public static class CloudEventBusServiceCollectionExtensions
{
    public static IServiceCollection AddCloudEventBus(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryEventBus>());
        return services;
    }

    public static IServiceCollection AddCloudEventHandlersFromAssembly(
        this IServiceCollection services, Assembly assembly)
        => services.AddCloudEventHandlers(assembly.GetTypes());

    internal static IServiceCollection AddCloudEventHandlers(
        this IServiceCollection services,
        IEnumerable<Type> discoveredTypes)
    {
        var handlerTypes = discoveredTypes
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => t.GetInterfaces().Any(IsCloudEventHandlerInterface))
            .ToList();

        foreach (var handlerType in handlerTypes)
        {
            var attr = handlerType.GetCustomAttribute<SubscriptionAttribute>()
                ?? throw new InvalidOperationException(
                    $"Handler {handlerType.FullName} must have [{nameof(SubscriptionAttribute)}] attribute");
            CloudEventTypeMatcher.ValidatePattern(attr.Type);

            services.AddSingleton(handlerType);

            if (typeof(ICloudEventHandler).IsAssignableFrom(handlerType))
            {
                services.AddSingleton<ICloudEventHandler>(sp =>
                    (ICloudEventHandler)sp.GetRequiredService(handlerType));
            }

            var genericInterface = handlerType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType &&
                                     i.GetGenericTypeDefinition() == typeof(ICloudEventHandler<>));
            if (genericInterface is not null)
            {
                var dataType = genericInterface.GetGenericArguments()[0];
                var typedInterface = typeof(ICloudEventHandler<>).MakeGenericType(dataType);
                services.AddSingleton(typedInterface, sp =>
                    sp.GetRequiredService(handlerType));
            }
        }

        services.AddSingleton<IEnumerable<Subscription>>(sp =>
        {
            var subs = new List<Subscription>();
            foreach (var handlerType in handlerTypes)
            {
                var handler = sp.GetRequiredService(handlerType);
                var attr = handlerType.GetCustomAttribute<SubscriptionAttribute>()!;
                var dispatch = MakeDelegate(handler);
                var identity = attr.Identity
                    ?? handlerType.FullName
                    ?? handlerType.Name;
                subs.Add(new Subscription(attr.Type, handler, dispatch, identity));
            }
            return subs;
        });

        return services;
    }

    private static bool IsCloudEventHandlerInterface(Type i) =>
        i == typeof(ICloudEventHandler)
        || (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICloudEventHandler<>));

    private static DispatchDelegate MakeDelegate(object handler)
    {
        var handlerType = handler.GetType();

        var genericInterface = handlerType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType &&
                                 i.GetGenericTypeDefinition() == typeof(ICloudEventHandler<>));

        if (genericInterface is not null)
        {
            var dataType = genericInterface.GetGenericArguments()[0];
            var method = typeof(CloudEventBusServiceCollectionExtensions).GetMethod(
                nameof(MakeTypedDelegate),
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var genericMethod = method.MakeGenericMethod(dataType);
            return (DispatchDelegate)genericMethod.Invoke(null, null)!;
        }

        return (instance, evt, ct) =>
        {
            var typedHandler = (ICloudEventHandler)instance;
            return typedHandler.Filter(evt)
                ? typedHandler.HandleAsync(evt, ct)
                : Task.CompletedTask;
        };
    }

    private static DispatchDelegate MakeTypedDelegate<TData>() where TData : class
    {
        return (handler, evt, ct) =>
        {
            var data = evt.Data?.Deserialize<TData>(CloudEvent.JsonOptions)
                ?? throw new InvalidOperationException(
                    $"CloudEvent payload for {evt.Type} could not be deserialized to {typeof(TData).Name}");

            var typed = new CloudEvent<TData>(
                evt.Id, evt.Source, evt.Type, evt.Time, data, evt.DataContentType, evt.Subject, evt.SpecVersion, evt.Extensions);

            var typedHandler = (ICloudEventHandler<TData>)handler;
            if (!typedHandler.Filter(typed))
                return Task.CompletedTask;
            return typedHandler.HandleAsync(typed, ct);
        };
    }
}
