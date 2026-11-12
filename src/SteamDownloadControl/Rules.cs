using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SteamDownloadControl;

internal sealed record Rule(string Path, int? KilobitsPerSecond)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Action => KilobitsPerSecond is int rate ? $"{rate / 8000m:0.###} MB/s" : "Pausieren";
}

internal sealed record Settings(List<Rule> Rules, bool Enabled, bool StartScheduledUpdates = false)
{
    public Settings() : this([], true) { }
}

internal sealed record DownloadLimitChange(SteamRun Steam, int Original, int Applied, int? Pending = null);
internal sealed record PauseChange(SteamRun Steam, bool Confirmed);

internal static class LocalFiles
{
    // Reuse SteamPause's settings and pending Steam changes after the rename.
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamPause");

    public static T? Read<T>(string name) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Folder, name))); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(Folder);
        var destination = Path.Combine(Folder, name);
        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, destination, overwrite: true);
    }
}

internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "SteamPause";
    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool Initialize()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var savedCommand = key?.GetValue(Name) as string;
        if (string.Equals(savedCommand, Command, StringComparison.OrdinalIgnoreCase))
            return true;

        // Retarget an existing SteamPause autostart entry to the renamed executable.
        if (savedCommand?.EndsWith("\\SteamPause.exe\" --tray", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(Path.GetFileName(Environment.ProcessPath), "SteamDownloadControl.exe",
                StringComparison.OrdinalIgnoreCase) && Command.Length <= 260)
        {
            SetEnabled(true);
            return true;
        }
        return false;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            if (Command.Length > 260)
                throw new InvalidOperationException("Der EXE-Pfad ist für den Windows-Autostart zu lang.");
            key.SetValue(Name, Command);
        }
        else
        {
            key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}

internal static class Processes
{
    private static readonly int SessionId = GetCurrentSession();
    public static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private static int GetCurrentSession()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    public static bool InCurrentSession(Process process) => process.SessionId == SessionId;

    public static (List<Rule> Active, bool Uncertain) ActiveRules(IEnumerable<Rule> rules)
    {
        var active = new List<Rule>();
        var uncertain = false;
        foreach (var group in rules.GroupBy(rule => rule.Name, PathComparer))
        {
            var paths = SessionPaths(Process.GetProcessesByName(group.Key), ref uncertain);
            active.AddRange(group.Where(rule => paths.Contains(rule.Path)));
        }
        return (active, uncertain);
    }

    public static string[] AvailablePaths()
    {
        var uncertain = false;
        return SessionPaths(Process.GetProcesses(), ref uncertain).Order(PathComparer).ToArray();
    }

    private static HashSet<string> SessionPaths(Process[] processes, ref bool uncertain)
    {
        var paths = new HashSet<string>(PathComparer);
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == SessionId && !process.HasExited)
                    {
                        var path = ImagePath(process.Id);
                        if (path is not null)
                            paths.Add(path);
                        else if (!process.HasExited)
                            uncertain = true;
                    }
                }
                catch (InvalidOperationException) { } // The process exited during enumeration.
                catch (Win32Exception) { uncertain = true; }
            }
        }
        return paths;
    }

    public static string? ImagePath(int processId)
    {
        using var handle = OpenProcess(0x1000, false, processId);
        if (handle.IsInvalid)
            return null;
        var buffer = new StringBuilder(32768);
        var length = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags,
        StringBuilder path, ref int size);
}
