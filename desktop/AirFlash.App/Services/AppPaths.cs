using AirFlash.Core;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
namespace AirFlash.App.Services;

public static class AppPaths
{
    public const string Name = "Airplay";
    private static Version AssemblyVersion => typeof(AppPaths).Assembly.GetName().Version!;
    private static string? InformationalVersion => typeof(AppPaths).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    public static string Version => AssemblyVersion.ToString(3);
    public static string DisplayVersion => AppVersion.Normalize(InformationalVersion, AssemblyVersion);
    public static string VersionLabel => AppVersion.Label(InformationalVersion, AssemblyVersion);
    public static bool IsPreview => AppVersion.IsPreview(InformationalVersion, AssemblyVersion);
    public static bool IsDevelopment => AppVersion.IsDevelopment(InformationalVersion, AssemblyVersion);
    public static string DataDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static void Log(string message)
    {
        try { lock (LogLock) { Directory.CreateDirectory(LogDirectory); File.AppendAllText(Path.Combine(LogDirectory, $"wpf-{DateTime.Today:yyyy-MM-dd}.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}"); } }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static readonly object LogLock = new();
    internal static byte[] ReadEmbeddedEngine()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AirFlash.Engine.exe")
            ?? throw new FileNotFoundException(L.Get("The Rust audio engine is missing. Rebuild the complete release package."));
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
    public const string EngineFileName = "airflash-engine.exe";
    /// <summary>Returns the engine to run. The MSI installs an identical copy next to
    /// Airplay.exe with a firewall rule for its fixed path, so that copy is used when
    /// its bytes match the embedded engine. Portable runs extract to a per-user
    /// hash-named folder instead; folders left by older engines are removed.</summary>
    public static string ExtractEngine()
    {
        var bytes = ReadEmbeddedEngine();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name, "engine");
        var installed = Path.Combine(AppContext.BaseDirectory, EngineFileName);
        if (Matches(installed, hash)) { RemoveStaleEngines(root, keep: null); return installed; }
        var directory = Path.Combine(root, hash);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, EngineFileName);
        if (!Matches(target, hash))
        {
            var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try { File.WriteAllBytes(temp, bytes); File.Move(temp, target, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        RemoveStaleEngines(root, keep: hash);
        return target;
    }
    private static bool Matches(string path, string hash)
    {
        try
        {
            return File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(hash, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    // Best effort: an engine still running from another session keeps its folder.
    internal static void RemoveStaleEngines(string root, string? keep)
    {
        if (!Directory.Exists(root)) return;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(folder);
            if (name.Length != 64 || !name.All(Uri.IsHexDigit) || string.Equals(name, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public static void OpenLogs() { Directory.CreateDirectory(LogDirectory); Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true }); }
}
