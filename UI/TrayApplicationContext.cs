using WifiBssidTool.Configuration;
using WifiBssidTool.Infrastructure;
using WifiBssidTool.Services;
using WifiBssidTool.Wlan;

namespace WifiBssidTool.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppConfig config;
    private readonly WatcherService watcher;
    private readonly MainForm form;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem miStatus;
    private readonly ToolStripMenuItem miPause;
    private readonly ToolStripMenuItem miAutostart;
    private readonly RegisteredWaitHandle? showWait;
    private bool exiting;

    public TrayApplicationContext(
        bool startMinimized, bool smokeTest, EventWaitHandle? showEvent)
    {
        config = AppConfig.Load(out string? warning);

        if (warning != null)
            AppLog.Write(warning);

        if (!smokeTest && !File.Exists(AppConfig.FilePath))
            config.TrySave(out _);

        watcher = new WatcherService();
        form = new MainForm(config, watcher);
        _ = form.Handle; // нужен для BeginInvoke, даже пока окно скрыто

        var menu = new ContextMenuStrip();
        miStatus = new ToolStripMenuItem("Запуск...") { Enabled = false };

        var miOpen = new ToolStripMenuItem("Открыть окно", null, (_, _) => ShowWindow())
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        };
        var miPick = new ToolStripMenuItem("Сменить точку…", null, (_, _) => ShowPicker());
        miPause = new ToolStripMenuItem("Приостановить", null, (_, _) => TogglePause());
        miAutostart = new ToolStripMenuItem(
            "Запускать вместе с Windows", null, (_, _) => ToggleAutostart());
        var miExit = new ToolStripMenuItem("Выход", null, (_, _) => ExitApp());

        menu.Items.AddRange(new ToolStripItem[]
        {
            miStatus, new ToolStripSeparator(),
            miOpen, miPick, miPause, new ToolStripSeparator(),
            miAutostart, new ToolStripSeparator(),
            miExit
        });
        menu.Opening += (_, _) => miAutostart.Checked = AutoStart.IsEnabled;

        tray = new NotifyIcon
        {
            Icon = StateStyle.Icon(WatchState.NotConfigured),
            Text = "Wi-Fi BSSID Watcher",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowWindow();
        };

        watcher.StatusChanged += s => Post(() => OnStatus(s));
        watcher.AccessPointsUpdated += a => Post(() => form.ApplyAccessPoints(a));
        watcher.Notification += (title, text) => Post(() =>
        {
            if (config.ShowNotifications)
                tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
        });

        form.TargetChanged += () => OnStatus(watcher.Status);
        form.ExitRequested += ExitApp;
        form.HiddenToTray += OnHiddenToTray;

        if (showEvent != null)
        {
            showWait = ThreadPool.RegisterWaitForSingleObject(
                showEvent, (_, _) => Post(ShowWindow), null,
                Timeout.Infinite, false);
        }

        if (smokeTest)
        {
            RunSmokeTest();
            return;
        }

        AutoStart.Refresh();

        watcher.Start(config.HasTarget
            ? new WatchTarget(config.Ssid, config.Bssid, config.Profile)
            : null);

        if (!startMinimized)
        {
            ShowWindow();
        }
        else if (!config.HasTarget)
        {
            tray.ShowBalloonTip(5000, "Wi-Fi BSSID Watcher",
                "Точка ещё не выбрана. Щёлкните по значку, чтобы выбрать.",
                ToolTipIcon.Info);
        }
    }

    // ---------- Окно ----------

    private void ShowWindow()
    {
        if (exiting)
            return;

        if (!form.Visible)
            form.Show();

        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;

        form.Activate();
    }

    private void ShowPicker()
    {
        ShowWindow();
        form.FocusPicker();
    }

    private void OnHiddenToTray()
    {
        if (config.TrayHintShown)
            return;

        config.TrayHintShown = true;
        config.TrySave(out _);

        tray.ShowBalloonTip(6000, "Wi-Fi BSSID Watcher",
            "Программа продолжает работать в трее. " +
            "Выйти можно через меню значка.",
            ToolTipIcon.Info);
    }

    // ---------- Меню ----------

    private void TogglePause() => watcher.Paused = !watcher.Paused;

    private void ToggleAutostart()
    {
        try
        {
            AutoStart.Set(!AutoStart.IsEnabled);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Не удалось изменить автозапуск: {ex.Message}");
        }
    }

    private void OnStatus(WatchStatus s)
    {
        tray.Icon = StateStyle.Icon(s.State);

        string who = config.HasTarget
            ? (config.NameOf(config.Bssid) is { Length: > 0 } name ? name : config.Bssid)
            : "точка не выбрана";

        string text = $"{who}: {StateStyle.Title(s.State)}";
        tray.Text = text.Length > 120 ? text[..120] : text;

        miStatus.Text = $"{StateStyle.Title(s.State)}";
        miPause.Text = s.State == WatchState.Paused ? "Продолжить" : "Приостановить";
        miPause.Enabled = s.State != WatchState.NotConfigured;

        form.ApplyStatus(s);
    }

    private void Post(Action action)
    {
        try
        {
            if (!form.IsDisposed && form.IsHandleCreated)
                form.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Окно уже закрывается (включая ObjectDisposedException).
        }
    }

    // ---------- Выход ----------

    private void ExitApp()
    {
        if (exiting)
            return;

        exiting = true;
        form.AllowExit = true;
        tray.Visible = false;
        showWait?.Unregister(null);
        watcher.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose();
            form.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---------- Проверка интерфейса без Wi-Fi (--smoke-test) ----------

    private void RunSmokeTest()
    {
        var points = new[]
        {
            new AccessPoint("WLAN-SR", "d8:b3:70:d8:19:55", 60, -73, "6 (2.4 ГГц)"),
            new AccessPoint("WLAN-SR", "f4:e2:c6:6e:d8:e5", 20, -81, "1 (2.4 ГГц)"),
            new AccessPoint("Other", "46:62:83:df:66:68", 100, -50, "153 (5 ГГц)")
        };

        ShowWindow();
        form.ApplyAccessPoints(points);

        var conn = new ConnectionInfo("WLAN-SR", "f4:e2:c6:6e:d8:e5", "WLAN-SR", 20);

        foreach (WatchState state in Enum.GetValues<WatchState>())
        {
            OnStatus(new WatchStatus(
                state, "демо", conn, null, DateTime.Now));
        }

        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ExitApp();
        };
        timer.Start();
    }
}
