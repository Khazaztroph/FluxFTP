using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace IoFtp.Desktop;

public partial class FileViewerWindow : Window
{
    public const int MaximumFileSize = 32 * 1024 * 1024;
    private readonly byte[] _content;
    private readonly IReadOnlyList<EncodingChoice> _encodings;
    private readonly string _name;

    public FileViewerWindow(string name, string path, byte[] content)
    {
        InitializeComponent();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Title = $"{name} — FluxFTP File Viewer";
        _name = name;
        PathText.Text = path;
        _content = content;
        _encodings =
        [
            new("Auto detect", null),
            new("UTF-8", new UTF8Encoding(false, false)),
            new("Windows-1252", Encoding.GetEncoding(1252)),
            new("IBM437 / DOS", Encoding.GetEncoding(437)),
            new("ISO-8859-1", Encoding.Latin1),
            new("ASCII", Encoding.ASCII),
            new("UTF-16 LE", Encoding.Unicode),
            new("UTF-16 BE", Encoding.BigEndianUnicode)
        ];
        EncodingBox.ItemsSource = _encodings;
        EncodingBox.SelectedIndex = 0;
    }

    private void Encoding_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EncodingBox.SelectedItem is not EncodingChoice choice) return;
        var encoding = choice.Encoding ?? DetectEncoding(_content, _name);
        ContentBox.Text = encoding.GetString(RemovePreamble(_content, encoding));
        InfoText.Text = $"{_content.Length:N0} bytes · {encoding.WebName}";
    }

    private static Encoding DetectEncoding(byte[] bytes, string name)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) return Encoding.UTF8;
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble)) return Encoding.Unicode;
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble)) return Encoding.BigEndianUnicode;
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return Path.GetExtension(name).Equals(".nfo", StringComparison.OrdinalIgnoreCase)
                ? Encoding.GetEncoding(437)
                : Encoding.GetEncoding(1252);
        }
    }

    private static ReadOnlySpan<byte> RemovePreamble(byte[] bytes, Encoding encoding)
    {
        var preamble = encoding.Preamble;
        return preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? bytes.AsSpan(preamble.Length) : bytes;
    }

    private void Wrap_Changed(object sender, RoutedEventArgs e) =>
        ContentBox.TextWrapping = WrapBox.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record EncodingChoice(string Name, Encoding? Encoding)
    {
        public override string ToString() => Name;
    }
}
