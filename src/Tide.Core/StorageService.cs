using System.Text.Json;

namespace Tide.Core;

public sealed class StorageService
{
    private readonly string _storagePath;
    private readonly string _backupPath;
    private readonly string _tempPath;
    private readonly TideLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public StorageService(string? storagePath = null, TideLogger? logger = null)
    {
        _storagePath = storagePath ?? PortablePaths.StoragePath;
        _backupPath = $"{_storagePath}.bak";
        _tempPath = $"{_storagePath}.tmp";
        _logger = logger ?? new TideLogger();
    }

    public string StoragePath => _storagePath;
    public string BackupPath => _backupPath;
    public string TempPath => _tempPath;

    public async Task<Snapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_storagePath))
        {
            return new Snapshot().Normalize();
        }

        try
        {
            return (await ReadSnapshotAsync(_storagePath, cancellationToken)).Normalize();
        }
        catch (Exception exception)
        {
            _logger.Error("Primary JSON storage could not be read. Trying backup.", exception);
            return await LoadBackupOrEmptyAsync(exception.Message, cancellationToken);
        }
    }

    public async Task<Snapshot> SaveAsync(Snapshot snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
        var normalized = snapshot.Normalize();

        try
        {
            await using (var stream = new FileStream(
                             _tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_storagePath))
            {
                File.Replace(_tempPath, _storagePath, _backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(_tempPath, _storagePath, overwrite: true);
                File.Copy(_storagePath, _backupPath, overwrite: true);
            }

            return normalized;
        }
        catch
        {
            TryDelete(_tempPath);
            throw;
        }
    }

    public async Task ExportAsync(string destinationPath, Snapshot snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var stream = File.Create(destinationPath);
        await JsonSerializer.SerializeAsync(stream, snapshot.Normalize(), _jsonOptions, cancellationToken);
    }

    public async Task<Snapshot> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("インポートするJSONが見つかりません。", sourcePath);
        }

        if (File.Exists(_storagePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
            File.Copy(_storagePath, _backupPath, overwrite: true);
        }

        var imported = (await ReadSnapshotAsync(sourcePath, cancellationToken)).Normalize();
        return await SaveAsync(imported, cancellationToken);
    }

    private async Task<Snapshot> LoadBackupOrEmptyAsync(string primaryError, CancellationToken cancellationToken)
    {
        if (File.Exists(_backupPath))
        {
            try
            {
                var backup = (await ReadSnapshotAsync(_backupPath, cancellationToken)).Normalize() with
                {
                    DataWarning = "保存データが壊れていたため、バックアップから復旧しました。"
                };
                await SaveAsync(backup, cancellationToken);
                _logger.Warn("Storage was restored from backup.");
                return backup;
            }
            catch (Exception backupException)
            {
                _logger.Error("Backup JSON storage could not be read. Starting with empty data.", backupException);
            }
        }

        return new Snapshot().Normalize() with
        {
            DataWarning = $"保存データを読み込めませんでした。空のデータで起動します。詳細: {primaryError}"
        };
    }

    private async Task<Snapshot> ReadSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Snapshot>(stream, _jsonOptions, cancellationToken)
            ?? new Snapshot();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
