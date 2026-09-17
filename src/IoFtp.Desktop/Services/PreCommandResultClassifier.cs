using System.Text.RegularExpressions;

namespace IoFtp.Desktop.Services;

internal enum PreCommandOutcome
{
    Success,
    Dupe,
    Failed
}

internal static class PreCommandResultClassifier
{
    private static readonly Regex DupePattern = new(
        @"(?:\b(?:already|previously)\b.{0,60}\b(?:pre(?:'d|ed)?|exist(?:s|ed)?|found|duplicate|dupe)\b|" +
        @"\b(?:pre(?:'d|ed)?|duplicate|dupe)\b.{0,60}\b(?:already|previously|exist(?:s|ed)?)\b|" +
        @"\brelease\b.{0,30}\b(?:already exists|is a dupe)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex FailurePattern = new(
        @"\b(?:not allowed|does not exist|no such|not found|failed|failure|error|invalid|cannot|can't|denied|" +
        @"not complete|incomplete|missing files?|unknown section)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsPreCommand(string? command) =>
        !string.IsNullOrWhiteSpace(command) &&
        Regex.IsMatch(command.Trim(), @"^SITE\s+PRE\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static PreCommandOutcome Classify(string? command, int statusCode, string? response)
    {
        if (!IsPreCommand(command))
            return statusCode is >= 200 and < 400 ? PreCommandOutcome.Success : PreCommandOutcome.Failed;

        var message = StripAnsi(response ?? string.Empty);
        if (DupePattern.IsMatch(message)) return PreCommandOutcome.Dupe;
        if (statusCode is < 200 or >= 400 || FailurePattern.IsMatch(message)) return PreCommandOutcome.Failed;
        return PreCommandOutcome.Success;
    }

    private static string StripAnsi(string value) =>
        Regex.Replace(value, "\u001B\\[[0-9;]*[A-Za-z]", string.Empty);
}
