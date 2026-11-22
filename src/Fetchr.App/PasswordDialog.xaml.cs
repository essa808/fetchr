using System.Windows;
using Fetchr.Core;

namespace Fetchr.App;

/// <summary>S06: asks for an archive's password and unpacks with it. Stays open on a wrong password.</summary>
public partial class PasswordDialog : Window
{
    private readonly string _downloadId;

    public PasswordDialog(string downloadId, string archiveName)
    {
        InitializeComponent();
        App.Prepare(this);
        _downloadId = downloadId;
        ArchiveName.Text = archiveName;
        Loaded += (_, _) => Pwd.Focus();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        UnpackButton.IsEnabled = Pwd.Password.Length > 0;
        ErrorRow.Visibility = Visibility.Collapsed;
        PwdBorder.BorderBrush = App.BrushOf("Brush.BorderInput");
    }

    private async void OnUnpack(object sender, RoutedEventArgs e)
    {
        UnpackButton.IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        var outcome = await App.Manager.ExtractAsync(_downloadId, Pwd.Password, Remember.IsChecked == true);
        Cursor = null;
        if (outcome?.State is ExtractState.WrongPassword or ExtractState.NeedsPassword)
        {
            ErrorRow.Visibility = Visibility.Visible;
            PwdBorder.BorderBrush = App.BrushOf("Brush.Danger");
            Pwd.SelectAll();
            Pwd.Focus();
            UnpackButton.IsEnabled = true;
            return;
        }
        DialogResult = true;
    }
}
