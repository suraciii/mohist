using System.Reflection;

namespace Mohist.Server.Infrastructure.Resources;

internal static class BuiltinTextResources
{
    private const string ResourcePrefix = "Mohist.Server.";

    public static string ReadWorkflowProfile(string fileName) =>
        ReadRequired($"Workflow.Services.Profiles.{fileName}");

    private static string ReadRequired(string resourcePath)
    {
        var resourceName = ResourcePrefix + resourcePath;
        var assembly = typeof(BuiltinTextResources).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Built-in text resource '{resourceName}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
