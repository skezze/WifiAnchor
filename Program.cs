using WifiBssidTool.Infrastructure;
using WifiBssidTool.UI;

namespace WifiBssidTool;

internal static class Program
{
    private const string MutexName = @"Local\WifiBssidTool.SingleInstance";
    private const string ShowEventName = @"Local\WifiBssidTool.ShowWindow";

    [STAThread]
    private static int Main(string[] args)
    {
        bool minimized = HasFlag(args, "--minimized");
        bool smokeTest = HasFlag(args, "--smoke-test");

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
                AppLog.Write($"Необработанная ошибка: {e.Exception}");
                Environment.ExitCode = 1;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                AppLog.Write($"Критическая ошибка: {e.ExceptionObject}");

            using var context = new TrayApplicationContext(
                minimized, smokeTest, showEvent);

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
}
