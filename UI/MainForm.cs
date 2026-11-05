using System.Diagnostics;
using WifiBssidTool.Configuration;
using WifiBssidTool.Infrastructure;
using WifiBssidTool.Localization;
using WifiBssidTool.Services;
using WifiBssidTool.Wlan;

namespace WifiBssidTool.UI;

internal sealed class MainForm : Form
{
    private const string AppTitle = "Wi-Fi Anchor";

    private static string AllNetworks => Loc.T("points.allNetworks");

    private readonly AppConfig config;
    private readonly WatcherService watcher;
    private readonly UpdateService updates;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };

    // Шапка
    private readonly Label lblState = new();
    private readonly Label lblDetail = new();
    private readonly Label lblTarget = new();
    private readonly Label lblCurrent = new();
    private readonly Button btnPause = new();

    // Вкладка «Точки»
    private readonly TabControl tabs = new();
    private readonly TabPage pagePoints = new();
    private readonly TabPage pageSettings = new();
    private readonly TabPage pageLog = new();
    private readonly Label lblNetwork = new();
    private readonly ComboBox cmbSsid = new();
    private readonly Button btnScan = new();
    private readonly Label lblScan = new();
    private readonly BufferedListView list = new();
    private readonly Button btnPin = new();
    private readonly Button btnRename = new();
    private readonly Button btnManual = new();
    private readonly ToolStripMenuItem miPin = new();
    private readonly ToolStripMenuItem miRename = new();

    // Вкладка «Настройки»
    private readonly CheckBox chkTray = new();
    private readonly CheckBox chkAutostart = new();
    private readonly CheckBox chkNotify = new();
    private readonly Label lblHint = new();
    private readonly Label lblPaths = new();
    private readonly Button btnFolder = new();
    private readonly Label lblLanguage = new();
    private readonly ComboBox cmbLanguage = new();
    private readonly CheckBox chkAutoUpdate = new();
    private readonly Label lblVersion = new();
    private readonly Button btnCheckUpdate = new();
    private readonly Button btnRestartUpdate = new();
    private readonly Label lblUpdate = new();

    // Вкладка «Журнал»
    private readonly TextBox txtLog = new();

    private WatchStatus currentStatus;
    private UpdateStatus updateStatus;
    private IReadOnlyList<AccessPoint> accessPoints = Array.Empty<AccessPoint>();
    private bool ssidInitialized;
    private bool suppressEvents;
    private bool scanning;
    private DateTime scanUpdatedAt = DateTime.MinValue;
    private string listSignature = "";

    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowExit { get; set; }

    /// <summary>Целевая точка или её название изменились.</summary>
    public event Action? TargetChanged;

    /// <summary>Окно спрятано в трей (первый раз — повод показать подсказку).</summary>
    public event Action? HiddenToTray;

    /// <summary>Окно закрыто так, что программу нужно завершить.</summary>
    public event Action? ExitRequested;

    /// <summary>Пользователь просит установить скачанное обновление.</summary>
    public event Action? UpdateRestartRequested;

    public MainForm(AppConfig config, WatcherService watcher, UpdateService updates)
    {
        this.config = config;
        this.watcher = watcher;
        this.updates = updates;
        currentStatus = watcher.Status;
        updateStatus = updates.Status;

        Text = AppTitle;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(880, 620);
        MinimumSize = new Size(760, 540);
        Icon = StateStyle.Icon(currentStatus.State);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildTabs(), 0, 1);
        Controls.Add(root);

        timer.Tick += (_, _) => UpdateStatusLines();
        AppLog.LineAdded += OnLogLine;
        Loc.LanguageChanged += OnLanguageChanged;

        foreach (string line in AppLog.Snapshot())
            txtLog.AppendText(line + Environment.NewLine);

        LoadSettings();
        ApplyLocalization();
    }

    // ---------- Построение интерфейса ----------

    private Control BuildHeader()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(16, 12, 16, 8)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        for (int i = 0; i < 4; i++)
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        ConfigureLabel(lblState, 15f, true, 34);
        ConfigureLabel(lblDetail, 9f, false, 22);
        ConfigureLabel(lblTarget, 9.5f, false, 24);
        ConfigureLabel(lblCurrent, 9.5f, false, 24);
        lblDetail.ForeColor = SystemColors.GrayText;

        btnPause.Size = new Size(130, 34);
        btnPause.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnPause.Margin = new Padding(8, 0, 0, 0);
        btnPause.Click += (_, _) => watcher.Paused = !watcher.Paused;

        table.Controls.Add(lblState, 0, 0);
        table.Controls.Add(lblDetail, 0, 1);
        table.Controls.Add(lblTarget, 0, 2);
        table.Controls.Add(lblCurrent, 0, 3);
        table.Controls.Add(btnPause, 1, 0);
        table.SetRowSpan(btnPause, 2);

        return table;
    }

    private void ConfigureLabel(Label label, float size, bool bold, int height)
    {
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        label.Height = height;
        label.AutoEllipsis = true;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Margin = Padding.Empty;
        label.Font = new Font(
            Font.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular);
    }

    private Control BuildTabs()
    {
        tabs.Dock = DockStyle.Fill;
        tabs.Margin = new Padding(8, 0, 8, 8);

        BuildPointsPage();
        BuildSettingsPage();
        BuildLogPage();

        tabs.TabPages.Add(pagePoints);
        tabs.TabPages.Add(pageSettings);
        tabs.TabPages.Add(pageLog);

        return tabs;
    }

    private void BuildPointsPage()
    {
        pagePoints.Padding = new Padding(10);
        pagePoints.UseVisualStyleBackColor = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Верхняя панель
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8)
        };

        lblNetwork.AutoSize = true;
        lblNetwork.TextAlign = ContentAlignment.MiddleLeft;
        lblNetwork.Margin = new Padding(0, 6, 6, 0);

        cmbSsid.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbSsid.Width = 240;
        cmbSsid.SelectedIndexChanged += (_, _) =>
        {
            if (!suppressEvents)
                RefreshList(true);
        };

        StyleButton(btnScan);
        btnScan.Click += (_, _) => RequestScan();

        lblScan.AutoSize = true;
        lblScan.ForeColor = SystemColors.GrayText;
        lblScan.Margin = new Padding(8, 6, 0, 0);

        top.Controls.AddRange(new Control[] { lblNetwork, cmbSsid, btnScan, lblScan });

        // Список
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.Columns.Add("", 150);
        list.Columns.Add("", 140);
        list.Columns.Add("", 150);
        list.Columns.Add("", 120);
        list.Columns.Add("", 110);
        list.Columns.Add("", 140);
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        list.DoubleClick += (_, _) => PinSelected();

        var menu = new ContextMenuStrip();
        miPin.Click += (_, _) => PinSelected();
        miRename.Click += (_, _) => RenameSelected();
        menu.Items.AddRange(new ToolStripItem[] { miPin, miRename });
        menu.Opening += (_, e) =>
        {
            if (list.SelectedItems.Count == 0)
                e.Cancel = true;
        };
        list.ContextMenuStrip = menu;

        // Нижняя панель
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0)
        };

        StyleButton(btnPin);
        btnPin.Click += (_, _) => PinSelected();

        StyleButton(btnRename);
        btnRename.Click += (_, _) => RenameSelected();

        StyleButton(btnManual);
        btnManual.Click += (_, _) => PinManual();

        bottom.Controls.AddRange(new Control[] { btnPin, btnRename, btnManual });

        layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(bottom, 0, 2);
        pagePoints.Controls.Add(layout);
    }

    private void BuildSettingsPage()
    {
        pageSettings.Padding = new Padding(16);
        pageSettings.UseVisualStyleBackColor = true;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        foreach (CheckBox c in new[] { chkTray, chkAutostart, chkNotify, chkAutoUpdate })
        {
            c.AutoSize = true;
            c.Margin = new Padding(0, 0, 0, 8);
        }

        lblHint.AutoSize = true;
        lblHint.MaximumSize = new Size(640, 0);
        lblHint.ForeColor = SystemColors.GrayText;
        lblHint.Margin = new Padding(0, 0, 0, 16);

        // Язык
        var languageRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 12)
        };

        lblLanguage.AutoSize = true;
        lblLanguage.TextAlign = ContentAlignment.MiddleLeft;
        lblLanguage.Margin = new Padding(0, 6, 6, 0);

        cmbLanguage.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbLanguage.Width = 240;
        cmbLanguage.SelectedIndexChanged += (_, _) => OnLanguagePicked();

        languageRow.Controls.AddRange(new Control[] { lblLanguage, cmbLanguage });

        // Обновления
        var updateRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 4)
        };

        lblVersion.AutoSize = true;
        lblVersion.TextAlign = ContentAlignment.MiddleLeft;
        lblVersion.Margin = new Padding(0, 6, 12, 0);

        StyleButton(btnCheckUpdate);
        btnCheckUpdate.Click += (_, _) => _ = updates.CheckAsync(true);

        StyleButton(btnRestartUpdate);
        btnRestartUpdate.Visible = false;
        btnRestartUpdate.Click += (_, _) => UpdateRestartRequested?.Invoke();

        updateRow.Controls.AddRange(
            new Control[] { lblVersion, btnCheckUpdate, btnRestartUpdate });

        lblUpdate.AutoSize = true;
        lblUpdate.MaximumSize = new Size(640, 0);
        lblUpdate.ForeColor = SystemColors.GrayText;
        lblUpdate.Margin = new Padding(0, 0, 0, 16);

        lblPaths.AutoSize = true;
        lblPaths.MaximumSize = new Size(640, 0);
        lblPaths.ForeColor = SystemColors.GrayText;
        lblPaths.Margin = new Padding(0, 0, 0, 8);

        StyleButton(btnFolder);
        btnFolder.Click += (_, _) => OpenFolder();

        flow.Controls.AddRange(new Control[]
        {
            chkTray, chkNotify, chkAutostart, lblHint,
            languageRow,
            chkAutoUpdate, updateRow, lblUpdate,
            lblPaths, btnFolder
        });
        pageSettings.Controls.Add(flow);
    }

    private void BuildLogPage()
    {
        pageLog.Padding = new Padding(10);
        pageLog.UseVisualStyleBackColor = true;

        txtLog.Dock = DockStyle.Fill;
        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.WordWrap = false;
        txtLog.ScrollBars = ScrollBars.Both;
        txtLog.Font = new Font("Consolas", 9f);
        txtLog.BackColor = SystemColors.Window;

        pageLog.Controls.Add(txtLog);
    }

    private static void StyleButton(Button button)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(8, 2, 8, 2);
        button.Margin = new Padding(0, 0, 8, 0);
    }

    // ---------- Локализация ----------

    /// <summary>Расставляет все тексты интерфейса на текущем языке.</summary>
    private void ApplyLocalization()
    {
        pagePoints.Text = Loc.T("tab.points");
        pageSettings.Text = Loc.T("tab.settings");
        pageLog.Text = Loc.T("tab.log");

        lblNetwork.Text = Loc.T("points.network");
        btnScan.Text = Loc.T("points.refresh");

        string[] columns = { "col.name", "col.bssid", "col.network",
                             "col.signal", "col.channel", "col.status" };
        for (int i = 0; i < columns.Length; i++)
            list.Columns[i].Text = Loc.T(columns[i]);

        btnPin.Text = Loc.T("points.pin");
        btnRename.Text = Loc.T("action.rename");
        btnManual.Text = Loc.T("points.manual");
        miPin.Text = Loc.T("points.pinThis");
        miRename.Text = Loc.T("action.rename");

        chkTray.Text = Loc.T("set.tray");
        chkAutostart.Text = Loc.T("set.autostart");
        chkNotify.Text = Loc.T("set.notify");
        lblHint.Text = Loc.T("set.hint");
        lblPaths.Text = Loc.T("set.paths", AppConfig.FilePath, AppLog.FilePath);
        btnFolder.Text = Loc.T("set.openFolder");
        lblLanguage.Text = Loc.T("set.language");
        chkAutoUpdate.Text = Loc.T("set.autoUpdate");
        lblVersion.Text = Loc.T("set.version", updates.CurrentVersion);
        btnCheckUpdate.Text = Loc.T("set.checkUpdates");
        btnRestartUpdate.Text = Loc.T("set.restartUpdate");

        FillLanguageList();
        ApplyUpdateStatus(updateStatus);

        UpdateScanLabel();
        UpdateTargetLabel();
        ApplyStatus(currentStatus);
        RefreshSsids();
        RefreshList(true);
    }

    private void OnLanguageChanged()
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
            BeginInvoke(ApplyLocalization);
        else
            ApplyLocalization();
    }

    private void FillLanguageList()
    {
        bool previous = suppressEvents;
        suppressEvents = true;

        cmbLanguage.BeginUpdate();
        cmbLanguage.Items.Clear();
        cmbLanguage.Items.Add(Loc.T("set.languageAuto"));

        foreach (LanguageInfo language in Loc.Languages)
            cmbLanguage.Items.Add(language.NativeName);

        int index = 0;

        if (config.Language.Length > 0)
        {
            string code = Loc.Resolve(config.Language);
            int found = Array.FindIndex(
                Loc.Languages.ToArray(), l => l.Code == code);
            index = found >= 0 ? found + 1 : 0;
        }

        cmbLanguage.SelectedIndex = index;
        cmbLanguage.EndUpdate();

        suppressEvents = previous;
    }

    private void OnLanguagePicked()
    {
        if (suppressEvents || cmbLanguage.SelectedIndex < 0)
            return;

        int index = cmbLanguage.SelectedIndex;
        config.Language = index == 0 ? Loc.Auto : Loc.Languages[index - 1].Code;
        Save();

        Loc.SetLanguage(config.Language);
    }

    // ---------- Настройки ----------

    private void LoadSettings()
    {
        suppressEvents = true;
        chkTray.Checked = config.MinimizeToTrayOnClose;
        chkNotify.Checked = config.ShowNotifications;
        chkAutostart.Checked = AutoStart.IsEnabled;
        chkAutoUpdate.Checked = config.AutoUpdate;
        suppressEvents = false;

        chkTray.CheckedChanged += (_, _) =>
        {
            if (suppressEvents) return;
            config.MinimizeToTrayOnClose = chkTray.Checked;
            Save();
        };

        chkNotify.CheckedChanged += (_, _) =>
        {
            if (suppressEvents) return;
            config.ShowNotifications = chkNotify.Checked;
            Save();
        };

        chkAutoUpdate.CheckedChanged += (_, _) =>
        {
            if (suppressEvents) return;
            config.AutoUpdate = chkAutoUpdate.Checked;
            Save();
            updates.AutoCheck = config.AutoUpdate;
        };

        chkAutostart.CheckedChanged += (_, _) =>
        {
            if (suppressEvents) return;

            try
            {
                AutoStart.Set(chkAutostart.Checked);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    Loc.T("autostart.failed", ex.Message),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

                suppressEvents = true;
                chkAutostart.Checked = AutoStart.IsEnabled;
                suppressEvents = false;
            }
        };
    }

    private void Save()
    {
        if (!config.TrySave(out string? error))
            AppLog.Write($"Could not save settings: {error}");
    }

    private static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Directory);
            Process.Start("explorer.exe", $"\"{AppConfig.Directory}\"");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not open the folder: {ex.Message}");
        }
    }

    // ---------- Обновления (вызывать из потока интерфейса) ----------

    public void ApplyUpdateStatus(UpdateStatus status)
    {
        updateStatus = status;

        lblUpdate.Text = status.State switch
        {
            UpdateState.Checking => Loc.T("upd.checking"),
            UpdateState.Downloading => Loc.T("upd.downloading", status.Detail),
            UpdateState.Ready => Loc.T("upd.ready", status.Detail),
            UpdateState.UpToDate => Loc.T("upd.upToDate"),
            UpdateState.NotInstalled => Loc.T("upd.notInstalled"),
            UpdateState.Failed => Loc.T("upd.failed", status.Detail),
            _ => ""
        };

        bool busy = status.State is UpdateState.Checking or UpdateState.Downloading;
        bool ready = status.State == UpdateState.Ready;

        btnCheckUpdate.Enabled = !busy && !ready;
        btnRestartUpdate.Visible = ready;
    }

    // ---------- Данные от службы (вызывать из потока интерфейса) ----------

    public void ApplyStatus(WatchStatus status)
    {
        currentStatus = status;

        Color color = StateStyle.ColorOf(status.State);
        lblState.Text = "● " + StateStyle.Title(status.State);
        lblState.ForeColor = color;

        lblCurrent.Text = status.Current == null
            ? Loc.T("main.current.none")
            : Loc.T("main.current", status.Current.Ssid,
                Describe(status.Current.Bssid), status.Current.Signal);

        btnPause.Text = Loc.T(
            status.State == WatchState.Paused ? "action.resume" : "action.pause");
        btnPause.Enabled = status.State != WatchState.NotConfigured;

        Icon = StateStyle.Icon(status.State);

        UpdateStatusLines();

        if (Visible)
            RefreshList(false);
    }

    public void ApplyAccessPoints(IReadOnlyList<AccessPoint> points)
    {
        accessPoints = points;
        scanning = false;
        scanUpdatedAt = DateTime.Now;
        UpdateScanLabel();

        if (Visible)
        {
            RefreshSsids();
            RefreshList(false);
        }
    }

    private void RequestScan()
    {
        scanning = true;
        UpdateScanLabel();
        watcher.RequestScan();
    }

    private void UpdateScanLabel()
    {
        lblScan.Text = scanning
            ? Loc.T("main.scanning")
            : scanUpdatedAt == DateTime.MinValue
                ? ""
                : Loc.T("main.updated", scanUpdatedAt.ToString("HH:mm:ss"));
    }

    private void UpdateStatusLines()
    {
        TimeSpan elapsed = DateTime.Now - currentStatus.Since;
        lblDetail.Text =
            $"{currentStatus.Message} · {StateStyle.FormatDuration(elapsed)}";
    }

    private void UpdateTargetLabel()
    {
        lblTarget.Text = config.HasTarget
            ? Loc.T("main.target", Describe(config.Bssid), config.Ssid)
            : Loc.T("main.target.none");
    }

    /// <summary>«Название (mac)» либо просто mac.</summary>
    public string Describe(string bssid)
    {
        string name = config.NameOf(bssid);
        return name.Length > 0 ? $"{name} ({bssid})" : bssid;
    }

    // ---------- Список точек ----------

    private void RefreshSsids()
    {
        List<string> ssids = accessPoints
            .Select(a => a.Ssid)
            .Where(s => s.Length > 0)
            .Distinct()
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (config.Ssid.Length > 0 && !ssids.Contains(config.Ssid))
            ssids.Insert(0, config.Ssid);

        List<string> items = new[] { AllNetworks }.Concat(ssids).ToList();

        if (items.SequenceEqual(cmbSsid.Items.Cast<string>()))
            return;

        string? previous = cmbSsid.SelectedItem as string;

        suppressEvents = true;
        cmbSsid.Items.Clear();
        cmbSsid.Items.AddRange(items.Cast<object>().ToArray());

        string pick = AllNetworks;

        if (previous != null && items.Contains(previous))
        {
            pick = previous;
        }
        else if (!ssidInitialized && accessPoints.Count > 0)
        {
            string? preferred = config.Ssid.Length > 0
                ? config.Ssid
                : currentStatus.Current?.Ssid;

            if (preferred != null && items.Contains(preferred))
                pick = preferred;
        }

        if (accessPoints.Count > 0)
            ssidInitialized = true;

        cmbSsid.SelectedItem = pick;
        suppressEvents = false;
    }

    private void RefreshList(bool force)
    {
        string filter = cmbSsid.SelectedItem as string ?? AllNetworks;
        bool allNetworks = filter == AllNetworks;
        string target = config.Bssid;
        string currentBssid = currentStatus.Current?.Bssid ?? "";
        string? selected = SelectedBssid();

        var rows = accessPoints
            .Where(a => a.Ssid.Length > 0 &&
                        (allNetworks || a.Ssid == filter))
            .GroupBy(a => a.Bssid)
            .Select(g => g.OrderByDescending(x => x.Rssi).First())
            .ToList();

        var seen = new HashSet<string>(rows.Select(r => r.Bssid));

        // Целевая точка должна быть видна в списке, даже если её нет в эфире.
        if (config.HasTarget &&
            (allNetworks || filter == config.Ssid) &&
            !seen.Contains(target))
        {
            rows.Add(new AccessPoint(config.Ssid, target, 0, 0, "—"));
        }

        rows = rows
            .OrderByDescending(a => a.Bssid == target)
            .ThenByDescending(a => seen.Contains(a.Bssid))
            .ThenByDescending(a => a.Signal)
            .ThenByDescending(a => a.Rssi)
            .ToList();

        var items = new List<ListViewItem>();

        foreach (AccessPoint a in rows)
        {
            bool isTarget = a.Bssid == target;
            bool visible = seen.Contains(a.Bssid);
            bool isCurrent = a.Bssid == currentBssid;

            string statusText = isTarget
                ? Loc.T(isCurrent ? "row.targetConnected"
                        : visible ? "row.target"
                        : "row.targetHidden")
                : isCurrent ? Loc.T("row.connected") : "";

            var item = new ListViewItem(new[]
            {
                config.NameOf(a.Bssid),
                a.Bssid,
                a.Ssid,
                visible ? $"{a.Signal}% ({a.Rssi} dBm)" : "—",
                a.Channel,
                statusText
            })
            {
                Tag = a,
                Selected = a.Bssid == selected
            };

            if (isTarget)
            {
                item.BackColor = Color.FromArgb(226, 244, 230);
                item.ForeColor = Color.FromArgb(20, 90, 40);
                item.Font = new Font(list.Font, FontStyle.Bold);
            }
            else if (!visible)
            {
                item.ForeColor = SystemColors.GrayText;
            }

            items.Add(item);
        }

        string signature = string.Join("\n", items.Select(i =>
            string.Join("|", i.SubItems.Cast<ListViewItem.ListViewSubItem>()
                .Select(s => s.Text)) + (i.Selected ? "*" : "")));

        if (!force && signature == listSignature)
            return;

        listSignature = signature;

        list.BeginUpdate();
        list.Items.Clear();
        list.Items.AddRange(items.ToArray());
        list.EndUpdate();

        UpdateButtons();
    }

    private string? SelectedBssid() =>
        list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is AccessPoint ap
            ? ap.Bssid
            : null;

    private void UpdateButtons()
    {
        string? bssid = SelectedBssid();
        bool has = bssid != null;

        btnPin.Enabled = has && bssid != config.Bssid;
        btnRename.Enabled = has;
        miPin.Enabled = btnPin.Enabled;
        miRename.Enabled = has;
    }

    // ---------- Действия ----------

    private void PinSelected()
    {
        if (list.SelectedItems.Count == 0 ||
            list.SelectedItems[0].Tag is not AccessPoint ap)
        {
            return;
        }

        if (ap.Bssid == config.Bssid)
            return;

        Pin(ap.Ssid, ap.Bssid);
    }

    private void PinManual()
    {
        string? text = InputDialog.Show(
            this, Loc.T("manual.title"), Loc.T("manual.prompt"));

        if (string.IsNullOrEmpty(text))
            return;

        if (!Mac.IsValid(text))
        {
            MessageBox.Show(this, Loc.T("manual.invalid"), Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string bssid = Mac.Normalize(text);

        string ssid = accessPoints
            .Where(a => a.Bssid == bssid && a.Ssid.Length > 0)
            .Select(a => a.Ssid)
            .FirstOrDefault() ?? "";

        if (ssid.Length == 0)
        {
            string suggestion = config.Ssid.Length > 0
                ? config.Ssid
                : currentStatus.Current?.Ssid ?? "";

            ssid = InputDialog.Show(
                this, Loc.T("ssid.title"), Loc.T("ssid.prompt"), suggestion) ?? "";
        }

        if (ssid.Length == 0)
            return;

        Pin(ssid, bssid);
    }

    private void Pin(string ssid, string bssid)
    {
        bssid = Mac.Normalize(bssid);

        if (!ssid.Equals(config.Ssid, StringComparison.Ordinal))
            config.Profile = "";

        config.Ssid = ssid;
        config.Bssid = bssid;
        Save();

        AppLog.Write(
            $"Selected access point {Describe(bssid)} (network \"{ssid}\").");

        watcher.SetTarget(new WatchTarget(ssid, bssid, config.Profile));

        UpdateTargetLabel();
        RefreshList(true);
        TargetChanged?.Invoke();
    }

    private void RenameSelected()
    {
        string? bssid = SelectedBssid();
        if (bssid == null)
            return;

        string? name = InputDialog.Show(
            this, Loc.T("rename.title"), Loc.T("rename.prompt", bssid),
            config.NameOf(bssid));

        if (name == null)
            return;

        config.SetName(bssid, name);
        Save();

        UpdateTargetLabel();
        RefreshList(true);
        TargetChanged?.Invoke();
    }

    /// <summary>Открывает вкладку выбора точки и ставит фокус на список.</summary>
    public void FocusPicker()
    {
        tabs.SelectedIndex = 0;
        list.Focus();
    }

    // ---------- Журнал ----------

    private void OnLogLine(string line)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            BeginInvoke(() =>
            {
                if (txtLog.TextLength > 80_000)
                    txtLog.Text = txtLog.Text[^40_000..];

                txtLog.AppendText(line + Environment.NewLine);
            });
        }
        catch (InvalidOperationException)
        {
            // Окно уже закрывается (включая ObjectDisposedException).
        }
    }

    // ---------- Жизненный цикл окна ----------

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        watcher.WatchAccessPoints = Visible;
        timer.Enabled = Visible;

        if (!Visible)
            return;

        suppressEvents = true;
        chkAutostart.Checked = AutoStart.IsEnabled;
        suppressEvents = false;

        accessPoints = watcher.LastAccessPoints;
        ApplyStatus(watcher.Status);
        UpdateTargetLabel();
        RefreshSsids();
        RefreshList(true);

        RequestScan();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowExit &&
            e.CloseReason == CloseReason.UserClosing &&
            config.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);

        if (!AllowExit)
            ExitRequested?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            AppLog.LineAdded -= OnLogLine;
            Loc.LanguageChanged -= OnLanguageChanged;
            timer.Dispose();
        }

        base.Dispose(disposing);
    }
}
