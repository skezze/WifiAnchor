using System.Diagnostics;
using WifiBssidTool.Configuration;
using WifiBssidTool.Infrastructure;
using WifiBssidTool.Services;
using WifiBssidTool.Wlan;

namespace WifiBssidTool.UI;

internal sealed class MainForm : Form
{
    private const string AllNetworks = "(все сети)";

    private readonly AppConfig config;
    private readonly WatcherService watcher;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };

    // Шапка
    private readonly Label lblState = new();
    private readonly Label lblDetail = new();
    private readonly Label lblTarget = new();
    private readonly Label lblCurrent = new();
    private readonly Button btnPause = new();

    // Вкладка «Точки»
    private readonly TabControl tabs = new();
    private readonly ComboBox cmbSsid = new();
    private readonly Label lblScan = new();
    private readonly BufferedListView list = new();
    private readonly Button btnPin = new();
    private readonly Button btnRename = new();
    private readonly ToolStripMenuItem miPin = new("Закрепить эту точку");
    private readonly ToolStripMenuItem miRename = new("Назвать…");

    // Вкладка «Настройки»
    private readonly CheckBox chkTray = new();
    private readonly CheckBox chkAutostart = new();
    private readonly CheckBox chkNotify = new();

    // Вкладка «Журнал»
    private readonly TextBox txtLog = new();

    private WatchStatus currentStatus;
    private IReadOnlyList<AccessPoint> accessPoints = Array.Empty<AccessPoint>();
    private bool ssidInitialized;
    private bool suppressEvents;
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

    public MainForm(AppConfig config, WatcherService watcher)
    {
        this.config = config;
        this.watcher = watcher;
        currentStatus = watcher.Status;

        Text = "Wi-Fi BSSID Watcher";
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

        foreach (string line in AppLog.Snapshot())
            txtLog.AppendText(line + Environment.NewLine);

        LoadSettings();
        UpdateTargetLabel();
        ApplyStatus(currentStatus);
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

        tabs.TabPages.Add(BuildPointsPage());
        tabs.TabPages.Add(BuildSettingsPage());
        tabs.TabPages.Add(BuildLogPage());

        return tabs;
    }

    private TabPage BuildPointsPage()
    {
        var page = new TabPage("Точки")
        {
            Padding = new Padding(10),
            UseVisualStyleBackColor = true
        };

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

        var lbl = new Label
        {
            Text = "Сеть:",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 6, 0)
        };

        cmbSsid.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbSsid.Width = 240;
        cmbSsid.SelectedIndexChanged += (_, _) =>
        {
            if (!suppressEvents)
                RefreshList(true);
        };

        var btnScan = MakeButton("Обновить");
        btnScan.Click += (_, _) =>
        {
            lblScan.Text = "Сканирую эфир...";
            watcher.RequestScan();
        };

        lblScan.AutoSize = true;
        lblScan.ForeColor = SystemColors.GrayText;
        lblScan.Margin = new Padding(8, 6, 0, 0);

        top.Controls.AddRange(new Control[] { lbl, cmbSsid, btnScan, lblScan });

        // Список
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.Columns.Add("Название", 150);
        list.Columns.Add("BSSID", 140);
        list.Columns.Add("Сеть", 150);
        list.Columns.Add("Сигнал", 120);
        list.Columns.Add("Канал", 110);
        list.Columns.Add("Статус", 140);
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

        btnPin.Text = "Закрепить выбранную точку";
        StyleButton(btnPin);
        btnPin.Click += (_, _) => PinSelected();

        btnRename.Text = "Назвать…";
        StyleButton(btnRename);
        btnRename.Click += (_, _) => RenameSelected();

        var btnManual = MakeButton("Ввести MAC вручную…");
        btnManual.Click += (_, _) => PinManual();

        bottom.Controls.AddRange(new Control[] { btnPin, btnRename, btnManual });

        layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(bottom, 0, 2);
        page.Controls.Add(layout);

        UpdateButtons();
        return page;
    }

    private TabPage BuildSettingsPage()
    {
        var page = new TabPage("Настройки")
        {
            Padding = new Padding(16),
            UseVisualStyleBackColor = true
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        chkTray.Text = "При закрытии окна продолжать работу в трее";
        chkAutostart.Text = "Запускать вместе с Windows (свёрнутым в трей)";
        chkNotify.Text = "Показывать уведомления, когда точка пропала и вернулась";

        foreach (CheckBox c in new[] { chkTray, chkAutostart, chkNotify })
        {
            c.AutoSize = true;
            c.Margin = new Padding(0, 0, 0, 8);
        }

        var hint = new Label
        {
            Text = "Если снять первую галочку, закрытие окна завершит программу. " +
                   "Выйти можно и через меню значка в трее.",
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 20)
        };

        var paths = new Label
        {
            Text = $"Настройки: {AppConfig.FilePath}\nЖурнал: {AppLog.FilePath}",
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 8)
        };

        var btnFolder = MakeButton("Открыть папку с настройками");
        btnFolder.Click += (_, _) => OpenFolder();

        flow.Controls.AddRange(new Control[]
        {
            chkTray, chkNotify, chkAutostart, hint, paths, btnFolder
        });
        page.Controls.Add(flow);

        return page;
    }

    private TabPage BuildLogPage()
    {
        var page = new TabPage("Журнал")
        {
            Padding = new Padding(10),
            UseVisualStyleBackColor = true
        };

        txtLog.Dock = DockStyle.Fill;
        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.WordWrap = false;
        txtLog.ScrollBars = ScrollBars.Both;
        txtLog.Font = new Font("Consolas", 9f);
        txtLog.BackColor = SystemColors.Window;

        page.Controls.Add(txtLog);
        return page;
    }

    private static Button MakeButton(string text)
    {
        var button = new Button { Text = text };
        StyleButton(button);
        return button;
    }

    private static void StyleButton(Button button)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(8, 2, 8, 2);
        button.Margin = new Padding(0, 0, 8, 0);
    }

    // ---------- Настройки ----------

    private void LoadSettings()
    {
        suppressEvents = true;
        chkTray.Checked = config.MinimizeToTrayOnClose;
        chkNotify.Checked = config.ShowNotifications;
        chkAutostart.Checked = AutoStart.IsEnabled;
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
                    $"Не удалось изменить автозапуск: {ex.Message}",
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
            AppLog.Write($"Не удалось сохранить настройки: {error}");
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
            AppLog.Write($"Не удалось открыть папку: {ex.Message}");
        }
    }

    // ---------- Данные от службы (вызывать из потока интерфейса) ----------

    public void ApplyStatus(WatchStatus status)
    {
        currentStatus = status;

        Color color = StateStyle.ColorOf(status.State);
        lblState.Text = "● " + StateStyle.Title(status.State);
        lblState.ForeColor = color;

        lblCurrent.Text = status.Current == null
            ? "Сейчас: нет подключения"
            : $"Сейчас: {status.Current.Ssid} · {Describe(status.Current.Bssid)} " +
              $"· сигнал {status.Current.Signal}%";

        btnPause.Text = status.State == WatchState.Paused ? "Продолжить" : "Приостановить";
        btnPause.Enabled = status.State != WatchState.NotConfigured;

        Icon = StateStyle.Icon(status.State);

        UpdateStatusLines();

        if (Visible)
            RefreshList(false);
    }

    public void ApplyAccessPoints(IReadOnlyList<AccessPoint> points)
    {
        accessPoints = points;
        lblScan.Text = $"Обновлено {DateTime.Now:HH:mm:ss}";

        if (Visible)
        {
            RefreshSsids();
            RefreshList(false);
        }
    }

    private void UpdateStatusLines()
    {
        TimeSpan elapsed = DateTime.Now - currentStatus.Since;
        lblDetail.Text =
            $"{currentStatus.Message} · {StateStyle.FormatDuration(elapsed)}";
    }

    private void UpdateTargetLabel()
    {
        if (!config.HasTarget)
        {
            lblTarget.Text = "Целевая точка: не выбрана. Выберите её на вкладке «Точки».";
            return;
        }

        lblTarget.Text =
            $"Целевая точка: {Describe(config.Bssid)} · сеть «{config.Ssid}»";
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
        string target = config.Bssid;
        string currentBssid = currentStatus.Current?.Bssid ?? "";
        string? selected = SelectedBssid();

        var rows = accessPoints
            .Where(a => a.Ssid.Length > 0 &&
                        (filter == AllNetworks || a.Ssid == filter))
            .GroupBy(a => a.Bssid)
            .Select(g => g.OrderByDescending(x => x.Rssi).First())
            .ToList();

        var seen = new HashSet<string>(rows.Select(r => r.Bssid));

        // Целевая точка должна быть видна в списке, даже если её нет в эфире.
        if (config.HasTarget &&
            (filter == AllNetworks || filter == config.Ssid) &&
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
                ? (isCurrent ? "Цель · подключено" : visible ? "Цель" : "Цель · не видна")
                : isCurrent ? "Подключено" : "";

            var item = new ListViewItem(new[]
            {
                config.NameOf(a.Bssid),
                a.Bssid,
                a.Ssid,
                visible ? $"{a.Signal}% ({a.Rssi} дБм)" : "—",
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
        string? text = InputDialog.Show(this, "Ввести MAC вручную",
            "MAC-адрес (BSSID) нужной точки, например aa:bb:cc:dd:ee:ff:");

        if (string.IsNullOrEmpty(text))
            return;

        if (!Mac.IsValid(text))
        {
            MessageBox.Show(this, "Это не похоже на MAC-адрес.", Text,
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

            ssid = InputDialog.Show(this, "Имя сети",
                "Эту точку сейчас не видно. Введите имя сети (SSID), " +
                "к которой она относится:", suggestion) ?? "";
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

        AppLog.Write($"Выбрана точка {Describe(bssid)} (сеть «{ssid}»).");

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

        string? name = InputDialog.Show(this, "Название точки",
            $"Как назвать точку {bssid}? Например, «Репитер в спальне».\n" +
            "Пустое название — убрать.", config.NameOf(bssid));

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

        lblScan.Text = "Сканирую эфир...";
        watcher.RequestScan();
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
            timer.Dispose();
        }

        base.Dispose(disposing);
    }
}
