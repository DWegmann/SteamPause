using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace SteamDownloadControl;

internal static class Program
{
    public const string AppName = "Steam Download Control";
    public static Version AppVersion { get; } = typeof(Program).Assembly.GetName().Version!;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--steam-worker", var exe, var id, var started])
        {
            if (int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) &&
                long.TryParse(started, NumberStyles.None, CultureInfo.InvariantCulture, out var startedTicks))
                Environment.ExitCode = SteamWorker.Run(new SteamRun(exe, processId, startedTicks));
            return;
        }

        // Keep the same mutex as SteamPause so both versions cannot control Steam at once.
        using var instance = new Mutex(true, @"Local\SteamPause.Tray", out var firstInstance);
        if (!firstInstance)
            return;

        ApplicationConfiguration.Initialize();
        try
        {
            using var app = new TrayApp(args.Contains("--tray"));
            Application.Run(app);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show($"{AppName} konnte seine lokalen Einstellungen nicht laden.\n\n{error.Message}",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private const string SettingsFile = "settings.json";
    private const string CheckForAppUpdatesText = "Nach App-Updates suchen …";
    private const int AppUpdateCheckInterval = 12 * 60 * 60 * 1000;
    private readonly Monitor monitor = new();
    private readonly NotifyIcon icon;
    private readonly SettingsWindow window;
    private readonly ToolStripMenuItem toggle;
    private readonly ToolStripMenuItem appUpdateItem;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer appUpdateTimer = new() { Interval = 5000 };
    private Settings settings = LocalFiles.Read<Settings>(SettingsFile) ?? new();
    private readonly Icon activeIcon = new(typeof(TrayApp), "active.ico");
    private readonly Icon inactiveIcon = new(typeof(TrayApp), "inactive.ico");
    private Task<MonitorResult>? currentUpdate;
    private string? availableAppUpdate;
    private bool needsAttention;
    private bool exiting;

    public TrayApp(bool startHidden)
    {
        window = new SettingsWindow(SaveSettings);
        window.FormClosing += (_, eventArgs) =>
        {
            if (!exiting && eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                window.Hide();
            }
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Einstellungen öffnen", null, (_, _) => OpenSettings());
        toggle = new ToolStripMenuItem("Automatik aktiv", null, (_, _) =>
            SaveSettings(settings with { Enabled = !settings.Enabled }));
        menu.Items.Add(toggle);
        menu.Items.Add(new ToolStripSeparator());
        appUpdateItem = new ToolStripMenuItem(CheckForAppUpdatesText, null, async (_, _) =>
        {
            if (availableAppUpdate is { } tag)
                OpenAppRelease(tag);
            else
                await CheckForAppUpdate(showResult: true);
        });
        menu.Items.Add(appUpdateItem);
        menu.Items.Add("Beenden", null, async (_, _) => await Quit());
        icon = new NotifyIcon { Text = Program.AppName, ContextMenuStrip = menu };
        icon.DoubleClick += (_, _) => OpenSettings();
        icon.BalloonTipClicked += (_, _) =>
        {
            if (availableAppUpdate is { } tag)
                OpenAppRelease(tag);
        };
        window.RefreshSettings(settings);
        RefreshAutomation();
        icon.Visible = true;
        if (!startHidden)
            OpenSettings();

        timer.Tick += async (_, _) => await Update();
        timer.Start();
        appUpdateTimer.Tick += async (_, _) =>
        {
            appUpdateTimer.Interval = AppUpdateCheckInterval;
            await CheckForAppUpdate(showResult: false);
        };
        appUpdateTimer.Start();
    }

    private void OpenSettings()
    {
        window.Show();
        if (window.WindowState == FormWindowState.Minimized)
            window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    private void SaveSettings(Settings updated)
    {
        try
        {
            LocalFiles.Write(SettingsFile, updated);
            settings = updated;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(window, $"Die Einstellungen konnten nicht gespeichert werden.\n\n{error.Message}",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        window.RefreshSettings(settings);
        RefreshAutomation();
    }

    private void RefreshAutomation()
    {
        toggle.Checked = settings.Enabled;
        window.Icon = settings.Enabled ? activeIcon : inactiveIcon;
        RefreshTrayIcon();
    }

    private void RefreshTrayIcon() => icon.Icon = needsAttention ? SystemIcons.Warning : window.Icon;

    private async Task CheckForAppUpdate(bool showResult)
    {
        if (exiting || !appUpdateItem.Enabled)
            return;

        appUpdateItem.Enabled = false;
        string? tag;
        try
        {
            tag = await AppUpdates.FindNewerTagAsync();
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            if (showResult && !exiting)
                MessageBox.Show(window, "GitHub konnte nicht auf App-Updates geprüft werden.",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            appUpdateItem.Enabled = true;
        }

        if (exiting)
            return;

        var newlyAvailable = tag != availableAppUpdate;
        availableAppUpdate = tag;
        appUpdateItem.Text = tag is null ? CheckForAppUpdatesText : $"App-Update {tag} herunterladen …";
        if (tag is null)
        {
            if (showResult)
                MessageBox.Show(window, "Du verwendest bereits die aktuelle Version.",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else if (showResult)
        {
            if (MessageBox.Show(window, $"Version {tag} ist verfügbar. Downloadseite auf GitHub öffnen?",
                    Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                OpenAppRelease(tag);
        }
        else if (newlyAvailable)
        {
            icon.ShowBalloonTip(8000, $"{Program.AppName} {tag} verfügbar",
                "Klicken, um die Downloadseite auf GitHub zu öffnen.", ToolTipIcon.Info);
        }
    }

    private void OpenAppRelease(string tag)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppUpdates.ReleasePage(tag)) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(window, "Die Downloadseite auf GitHub konnte nicht geöffnet werden.",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task Update()
    {
        if (currentUpdate is not null || exiting)
            return;
        currentUpdate = RunMonitor(settings);
        var result = await currentUpdate;
        currentUpdate = null;
        ShowResult(result);
    }

    private static string Shorten(string text) => text.Length <= 63 ? text : text[..62] + "…";

    private void ShowResult(MonitorResult result)
    {
        icon.Text = Shorten(result.Detail);
        needsAttention = result.Attention;
        RefreshTrayIcon();
        window.ShowStatus(result);
    }

    private Task<MonitorResult> RunMonitor(Settings snapshot, bool retryNow = false) => Task.Run(() =>
    {
        try { return monitor.Update(snapshot, retryNow); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception
            or InvalidOperationException or System.Security.SecurityException)
        {
            return new MonitorResult(error.Message, true);
        }
    });

    private async Task Quit()
    {
        if (exiting)
            return;
        exiting = true;
        timer.Stop();
        if (currentUpdate is not null)
            await currentUpdate;
        var result = await RunMonitor(settings with { Enabled = false }, retryNow: true);
        if (result.Attention && MessageBox.Show(window,
                $"{result.Detail}\n\n{Program.AppName} trotzdem beenden?", Program.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            exiting = false;
            timer.Start();
            ShowResult(result);
            return;
        }
        window.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            appUpdateTimer.Dispose();
            timer.Dispose();
            monitor.Dispose();
            icon.Visible = false;
            icon.ContextMenuStrip?.Dispose();
            icon.Dispose();
            window.Dispose();
            inactiveIcon.Dispose();
            activeIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
