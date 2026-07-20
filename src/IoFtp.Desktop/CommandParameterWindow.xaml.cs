using System.Windows;

namespace IoFtp.Desktop;

public partial class CommandParameterWindow : Window
{
    public string Value => ValueBox.Text;
    public CommandParameterWindow(string parameter, string initialValue = "") { InitializeComponent(); PromptText.Text = parameter; ValueBox.Text = initialValue; Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); }; }
    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
