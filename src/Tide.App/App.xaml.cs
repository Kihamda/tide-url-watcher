using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Tide.App.Services;
using Tide.Core;
using Windows.Graphics;

namespace Tide.App;

public partial class App : Application
{
    private readonly TideLogger _logger = new();
    private readonly SingleInstanceService _singleInstance;
    private Window? _window;
    private TideRepository? _repository;
    private BackgroundRefreshService? _backgroundRefresh;
    private INotificationService? _notifications;
    private TrayIconService? _tray;
    private StartupService? _startup;
    private DispatcherQueue? _dispatcher;

    public App()
    {
        _singleInstance = new SingleInstanceService(_logger);
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

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (!_singleInstance.TryAcquire())
            {
                await SingleInstanceService.NotifyExistingInstanceAsync(args.Arguments);
                Current.Exit();
                return;
            }

            PortablePaths.MigrateLegacyData();
            PortablePaths.EnsureDataDirectory();
            _logger.Info($"App start. Version={typeof(App).Assembly.GetName().Version}, Data={PortablePaths.DataDirectory}");

            _dispatcher = DispatcherQueue.GetForCurrentThread();
            _repository = new TideRepository(logger: _logger);
            _notifications = new WindowsNotificationService(_logger);
            _notifications.Initialize();
            _startup = new StartupService(_logger);
            _tray = new TrayIconService(_logger);
            _backgroundRefresh = new BackgroundRefreshService(_repository, _notifications, _logger);

            var mainWindow = new MainWindow(
                _repository,
                _backgroundRefresh,
                _notifications,
                _tray,
                _startup,
                _logger,
                args.Arguments);
            _window = mainWindow;
            _window.AppWindow.Resize(new SizeInt32(1280, 820));

            _notifications.Activated += (_, payload) =>
                _dispatcher?.TryEnqueue(() => mainWindow.ActivateFromExternal(payload));
            _singleInstance.Start(payload =>
                _dispatcher?.TryEnqueue(() => mainWindow.ActivateFromExternal(payload)));

            _window.Activate();
        }
        catch (Exception exception)
        {
            WriteStartupError(exception);
            throw;
        }
    }

    private void WriteStartupError(Exception exception)
    {
        try
        {
            _logger.Error("Startup failure.", exception);
            PortablePaths.EnsureDataDirectory();
            File.AppendAllText(
                PortablePaths.StartupLogPath,
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Startup diagnostics are best-effort for portable, read-only locations.
        }
    }
}
