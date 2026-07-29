using System.Collections.ObjectModel;
using System.Windows;
using IoFtp.Desktop.Models;

namespace IoFtp.Desktop;

public partial class AdvancedSkiplistWindow : Window
{
    private readonly ObservableCollection<EditableRule> _rules;
    public IReadOnlyList<AdvancedSkipRule> Rules { get; private set; } = [];

    public AdvancedSkiplistWindow(IEnumerable<AdvancedSkipRule> rules)
    {
        InitializeComponent();
        _rules = new(rules.Select(rule => new EditableRule(rule.Pattern, rule.Regex, rule.EntryType, rule.Action, rule.Scope)));
        RulesGrid.ItemsSource = _rules;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var rule = new EditableRule("*", false, "File", "Deny", "Allround");
        _rules.Add(rule);
        RulesGrid.SelectedItem = rule;
        RulesGrid.ScrollIntoView(rule);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is EditableRule rule) _rules.Remove(rule);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int offset)
    {
        if (RulesGrid.SelectedItem is not EditableRule rule) return;
        var current = _rules.IndexOf(rule);
        var target = current + offset;
        if (current < 0 || target < 0 || target >= _rules.Count) return;
        _rules.Move(current, target);
        RulesGrid.SelectedItem = rule;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        RulesGrid.CommitEdit();
        RulesGrid.CommitEdit();
        Rules = _rules.Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern))
            .Select(rule => new AdvancedSkipRule(rule.Pattern.Trim(), rule.Regex,
                Normalize(rule.EntryType, "File", "Directory", "Both"),
                Normalize(rule.Action, "Allow", "Deny"),
                string.IsNullOrWhiteSpace(rule.Scope) ? "Allround" : rule.Scope.Trim()))
            .ToArray();
        DialogResult = true;
    }

    private static string Normalize(string value, params string[] allowed) =>
        allowed.FirstOrDefault(item => item.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? allowed[0];

    private sealed class EditableRule(string pattern, bool regex, string entryType, string action, string scope)
    {
        public string Pattern { get; set; } = pattern;
        public bool Regex { get; set; } = regex;
        public string EntryType { get; set; } = entryType;
        public string Action { get; set; } = action;
        public string Scope { get; set; } = scope;
    }
}
