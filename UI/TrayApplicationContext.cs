using WifiBssidTool.Configuration;
using WifiBssidTool.Infrastructure;
using WifiBssidTool.Localization;
using WifiBssidTool.Services;
using WifiBssidTool.Wlan;

namespace WifiBssidTool.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string AppTitle = "Wi-Fi Anchor";

    private readonly AppConfig config;
    private readonly WatcherService watcher;
    private readonly UpdateService updates;
    private readonly MainForm form;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem miStatus;
    private readonly ToolStripMenuItem miOpen;
    private readonly ToolStripMenuItem miPick;
    private readonly ToolStripMenuItem miPause;
    private readonly ToolStripMenuItem miAutostart;
    private readonly ToolStripMenuItem miUpdate;
    private readonly ToolStripMenuItem miExit;
    private readonly ToolStripSeparator sepUpdate;
    private readonly RegisteredWaitHandle? showWait;
    private string notifiedUpdateVersion = "";
    private bool exiting;

    public TrayApplicationContext(
        bool startMinimized, bool smokeTest, string? languageOverride,
        EventWaitHandle? showEvent)
    {
        config = AppConfig.Load(out string? warning);
        Loc.SetLanguage(languageOverride ?? config.Language);

        if (warning != null)
            AppLog.Write(warning);

        if (!smokeTest && !File.Exists(AppConfig.FilePath))
            config.TrySave(out _);

        watcher = new WatcherService();
        updates = new UpdateService(config.AutoUpdate && !smokeTest);
        form = new MainForm(config, watcher, updates);
        _ = form.Handle; // нужен для BeginInvoke, даже пока окно скрыто

        var menu = new ContextMenuStrip();
        miStatus = new ToolStripMenuItem { Enabled = false };

        miOpen = new ToolStripMenuItem(null, null, (_, _) => ShowWindow())
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        };
        miPick = new ToolStripMenuItem(null, null, (_, _) => ShowPicker());
        miPause = new ToolStripMenuItem(null, null, (_, _) => TogglePause());
        miAutostart = new ToolStripMenuItem(null, null, (_, _) => ToggleAutostart());
        miUpdate = new ToolStripMenuItem(null, null, (_, _) => RestartForUpdate())
        {
            Visible = false,
            Font = new Font(menu.Font, FontStyle.Bold)
        };
        miExit = new ToolStripMenuItem(null, null, (_, _) => ExitApp());
        sepUpdate = new ToolStripSeparator { Visible = false };

        menu.Items.AddRange(new ToolStripItem[]
        {
            miStatus, new ToolStripSeparator(),
            miOpen, miPick, miPause, new ToolStripSeparator(),
            miAutostart, new ToolStripSeparator(),
            miUpdate, sepUpdate,
            miExit
        });
        menu.Opening += (_, _) => miAutostart.Checked = AutoStart.IsEnabled;

        tray = new NotifyIcon
        {
            Icon = StateStyle.Icon(WatchState.NotConfigured),
            Text = AppTitle,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowWindow();
        };

        ApplyMenuLocalization();
        Loc.LanguageChanged += OnLanguageChanged;

        watcher.StatusChanged += s => Post(() => OnStatus(s));
        watcher.AccessPointsUpdated += a => Post(() => form.ApplyAccessPoints(a));
        watcher.Notification += (title, text) => Post(() =>
        {
            if (config.ShowNotifications)
                tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
        });
        updates.StatusChanged += s => Post(() => OnUpdateStatus(s));

        form.TargetChanged += () => OnStatus(watcher.Status);
        form.ExitRequested += ExitApp;
        form.HiddenToTray += OnHiddenToTray;
        form.UpdateRestartRequested += RestartForUpdate;

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

        updates.Start();
        OnUpdateStatus(updates.Status);

        if (!startMinimized)
        {
            ShowWindow();
        }
        else if (!config.HasTarget)
        {
            tray.ShowBalloonTip(5000, AppTitle,
                Loc.T("tray.noTargetHint"), ToolTipIcon.Info);
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

        tray.ShowBalloonTip(6000, AppTitle,
            Loc.T("tray.hiddenHint"), ToolTipIcon.Info);
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
            AppLog.Write($"Could not change autostart: {ex.Message}");
        }
    }

    private void OnLanguageChanged() => Post(() =>
    {
        ApplyMenuLocalization();
        OnStatus(watcher.Status);
        OnUpdateStatus(updates.Status);
    });

    private void ApplyMenuLocalization()
    {
        miOpen.Text = Loc.T("tray.open");
        miPick.Text = Loc.T("tray.pick");
        miAutostart.Text = Loc.T("tray.autostart");
        miExit.Text = Loc.T("tray.exit");
        miStatus.Text = Loc.T("tray.starting");
    }

    private void OnStatus(WatchStatus s)
    {
        tray.Icon = StateStyle.Icon(s.State);

        string who = config.HasTarget
            ? (config.NameOf(config.Bssid) is { Length: > 0 } name ? name : config.Bssid)
            : Loc.T("tray.noTarget");

        string text = $"{who}: {StateStyle.Title(s.State)}";
        tray.Text = text.Length > 120 ? text[..120] : text;

        miStatus.Text = StateStyle.Title(s.State);
        miPause.Text = Loc.T(
            s.State == WatchState.Paused ? "action.resume" : "action.pause");
        miPause.Enabled = s.State != WatchState.NotConfigured;

        form.ApplyStatus(s);
    }

    // ---------- Обновления ----------

    private void OnUpdateStatus(UpdateStatus s)
    {
        form.ApplyUpdateStatus(s);

        bool ready = s.State == UpdateState.Ready;
        miUpdate.Visible = ready;
        sepUpdate.Visible = ready;

        if (!ready)
            return;

        miUpdate.Text = Loc.T("tray.updateReady", s.Detail);

        if (notifiedUpdateVersion != s.Detail)
        {
            notifiedUpdateVersion = s.Detail;

            tray.ShowBalloonTip(8000, Loc.T("upd.balloonTitle"),
                Loc.T("upd.balloonText", s.Detail), ToolTipIcon.Info);
        }
    }

    private void RestartForUpdate()
    {
        if (exiting)
            return;

        exiting = true;
        tray.Visible = false;

        try
        {
            // Возврат в трей, если окно было скрыто. При успехе процесс завершается внутри вызова.
            if (updates.ApplyAndRestart(!form.Visible))
                return;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not apply the update: {ex.Message}");
        }

        exiting = false;
        tray.Visible = true;
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
        updates.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Loc.LanguageChanged -= OnLanguageChanged;
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
            new AccessPoint("MyNetwork", "aa:bb:cc:dd:ee:01", 60, -73, "6 (2.4 GHz)"),
            new AccessPoint("MyNetwork", "aa:bb:cc:dd:ee:02", 20, -81, "1 (2.4 GHz)"),
            new AccessPoint("Other", "aa:bb:cc:dd:ee:03", 100, -50, "153 (5 GHz)")
        };

        ShowWindow();
        form.ApplyAccessPoints(points);

        var conn = new ConnectionInfo("MyNetwork", "aa:bb:cc:dd:ee:02", "MyNetwork", 20);

        // Прогон по всем языкам: интерфейс должен перестроиться без ошибок.
        foreach (LanguageInfo language in Loc.Languages)
        {
            Loc.SetLanguage(language.Code);

            foreach (WatchState state in Enum.GetValues<WatchState>())
            {
                OnStatus(new WatchStatus(
                    state, Loc.T("watch.onTarget"), conn, null, DateTime.Now));
            }

            OnUpdateStatus(new UpdateStatus(UpdateState.Ready, "9.9.9"));
        }

        Loc.SetLanguage("en");

        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ExitApp();
        };
        timer.Start();
    }
}
