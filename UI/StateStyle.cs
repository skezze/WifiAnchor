using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WifiBssidTool.Localization;
using WifiBssidTool.Services;

namespace WifiBssidTool.UI;

/// <summary>Цвета, тексты и иконки для состояний службы.</summary>
internal static class StateStyle
{
    private static readonly Dictionary<int, Icon> Icons = new();

    public static Color ColorOf(WatchState state) => state switch
    {
        WatchState.OnTarget => Color.FromArgb(46, 160, 67),
        WatchState.Connecting => Color.FromArgb(224, 150, 0),
        WatchState.Searching => Color.FromArgb(210, 60, 60),
        _ => Color.FromArgb(128, 128, 128)
    };

    public static string Title(WatchState state) => state switch
    {
        WatchState.NotConfigured => Loc.T("state.notConfigured"),
        WatchState.Paused => Loc.T("state.paused"),
        WatchState.NoAdapter => Loc.T("state.noAdapter"),
        WatchState.OnTarget => Loc.T("state.onTarget"),
        WatchState.Connecting => Loc.T("state.connecting"),
        WatchState.Searching => Loc.T("state.searching"),
        _ => ""
    };

    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 60)
            return Loc.T("dur.s", Math.Max(0, (int)span.TotalSeconds));

        if (span.TotalMinutes < 60)
            return Loc.T("dur.min", (int)span.TotalMinutes);

        if (span.TotalHours < 24)
            return Loc.T("dur.hm", (int)span.TotalHours, span.Minutes);

        return Loc.T("dur.dh", (int)span.TotalDays, span.Hours);
    }

    public static Icon Icon(WatchState state)
    {
        Color color = ColorOf(state);
        int key = color.ToArgb();

        if (!Icons.TryGetValue(key, out Icon? icon))
        {
            icon = Create(color);
            Icons[key] = icon;
        }

        return icon;
    }

    private static Icon Create(Color color)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var bg = new SolidBrush(color))
                g.FillEllipse(bg, 0, 0, size - 1, size - 1);

            float cx = size / 2f;
            float baseY = size * 0.72f;

            using var pen = new Pen(Color.White, Math.Max(1.4f, size / 10f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            foreach (float k in new[] { 0.20f, 0.34f, 0.48f })
            {
                float r = size * k;
                g.DrawArc(pen, cx - r, baseY - r, r * 2, r * 2, 225, 90);
            }

            float dot = Math.Max(1.2f, size / 12f);
            g.FillEllipse(Brushes.White, cx - dot, baseY - dot, dot * 2, dot * 2);
        }

        // Дескриптор намеренно не освобождается: иконок всего несколько,
        // они живут до конца работы программы.
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
