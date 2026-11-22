using System.Windows;

namespace Fetchr.App;

public partial class ConfirmDeleteDialog : Window
{
    public bool RecycleFiles => Recycle.IsChecked == true;

    public ConfirmDeleteDialog()
    {
        InitializeComponent();
        App.Prepare(this);
        // Destructive confirm: focus starts on Cancel, so Enter never deletes by accident.
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
