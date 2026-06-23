using System.Text.RegularExpressions;
using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.Issue.Domain;

public sealed partial class Issue
{
    private static readonly Regex LabelKeyPattern =
        new("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const string LabelKeyValidationPattern = "^[a-z0-9]([-a-z0-9]*[a-z0-9])?$";

    public void SetLabel(string key, string value, DateTime? now = null)
    {
        ValidateLabelKey(key);
        ValidateLabelValue(value);
        if (_labels.TryGetValue(key, out var current) && string.Equals(current, value, StringComparison.Ordinal)) return;
        var snapshot = SnapshotLabels();
        _labels[key] = value;
        Touch(now);
        RecordLabelsChangeIfDifferent(snapshot);
    }

    public void RemoveLabel(string key, DateTime? now = null)
    {
        ValidateLabelKey(key);
        if (!_labels.ContainsKey(key)) return;
        var snapshot = SnapshotLabels();
        _labels.Remove(key);
        Touch(now);
        RecordLabelsChangeIfDifferent(snapshot);
    }

    public void ReplaceLabels(IReadOnlyDictionary<string, string>? labels, DateTime? now = null)
    {
        ReplaceLabels(labels, recordEvent: true, now: now);
    }

    public void ClearLabels(DateTime? now = null)
    {
        if (_labels.Count == 0) return;
        var snapshot = SnapshotLabels();
        _labels = new Dictionary<string, string>(StringComparer.Ordinal);
        Touch(now);
        RecordLabelsChangeIfDifferent(snapshot);
    }

    public bool LabelsMatch(IReadOnlyDictionary<string, string>? labels)
    {
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        if (labels is not null)
        {
            foreach (var (key, value) in labels)
            {
                ValidateLabelKey(key);
                ValidateLabelValue(value);
                next[key] = value;
            }
        }
        return LabelsEqual(_labels, next);
    }

    public void ReplaceLabelsSilently(IReadOnlyDictionary<string, string>? labels)
    {
        ReplaceLabels(labels, recordEvent: false, now: null);
    }

    private void ReplaceLabels(IReadOnlyDictionary<string, string>? labels, bool recordEvent, DateTime? now = null)
    {
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        if (labels is not null)
        {
            foreach (var (key, value) in labels)
            {
                ValidateLabelKey(key);
                ValidateLabelValue(value);
                next[key] = value;
            }
        }

        var snapshot = SnapshotLabels();
        _labels = next;
        if (LabelsEqual(snapshot, _labels)) return;
        Touch(now);
        if (recordEvent)
        {
            RecordLabelsChangeIfDifferent(snapshot);
        }
    }

    public static void ValidateLabelKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException(
                $"Issue label key is required and must match {LabelKeyValidationPattern}",
                nameof(key));
        if (!LabelKeyPattern.IsMatch(key))
            throw new ArgumentException(
                $"Issue label key '{key}' is invalid; keys must match {LabelKeyValidationPattern} (lowercase alphanumerics with optional interior dashes)",
                nameof(key));
    }

    public static void ValidateLabelValue(string? value)
    {
        if (value is null)
            throw new ArgumentException("Issue label value must be a non-empty string", nameof(value));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Issue label value must be a non-empty, non-whitespace string", nameof(value));
    }

    private IReadOnlyDictionary<string, string> SnapshotLabels() =>
        new Dictionary<string, string>(_labels, StringComparer.Ordinal);

    private void RecordLabelsChangeIfDifferent(IReadOnlyDictionary<string, string> oldLabels)
    {
        if (LabelsEqual(oldLabels, _labels)) return;
        RecordEvent(new IssueLabelsChanged(oldLabels, SnapshotLabels()));
    }

    private static bool LabelsEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other)) return false;
            if (!string.Equals(value, other, StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
