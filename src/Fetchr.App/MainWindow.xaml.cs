using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fetchr.Core;

namespace Fetchr.App;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand AddLinkCommand = new();

    private readonly MainViewModel _vm = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<string, ProgressWindow> _progress = [];
    private bool _refreshing;
    private bool _toldAboutTray;
    private CompletedDialog? _completed;

    public MainWindow()
    {
        InitializeComponent();
        App.Prepare(this);
        DataContext = _vm;
        AddCategories();
        Filters.SelectedIndex = 0;
        UpdateLanguageButton();

        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        App.Manager.Finished += id => Dispatcher.BeginInvoke(() => OnDownloadFinished(id));
        App.Manager.Extracted += (id, outcome) => Dispatcher.BeginInvoke(() => OnExtracted(id, outcome));
        Loaded += async (_, _) => await RefreshAsync();
        Closing += OnClosing;
    }

    private void AddCategories()
    {
        var at = Filters.Items.IndexOf(CategoriesHeader) + 1;
        foreach (var (id, name) in App.Store.ListCategories())
        {
            var label = new TextBlock();
            // Built-in categories are translated; ones the user creates keep their own name.
            if (App.Current.TryFindResource("S.Cat." + name) is string) label.SetResourceReference(TextBlock.TextProperty, "S.Cat." + name);
            else label.Text = name;
            Filters.Items.Insert(at++, new ListBoxItem
            {
                Tag = "cat:" + id,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { new IconView { Data = App.Res<Geometry>("Icon.Tag"), Margin = new Thickness(0, 0, 8, 0) }, label },
                },
            });
        }
    }

    // Closing the window keeps downloads going in the tray; Exit is in the tray menu.
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.Exiting) return;
        e.Cancel = true;
        Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            App.Tray?.Notify("Fetchr", App.Str("S.Tray.StillRunning"));
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            await _vm.RefreshAsync();
            UpdateToolbar();
            EmptyTitle.SetResourceReference(TextBlock.TextProperty, _vm.Items.Count == 0 ? "S.Empty.Title" : "S.Empty.Filtered");
            EmptyHint.Visibility = EmptyAdd.Visibility = _vm.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _refreshing = false; }
    }

    private List<DownloadItemVm> Selected => DownloadsGrid.SelectedItems.Cast<DownloadItemVm>().ToList();

    private void UpdateToolbar()
    {
        var sel = Selected;
        ResumeButton.IsEnabled = sel.Any(d => d.CanResume);
        PauseButton.IsEnabled = sel.Any(d => d.CanPause || d.Status == DownloadStatus.Queued);
        ExtractButton.IsEnabled = sel.Any(d => d.CanExtract);
        FolderButton.IsEnabled = sel.Count == 1;
        DeleteButton.IsEnabled = sel.Count > 0;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateToolbar();

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Filters.SelectedItem is ListBoxItem { Tag: string tag }) _vm.Filter = tag;
    }

    // ---- toolbar ----

    private async void OnAddLink(object sender, RoutedEventArgs e) => await ShowAddDialog(new AddLinkDialog());

    /// <summary>A download the browser extension handed over.</summary>
    public async void ShowCapture(IpcMessage capture)
    {
        var dlg = new AddLinkDialog(capture);
        if (!IsVisible)
        {
            // Window is in the tray: show the dialog on its own, in front of the browser.
            dlg.ShowInTaskbar = true;
            dlg.Topmost = true;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        await ShowAddDialog(dlg);
    }

    private async Task ShowAddDialog(AddLinkDialog dlg)
    {
        if (IsVisible) dlg.Owner = this;
        if (dlg.ShowDialog() == true && dlg.Added is { } rec)
        {
            await RefreshAsync();
            if (_vm.Find(rec.Id) is { } vm && dlg.Started && App.Manager.IsRunning(rec.Id) && IsVisible) ShowProgress(vm);
        }
    }

    private async void OnResume(object sender, RoutedEventArgs e)
    {
        foreach (var d in Selected.Where(d => d.CanResume)) App.Manager.Start(d.Id);
        await RefreshAsync();
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        await Task.WhenAll(Selected.Where(d => d.CanPause || d.Status == DownloadStatus.Queued).Select(d => App.Manager.PauseAsync(d.Id)));
        await RefreshAsync();
    }

    public async void PauseAll()
    {
        await Task.WhenAll(_vm.Items.Where(d => d.CanPause || d.Status == DownloadStatus.Queued).Select(d => App.Manager.PauseAsync(d.Id)));
        await RefreshAsync();
    }

    public async void ResumeAll()
    {
        foreach (var d in _vm.Items.Where(d => d.CanResume).Reverse()) App.Manager.Start(d.Id); // oldest first
        await RefreshAsync();
    }

    private async void OnExtract(object sender, RoutedEventArgs e)
    {
        foreach (var d in Selected.Where(d => d.CanExtract).ToList()) await ExtractItem(d);
    }

    private async Task ExtractItem(DownloadItemVm d)
    {
        if (d.NeedsPassword)
        {
            AskPassword(d.Id, d.Name);
            return;
        }
        var task = App.Manager.ExtractAsync(d.Id);
        await RefreshAsync();
        await task;
        await RefreshAsync();
    }

    private void AskPassword(string id, string name)
    {
        var dlg = new PasswordDialog(id, name);
        if (IsVisible) dlg.Owner = this;
        else
        {
            dlg.ShowInTaskbar = true;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dlg.ShowDialog();
        _ = RefreshAsync();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { } d) App.ShowInFolder(d.ExtractedTo ?? (d.IsDone ? d.FullPath : Path.GetDirectoryName(d.FullPath)!));
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var sel = Selected;
        if (sel.Count == 0) return;
        var dlg = new ConfirmDeleteDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        foreach (var d in sel)
        {
            await App.Manager.RemoveAsync(d.Id);
            if (dlg.RecycleFiles && d.IsDone && File.Exists(d.FullPath))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(d.FullPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            if (_progress.Remove(d.Id, out var w)) w.Close();
        }
        await RefreshAsync();
    }

    private void OnBrowser(object sender, RoutedEventArgs e) => new BrowserDialog { Owner = this }.ShowDialog();

    private void OnToggleLanguage(object sender, RoutedEventArgs e)
    {
        App.SetLanguage(App.Language == "ar" ? "en" : "ar");
        UpdateLanguageButton();
        _ = RefreshAsync();
    }

    private void UpdateLanguageButton() => LangButton.Content = App.Language == "ar" ? "English" : "العربية";

    // ---- table ----

    private async void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not DownloadItemVm d) return;
        await OpenItem(d);
    }

    private async Task OpenItem(DownloadItemVm d)
    {
        if (d.NeedsPassword) AskPassword(d.Id, d.Name);
        else if (d.IsDone) App.OpenFile(d.ExtractedTo ?? d.FullPath);
        else ShowProgress(d);
        await Task.CompletedTask;
    }

    private async void OnGridKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete:
                OnDelete(sender, e);
                e.Handled = true;
                break;
            case Key.Enter when Selected.FirstOrDefault() is { } d:
                e.Handled = true;
                await OpenItem(d);
                break;
            case Key.Space:
                var sel = Selected;
                if (sel.Any(x => x.CanPause)) OnPause(sender, e);
                else if (sel.Any(x => x.CanResume)) OnResume(sender, e);
                e.Handled = true;
                break;
        }
    }

    private void ShowProgress(DownloadItemVm vm)
    {
        if (_progress.TryGetValue(vm.Id, out var existing))
        {
            existing.Activate();
            return;
        }
        var w = new ProgressWindow(vm) { Owner = this };
        w.Closed += (_, _) => _progress.Remove(vm.Id);
        _progress[vm.Id] = w;
        w.Show();
    }

    // ---- notifications ----

    private async void OnDownloadFinished(string id)
    {
        await RefreshAsync();
        if (_progress.Remove(id, out var w)) w.Close();
        if (_vm.Find(id) is not { } vm) return;
        // Archives get their notice when unpacking ends; other files now.
        if (!vm.IsArchive || !App.Store.GetBool("auto_extract", true))
            App.Tray?.Notify(App.Str("S.Note.Done"), vm.Name, () => App.OpenFile(vm.FullPath));
        // One "complete" dialog at a time, and none for single parts of a multi-part archive.
        if (!IsVisible || !App.Store.GetBool("show_complete_dialog", true) || Archives.Identify(vm.Name) is { MultiPart: true }) return;
        _completed?.Close();
        _completed = new CompletedDialog(vm) { Owner = this };
        _completed.Closed += (_, _) => _completed = null;
        _completed.Show();
    }

    private async void OnExtracted(string id, ExtractOutcome outcome)
    {
        await RefreshAsync();
        var vm = _vm.Find(id);
        var name = vm?.Name ?? "";
        switch (outcome.State)
        {
            case ExtractState.Done:
                App.Tray?.Notify(App.Str("S.Note.Extracted"), name, () => App.OpenFile(outcome.Target!));
                break;
            case ExtractState.NeedsPassword:
                App.Tray?.Notify(App.Str("S.Note.NeedsPassword"), name, () => AskPassword(id, name), warning: true);
                break;
            case ExtractState.NeedsSevenZip:
                MessageBox.Show(this, App.Str("S.Need7z"), "Fetchr", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
        }
    }
}
