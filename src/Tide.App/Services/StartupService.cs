using Microsoft.Win32;
using Tide.Core;

namespace Tide.App.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Tide";
    private readonly TideLogger _logger;

    public StartupService(TideLogger logger)
    {
        _logger = logger;
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                var value = key?.GetValue(ValueName) as string;
                return value is not null &&
                       value.Contains(CurrentExePath(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public string? LastError { get; private set; }

    public bool SetEnabled(bool enabled)
    {
        LastError = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{CurrentExePath()}\" --startup", RegistryValueKind.String);
                _logger.Info("Startup registration enabled.");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.Info("Startup registration disabled.");
            }

            return true;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            _logger.Warn("Startup registration failed.", exception);
            return false;
        }
    }

    private static string CurrentExePath() =>
        Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "Tide.App.exe";
}
