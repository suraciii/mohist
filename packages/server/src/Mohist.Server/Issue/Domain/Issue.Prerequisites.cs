namespace Mohist.Server.Issue.Domain;

public static partial class IssueExtensions
{
    extension(Issue issue)
    {
        public void AddPrerequisite(int prerequisiteNumber)
        {
            if (prerequisiteNumber == issue.Number)
                throw new InvalidOperationException("Issue cannot depend on itself");
            if (issue.PrerequisiteNumbers.Contains(prerequisiteNumber)) return;
            issue.PrerequisiteNumbers = [.. issue.PrerequisiteNumbers, prerequisiteNumber];
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public void RemovePrerequisite(int prerequisiteNumber)
        {
            var next = issue.PrerequisiteNumbers.Where(number => number != prerequisiteNumber).ToArray();
            if (next.Length == issue.PrerequisiteNumbers.Length) return;
            issue.PrerequisiteNumbers = next;
            issue.UpdatedAt = DateTime.UtcNow;
        }
    }
}
