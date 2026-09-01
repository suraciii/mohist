using System.Text;

namespace Mohist.Server.Infrastructure;

public static class ProjectVerificationCommand
{
    public const int MaxUtf8Bytes = 4096;

    public static string? Validate(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "verification command is required";
        if (command.IndexOf('\0') >= 0)
            return "verification command must not contain NUL characters";
        if (Encoding.UTF8.GetByteCount(command) > MaxUtf8Bytes)
            return $"verification command must be no more than {MaxUtf8Bytes} UTF-8 bytes";
        return null;
    }

    public static string Require(string? command)
    {
        var error = Validate(command);
        if (error is not null)
            throw new ArgumentException(error, nameof(command));
        return command!;
    }
}
