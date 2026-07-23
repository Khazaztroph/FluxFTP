using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace IoFtp.Desktop.Services;

internal sealed record SectionValidationResult(
    bool Accepted,
    SectionValidationMode Mode,
    string Section,
    string Release,
    string Message,
    string? Pattern = null);

internal static class SectionReleaseValidator
{
    public static SectionValidationResult Validate(string sectionName, string releaseName)
    {
        var section = new SectionStore().Load().FirstOrDefault(item =>
            item.Name.Equals(sectionName, StringComparison.OrdinalIgnoreCase));
        if (section is null || section.ValidationMode == SectionValidationMode.Disabled)
            return new(true, SectionValidationMode.Disabled, sectionName, releaseName, "Validation disabled.");

        var allow = Patterns(section.AllowPatterns);
        if (allow.Count > 0)
        {
            var anyMatch = false;
            foreach (var pattern in allow)
            {
                if (!TryMatches(pattern, releaseName, out var matches, out var error))
                    return new(false, section.ValidationMode, section.Name, releaseName,
                        $"Invalid allow rule '{pattern}': {error}", pattern);
                if (matches) { anyMatch = true; break; }
            }
            if (!anyMatch)
                return new(false, section.ValidationMode, section.Name, releaseName,
                    $"Release does not match any allow rule for section {section.Name}.");
        }

        foreach (var pattern in Patterns(section.DenyPatterns))
        {
            if (!TryMatches(pattern, releaseName, out var matches, out var error))
                return new(false, section.ValidationMode, section.Name, releaseName,
                    $"Invalid deny rule '{pattern}': {error}", pattern);
            if (matches)
                return new(false, section.ValidationMode, section.Name, releaseName,
                    $"Release matches deny rule '{pattern}' for section {section.Name}.", pattern);
        }

        return new(true, section.ValidationMode, section.Name, releaseName,
            $"Release matches the validation rules for section {section.Name}.");
    }

    private static List<string> Patterns(string value) => value
        .Split(['\r', '\n', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool Matches(string pattern, string value)
    {
        if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(value, pattern[6..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        if (pattern.Length > 2 && pattern.StartsWith('/') && pattern.EndsWith('/'))
            return Regex.IsMatch(value, pattern[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        return FileSystemName.MatchesSimpleExpression(pattern, value, true);
    }

    private static bool TryMatches(string pattern, string value, out bool matches, out string? error)
    {
        try { matches = Matches(pattern, value); error = null; return true; }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        { matches = false; error = exception.Message; return false; }
    }
}
