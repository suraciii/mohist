using System.Globalization;

namespace Mohist.Server.Infrastructure.Orleans;

/// <summary>
/// Lossless decode of a scoped grain-key string into its Project and
/// number components. Used by <see cref="ScopedGrainKeyCodec"/>.
/// </summary>
public readonly record struct GrainKeyString(string ProjectId, int SubjectNumber)
{
    public override string ToString() => ScopedGrainKeyCodec.Format(ProjectId, SubjectNumber);
}

/// <summary>
/// Project-scoped identity for an Issue. The number is permanent within its
/// Project; the (ProjectId, IssueNumber) pair is the canonical identity.
/// </summary>
public readonly record struct IssueKey(string ProjectId, int IssueNumber)
{
    public static IssueKey Parse(string projectId, int issueNumber) => new(projectId, issueNumber);

    public static IssueKey From(GrainKeyString parsed) =>
        new(parsed.ProjectId, parsed.SubjectNumber);

    public string ToGrainKeyString() =>
        ScopedGrainKeyCodec.Format(ProjectId, IssueNumber);

    public override string ToString() => ToGrainKeyString();
}

/// <summary>
/// Project-scoped identity for an Epic. The number is permanent within its
/// Project; the (ProjectId, EpicNumber) pair is the canonical identity.
/// </summary>
public readonly record struct EpicKey(string ProjectId, int EpicNumber)
{
    public static EpicKey Parse(string projectId, int epicNumber) => new(projectId, epicNumber);

    public static EpicKey From(GrainKeyString parsed) =>
        new(parsed.ProjectId, parsed.SubjectNumber);

    public string ToGrainKeyString() =>
        ScopedGrainKeyCodec.Format(ProjectId, EpicNumber);

    public override string ToString() => ToGrainKeyString();
}

/// <summary>
/// Lossless codec between typed Project-scoped keys and the Orleans
/// grain-key string format. Callers MUST go through this codec — hand-built
/// "project:number" strings are not allowed (see <see cref="GrainKey"/>).
/// </summary>
public static class ScopedGrainKeyCodec
{
    public const char Separator = ':';

    /// <summary>
    /// Format the canonical Project-scoped grain key for a typed identity.
    /// </summary>
    public static string Format(string projectId, int number)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        if (number < 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Scoped number must be non-negative.");
        ValidateProjectId(projectId);

        // Allocate the exact final length (projectId + ":" + number digits)
        // to avoid trailing NUL padding that comes from over-allocating the
        // shared intermediate buffer.
        var numberDigits = NumberDigits(number);
        var totalLength = projectId.Length + 1 + numberDigits;
        return string.Create(totalLength, (ProjectId: projectId, Number: number),
            static (span, state) =>
            {
                state.ProjectId.AsSpan().CopyTo(span);
                var pos = state.ProjectId.Length;
                span[pos++] = Separator;
                if (!state.Number.TryFormat(span[pos..], out _, default, CultureInfo.InvariantCulture))
                    throw new InvalidOperationException("Unable to format scoped grain-key number.");
            });
    }

    private static int NumberDigits(int value)
    {
        if (value == 0) return 1;
        var digits = 0;
        var v = value;
        while (v > 0)
        {
            digits++;
            v /= 10;
        }
        return digits;
    }

    public static GrainKeyString Parse(string grainKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(grainKey);
        var separatorIndex = grainKey.IndexOf(Separator);
        if (separatorIndex <= 0 || separatorIndex == grainKey.Length - 1)
            throw new FormatException($"Scoped grain key '{grainKey}' must be 'ProjectId{Separator}Number'.");

        var projectId = grainKey[..separatorIndex];
        var numberSpan = grainKey.AsSpan()[(separatorIndex + 1)..];
        if (!int.TryParse(numberSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0)
            throw new FormatException($"Scoped grain key '{grainKey}' has an invalid number segment.");

        return new GrainKeyString(projectId, number);
    }

    public static bool TryParse(string? grainKey, out GrainKeyString parsed)
    {
        if (string.IsNullOrEmpty(grainKey))
        {
            parsed = default;
            return false;
        }
        var separatorIndex = grainKey.IndexOf(Separator);
        if (separatorIndex <= 0 || separatorIndex == grainKey.Length - 1)
        {
            parsed = default;
            return false;
        }
        var projectId = grainKey[..separatorIndex];
        var numberSpan = grainKey.AsSpan()[(separatorIndex + 1)..];
        if (!int.TryParse(numberSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            parsed = default;
            return false;
        }
        parsed = new GrainKeyString(projectId, number);
        return true;
    }

    private static void ValidateProjectId(string projectId)
    {
        if (projectId.Contains(Separator))
            throw new ArgumentException(
                $"ProjectId '{projectId}' must not contain the scoped grain-key separator '{Separator}'.",
                nameof(projectId));
    }
}
