using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Fetchr.App;

/// <summary>Connects Fetchr to Chrome/Edge (native host registration) and shows how to load the extension.</summary>
public partial class BrowserDialog : Window
{
    public sealed record Row(string Name, string State, Geometry Icon, Brush Brush);

    public BrowserDialog()
    {
        InitializeComponent();
        App.Prepare(this);
        FolderBox.Text = BrowserSetup.ExtensionFolder;
        Refresh();
    }

    private void Refresh()
    {
        var rows = BrowserSetup.Browsers.Select(b => BrowserSetup.IsRegistered(b)
            ? new Row(b.Name, App.Str("S.Br.Connected"), App.Res<Geometry>("Icon.CircleCheck"), App.BrushOf("Brush.Success"))
            : new Row(b.Name, App.Str("S.Br.NotConnected"), App.Res<Geometry>("Icon.CircleX"), App.BrushOf("Brush.TextMuted"))).ToList();
        BrowserList.ItemsSource = rows;
        var all = BrowserSetup.Browsers.All(BrowserSetup.IsRegistered);
        ConnectButton.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.Visibility = BrowserSetup.Browsers.Any(BrowserSetup.IsRegistered) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        BrowserSetup.Register();
        Refresh();
    }

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        BrowserSetup.Unregister();
        Refresh();
    }

    private void OnCopyAddress(object sender, RoutedEventArgs e)
    {
        // Browsers don't let other programs open chrome:// pages, so the address is copied for pasting.
        Clipboard.SetText("chrome://extensions");
        CopyText.SetResourceReference(TextBlock.TextProperty, "S.Br.Copied");
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) => App.ShowInFolder(FolderBox.Text);
}
