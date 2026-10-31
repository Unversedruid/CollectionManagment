using Avalonia;

namespace CollectionDowngrader;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2)
        {
            return CollectionDowngrader.Run(args);
        }

        App.InitialRealmPath = args.Length == 1 ? args[0] : null;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
