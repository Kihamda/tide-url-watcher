using System.IO.Pipes;
using System.Text;
using Tide.Core;

namespace Tide.App.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = "Global\\TideUrlWatcherSingleInstance";
    private const string PipeName = "TideUrlWatcherActivationPipe";
    private readonly TideLogger _logger;
    private readonly Mutex _mutex = new(false, MutexName);
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private bool _ownsMutex;

    public SingleInstanceService(TideLogger logger)
    {
        _logger = logger;
    }

    public bool TryAcquire()
    {
        try
        {
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);
            return _ownsMutex;
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
            return true;
        }
    }

    public void Start(Action<string> activationHandler)
    {
        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => ListenAsync(activationHandler, _cts.Token));
    }

    public static async Task NotifyExistingInstanceAsync(string arguments)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(cts.Token);
            var bytes = Encoding.UTF8.GetBytes(arguments);
            await client.WriteAsync(bytes, cts.Token);
        }
        catch
        {
            // If the pipe is unavailable, the second launch can simply exit.
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // Shutdown cleanup is best-effort.
        }
        finally
        {
            _mutex.Dispose();
        }
    }

    private async Task ListenAsync(Action<string> activationHandler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                using var memory = new MemoryStream();
                await server.CopyToAsync(memory, cancellationToken);
                activationHandler(Encoding.UTF8.GetString(memory.ToArray()));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.Warn("Single-instance activation pipe failed.", exception);
            }
        }
    }
}
