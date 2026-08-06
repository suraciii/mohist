using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Slack.Ports;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackOutboundPortTests
{
    [Fact]
    public void Server_removes_the_legacy_wide_Slack_api_client()
    {
        var serverAssembly = typeof(ISlackAppManagementPort).Assembly;

        Assert.Null(serverAssembly.GetType("Mohist.Server.Slack.ISlackApiClient"));
        Assert.NotNull(serverAssembly.GetType(typeof(ISlackConfigurationCredentialPort).FullName!));
        Assert.NotNull(serverAssembly.GetType(typeof(ISlackAppManagementPort).FullName!));
        Assert.NotNull(serverAssembly.GetType(typeof(ISlackBotIdentityVerificationPort).FullName!));
        Assert.NotNull(serverAssembly.GetType(typeof(ISlackMemberIdentityPort).FullName!));
    }

    [Fact]
    public void Server_Slack_boundary_does_not_directly_use_protocol_clients_or_register_Slack_http_clients()
    {
        var surfaceLeaks = SlackBoundaryTypes()
            .Where(type => !IsProductionTransport(type))
            .SelectMany(ReferencedSurfaceTypes)
            .Where(IsDirectProtocolType)
            .Where(type => !IsProductionTransport(type))
            .Select(type => type.FullName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(surfaceLeaks);

        var protocolCalls = SlackBoundaryMethods()
            .Where(method => !IsProductionTransport(method.DeclaringType!))
            .SelectMany(ReferencedMethods)
            .Where(method => IsDirectProtocolCall(method) && !IsProductionTransport(method.DeclaringType!))
            .Select(method => $"{method.DeclaringType?.FullName}.{method.Name}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(protocolCalls);
    }

    [Fact]
    public void Production_adapters_wire_the_ports_through_the_transport_only()
    {
        var serverAssembly = typeof(ISlackAppManagementPort).Assembly;
        var adapterTypes = new[]
        {
            serverAssembly.GetType("Mohist.Server.Infrastructure.Slack.Ports.SlackAppManagementPortAdapter")!,
            serverAssembly.GetType("Mohist.Server.Infrastructure.Slack.Ports.SlackConfigurationCredentialPortAdapter")!,
            serverAssembly.GetType("Mohist.Server.Infrastructure.Slack.Ports.SlackBotIdentityVerificationPortAdapter")!,
            serverAssembly.GetType("Mohist.Server.Infrastructure.Slack.Ports.SlackMemberIdentityPortAdapter")!,
        };
        Assert.Contains(typeof(ISlackAppManagementPort), adapterTypes[0].GetInterfaces());
        Assert.Contains(typeof(ISlackAppManagementFactPort), adapterTypes[0].GetInterfaces());
        Assert.Contains(typeof(ISlackConfigurationCredentialPort), adapterTypes[1].GetInterfaces());
        Assert.Contains(typeof(ISlackBotIdentityVerificationPort), adapterTypes[2].GetInterfaces());
        Assert.Contains(typeof(ISlackMemberIdentityPort), adapterTypes[3].GetInterfaces());

        var transport = serverAssembly.GetType("Mohist.Server.Infrastructure.Slack.Ports.SlackApiTransport")!;
        Assert.Contains(typeof(HttpClient), transport.GetConstructors().SelectMany(ctor => ctor.GetParameters()).Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(adapterTypes, adapter =>
            adapter.GetConstructors().SelectMany(ctor => ctor.GetParameters()).Any(parameter => IsDirectProtocolType(parameter.ParameterType)));
    }

    [Fact]
    public async Task ConfigurationCredentialPort_ReceivesOnlyTheCredentialPair()
    {
        var port = new FakeSlackConfigurationCredentialPort();
        port.Enqueue(new(SlackConfigurationCredentialRotationOutcome.Succeeded, new("next-access", "next-refresh"), "T123", DateTimeOffset.UnixEpoch.AddHours(1)));

        var result = await port.RotateAsync(new("access", "refresh"));

        Assert.Equal(SlackConfigurationCredentialRotationOutcome.Succeeded, result.Outcome);
        Assert.Equal([new SlackConfigurationCredentialPair("access", "refresh")], port.Requests);
    }

    [Fact]
    public async Task AppManagementPort_UsesManifestRequestsForValidationUpdateAndExport()
    {
        var port = new FakeSlackAppManagementPort();
        var app = new SlackAppManagementRequest("enrollment", "child", "T123", "A123");
        var manifest = new SlackManifest(2, "{}", "hash");
        port.SetResponse("child", new(Export: new(SlackAppManagementFactOutcome.Present, "{}")));

        await port.ValidateManifestAsync(new(app, manifest));
        await port.UpdateManifestAsync(new(app, manifest));
        var exported = await port.ExportManifestAsync(app);

        Assert.Equal(2, port.ManifestCalls);
        Assert.Equal(SlackAppManagementFactOutcome.Present, exported.Outcome);
    }

    [Fact]
    public async Task BotIdentityVerificationPort_ReceivesOnlyCandidateBotToken()
    {
        var port = new FakeSlackBotIdentityVerificationPort
        {
            Result = new(true, "T123", "U123", "A123", new HashSet<string>(["chat:write"]))
        };

        var result = await port.VerifyAsync(new("xoxb-candidate"));

        Assert.True(result.Verified);
        Assert.Equal([new SlackBotIdentityVerificationRequest("xoxb-candidate")], port.Requests);
    }

    [Fact]
    public void Named_non_generic_AddHttpClient_is_flagged_as_a_direct_protocol_leak()
        => Assert.True(HasDirectProtocolCall(typeof(NamedHttpClientBypass), nameof(NamedHttpClientBypass.Register)));

    [Fact]
    public void IHttpClientFactory_CreateClient_is_flagged_as_a_direct_protocol_leak()
    {
        Assert.True(IsDirectProtocolType(typeof(IHttpClientFactory)));
        Assert.True(HasDirectProtocolCall(typeof(HttpClientFactoryBypass), nameof(HttpClientFactoryBypass.Resolve)));
    }

    [Fact]
    public void Generic_Slack_typed_HttpClient_is_flagged_as_a_direct_protocol_leak()
        => Assert.True(HasDirectProtocolCall(typeof(GenericTypedHttpClientBypass), nameof(GenericTypedHttpClientBypass.Register)));

    [Fact]
    public void An_external_Slack_sdk_type_is_direct_protocol_but_server_ports_and_fakes_are_not()
    {
        Assert.True(IsDirectProtocolType(typeof(ExternalSlackSdkProbe)));
        Assert.False(IsDirectProtocolType(typeof(ISlackConfigurationCredentialPort)));
        Assert.False(IsDirectProtocolType(typeof(FakeSlackConfigurationCredentialPort)));
    }

    private static bool HasDirectProtocolCall(Type type, string methodName)
    {
        var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return method is not null && ReferencedMethods(method).Any(IsDirectProtocolCall);
    }

    private sealed class NamedHttpClientBypass
    {
        public void Register(IServiceCollection services) => services.AddHttpClient("Slack");
    }

    private sealed class HttpClientFactoryBypass
    {
        public void Resolve(IHttpClientFactory factory) => factory.CreateClient("Slack");
    }

    private sealed class SlackApiClient;

    private sealed class GenericTypedHttpClientBypass
    {
        public void Register(IServiceCollection services) => services.AddHttpClient<SlackApiClient>();
    }

    private sealed class ExternalSlackSdkProbe;

    private static bool IsProductionTransport(Type type) =>
        type == typeof(SlackApiTransport)
        || type.DeclaringType == typeof(SlackApiTransport);

    private static IEnumerable<Type> SlackBoundaryTypes() => typeof(ISlackAppManagementPort).Assembly
        .GetTypes()
        .Where(type => type == typeof(MohistServiceRegistration)
            || type.Namespace?.StartsWith("Mohist.Server.Slack", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("Mohist.Server.Infrastructure.Slack", StringComparison.Ordinal) == true);

    private static IEnumerable<MethodBase> SlackBoundaryMethods() => SlackBoundaryTypes()
        .SelectMany(type => type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Cast<MethodBase>()
            .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)));

    private static IEnumerable<Type> ReferencedSurfaceTypes(Type type)
    {
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var referenced in Expand(field.FieldType))
                yield return referenced;

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var referenced in Expand(property.PropertyType))
                yield return referenced;
            foreach (var parameter in property.GetIndexParameters())
                foreach (var referenced in Expand(parameter.ParameterType))
                    yield return referenced;
        }

        foreach (var method in type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .Cast<MethodBase>()
                     .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)))
        {
            if (method is MethodInfo methodInfo)
                foreach (var referenced in Expand(methodInfo.ReturnType))
                    yield return referenced;
            foreach (var parameter in method.GetParameters())
                foreach (var referenced in Expand(parameter.ParameterType))
                    yield return referenced;
        }
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.HasElementType)
        {
            yield return type.GetElementType()!;
            yield break;
        }

        yield return type;
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                foreach (var referenced in Expand(argument))
                    yield return referenced;
    }

    private static bool IsDirectProtocolType(Type type)
    {
        if (type == typeof(HttpClient)
            || type == typeof(WebSocket)
            || type == typeof(IHttpClientFactory)
            || type.Namespace?.StartsWith("System.Net.Http", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("System.Net.WebSockets", StringComparison.Ordinal) == true)
            return true;

        return IsExternalSlackClient(type);
    }

    // The three outbound ports and their fakes live in the server assembly; any other type
    // whose name or namespace mentions Slack is an external SDK dependency the boundary must
    // not reach directly.
    private static bool IsExternalSlackClient(Type type)
    {
        if (type.Assembly == typeof(ISlackAppManagementPort).Assembly)
            return false;

        return type.Name.Contains("Slack", StringComparison.Ordinal)
            || (type.Namespace?.Contains("Slack", StringComparison.Ordinal) ?? false);
    }

    private static IEnumerable<MethodBase> ReferencedMethods(MethodBase method)
    {
        var body = method.GetMethodBody()?.GetILAsByteArray();
        if (body is null)
            yield break;

        for (var offset = 0; offset < body.Length;)
        {
            var opcode = ReadOpcode(body, ref offset);
            if (opcode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(body, offset);
                MethodBase? referenced = null;
                try
                {
                    referenced = method.Module.ResolveMethod(
                        token,
                        method.DeclaringType?.GetGenericArguments(),
                        method is MethodInfo methodInfo ? methodInfo.GetGenericArguments() : null);
                }
                catch (ArgumentException)
                {
                }

                if (referenced is not null)
                    yield return referenced;
            }

            offset += OperandSize(opcode.OperandType, body, offset);
        }
    }

    private static bool IsDirectProtocolCall(MethodBase method)
    {
        if (method.DeclaringType is { } declaringType && IsDirectProtocolType(declaringType))
            return true;

        if (method.DeclaringType?.Name != "HttpClientFactoryServiceCollectionExtensions"
            || method.Name != "AddHttpClient")
            return false;

        if (method.IsGenericMethod)
        {
            var argument = method.GetGenericArguments().FirstOrDefault();
            return argument is not null
                && argument != typeof(SlackApiTransport)
                && argument.Name.Contains("Slack", StringComparison.Ordinal);
        }

        // Named overload AddHttpClient("Slack", ...): the client name is a string operand the
        // method token cannot expose, and the boundary must not register a named HTTP client.
        return true;
    }

    private static OpCode ReadOpcode(byte[] body, ref int offset)
    {
        ushort value = body[offset++];
        if (value == 0xfe)
            value = (ushort)(0xfe00 | body[offset++]);
        return Opcodes[value];
    }

    private static int OperandSize(OperandType operandType, byte[] body, int offset) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(body, offset) * 4),
        _ => throw new ArgumentOutOfRangeException(nameof(operandType)),
    };

    private static IReadOnlyDictionary<ushort, OpCode> Opcodes { get; } = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => unchecked((ushort)opcode.Value));
}
