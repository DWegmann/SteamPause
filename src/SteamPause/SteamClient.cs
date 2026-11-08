using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace SteamPause;

internal sealed record SteamRun(string Exe, int Id, long Started)
{
    public static SteamRun? FindRunning()
    {
        using var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        using var machine = Registry.LocalMachine.OpenSubKey(@"Software\WOW6432Node\Valve\Steam");
        var directories = new[] { user?.GetValue("SteamPath") as string, machine?.GetValue("InstallPath") as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam") };
        var executables = directories.Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            .Select(path => Path.GetFullPath(Path.Combine(path!, "steam.exe"))).ToHashSet(Processes.PathComparer);

        var candidates = new List<SteamRun>();
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                try
                {
                    if (Processes.InCurrentSession(process) && Processes.ImagePath(process.Id) is { } exe &&
                        executables.Contains(exe) && !process.HasExited)
                        candidates.Add(new SteamRun(exe, process.Id, process.StartTime.ToUniversalTime().Ticks));
                }
                catch (InvalidOperationException) { } // Steam exited during enumeration.
            }
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    public bool IsRunning()
    {
        try
        {
            using var process = Process.GetProcessById(Id);
            return !process.HasExited && Processes.InCurrentSession(process) &&
                process.StartTime.ToUniversalTime().Ticks == Started &&
                Processes.PathComparer.Equals(Processes.ImagePath(Id), Exe);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

internal enum SteamOperation { Read, SetDownloadsEnabled, SetRate, StartScheduledUpdate }
internal enum ScheduledUpdateStatus { Paused, QueueBusy, None, Requested, Rejected }
internal sealed record SteamRequest(SteamOperation Operation, int Expected = 0, int Target = 0);
internal sealed record SteamState(bool DownloadsEnabled, int Rate);
internal sealed record SteamResponse(SteamState? State, bool Applied = false, string? Error = null,
    ScheduledUpdateStatus? ScheduledUpdate = null, uint? ScheduledAppId = null);

internal sealed class SteamClient : IDisposable
{
    private readonly Process worker;

    public SteamClient(SteamRun steam)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SteamPause.dll"));
        start.ArgumentList.Add("--steam-worker");
        start.ArgumentList.Add(steam.Exe);
        start.ArgumentList.Add(steam.Id.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(steam.Started.ToString(CultureInfo.InvariantCulture));
        worker = Process.Start(start) ?? throw new InvalidOperationException("Der Steam-Hintergrundprozess konnte nicht starten.");
    }

    public SteamResponse Request(SteamRequest request)
    {
        worker.StandardInput.WriteLine(JsonSerializer.Serialize(request));
        worker.StandardInput.Flush();
        var line = worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        var response = line is null ? null : JsonSerializer.Deserialize<SteamResponse>(line);
        if (response?.State is null)
            throw new InvalidOperationException(response?.Error ?? "Der Steam-Hintergrundprozess hat keine Antwort geliefert.");
        return response;
    }

    public void Dispose()
    {
        try
        {
            worker.StandardInput.Close();
        }
        catch (IOException) { }
        try
        {
            if (!worker.WaitForExit(500))
                worker.Kill();
        }
        catch (InvalidOperationException) { } // The worker already exited.
        catch (Win32Exception) { }
        finally { worker.Dispose(); }
    }
}
