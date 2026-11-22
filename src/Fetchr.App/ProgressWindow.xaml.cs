using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Fetchr.App;

/// <summary>S04: live progress of one download. Bound to the same row view model as the table.</summary>
public partial class ProgressWindow : Window
{
    private readonly DownloadItemVm _vm;

    public ProgressWindow(DownloadItemVm vm)
    {
        InitializeComponent();
        App.Prepare(this);
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnVmChanged;
        Closed += (_, _) => vm.PropertyChanged -= OnVmChanged;
        UpdateToggle();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItemVm.CanPause) or nameof(DownloadItemVm.CanResume)) UpdateToggle();
    }

    private void UpdateToggle()
    {
        var pause = _vm.CanPause;
        ToggleIcon.Data = App.Res<Geometry>(pause ? "Icon.Pause" : "Icon.Play");
        ToggleText.SetResourceReference(TextBlock.TextProperty, pause ? "S.Pause" : "S.Resume");
        ToggleButton.IsEnabled = pause || _vm.CanResume;
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        ToggleButton.IsEnabled = false;
        if (_vm.CanPause) await App.Manager.PauseAsync(_vm.Id);
        else App.Manager.Start(_vm.Id);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
