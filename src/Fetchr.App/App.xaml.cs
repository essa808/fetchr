using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Fetchr.Core;
using Microsoft.Win32;

namespace Fetchr.App;

public partial class App : Application
{
    private static ResourceDictionary? _theme;
    private static ResourceDictionary? _strings;

    public static Store Store { get; private set; } = null!;
    public static DownloadManager Manager { get; private set; } = null!;
    public static string Language { get; private set; } = "en";
    public static FlowDirection Flow => Language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            var log = Path.Combine(Path.GetDirectoryName(Store.DefaultPath)!, "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTime.Now:O}\n{args.Exception}\n\n");
            MessageBox.Show(args.Exception.Message + "\n\n" + log, "Fetchr", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = Manager is not null; // keep running once started (window may be in the tray); otherwise exit
        };
        // Used by the installer: connect/disconnect the browsers, then exit without showing anything.
        if (e.Args.Contains("--register-browsers") || e.Args.Contains("--unregister-browsers"))
        {
            try
            {
                if (e.Args.Contains("--register-browsers")) BrowserSetup.Register();
                else BrowserSetup.Unregister();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Shutdown(1); return; }
            Shutdown(0);
            return;
        }

        var dbPath = Ipc.DbPath;
        var pipe = Ipc.PipeName(dbPath);

        // One instance per database. A second launch just brings the first one forward.
        _single = new Mutex(true, "Local\\" + pipe, out var first);
        if (!first)
        {
            Task.Run(() => Ipc.SendAsync(pipe, new IpcMessage("show"), TimeSpan.FromSeconds(3))).Wait();
            Shutdown();
            return;
        }

        Store = new Store(dbPath);
        Manager = new DownloadManager(Store);

        var merged = Resources.MergedDictionaries;
        merged.Add(Dict("Tokens.Shared"));
        _theme = Dict(SystemUsesLightTheme() ? "Tokens.Light" : "Tokens.Dark");
        merged.Add(_theme);
        merged.Add(Dict("Icons"));
        var lang = Store.GetSetting("ui_language")?.Trim('"');
        _strings = Dict(lang == "en" ? "Strings.en" : "Strings.ar");
        Language = lang == "en" ? "en" : "ar";
        merged.Add(_strings);
        merged.Add(Dict("Styles"));

        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General) Dispatcher.Invoke(ApplyTheme);
        };

        var main = new MainWindow();
        MainWindow = main;
        Tray = new Tray(ShowMain, () => main.PauseAll(), () => main.ResumeAll(), ExitApp);
        if (!e.Args.Contains("--minimized")) main.Show();

        Manager.Recover(); // resumes the queue; after the window exists so it sees the first refresh
        _ipc = new IpcServer(pipe, msg => Dispatcher.InvokeAsync(() => HandleIpc(msg)).Task);
    }

    private static Mutex? _single;
    private static IpcServer? _ipc;
    public static Tray? Tray { get; private set; }
    public static bool Exiting { get; private set; }

    /// <summary>Extensions the browser hands over to Fetchr. Others stay with the browser.</summary>
    public static string[] CaptureTypes =>
        (Store.GetSetting("capture_types")?.Trim('"') ?? "zip rar 7z gz tgz xz bz2 tar iso 001 exe msi msix apk dmg mp4 mkv avi mov webm m4v mp3 m4a flac wav pdf epub")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private IpcReply HandleIpc(IpcMessage m)
    {
        switch (m.Type)
        {
            case "ping":
                return new IpcReply(true, Version: Ipc.Version);
            case "get_config":
                return new IpcReply(true, Version: Ipc.Version, Types: CaptureTypes, MinSize: Store.GetInt("min_capture_size", 0));
            // Dialogs open after the reply goes back: the browser must not wait on the user.
            case "show":
                Dispatcher.BeginInvoke(ShowMain);
                return new IpcReply(true);
            case "capture" when Uri.TryCreate(m.Url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"):
                Dispatcher.BeginInvoke(() => ((MainWindow)MainWindow).ShowCapture(m));
                return new IpcReply(true, Accepted: true);
            default:
                return new IpcReply(false, Error: "unsupported");
        }
    }

    public static void ShowMain()
    {
        var w = Current.MainWindow;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    public static void ExitApp()
    {
        Exiting = true;
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Manager is not null)
        {
            _ipc?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            // Pausing saves every segment's offset, so the next start resumes where this one stopped.
            // Queued downloads stay queued and start again next time.
            Manager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            Tray?.Dispose();
        }
        _single?.Dispose();
        base.OnExit(e);
    }

    private static ResourceDictionary Dict(string name) =>
        new() { Source = new Uri($"pack://application:,,,/Fetchr.App;component/Themes/{name}.xaml") };

    public static string Str(string key) => Current.TryFindResource(key) as string ?? key;
    public static T Res<T>(string key) where T : class => (T)Current.FindResource(key);

    public static void SetLanguage(string lang)
    {
        Language = lang;
        var merged = Current.Resources.MergedDictionaries;
        var i = merged.IndexOf(_strings!);
        _strings = Dict(lang == "en" ? "Strings.en" : "Strings.ar");
        merged[i] = _strings;
        foreach (Window w in Current.Windows) w.FlowDirection = Flow;
        Store.SetSetting("ui_language", $"\"{lang}\"");
        Tray?.RebuildMenu();
    }

    private static void ApplyTheme()
    {
        var merged = Current.Resources.MergedDictionaries;
        var i = merged.IndexOf(_theme!);
        var light = SystemUsesLightTheme();
        _theme = Dict(light ? "Tokens.Light" : "Tokens.Dark");
        merged[i] = _theme;
        foreach (Window w in Current.Windows) DarkTitleBar(w, !light);
    }

    public static bool IsDark => !SystemUsesLightTheme();

    private static bool SystemUsesLightTheme()
    {
        // FETCHR_THEME=light|dark overrides Windows, for checking both themes during development.
        if (Environment.GetEnvironmentVariable("FETCHR_THEME") is "light" or "dark")
            return Environment.GetEnvironmentVariable("FETCHR_THEME") == "light";
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
    }

    /// <summary>Applies theme, direction and title-bar colour to any window we open.</summary>
    public static void Prepare(Window w)
    {
        w.FlowDirection = Flow;
        w.WindowStartupLocation = w is MainWindow ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        w.SourceInitialized += (_, _) => DarkTitleBar(w, IsDark);
    }

    private static void DarkTitleBar(Window w, bool dark)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void OpenFile(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void ShowInFolder(string path)
    {
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
    }

    public static Brush BrushOf(string key) => Res<Brush>(key);
}
