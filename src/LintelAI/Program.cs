using Avalonia;
using Velopack;

namespace LintelAI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first. When the setup installs, updates or uninstalls the app it starts it with
        // special arguments, and Velopack handles those and exits before any window is created.
        // In an ordinary launch (or "dotnet run") it does nothing.
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
