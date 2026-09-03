using System.Runtime.ExceptionServices;

namespace ViennaBar;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ViennaBar.ShellNative.ShellNative.DebugLog("Main enter");

        // crash-log: toda excepción no manejada queda en %TEMP%\viennabar-crash.log
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "viennabar-crash.log"),
                    $"[{DateTime.Now:HH:mm:ss}] FATAL\n{e.ExceptionObject}\n---\n{Environment.StackTrace}");
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                e.SetObserved();
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "viennabar-crash.log"),
                    $"\n[{DateTime.Now:HH:mm:ss}] TASK {e.Exception}");
            }
            catch { }
        };

        return App.Run(args);
    }
}
