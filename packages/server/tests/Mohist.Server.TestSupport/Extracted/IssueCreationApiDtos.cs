namespace Mohist.Server.TestSupport;

internal sealed record ApiEnvelope<T>(bool Success, T? Data, string? Error = null, string? Code = null);

internal sealed record CreateIssueApiDto(
        int Number,
        string Title,
        int[] PrerequisiteNumbers,
        CreateIssueApiPrerequisiteDto[] Prereq,
        bool CanStart,
        CreateIssueApiBlockerDto? Blocker);

internal sealed record CreateIssueApiPrerequisiteDto(
        int Number,
        string Title,
        string Status,
        string Health,
        bool Completed);

internal sealed record CreateIssueApiBlockerDto(string Kind, CreateIssueApiBlockerIssueDto? Issue);

internal sealed record CreateIssueApiBlockerIssueDto(int Number, string Title);
