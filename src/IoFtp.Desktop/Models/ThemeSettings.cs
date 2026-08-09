namespace IoFtp.Desktop.Models;

public sealed record ThemeSettings(
    string Name = "FluxFTP Dark",
    string Window = "#10151C",
    string Surface = "#18212B",
    string SurfaceRaised = "#202C38",
    string Border = "#314252",
    string Accent = "#42C9B4",
    string AccentStrong = "#19A990",
    string Text = "#E6EDF3",
    string MutedText = "#91A2B1",
    string Selection = "#285C68",
    string Hover = "#263746",
    string FontFamily = "Segoe UI",
    string MonospaceFontFamily = "Cascadia Mono, Consolas",
    double FontSize = 13);
