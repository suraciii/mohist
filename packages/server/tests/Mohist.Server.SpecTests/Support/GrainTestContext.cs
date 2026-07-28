using System.Reflection;
using Orleans.Runtime;

namespace Mohist.Server.SpecTests.Support;

public sealed record GrainTestContextPair(IGrainContext Context, IGrainRuntime Runtime);

public static class GrainTestContext
{
    public static GrainTestContextPair Create(string key)
    {
        var context = DispatchProxy.Create<IGrainContext, GrainContextProxy>();
        ((GrainContextProxy)(object)context).GrainId = GrainId.Create("test", key);
        var runtime = DispatchProxy.Create<IGrainRuntime, GrainRuntimeProxy>();
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
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            DefaultValue(targetMethod?.ReturnType);
    }

    private static object? DefaultValue(Type? type) =>
        type is null || type == typeof(void) || !type.IsValueType
            ? null
            : Activator.CreateInstance(type);
}
