using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Fetchr.Core;

namespace Fetchr.App;

/// <summary>One row of the download table, refreshed from a <see cref="DownloadSnapshot"/>.</summary>
public sealed partial class DownloadItemVm(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _fullPath = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private Geometry? _statusIcon;
    [ObservableProperty] private Brush? _statusBrush;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _downloadedText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressTag = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private string _addedText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _connectionsText = "";
    [ObservableProperty] private string _resumableText = "";
    [ObservableProperty] private string? _extractedTo;
    [ObservableProperty] private IReadOnlyList<SegmentView>? _segments;
    [ObservableProperty] private long _total;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _bytesPerSecond;
    [ObservableProperty] private string? _categoryId;

    public bool IsArchive => Archives.Identify(Name) is not null;
    public bool CanResume => !IsRunning && Status is DownloadStatus.Paused or DownloadStatus.Error;
    public bool CanPause => IsRunning;
    public bool CanExtract => IsArchive && Status is DownloadStatus.Completed or DownloadStatus.Extracted or DownloadStatus.ExtractFailed
        or DownloadStatus.WaitingParts or DownloadStatus.NeedsPassword;
    public bool NeedsPassword => Status == DownloadStatus.NeedsPassword;
    public bool IsDone => DownloadStatus.IsFinished(Status);

    public void Update(DownloadSnapshot s, bool running)
    {
        Name = s.FileName;
        FullPath = s.FullPath;
        IsRunning = running;
        Status = s.Status;
        StatusText = App.Str("S.Status." + s.Status);
        (StatusIcon, StatusBrush) = s.Status switch
        {
            DownloadStatus.Queued => (Icon("Clock"), App.BrushOf("Brush.TextMuted")),
            DownloadStatus.Downloading => (Icon("ArrowDown"), App.BrushOf("Brush.Accent")),
            DownloadStatus.Paused => (Icon("Pause"), App.BrushOf("Brush.TextMuted")),
            DownloadStatus.Completed => (Icon("Check"), App.BrushOf("Brush.Success")),
            DownloadStatus.Extracting => (Icon("Loader"), App.BrushOf("Brush.Accent")),
            DownloadStatus.Extracted => (Icon("FolderCheck"), App.BrushOf("Brush.Success")),
            DownloadStatus.ExtractFailed => (Icon("ArchiveX"), App.BrushOf("Brush.Danger")),
            DownloadStatus.WaitingParts => (Icon("Hourglass"), App.BrushOf("Brush.TextMuted")),
            DownloadStatus.NeedsPassword => (Icon("Lock"), App.BrushOf("Brush.Warning")),
            _ => (Icon("TriangleAlert"), App.BrushOf("Brush.Danger")),
        };
        Error = s.Error;
        Total = s.TotalSize ?? 0;
        SizeText = s.TotalSize is null ? "?" : Bytes.Format(s.TotalSize);
        DownloadedText = s.TotalSize is long t && t > 0
            ? $"{Bytes.Format(s.Downloaded)} / {Bytes.Format(t)} ({s.Downloaded * 100 / t}%)"
            : Bytes.Format(s.Downloaded);
        CategoryId = s.CategoryId;
        Progress = DownloadStatus.IsFinished(s.Status) ? 100
            : s.TotalSize is long size && size > 0 ? s.Downloaded * 100.0 / size : 0;
        ProgressTag = s.Status switch
        {
            DownloadStatus.Completed or DownloadStatus.Extracted or DownloadStatus.WaitingParts or DownloadStatus.NeedsPassword => "done",
            DownloadStatus.Error or DownloadStatus.ExtractFailed => "error",
            DownloadStatus.Paused or DownloadStatus.Queued => "paused",
            _ => "",
        };
        BytesPerSecond = running ? s.BytesPerSecond : 0;
        SpeedText = running && s.BytesPerSecond > 0 ? Bytes.Format((long)s.BytesPerSecond) + "/s" : "—";
        EtaText = running && s.BytesPerSecond > 1 && s.TotalSize is long total
            ? FormatEta(TimeSpan.FromSeconds((total - s.Downloaded) / s.BytesPerSecond))
            : "—";
        AddedText = DateTime.TryParse(s.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "";
        ConnectionsText = running ? $"{s.ActiveConnections} / {s.Connections}" : "—";
        ResumableText = App.Str(s.Resumable ? "S.Yes" : "S.No");
        ExtractedTo = s.ExtractedTo;
        Segments = s.Segments;

        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(IsArchive));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(NeedsPassword));
    }

    private static Geometry Icon(string name) => App.Res<Geometry>("Icon." + name);

    internal static string FormatEta(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{t.Minutes}:{t.Seconds:00}";
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, DownloadItemVm> _byId = [];

    public ObservableCollection<DownloadItemVm> Items { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _totalSpeedText = "";
    [ObservableProperty] private string _activeText = "";
    [ObservableProperty] private bool _autoExtract;

    public MainViewModel()
    {
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is DownloadItemVm d && Filter switch
        {
            "downloading" => d.IsRunning || d.Status == DownloadStatus.Downloading,
            "completed" => d.IsDone,
            "unfinished" => !d.IsDone,
            var f when f.StartsWith("cat:") => d.CategoryId == f[4..],
            _ => true,
        };
        _autoExtract = App.Store.GetBool("auto_extract", true);
    }

    partial void OnFilterChanged(string value) => RefreshView();
    partial void OnAutoExtractChanged(bool value) => App.Store.SetSetting("auto_extract", value ? "true" : "false");

    public DownloadItemVm? Find(string id) => _byId.GetValueOrDefault(id);

    public async Task RefreshAsync()
    {
        var snaps = await Task.Run(App.Manager.Snapshot);
        var seen = new HashSet<string>();
        var statusChanged = false;
        for (var i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            seen.Add(s.Id);
            if (!_byId.TryGetValue(s.Id, out var vm))
            {
                vm = new DownloadItemVm(s.Id);
                _byId[s.Id] = vm;
                Items.Insert(Math.Min(i, Items.Count), vm);
                statusChanged = true;
            }
            var before = (vm.Status, vm.IsRunning);
            vm.Update(s, App.Manager.IsRunning(s.Id));
            statusChanged |= before != (vm.Status, vm.IsRunning);
        }
        foreach (var gone in _byId.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Items.Remove(_byId[gone]);
            _byId.Remove(gone);
            statusChanged = true;
        }

        var running = Items.Where(d => d.IsRunning).ToList();
        TotalSpeedText = Bytes.Format((long)running.Sum(d => d.BytesPerSecond)) + "/s";
        ActiveText = $"{running.Count} {App.Str("S.Active")}";
        if (statusChanged) RefreshView();
    }

    private void RefreshView()
    {
        View.Refresh();
        IsEmpty = View.IsEmpty;
    }
}
