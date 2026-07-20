using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using System.Xml.Linq;

namespace IoFtp.Desktop.Services;

internal sealed record ImportedCommand(string Name, string GroupPath, IReadOnlyList<string> Lines)
{
    public string DisplayName => string.IsNullOrWhiteSpace(GroupPath) ? Name : $"{GroupPath} / {Name}";
}

internal static partial class FtpRushCommandImporter
{
    public static IReadOnlyList<ImportedCommand> Import(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var source = File.ReadAllText(path, Encoding.GetEncoding(1252));
        // FTPRush writes client directives such as &window without XML escaping.
        source = BareAmpersand().Replace(source, "&amp;");
        var document = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
        return document.Descendants("CMD").Select(command =>
        {
            var groups = command.Ancestors("GROUP").Reverse().Select(group => (string?)group.Attribute("NAME") ?? "")
                .Where(name => name.Length > 0 && !name.Equals("COMMAND", StringComparison.OrdinalIgnoreCase));
            var lines = command.Descendants("I").Select(line => line.Value.Trim()).Where(line => line.Length > 0).ToList();
            return new ImportedCommand((string?)command.Attribute("NAME") ?? "Unnamed command", string.Join(" / ", groups), lines);
        }).Where(command => command.Lines.Count > 0).OrderBy(command => command.GroupPath).ThenBy(command => command.Name).ToList();
    }

    [GeneratedRegex("&(?!amp;|lt;|gt;|quot;|apos;)", RegexOptions.IgnoreCase)]
    private static partial Regex BareAmpersand();
}
