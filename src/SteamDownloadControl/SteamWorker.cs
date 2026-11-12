using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SteamDownloadControl;

internal static class SteamWorker
{
    public static int Run(SteamRun steam)
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            if (Console.ReadLine() is not { } line)
                return 0;
            if (SteamRun.FindRunning() != steam)
                throw new InvalidOperationException("Die Steam-Sitzung hat sich geändert.");
            using var manager = NativeDownloadManager.Open(steam);
            do
            {
                if (!steam.IsRunning())
                    throw new InvalidOperationException("Steam wurde beendet oder neu gestartet.");
                var request = JsonSerializer.Deserialize<SteamRequest>(line)
                    ?? throw new InvalidOperationException("Ungültige Steam-Anfrage.");
                Reply(manager.Handle(request));
                line = Console.ReadLine();
            }
            while (line is not null);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception
            or InvalidOperationException or JsonException or BadImageFormatException or System.Security.SecurityException)
        {
            Reply(new SteamResponse(null, Error: error.Message));
            return 1;
        }
    }

    private static void Reply(SteamResponse response)
    {
        Console.WriteLine(JsonSerializer.Serialize(response));
        Console.Out.Flush();
    }
}

internal sealed class NativeDownloadManager : IDisposable
{
    private const int LoadLibrarySearchDllLoadDir = 0x00000100;
    private const int LoadLibrarySearchSystem32 = 0x00000800;
    private const string EngineVersion = "CLIENTENGINE_INTERFACE_VERSION005";
    private const uint UpdateAvailable = 0x02 | 0x10; // Required or optional.
    private const uint IneligibleUpdateState = 0x01 | 0x08 | 0x100 | 0x200; // Uninstalled, queued, running, or paused.
    private const int DownloadFirst = 1; // Downloads.ResumeAppUpdate in SteamUI.dll.
    private readonly nint manager;
    private readonly int pipe;
    private readonly int user;
    private readonly ReleaseUser releaseUser;
    private readonly ReleasePipe releasePipe;
    private readonly ReadEnabled readEnabled;
    private readonly ReadRate readRate;
    private readonly SetEnabled setEnabled;
    private readonly SetRate setRate;
    private readonly ReadAppValue readInstallState;
    private readonly ReadValue countInstalledApps;
    private readonly ReadInstalledApps readInstalledApps;
    private readonly ReadValue readDownloadingApp;
    private readonly ReadValue countQueuedApps;
    private readonly ReadAppValue readDeferredUntil;
    private readonly ChangeQueuePlacement changeQueuePlacement;

    private NativeDownloadManager(SteamInterface client, nint manager, int pipe, int user, ReleaseUser releaseUser,
        ReleasePipe releasePipe)
    {
        this.manager = manager;
        this.pipe = pipe;
        this.user = user;
        this.releaseUser = releaseUser;
        this.releasePipe = releasePipe;
        var methods = client.GetAppManagerMethods(manager);
        readEnabled = methods.Get<ReadEnabled>("BIsDownloadingEnabled");
        readRate = methods.Get<ReadRate>("GetDownloadThrottleRateKbps");
        setEnabled = methods.Get<SetEnabled>("SetDownloadingEnabled");
        setRate = methods.Get<SetRate>("SetDownloadThrottleRateKbps");
        readInstallState = methods.Get<ReadAppValue>("GetAppInstallState");
        countInstalledApps = methods.Get<ReadValue>("GetNumInstalledApps");
        readInstalledApps = methods.Get<ReadInstalledApps>("GetInstalledApps");
        readDownloadingApp = methods.Get<ReadValue>("GetDownloadingAppID");
        changeQueuePlacement = methods.Get<ChangeQueuePlacement>("ChangeAppDownloadQueuePlacement");
        readDeferredUntil = methods.Get<ReadAppValue>("GetAppAutoUpdateDelayedUntilTime");
        countQueuedApps = methods.Get<ReadValue>("GetNumAppsInDownloadQueue");
    }

