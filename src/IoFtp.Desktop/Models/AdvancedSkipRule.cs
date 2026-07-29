namespace IoFtp.Desktop.Models;

public sealed record AdvancedSkipRule(
    string Pattern = "",
    bool Regex = false,
    string EntryType = "File",
    string Action = "Deny",
    string Scope = "Allround");
