using System.Text.RegularExpressions;

namespace Mohist.Server.Project.Domain;

public static partial class ProjectName
{
    public const int MaxLength = 63;

    public static bool TryNormalize(string? value, out string name, out string? error)
    {
        name = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "project name is required";
            return false;
        }

        name = value.Trim().ToLowerInvariant();
        if (name.Length > MaxLength)
        {
            error = $"project name must be at most {MaxLength} characters";
            return false;
        }

        if (!DnsLabelRegex().IsMatch(name))
        {
            error = "project name must be a DNS label: lowercase letters, digits, and hyphens; it must start and end with a letter or digit";
            return false;
        }

        error = null;
        return true;
    }

    public static string NormalizeOrThrow(string value)
    {
        if (TryNormalize(value, out var name, out var error))
            return name;

        throw new InvalidOperationException(error);
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex DnsLabelRegex();
}
