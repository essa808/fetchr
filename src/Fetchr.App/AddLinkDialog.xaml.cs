using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fetchr.Core;
using Microsoft.Win32;

namespace Fetchr.App;

/// <summary>S02 (enter a link) and S03 (confirm name, size and folder) in one dialog.</summary>
public partial class AddLinkDialog : Window
{
    private ProbeResult? _probe;
    private CancellationTokenSource? _checking;
    private readonly string? _referer;
    private readonly Dictionary<string, string>? _headers;
    private readonly string? _nameHint;

    public DownloadRecord? Added { get; private set; }
    public bool Started { get; private set; }

    public AddLinkDialog()
    {
        InitializeComponent();
        App.Prepare(this);
        if (Clipboard.ContainsText() && Clipboard.GetText().Trim() is var clip
            && Uri.TryCreate(clip, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
        {
            UrlBox.Text = clip;
        }
        Loaded += (_, _) =>
        {
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
        Closed += (_, _) => _checking?.Cancel();
    }

    /// <summary>A download the browser handed over: link, page, cookies and the name the browser chose.</summary>
    public AddLinkDialog(IpcMessage capture) : this()
    {
        _referer = capture.Referrer;
        _headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(capture.Cookies)) _headers["Cookie"] = capture.Cookies;
        if (!string.IsNullOrEmpty(capture.UserAgent)) _headers["User-Agent"] = capture.UserAgent;
        _nameHint = string.IsNullOrWhiteSpace(capture.FileName) ? null : FileNames.Sanitize(Path.GetFileName(capture.FileName));
        UrlBox.Text = capture.FinalUrl ?? capture.Url ?? "";
        Loaded += async (_, _) => await CheckAsync();
    }

    private void OnUrlChanged(object sender, TextChangedEventArgs e)
    {
        // Editing the link invalidates what we learned about the old one.
        _probe = null;
        Details.Visibility = LaterButton.Visibility = Visibility.Collapsed;
        StartText.SetResourceReference(TextBlock.TextProperty, "S.Add.Check");
        ShowError(null);
    }

    private async void OnCheck(object sender, RoutedEventArgs e) => await CheckAsync();

    private async Task CheckAsync()
    {
        var url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
        {
            ShowError(App.Str("S.Add.Invalid"));
            UrlBox.Focus();
            return;
        }

        _checking?.Cancel();
        _checking = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var p = await App.Manager.ProbeAsync(url, _referer, _headers, _checking.Token);
            _probe = p;
            // The browser's choice of name wins: it has seen the page, and the server name may be generic.
            NameBox.Text = _nameHint is { Length: > 0 } hint ? hint : p.FileName;
            FolderBox.Text = App.Manager.SuggestFolder(NameBox.Text);
            SizeText.Text = p.Size is long s ? Bytes.Format(s) : App.Str("S.Add.UnknownSize");
            UpdateFree();
            ResumeIcon.Data = App.Res<Geometry>(p.Resumable ? "Icon.Check" : "Icon.TriangleAlert");
            ResumeRow.SetValue(TextElement.ForegroundProperty, App.BrushOf(p.Resumable ? "Brush.Success" : "Brush.Warning"));
            ResumeText.SetResourceReference(TextBlock.TextProperty, p.Resumable ? "S.Add.Resumable" : "S.Add.NotResumable");
            var connections = p.Resumable ? Math.Clamp(App.Store.GetInt("default_connections", 8), 1, 32) : 1;
            ConnectionsText.Text = string.Format(App.Str("S.Add.Connections"), connections);
            ExtractRow.Visibility = Extractor.CanExtract(NameBox.Text) && App.Store.GetBool("auto_extract", true) ? Visibility.Visible : Visibility.Collapsed;
            Details.Visibility = LaterButton.Visibility = Visibility.Visible;
            StartText.SetResourceReference(TextBlock.TextProperty, "S.Add.Start");
        }
        catch (ProbeException ex)
        {
            ShowError(ex.Message);
        }
        catch (OperationCanceledException) { }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateFree()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(FolderBox.Text));
            var free = new DriveInfo(root!).AvailableFreeSpace;
            // LRE…PDF keeps "167 GB" in reading order inside Arabic text.
            FreeText.Text = string.Format(App.Str("S.Add.Free"), "‪" + Bytes.Format(free) + "‬");
            var tooSmall = _probe?.Size is long need && need > free;
            FreeText.Foreground = App.BrushOf(tooSmall ? "Brush.Danger" : "Brush.TextMuted");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            FreeText.Text = "";
        }
    }

    private void SetBusy(bool busy)
    {
        CheckButton.IsEnabled = StartButton.IsEnabled = !busy;
        CheckButton.SetResourceReference(ContentProperty, busy ? "S.Add.Checking" : "S.Add.Check");
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.FlowDirection = TextDirectionConverter.Of(message);
        ErrorRow.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        UrlBox.Tag = message is null ? null : "error";
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : KnownFolders.Downloads };
        if (dlg.ShowDialog(this) == true)
        {
            FolderBox.Text = dlg.FolderName;
            UpdateFree();
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_probe is null)
        {
            await CheckAsync(); // first press of Enter checks the link
            return;
        }
        Add(start: true);
    }

    private void OnLater(object sender, RoutedEventArgs e) => Add(start: false);

    private void Add(bool start)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim();
            Added = App.Manager.Add(_probe!, FolderBox.Text.Trim(), name, start, _referer, _headers);
            Started = start;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowError(ex.Message);
        }
    }
}
