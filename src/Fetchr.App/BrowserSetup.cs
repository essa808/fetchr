using System.Text.Json;
using Fetchr.Core;
using Microsoft.Win32;

namespace Fetchr.App;

/// <summary>
/// Registers the native messaging host for the current user only (HKCU), the documented way:
/// a manifest JSON naming the host exe and the one extension allowed to call it, plus a registry
/// value per browser pointing at that manifest. Removing it deletes exactly those keys and the file.
/// </summary>
public static class BrowserSetup
{
    public sealed record Browser(string Name, string RegistryPath);

    public static readonly Browser[] Browsers =
    [
        new("Google Chrome", @"Software\Google\Chrome\NativeMessagingHosts\" + Ipc.HostName),
        new("Microsoft Edge", @"Software\Microsoft\Edge\NativeMessagingHosts\" + Ipc.HostName),
    ];

    public static string ManifestPath =>
        Path.Combine(Path.GetDirectoryName(Store.DefaultPath)!, "native-host", Ipc.HostName + ".json");

    public static string HostExe => Path.Combine(AppContext.BaseDirectory, "Fetchr.Host.exe");

    /// <summary>The unpacked extension that ships next to the app (load it from chrome://extensions).</summary>
    public static string ExtensionFolder
    {
        get
        {
            var shipped = Path.Combine(AppContext.BaseDirectory, "extension");
            if (Directory.Exists(shipped)) return shipped;
            // Running from the source tree: src/Fetchr.App/bin/Debug/net10.0-windows → repo/extension
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "extension"))) dir = dir.Parent;
            return dir is null ? shipped : Path.Combine(dir.FullName, "extension");
        }
    }

    public static bool IsRegistered(Browser b)
    {
        using var key = Registry.CurrentUser.OpenSubKey(b.RegistryPath);
        return key?.GetValue(null) as string is { } path
               && string.Equals(path, ManifestPath, StringComparison.OrdinalIgnoreCase)
               && File.Exists(path) && ManifestPointsHere();
    }

    private static bool ManifestPointsHere()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            return string.Equals(doc.RootElement.GetProperty("path").GetString(), HostExe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException) { return false; }
    }

    public static void Register()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        var manifest = new
        {
            name = Ipc.HostName,
            description = "Fetchr download manager",
            path = HostExe,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{Ipc.ExtensionId}/" },
        };
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var b in Browsers)
        {
            using var key = Registry.CurrentUser.CreateSubKey(b.RegistryPath);
            key.SetValue(null, ManifestPath);
        }
    }

    public static void Unregister()
    {
        foreach (var b in Browsers) Registry.CurrentUser.DeleteSubKeyTree(b.RegistryPath, throwOnMissingSubKey: false);
        if (File.Exists(ManifestPath)) File.Delete(ManifestPath);
    }
}
