using Velopack;
using WifiAnchor.Infrastructure;
using WifiAnchor.UI;

namespace WifiAnchor;

internal static class Program
{
    private const string MutexName = @"Local\WifiAnchor.SingleInstance";
    private const string ShowEventName = @"Local\WifiAnchor.ShowWindow";

    [STAThread]
    private static int Main(string[] args)
    {
        // Должно быть первым: при установке, обновлении и удалении Velopack
        // запускает exe со служебными аргументами, и процесс завершается внутри Run().
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                try
                {
                    AutoStart.Set(false);
                }
                catch
                {
                }
            })
            .Run();

        bool minimized = HasFlag(args, "--minimized");
        bool smokeTest = HasFlag(args, "--smoke-test");
        string? language = ValueOf(args, "--lang=");

        Mutex? mutex = null;
        EventWaitHandle? showEvent = null;

        if (!smokeTest)
        {
            mutex = new Mutex(true, MutexName, out bool created);

            if (!created)
            {
                // Уже запущена другая копия: просим её показать окно.
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                    existing.Set();
                }
                catch
                {
                }

                mutex.Dispose();
                return 0;
            }

            showEvent = new EventWaitHandle(
                false, EventResetMode.AutoReset, ShowEventName);
        }

        try
        {
            ApplicationConfiguration.Initialize();

            Application.ThreadException += (_, e) =>
            {
                AppLog.Write($"Unhandled error: {e.Exception}");
                Environment.ExitCode = 1;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                AppLog.Write($"Fatal error: {e.ExceptionObject}");

            using var context = new TrayApplicationContext(
                minimized, smokeTest, language, showEvent);

            Application.Run(context);
            return Environment.ExitCode;
        }
        finally
        {
            showEvent?.Dispose();

            try
            {
                mutex?.ReleaseMutex();
            }
            catch
            {
            }

            mutex?.Dispose();
        }
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Значение параметра вида --lang=de (null, если параметра нет).</summary>
    private static string? ValueOf(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..];
}
