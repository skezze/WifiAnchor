using WifiBssidTool.Infrastructure;
using WifiBssidTool.Localization;
using WifiBssidTool.Wlan;

namespace WifiBssidTool.Services;

internal enum WatchState
{
    NotConfigured,
    Paused,
    NoAdapter,
    OnTarget,
    Connecting,
    Searching
}

internal sealed record WatchTarget(string Ssid, string Bssid, string Profile);

internal sealed record WatchStatus(
    WatchState State,
    string Message,
    ConnectionInfo? Current,
    AccessPoint? TargetAp,
    DateTime Since);

/// <summary>
/// Фоновая служба: держит подключение к выбранной точке. Пока точка
/// доступна и подключение к ней есть, ничего не делает. Если ушли на
/// другую точку, ищет выбранную (сканирует эфир) и просит Windows
/// подключиться к ней. События приходят из фонового потока.
/// </summary>
internal sealed class WatcherService : IDisposable
{
    private const int TickMs = 1000;
    private const int ScanCooldownMs = 15000;
    private const int SearchCooldownMs = 10000;
    private const int ScanWaitMs = 4000;
    private const int ReconnectCooldownMs = 10000;
    private const int VerifyWaitMs = 8000;
    private const int ApCacheReadMs = 3000;
    private const int HardReconnectAfterFailures = 2;

    private readonly CancellationTokenSource cts = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly object gate = new();
    private Thread? thread;

    // Доступны из любого потока.
    private volatile WatchTarget? target;
    private WatchTarget? pendingTarget;
    private bool pendingReset;
    private volatile bool paused;
    private volatile bool scanRequested;
    private volatile bool watchAccessPoints;
    private volatile WatchStatus status = new(
        WatchState.NotConfigured, Loc.T("watch.starting"), null, null, DateTime.Now);
    private volatile IReadOnlyList<AccessPoint> lastAps = Array.Empty<AccessPoint>();

    // Только поток слежения.
    private WlanClient? client;
    private string lastClientError = "";
    private int noAdapterTicks;
    private DateTime lastScan = DateTime.MinValue;
    private DateTime scanReadyAt = DateTime.MinValue;
    private DateTime nextAttempt = DateTime.MinValue;
    private DateTime verifyUntil = DateTime.MinValue;
    private DateTime nextApRead = DateTime.MinValue;
    private int failures;
    private bool? wasOk;
    private bool? targetVisible;
    private bool outageNotified;
    private AccessPoint? lastTargetAp;
    private uint lastScanError;
    private WatchState state = WatchState.NotConfigured;
    private DateTime since = DateTime.Now;

    public event Action<WatchStatus>? StatusChanged;
    public event Action<IReadOnlyList<AccessPoint>>? AccessPointsUpdated;
    public event Action<string, string>? Notification;

    public WatchStatus Status => status;
    public IReadOnlyList<AccessPoint> LastAccessPoints => lastAps;

    public bool Paused
    {
        get => paused;
        set
        {
            paused = value;
            lock (gate)
                pendingReset = true;
            wake.Set();
        }
    }

    /// <summary>Пока true, список точек из кэша Windows читается каждые пару секунд.</summary>
    public bool WatchAccessPoints
    {
        get => watchAccessPoints;
        set
        {
            watchAccessPoints = value;
            wake.Set();
        }
    }

