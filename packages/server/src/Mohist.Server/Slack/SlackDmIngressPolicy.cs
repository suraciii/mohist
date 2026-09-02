namespace Mohist.Server.Slack;

/// <summary>
/// Makes deterministic DM ingress decisions that do not require persistence,
/// Orleans, or delivery. The route remains responsible for applying the
/// selected response and side effect.
/// </summary>
internal static class SlackDmIngressPolicy
{
    public static string? EmptyTaskRejectionReason(
        string prompt,
        bool isNewTask,
        string newTaskPrompt,
        int attachmentCount)
    {
        if (attachmentCount > 0)
            return null;
        if (string.IsNullOrWhiteSpace(prompt)
            || isNewTask && string.IsNullOrWhiteSpace(newTaskPrompt))
            return "Please send a task for the Agent to perform.";
        return null;
    }

    public static bool RequiresNewWorkAdmission(bool isNewTask, string? currentSessionId) =>
        isNewTask || string.IsNullOrWhiteSpace(currentSessionId);
}
