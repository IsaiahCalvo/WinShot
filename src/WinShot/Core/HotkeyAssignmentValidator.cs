using System.Windows.Controls;

namespace WinShot.Core;

public enum HotkeyAssignmentIssueKind
{
    InvalidGesture,
    DuplicateInWinShot,
    UsedByAnotherApp,
}

public static class HotkeyAssignmentValidator
{
    public sealed record Field(string Label, TextBox Box, string CurrentGesture);

    public sealed record Issue(
        HotkeyAssignmentIssueKind Kind,
        string Gesture,
        IReadOnlyList<TextBox> Boxes,
        IReadOnlyList<string> Labels,
        string Message);

    public sealed record Result(IReadOnlyList<Issue> Issues)
    {
        public bool IsValid => Issues.Count == 0;
    }

    public static Result Validate(
        IEnumerable<Field> fields,
        Func<string, HotkeyAvailabilityStatus> checkAvailability)
    {
        var prepared = fields
            .Select(field => new PreparedField(
                field,
                HotkeyManager.TryNormalizeGesture(field.Box.Text, out string? normalized) ? normalized : null,
                HotkeyManager.TryNormalizeGesture(field.CurrentGesture, out string? current) ? current : null))
            .ToArray();

        var issues = new List<Issue>();
        var blockedBoxes = new HashSet<TextBox>();

        foreach (var item in prepared.Where(item => item.NormalizedGesture is null))
        {
            issues.Add(new Issue(
                HotkeyAssignmentIssueKind.InvalidGesture,
                item.Field.Box.Text.Trim(),
                [item.Field.Box],
                [item.Field.Label],
                "Use a gesture like Ctrl+Shift+1."));
            blockedBoxes.Add(item.Field.Box);
        }

        foreach (var group in prepared
                     .Where(item => item.NormalizedGesture is not null)
                     .GroupBy(item => item.NormalizedGesture!, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var boxes = group.Select(item => item.Field.Box).ToArray();
            var labels = group.Select(item => item.Field.Label).ToArray();
            issues.Add(new Issue(
                HotkeyAssignmentIssueKind.DuplicateInWinShot,
                group.Key,
                boxes,
                labels,
                "Already used by another WinShot action."));
            foreach (var box in boxes)
                blockedBoxes.Add(box);
        }

        var currentWinShotGestures = prepared
            .Where(item => item.CurrentGesture is not null)
            .Select(item => item.CurrentGesture!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in prepared.Where(item =>
                     item.NormalizedGesture is not null &&
                     !blockedBoxes.Contains(item.Field.Box)))
        {
            string gesture = item.NormalizedGesture!;
            if (currentWinShotGestures.Contains(gesture))
                continue;

            HotkeyAvailabilityStatus status = checkAvailability(gesture);
            if (status == HotkeyAvailabilityStatus.Unavailable)
            {
                issues.Add(new Issue(
                    HotkeyAssignmentIssueKind.UsedByAnotherApp,
                    gesture,
                    [item.Field.Box],
                    [item.Field.Label],
                    "Already used by another app."));
                blockedBoxes.Add(item.Field.Box);
            }
            else if (status == HotkeyAvailabilityStatus.Invalid)
            {
                issues.Add(new Issue(
                    HotkeyAssignmentIssueKind.InvalidGesture,
                    gesture,
                    [item.Field.Box],
                    [item.Field.Label],
                    "Use a gesture like Ctrl+Shift+1."));
                blockedBoxes.Add(item.Field.Box);
            }
        }

        return new Result(issues);
    }

    private sealed record PreparedField(Field Field, string? NormalizedGesture, string? CurrentGesture);
}
