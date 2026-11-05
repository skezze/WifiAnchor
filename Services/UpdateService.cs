using Velopack;
using Velopack.Sources;
using WifiAnchor.Infrastructure;

namespace WifiAnchor.Services;

internal enum UpdateState
{
    Idle,
    Checking,
    Downloading,
    Ready,
    UpToDate,
    NotInstalled,
    Failed
}

/// <param name="Detail">Версия (Downloading, Ready) или текст ошибки (Failed).</param>
internal sealed record UpdateStatus(UpdateState State, string Detail = "");

/// <summary>
/// Автообновление через GitHub Releases (Velopack). Новая версия скачивается
/// в фоне и ставится по запросу пользователя либо при следующем запуске.
/// События приходят из фонового потока.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private const string RepoUrl = "https://github.com/skezze/WifiAnchor";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager manager =
        new(new GithubSource(RepoUrl, null, false));

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly System.Threading.Timer timer;
    private volatile bool autoCheck;
    private volatile UpdateStatus status = new(UpdateState.Idle);
    private VelopackAsset? pending;

    public UpdateService(bool autoCheck)
    {
        this.autoCheck = autoCheck;

        timer = new System.Threading.Timer(
            _ => _ = RunScheduledCheck(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        pending = SafeGetPending();

        if (pending != null)
            status = new UpdateStatus(UpdateState.Ready, pending.Version.ToString());
    }

    public event Action<UpdateStatus>? StatusChanged;

    public UpdateStatus Status => status;

    /// <summary>Приложение установлено установщиком (а не запущено из папки сборки).</summary>
    public bool IsInstalled => manager.IsInstalled;

    public string CurrentVersion =>
        manager.CurrentVersion?.ToString() ??
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ??
        "";

    public bool AutoCheck
    {
        get => autoCheck;
        set
        {
            autoCheck = value;
            Schedule(value ? FirstCheckDelay : Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Запускает периодическую проверку.</summary>
    public void Start()
    {
        if (autoCheck)
            Schedule(FirstCheckDelay);
    }

    /// <summary>Проверка и скачивание. Ручной вызов показывает и «обновлений нет».</summary>
    public async Task CheckAsync(bool manual)
    {
        if (!manager.IsInstalled)
        {
            if (manual)
                Publish(new UpdateStatus(UpdateState.NotInstalled));

            return;
        }

        if (!await gate.WaitAsync(0))
            return;

        try
        {
            if (status.State == UpdateState.Ready)
            {
                Publish(status);
                return;
            }

            Publish(new UpdateStatus(UpdateState.Checking));
            AppLog.Write("Checking for updates...");

            UpdateInfo? info = await manager.CheckForUpdatesAsync();

            if (info == null)
            {
                AppLog.Write("No updates found.");
                Publish(new UpdateStatus(
                    manual ? UpdateState.UpToDate : UpdateState.Idle));
                return;
            }

            string version = info.TargetFullRelease.Version.ToString();
            AppLog.Write($"Update found: {version}. Downloading...");
            Publish(new UpdateStatus(UpdateState.Downloading, version));

            await manager.DownloadUpdatesAsync(info);

            pending = info.TargetFullRelease;
            AppLog.Write($"Update {version} downloaded and ready to install.");
            Publish(new UpdateStatus(UpdateState.Ready, version));
        }
        catch (Exception ex)
        {
            AppLog.Write($"Update check failed: {ex.Message}");

            // Фоновые сбои (нет сети и т.п.) пользователю не показываем.
            Publish(manual
                ? new UpdateStatus(UpdateState.Failed, ex.Message)
                : new UpdateStatus(UpdateState.Idle));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Устанавливает скачанное обновление и перезапускает программу.</summary>
    public bool ApplyAndRestart(bool minimized)
    {
        if (pending == null)
            return false;

        AppLog.Write($"Applying update {pending.Version} and restarting.");
        manager.ApplyUpdatesAndRestart(
            pending, minimized ? new[] { "--minimized" } : null);
        return true;
    }

    public void Dispose()
    {
        timer.Dispose();
        gate.Dispose();
    }

    private async Task RunScheduledCheck()
    {
        try
        {
            if (autoCheck)
                await CheckAsync(false);
        }
        finally
        {
            if (autoCheck)
                Schedule(CheckInterval);
        }
    }

    private void Schedule(TimeSpan due)
    {
        try
        {
            timer.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private VelopackAsset? SafeGetPending()
    {
        try
        {
            return manager.IsInstalled ? manager.UpdatePendingRestart : null;
        }
        catch
        {
            return null;
        }
    }

    private void Publish(UpdateStatus next)
    {
        status = next;
        StatusChanged?.Invoke(next);
    }
}
