using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Fetchr.App;

/// <summary>S05: the file is done. For archives the primary action becomes "Open extracted folder" once unpacked.</summary>
public partial class CompletedDialog : Window
{
    private readonly DownloadItemVm _vm;

    public CompletedDialog(DownloadItemVm vm)
    {
        InitializeComponent();
        App.Prepare(this);
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnVmChanged;
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnVmChanged;
            if (DontShow.IsChecked == true) App.Store.SetSetting("show_complete_dialog", "false");
        };
        Update();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItemVm.ExtractedTo) or nameof(DownloadItemVm.Status)) Update();
    }

    private void Update()
    {
        ExtractState.Visibility = _vm.IsArchive ? Visibility.Visible : Visibility.Collapsed;
        var extracted = _vm.ExtractedTo is not null;
        OpenIcon.Data = App.Res<Geometry>(extracted ? "Icon.FolderOpen" : "Icon.ExternalLink");
        OpenText.SetResourceReference(TextBlock.TextProperty, extracted ? "S.Done.OpenExtracted" : "S.Open");
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        App.OpenFile(_vm.ExtractedTo ?? _vm.FullPath);
        Close();
    }

    private void OnFolder(object sender, RoutedEventArgs e)
    {
        App.ShowInFolder(_vm.FullPath);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
