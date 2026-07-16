using System.Globalization;

namespace Mohist.Server.Infrastructure.Orleans;

public static class ScopedGrainKeyCodec
{
    public const char Separator = ':';

    public static string Format(string projectId, int number)
    {
        ValidateProjectId(projectId);
        ValidateNumber(number);

        var digits = NumberDigits(number);
        var totalLength = projectId.Length + 1 + digits;
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

    public static bool TryParse(string? grainKey, out string projectId, out int number)
    {
        projectId = string.Empty;
        number = 0;

        if (string.IsNullOrEmpty(grainKey))
            return false;

        var separatorIndex = grainKey.IndexOf(Separator);
        if (separatorIndex <= 0 || separatorIndex == grainKey.Length - 1)
            return false;

        if (grainKey.IndexOf(Separator, separatorIndex + 1) >= 0)
            return false;

        var projectIdValue = grainKey[..separatorIndex];
        if (string.IsNullOrWhiteSpace(projectIdValue) || projectIdValue.Contains(Separator))
            return false;

        var numberSpan = grainKey.AsSpan()[(separatorIndex + 1)..];
        if (numberSpan.IsEmpty || numberSpan.Contains(Separator))
            return false;
        if (numberSpan.Length > 1 && numberSpan[0] == '0')
            return false;
        foreach (var ch in numberSpan)
        {
            if (ch is < '0' or > '9')
                return false;
        }
        if (!int.TryParse(numberSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedNumber))
            return false;
        if (parsedNumber <= 0)
            return false;

        projectId = projectIdValue;
        number = parsedNumber;
        return true;
    }

    public static void Parse(string grainKey, out string projectId, out int number)
    {
        if (!TryParse(grainKey, out projectId, out number))
            throw new FormatException(
                $"Scoped grain key '{grainKey}' must be 'ProjectId{Separator}Number' with a positive number and no other separators.");
    }

    private static int NumberDigits(int value)
    {
        var digits = 0;
        var remaining = value;
        while (remaining > 0)
        {
            digits++;
            remaining /= 10;
        }
        return digits == 0 ? 1 : digits;
    }

    private static void ValidateProjectId(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required and must not be blank.", nameof(projectId));
        if (projectId.Contains(Separator))
            throw new ArgumentException(
                $"ProjectId must not contain the scoped grain-key separator '{Separator}'.",
                nameof(projectId));
    }

    private static void ValidateNumber(int number)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), number,
                "Scoped grain-key number must be strictly positive.");
    }
}
