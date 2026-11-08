using System.ComponentModel;
using System.Text.Json;

namespace SteamPause;

internal sealed record MonitorResult(string Detail, bool Attention = false);

internal sealed class Monitor : IDisposable
{
    private const string LimitJournal = "limit-change.json";
    private const string PauseJournal = "pause-change.json";
    private const int ScheduledCheckIntervalMs = 10_000;
    private DownloadLimitChange? limit = LocalFiles.Read<DownloadLimitChange>(LimitJournal);
    private PauseChange? pause = LocalFiles.Read<PauseChange>(PauseJournal);
    private SteamRun? steam;
    private SteamClient? client;
    private long retryAt;
    private string? steamError;
    private long nextScheduledCheck;
    private MonitorResult? scheduledStatus;

    public MonitorResult Update(Settings settings, bool retryNow = false)
    {
        var startScheduled = settings.Enabled && settings.StartScheduledUpdates;
        if (!startScheduled)
            ResetScheduledCheck();

        var (active, uncertain) = Processes.ActiveRules(settings.Enabled ? settings.Rules : []);
        if (uncertain)
            return new("Ein überwachter Prozess konnte nicht eindeutig geprüft werden.", true);

        if (active.Count == 0 && limit is null && pause is null && !startScheduled)
            return Idle(settings.Enabled);

        var current = SteamRun.FindRunning();
        if (steam != current)
        {
            CloseClient();
            steam = current;
            retryAt = 0;
            ResetScheduledCheck();
        }
        if (limit is not null && limit.Steam != current && !limit.Steam.IsRunning())
            SaveLimit(null);
        if (pause is not null && pause.Steam != current && !pause.Steam.IsRunning())
            SavePause(null);

        if (active.Count == 0 && limit is null && pause is null && !startScheduled)
            return Idle(settings.Enabled);
        if (current is null || limit is not null && limit.Steam != current || pause is not null && pause.Steam != current)
        {
            if (limit is not null || pause is not null)
                return new("Warte auf die ursprüngliche Steam-Sitzung.", true);
            return new(active.Count > 0 ? "Regel aktiv. Warte auf Steam." : "Warte auf Steam.");
        }
        if (!retryNow && Environment.TickCount64 < retryAt)
            return new(steamError!, true);

        var state = Send(current, new SteamRequest(SteamOperation.Read)).State!;
        ObserveChanges(state);

        var names = string.Join(", ", active.Select(rule => rule.Name));
        if (active.Any(rule => rule.KilobitsPerSecond is null))
        {
            ResetScheduledCheck();
            return ApplyPause(current, state, names);
        }

        var wantedRate = active.Min(rule => rule.KilobitsPerSecond);
        if (!ApplyRate(current, state.Rate, wantedRate, out var effectiveRate))
            return new("Steam hat das gewünschte Downloadlimit nicht bestätigt.", true);

        var limitStatus = wantedRate is null ? null : $"Steam-Limit: {effectiveRate / 8000m:0.###} MB/s · {names}";
        if (pause is not null)
        {
            if (!pause.Confirmed)
                return new("Die vorherige Pause konnte nicht bestätigt werden. Prüfe den Download in Steam.", true);
            var response = Send(current, new SteamRequest(SteamOperation.SetDownloadsEnabled, Expected: 0, Target: 1));
            if (!response.State!.DownloadsEnabled)
                return new("Steam hat das Fortsetzen nicht bestätigt.", true);
            SavePause(null);
        }
        else if (startScheduled)
        {
            var result = CheckScheduledUpdate(current, state);
            return limitStatus is null ? result : result with { Detail = $"{limitStatus}\n{result.Detail}" };
        }

        return limitStatus is null ? Idle(settings.Enabled) : new(limitStatus);
    }

