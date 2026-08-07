using System.Reflection;
using Orleans.Runtime;

namespace Mohist.Server.TestSupport;

public sealed record GrainTestContextPair(IGrainContext Context, IGrainRuntime Runtime);

public static class GrainTestContext
{
    public static GrainTestContextPair Create(string key)
    {
        return Create(key, grainFactory: null);
    }

    /// <summary>
    /// Build a manual-grain context pair with an optional <see cref="IGrainFactory"/>
    /// returned by the runtime proxy's <c>get_GrainFactory</c>. When
    /// <paramref name="grainFactory"/> is <c>null</c>, the runtime returns
    /// <c>null</c> for the factory (matching the historical behaviour for
    /// manual-grain specs that do not need a factory). Specs that exercise
    /// grain methods which reach into <c>GrainFactory.GetGrain&lt;...&gt;</c>
    /// (e.g. <c>WorkflowGrain.PersistProfileBindingAsync</c>) MUST pass a
    /// factory that resolves the relevant grain interfaces.
    /// </summary>
    public static GrainTestContextPair Create(string key, IGrainFactory? grainFactory)
    {
        var context = DispatchProxy.Create<IGrainContext, GrainContextProxy>();
        ((GrainContextProxy)(object)context).GrainId = GrainId.Create("test", key);
        var runtime = DispatchProxy.Create<IGrainRuntime, GrainRuntimeProxy>();
        ((GrainRuntimeProxy)(object)runtime).GrainFactory = grainFactory;
        return new GrainTestContextPair(context, runtime);
    }

    private class GrainContextProxy : DispatchProxy
    {
        public GrainId GrainId { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_GrainId")
                return GrainId;
            return DefaultValue(targetMethod?.ReturnType);
        }
    }

    private class GrainRuntimeProxy : DispatchProxy
    {
        public IGrainFactory? GrainFactory { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_GrainFactory")
                return GrainFactory;
            return DefaultValue(targetMethod?.ReturnType);
        }
    }

    private static object? DefaultValue(Type? type) =>
        type is null || type == typeof(void) || !type.IsValueType
            ? null
            : Activator.CreateInstance(type);
}
