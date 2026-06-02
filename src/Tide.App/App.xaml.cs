using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Tide.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        UnhandledException += (_, args) => WriteStartupError(args.Exception);

        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            WriteStartupError(exception);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.AppWindow.Resize(new SizeInt32(1280, 820));
            _window.Activate();
        }
        catch (Exception exception)
        {
            WriteStartupError(exception);
            throw;
        }
    }

    private static void WriteStartupError(Exception exception)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tide");
        Directory.CreateDirectory(directory);
        File.AppendAllText(
            Path.Combine(directory, "startup.log"),
            $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
    }
}
