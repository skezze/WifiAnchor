using System.Text;
using WifiBssidTool.Configuration;

namespace WifiBssidTool.Infrastructure;

/// <summary>Журнал в памяти и в файле %AppData%\WifiBssidTool\log.txt.</summary>
internal static class AppLog
{
    private const int MaxRecent = 500;
    private const long MaxFileBytes = 1_000_000;

    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private static bool fileChecked;

    public static string FilePath => Path.Combine(AppConfig.Directory, "log.txt");

    public static event Action<string>? LineAdded;

    public static void Write(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > MaxRecent)
                Recent.Dequeue();

            try
            {
                Directory.CreateDirectory(AppConfig.Directory);

                if (!fileChecked)
                {
                    fileChecked = true;
                    var info = new FileInfo(FilePath);

                    if (info.Exists && info.Length > MaxFileBytes)
                        File.Move(FilePath, FilePath + ".old", true);
                }

                File.AppendAllText(FilePath, line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // Журнал не должен ронять программу.
            }
        }

        LineAdded?.Invoke(line);
    }

    public static string[] Snapshot()
    {
        lock (Gate)
            return Recent.ToArray();
    }
}
