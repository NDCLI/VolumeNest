using System.Diagnostics;

namespace VolumeNest;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Bắt mọi exception chưa được xử lý → ghi vào file log
        Application.ThreadException += (_, e) => LogException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogException(e.ExceptionObject as Exception);

        // Chỉ cho phép 1 instance chạy cùng lúc
        using var mutex = new Mutex(true, "VolumeNest.SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        using var tray = new TrayApp();
        Application.Run();
    }

    private static void LogException(Exception? ex)
    {
        try
        {
            // Ghi vào cùng thư mục với exe — luôn tìm được
            string logPath = Path.Combine(
                AppContext.BaseDirectory,
                "VolumeNest_crash.txt");
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch { }
    }
}
