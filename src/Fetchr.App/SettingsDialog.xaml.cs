using System.Globalization;
using System.Windows;

namespace Fetchr.App;

public partial class SettingsDialog : Window
{
    private const int MinConnections = 1, MaxConnections = 32;
    private const int MinParallel = 1, MaxParallel = 16;

    public SettingsDialog()
    {
        InitializeComponent();
        App.Prepare(this);
        ConnectionsBox.Text = Math.Clamp(App.Store.GetInt("default_connections", 8), MinConnections, MaxConnections)
            .ToString(CultureInfo.InvariantCulture);
        ParallelBox.Text = App.Store.MaxParallel().ToString(CultureInfo.InvariantCulture);
        Loaded += (_, _) => { ConnectionsBox.Focus(); ConnectionsBox.SelectAll(); };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryRead(ConnectionsBox, MinConnections, MaxConnections, out var connections)) return;
        if (!TryRead(ParallelBox, MinParallel, MaxParallel, out var parallel)) return;
        App.Store.SetSetting("default_connections", connections.ToString(CultureInfo.InvariantCulture));
        App.Store.SetMaxParallel(parallel);
        DialogResult = true;
    }

    private bool TryRead(System.Windows.Controls.TextBox box, int min, int max, out int value)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            return true;
        }
        ErrorText.Text = string.Format(App.Str("S.Set.Invalid"), min, max);
        ErrorText.Visibility = Visibility.Visible;
        box.Focus();
        box.SelectAll();
        value = 0;
        return false;
    }
}