    public static NativeDownloadManager Open(SteamRun steam)
    {
        var dllPath = Path.Combine(Path.GetDirectoryName(steam.Exe)!, "steamclient64.dll");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("Die Steam-Download-Schnittstelle benötigt einen x64-Prozess.");

        // Keep the file open across LoadLibraryEx so the verified image cannot be replaced in between.
        using var dll = File.OpenRead(dllPath);
        if (!SteamSignature.IsValid(dll))
            throw new InvalidOperationException("Die Valve-Signatur der Steam-Bibliothek konnte nicht bestätigt werden.");
        var module = LoadLibraryEx(dllPath, 0, LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
        if (module == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Die Steam-Bibliothek konnte nicht geladen werden.");

        using var client = new SteamInterface(module, dll);
        var factory = Export<CreateInterface>(module, "CreateInterface");
        var engine = factory(EngineVersion, out var result);
        if (result != 0 || engine == 0)
            throw new InvalidOperationException("Steam stellt die benötigte Engine-Schnittstelle nicht bereit.");
        // These slots are part of CLIENTENGINE_INTERFACE_VERSION005.
        var validate = client.GetEngineMethod<CheckHandles>(engine, 7);
        var getManager = client.GetEngineMethod<GetAppManager>(engine, 43);

        var createPipe = Export<CreatePipe>(module, "Steam_CreateSteamPipe");
        var connect = Export<ConnectUser>(module, "Steam_ConnectToGlobalUser");
        var releaseUser = Export<ReleaseUser>(module, "Steam_ReleaseUser");
        var releasePipe = Export<ReleasePipe>(module, "Steam_BReleaseSteamPipe");
        if (!steam.IsRunning())
            throw new InvalidOperationException("Steam wurde beendet oder neu gestartet.");
        var pipe = createPipe();
        if (pipe == 0)
            throw new InvalidOperationException("Steam konnte keinen lokalen Verbindungskanal bereitstellen.");

        var user = 0;
        try
        {
            user = connect(pipe);
            if (user == 0 || validate(engine, user, pipe) == 0)
                throw new InvalidOperationException("Die lokale Steam-Sitzung konnte nicht bestätigt werden.");
            var manager = getManager(engine, user, pipe);
            return new NativeDownloadManager(client, manager, pipe, user, releaseUser, releasePipe);
        }
        catch
        {
            if (user != 0)
                releaseUser(pipe, user);
            releasePipe(pipe);
            throw;
        }
    }

    public SteamResponse Handle(SteamRequest request)
    {
        var state = ReadState();
        if (request.Operation == SteamOperation.StartScheduledUpdate)
            return StartScheduledUpdate(state);

        var applied = request.Operation switch
        {
            SteamOperation.Read => false,
            SteamOperation.SetDownloadsEnabled when request.Expected == (state.DownloadsEnabled ? 1 : 0) &&
                request.Target is 0 or 1 && request.Expected != request.Target => setEnabled(manager, (byte)request.Target) != 0,
            SteamOperation.SetRate when request.Expected == state.Rate && request.Target >= 0 &&
                request.Expected != request.Target => setRate(manager, request.Target, persistent: 0) != 0,
            _ => false
        };
        return new SteamResponse(request.Operation == SteamOperation.Read ? state : ReadState(), applied);
    }

    private SteamResponse StartScheduledUpdate(SteamState state)
    {
        if (!state.DownloadsEnabled)
            return new(state, ScheduledUpdate: ScheduledUpdateStatus.Paused);
        if (QueueIsBusy())
            return new(state, ScheduledUpdate: ScheduledUpdateStatus.QueueBusy);

        var apps = new uint[countInstalledApps(manager)];
        var count = readInstalledApps(manager, apps, (uint)apps.Length);
        if (count > apps.Length)
            return new(state, ScheduledUpdate: ScheduledUpdateStatus.QueueBusy);

        uint nextApp = 0;
        uint nextTime = 0;
        for (var index = 0; index < count; index++)
        {
            var app = apps[index];
            var time = DeferredUpdateTime(app);
            if (time != 0 && (nextApp == 0 || time < nextTime || time == nextTime && app < nextApp))
                (nextApp, nextTime) = (app, time);
        }

        if (nextApp == 0)
            return new(state, ScheduledUpdate: ScheduledUpdateStatus.None);

        // Recheck after scanning, since the user or Steam may have changed the queue.
        if (DeferredUpdateTime(nextApp) != nextTime || QueueIsBusy())
            return new(state, ScheduledUpdate: ScheduledUpdateStatus.QueueBusy);
        if (readEnabled(manager) != 1)
            return new(ReadState(), ScheduledUpdate: ScheduledUpdateStatus.Paused);

        var accepted = changeQueuePlacement(manager, nextApp, DownloadFirst) != 0;
        return new(ReadState(), accepted, ScheduledUpdate: accepted ? ScheduledUpdateStatus.Requested :
            ScheduledUpdateStatus.Rejected, ScheduledAppId: nextApp);
    }

    private bool QueueIsBusy() => readDownloadingApp(manager) != 0 || countQueuedApps(manager) != 0;

    private uint DeferredUpdateTime(uint app)
    {
        var flags = readInstallState(manager, app);
        return (flags & UpdateAvailable) != 0 && (flags & IneligibleUpdateState) == 0 ? readDeferredUntil(manager, app) : 0;
    }

    private SteamState ReadState()
    {
        var enabled = readEnabled(manager);
        var rate = readRate(manager, 0);
        if (enabled > 1 || rate < 0)
            throw new InvalidOperationException("Steam meldet einen ungültigen Download-Zustand.");
        return new SteamState(enabled == 1, rate);
    }

    public void Dispose()
    {
        releaseUser(pipe, user);
        releasePipe(pipe);
        // The isolated process exits next. Steam's loaded module stays mapped until then.
    }

    private static T Export<T>(nint module, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(module, name, out var function))
            throw new InvalidOperationException($"In der Steam-Bibliothek fehlt die Funktion {name}.");
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateInterface([MarshalAs(UnmanagedType.LPStr)] string name, out int result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreatePipe();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ConnectUser(int pipe);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReleaseUser(int pipe, int user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ReleasePipe(int pipe);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte CheckHandles(nint engine, int user, int pipe);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetAppManager(nint engine, int user, int pipe);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ReadEnabled(nint manager);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadRate(nint manager, byte parameter);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SetEnabled(nint manager, byte enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SetRate(nint manager, int rate, byte persistent);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ReadValue(nint manager);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ReadAppValue(nint manager, uint appId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ReadInstalledApps(nint manager, [Out] uint[] apps, uint capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ChangeQueuePlacement(nint manager, uint appId, int placement);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string filename, nint file, uint flags);
}