    public void Start(WatchTarget? initial)
    {
        target = initial;

        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WifiWatcher"
        };
        thread.Start();
    }

    public void SetTarget(WatchTarget newTarget)
    {
        lock (gate)
            pendingTarget = newTarget;

        wake.Set();
    }

    /// <summary>Запросить свежее сканирование эфира.</summary>
    public void RequestScan()
    {
        scanRequested = true;
        wake.Set();
    }

    public void Dispose()
    {
        cts.Cancel();
        wake.Set();
        thread?.Join(3000);
        cts.Dispose();
        wake.Dispose();
    }

    // ---------- Основной цикл ----------

    private void Run()
    {
        AppLog.Write("Watcher service started.");

        while (!cts.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                AppLog.Write($"Error: {ex.Message}");
                DropClient();
                Publish(WatchState.NoAdapter, Loc.T("watch.apiError", ex.Message), null);
            }

            WaitHandle.WaitAny(new[] { cts.Token.WaitHandle, wake }, TickMs);
        }

        DropClient();
    }

    private void Tick()
    {
        lock (gate)
        {
            if (pendingTarget != null)
            {
                target = pendingTarget;
                pendingTarget = null;
                pendingReset = true;
                AppLog.Write(
                    $"New target access point: {target.Bssid} (network \"{target.Ssid}\").");
            }

            if (pendingReset)
            {
                pendingReset = false;
                ResetRuntime();
            }
        }

        DateTime now = DateTime.Now;
        WatchTarget? t = target;

        if (!EnsureClient())
        {
            Publish(WatchState.NoAdapter, Loc.T("watch.serviceUnavailable"), null);
            return;
        }

        WlanClient wlan = client!;

        if (wlan.GetInterface() == null)
        {
            // После сна или выключения адаптера дескриптор может устареть.
            if (++noAdapterTicks % 5 == 0)
                DropClient();

            Publish(WatchState.NoAdapter, Loc.T("watch.noAdapter"), null);
            return;
        }

        noAdapterTicks = 0;

        ConnectionInfo? conn = wlan.GetCurrentConnection();

        // Свежие результаты сканирования или чтение кэша для окна.
        bool scanArrived = false;

        if (scanReadyAt != DateTime.MinValue && now >= scanReadyAt)
        {
            scanReadyAt = DateTime.MinValue;
            nextApRead = now.AddMilliseconds(ApCacheReadMs);
            PublishAccessPoints(wlan.GetAccessPoints(), t);
            scanArrived = true;
        }
        else if (watchAccessPoints && now >= nextApRead)
        {
            nextApRead = now.AddMilliseconds(ApCacheReadMs);
            PublishAccessPoints(wlan.GetAccessPoints(), t);
        }

        if (scanRequested)
        {
            scanRequested = false;

            if (scanReadyAt == DateTime.MinValue)
                StartScan(wlan, now);
        }

        if (t == null || t.Bssid.Length == 0)
        {
            Publish(WatchState.NotConfigured, Loc.T("watch.notConfigured"), conn);
            return;
        }

        if (paused)
        {
            Publish(WatchState.Paused, Loc.T("watch.paused"), conn);
            return;
        }

        bool ok = conn != null &&
                  conn.Bssid == t.Bssid &&
                  conn.Ssid.Equals(t.Ssid, StringComparison.Ordinal);

        if (ok)
        {
            if (wasOk != true)
            {
                AppLog.Write($"Connected to the target access point {t.Bssid}.");

                if (outageNotified)
                {
                    outageNotified = false;
                    Notification?.Invoke(
                        "Wi-Fi", Loc.T("watch.reconnected"));
                }
            }

            wasOk = true;
            failures = 0;
            lastScan = DateTime.MinValue;
            verifyUntil = DateTime.MinValue;
            targetVisible = null;

            Publish(WatchState.OnTarget, Loc.T("watch.onTarget"), conn);
            return;
        }

        if (wasOk != false)
        {
            string cur = conn == null
                ? "not connected"
                : $"{conn.Ssid} / {conn.Bssid}";
            AppLog.Write($"Not the target access point: {cur}. Wanted: {t.Bssid}.");
        }

        wasOk = false;

        if (now < verifyUntil)
        {
            Publish(WatchState.Connecting, Loc.T("watch.connecting"), conn);
            return;
        }

        if (verifyUntil != DateTime.MinValue)
        {
            verifyUntil = DateTime.MinValue;
            failures++;
            AppLog.Write($"Connection failed (failures in a row: {failures}).");
        }

        if (scanArrived)
            HandleScanResult(wlan, t, conn, now);

        bool visible = targetVisible == true;
        int cooldown = visible ? ScanCooldownMs : SearchCooldownMs;

        if (scanReadyAt == DateTime.MinValue &&
            (now - lastScan).TotalMilliseconds >= cooldown)
        {
            StartScan(wlan, now);
        }

        if (verifyUntil > now)
        {
            Publish(WatchState.Connecting, Loc.T("watch.connecting"), conn);
        }
        else if (visible)
        {
            Publish(WatchState.Connecting,
                Loc.T(now < nextAttempt
                    ? "watch.visibleWaiting"
                    : "watch.visibleRequesting"),
                conn);
        }
        else
        {
            Publish(WatchState.Searching,
                Loc.T(scanReadyAt != DateTime.MinValue || targetVisible == null
                    ? "watch.searchScanning"
                    : "watch.searchContinue"),
                conn);
        }
    }

    private void HandleScanResult(
        WlanClient wlan, WatchTarget t, ConnectionInfo? conn, DateTime now)
    {
        AccessPoint? ap = lastAps
            .Where(a => a.Bssid == t.Bssid)
            .OrderByDescending(a => a.Rssi)
            .FirstOrDefault();

        lastTargetAp = ap;

        if (ap == null)
        {
            if (targetVisible != false)
                AppLog.Write("Target access point is not visible (off or out of range).");

            targetVisible = false;
            failures = 0;

            if (!outageNotified)
            {
                outageNotified = true;
                Notification?.Invoke(
                    Loc.T("watch.lostTitle"), Loc.T("watch.lostText"));
            }

            return;
        }

        if (targetVisible != true)
        {
            AppLog.Write(
                $"Target access point is visible (signal {ap.Signal}%, channel {ap.Channel}).");
        }

        targetVisible = true;

        if (now < nextAttempt)
            return;

        if (TryConnect(wlan, t, conn))
            verifyUntil = DateTime.Now.AddMilliseconds(VerifyWaitMs);
        else
            failures++;

        nextAttempt = DateTime.Now.AddMilliseconds(ReconnectCooldownMs);
    }

    /// <summary>
    /// Первые попытки — обычный запрос подключения к BSSID. Если Windows
    /// упорно остаётся на другой точке, делаем «жёсткое» переподключение:
    /// сначала отключаемся, затем подключаемся к нужной точке.
    /// </summary>
    private bool TryConnect(WlanClient wlan, WatchTarget t, ConnectionInfo? conn)
    {
        string profile = ResolveProfile(wlan, t, conn);

        if (failures >= HardReconnectAfterFailures)
        {
            AppLog.Write("The regular request did not help. Disconnecting and reconnecting.");
            wlan.Disconnect();
            cts.Token.WaitHandle.WaitOne(1500);
        }

        uint result = wlan.ConnectToBssid(profile, t.Bssid);

        if (result == 0)
        {
            AppLog.Write("Connection request passed to Windows.");
            return true;
        }

        AppLog.Write(
            $"Connection request error: {WlanClient.Describe(result)}");
        return false;
    }

    private string ResolveProfile(
        WlanClient wlan, WatchTarget t, ConnectionInfo? conn)
    {
        if (!string.IsNullOrWhiteSpace(t.Profile))
            return t.Profile;

        List<string> profiles = wlan.GetProfileNames();

        string? match = profiles.FirstOrDefault(p =>
            p.Equals(t.Ssid, StringComparison.OrdinalIgnoreCase));

        if (match != null)
            return match;

        if (conn != null &&
            conn.Ssid.Equals(t.Ssid, StringComparison.Ordinal) &&
            conn.Profile.Length > 0)
        {
            return conn.Profile;
        }

        match = profiles.FirstOrDefault(p =>
            p.StartsWith(t.Ssid, StringComparison.OrdinalIgnoreCase));

        if (match != null)
            return match;

        AppLog.Write(
            $"Warning: no Wi-Fi profile found for \"{t.Ssid}\". " +
            "Connect to the network manually once (with the password saved).");

        return t.Ssid;
    }

    // ---------- Вспомогательное ----------

    private void StartScan(WlanClient wlan, DateTime now)
    {
        uint r = wlan.RequestScan();
        lastScan = now;
        scanReadyAt = now.AddMilliseconds(ScanWaitMs);

        if (r != 0 && r != lastScanError)
            AppLog.Write($"Scan error: {WlanClient.Describe(r)}");

        lastScanError = r;
    }

    private void PublishAccessPoints(List<AccessPoint> aps, WatchTarget? t)
    {
        lastAps = aps;

        if (t != null && t.Bssid.Length > 0)
        {
            lastTargetAp = aps
                .Where(a => a.Bssid == t.Bssid)
                .OrderByDescending(a => a.Rssi)
                .FirstOrDefault();
        }

        AccessPointsUpdated?.Invoke(aps);
    }

    private void ResetRuntime()
    {
        lastScan = DateTime.MinValue;
        nextAttempt = DateTime.MinValue;
        verifyUntil = DateTime.MinValue;
        failures = 0;
        wasOk = null;
        targetVisible = null;
        lastTargetAp = null;
        outageNotified = false;
    }

    private bool EnsureClient()
    {
        if (client != null)
            return true;

        try
        {
            client = WlanClient.Open();
            lastClientError = "";
            return true;
        }
        catch (Exception ex)
        {
            if (ex.Message != lastClientError)
            {
                lastClientError = ex.Message;
                AppLog.Write(ex.Message);
            }

            return false;
        }
    }

    private void DropClient()
    {
        client?.Dispose();
        client = null;
    }

    private void Publish(WatchState newState, string message, ConnectionInfo? conn)
    {
        DateTime now = DateTime.Now;

        if (newState != state)
        {
            state = newState;
            since = now;
        }

        var next = new WatchStatus(newState, message, conn, lastTargetAp, since);

        if (next == status)
            return;

        status = next;
        StatusChanged?.Invoke(next);
    }
}
