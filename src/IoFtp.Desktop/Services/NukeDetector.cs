using System.Text.RegularExpressions;
using IoFtp.Core.Abstractions;

namespace IoFtp.Desktop.Services;

internal sealed record NukeStatus(bool IsNuked, string Reason = "", string Marker = "")
{
    public string Display => IsNuked
        ? string.IsNullOrWhiteSpace(Reason) ? "NUKED" : $"NUKED: {Reason}"
        : "";
}

internal static partial class NukeDetector
{
    private static readonly string[] MarkerNames =
    [
        ".nuke", ".nuked", "nuke", "nuked", "nuked.txt", ".message"
    ];

    public static NukeStatus DetectName(string name)
    {
        var value = name.Trim();
        if (MarkerNames.Contains(value, StringComparer.OrdinalIgnoreCase))
            return new(true, "", value);

        var match = NukeNamePattern().Match(value);
        if (!match.Success) return new(false);

        var reason = match.Groups["reason"].Value.Trim(' ', '-', '_', '[', ']', '(', ')', '.');
        return new(true, reason.Replace('_', ' '), value);
    }

    public static NukeStatus DetectDirectory(string directoryName, IEnumerable<RemoteEntry> children)
    {
        var directoryStatus = DetectName(directoryName);
        if (directoryStatus.IsNuked) return directoryStatus;

        foreach (var child in children)
        {
            var status = DetectName(child.Name);
            if (status.IsNuked) return status;
        }
        return new(false);
    }

    [GeneratedRegex(@"(?:^|[-_.\[(])NUKED(?:[-_.\])]|$)(?<reason>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex NukeNamePattern();
}