    private MonitorResult CheckScheduledUpdate(SteamRun current, SteamState state)
    {
        if (!state.DownloadsEnabled)
        {
            ResetScheduledCheck();
            return new("Steam ist pausiert. Geplante Updates warten.");
        }
        if (scheduledStatus is not null && Environment.TickCount64 < nextScheduledCheck)
            return scheduledStatus;

        var response = Send(current, new SteamRequest(SteamOperation.StartScheduledUpdate));
        nextScheduledCheck = Environment.TickCount64 + ScheduledCheckIntervalMs;
        scheduledStatus = response.ScheduledUpdate switch
        {
            ScheduledUpdateStatus.Paused => new("Steam ist pausiert. Geplante Updates warten."),
            ScheduledUpdateStatus.QueueBusy => new("Geplante Updates warten auf eine freie Warteschlange."),
            ScheduledUpdateStatus.None => new("Keine geplanten Steam-Updates gefunden."),
            ScheduledUpdateStatus.Requested => new($"Start bei Steam angefordert (App-ID {response.ScheduledAppId})."),
            ScheduledUpdateStatus.Rejected => new($"Steam hat den Start des geplanten Updates nicht bestätigt (App-ID {response.ScheduledAppId}).", true),
            _ => new("Der Steam-Hintergrundprozess hat keinen Update-Status geliefert.", true)
        };
        return scheduledStatus;
    }

    private void ResetScheduledCheck()
    {
        nextScheduledCheck = 0;
        scheduledStatus = null;
    }

    private void ObserveChanges(SteamState state)
    {
        if (pause is not null && state.DownloadsEnabled)
            SavePause(null);
        if (limit is not null)
        {
            if (state.Rate == limit.Pending)
                SaveLimit(state.Rate == limit.Original ? null : limit with { Applied = state.Rate, Pending = null });
            else if (state.Rate != limit.Applied)
                SaveLimit(null); // Steam or the user has changed the limit since the app applied it.
        }
    }

    private MonitorResult ApplyPause(SteamRun current, SteamState state, string names)
    {
        if (!state.DownloadsEnabled)
        {
            if (pause is { Confirmed: false })
                return new("Steam ist pausiert. Die Ursache konnte nicht eindeutig bestätigt werden.", true);
            return new(pause is null ? $"Steam war bereits pausiert · {names}" : $"Steam pausiert · {names}");
        }

        SavePause(new PauseChange(current, Confirmed: false));
        var response = Send(current, new SteamRequest(SteamOperation.SetDownloadsEnabled, Expected: 1, Target: 0));
        if (response.Applied && !response.State!.DownloadsEnabled)
        {
            SavePause(new PauseChange(current, Confirmed: true));
            return new($"Steam pausiert · {names}");
        }

        SavePause(null);
        return response.State!.DownloadsEnabled ? new("Steam hat die Pause nicht bestätigt.", true)
            : new($"Steam war bereits pausiert · {names}");
    }

    private bool ApplyRate(SteamRun current, int observed, int? wanted, out int effectiveRate)
    {
        var original = limit?.Original ?? observed;
        var target = wanted is int requested && (original == 0 || requested < original) ? requested : original;
        effectiveRate = target;
        if (target != observed)
        {
            var previous = limit;
            SaveLimit(new DownloadLimitChange(current, original, observed, target));
            var response = Send(current, new SteamRequest(SteamOperation.SetRate, observed, target));
            if (!response.Applied)
            {
                // A concurrent change wins over this request. Re-evaluate it on the next tick.
                SaveLimit(response.State!.Rate == observed ? previous : null);
                return false;
            }
            if (response.State!.Rate != target)
                return false;
        }
        SaveLimit(target == original ? null : new DownloadLimitChange(current, original, target));
        return true;
    }

    private SteamResponse Send(SteamRun current, SteamRequest request)
    {
        try
        {
            client ??= new SteamClient(current);
            return client.Request(request);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception
            or TimeoutException or JsonException)
        {
            CloseClient();
            steamError = error is TimeoutException ? "Der native Steam-Zugriff hat nicht rechtzeitig geantwortet." : error.Message;
            retryAt = Environment.TickCount64 + 8000;
            throw new InvalidOperationException(steamError, error);
        }
    }

    private MonitorResult Idle(bool enabled)
    {
        CloseClient();
        return new(enabled ? "Bereit. Keine Regel aktiv." : "Automatik ausgeschaltet.");
    }

    private void SaveLimit(DownloadLimitChange? change)
    {
        if (limit != change)
        {
            LocalFiles.Write(LimitJournal, change);
            limit = change;
        }
    }

    private void SavePause(PauseChange? change)
    {
        if (pause != change)
        {
            LocalFiles.Write(PauseJournal, change);
            pause = change;
        }
    }

    private void CloseClient()
    {
        client?.Dispose();
        client = null;
    }

    public void Dispose() => CloseClient();
}
