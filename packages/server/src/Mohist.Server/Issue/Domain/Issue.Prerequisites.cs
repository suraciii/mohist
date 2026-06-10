using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.Issue.Domain;

public sealed partial class Issue
{
    public void AddPrerequisite(int prerequisiteNumber, DateTime? now = null)
    {
        if (prerequisiteNumber == Number)
            throw new ArgumentException("Issue cannot depend on itself");
        if (_prerequisiteNumbers.Contains(prerequisiteNumber)) return;
        _prerequisiteNumbers = [.. _prerequisiteNumbers, prerequisiteNumber];
        Touch(now);
        RecordEvent(new IssuePrerequisiteAdded(prerequisiteNumber));
    }

    public void RemovePrerequisite(int prerequisiteNumber, DateTime? now = null)
    {
        var next = _prerequisiteNumbers.Where(number => number != prerequisiteNumber).ToArray();
        if (next.Length == _prerequisiteNumbers.Length) return;
        _prerequisiteNumbers = next;
        Touch(now);
        RecordEvent(new IssuePrerequisiteRemoved(prerequisiteNumber));
    }
}
