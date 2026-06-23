using System.Globalization;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Services;

/// <summary>
/// Creates consistent, hot backups of the SQLite database using the online backup API
/// (<see cref="SqliteConnection.BackupDatabase(SqliteConnection)"/>), which safely snapshots the
/// live database including any WAL pages while the server keeps running. Applies a simple
/// keep-newest-N retention policy.
/// </summary>
public sealed class SqliteBackupService : IBackupService
{
    private const string FilePrefix = "inbix-";
    private const string FileExtension = ".db";

    private readonly IDbConnectionFactory _factory;
    private readonly BackupOptions _options;
    private readonly ILogger<SqliteBackupService> _logger;

    public SqliteBackupService(IDbConnectionFactory factory, IOptions<InbixOptions> options, ILogger<SqliteBackupService> logger)
    {
        _factory = factory;
        _options = options.Value.Backups;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    public async Task<BackupInfo> CreateBackupAsync(CancellationToken ct = default)
    {
        var directory = Path.GetFullPath(_options.Directory);
        Directory.CreateDirectory(directory);

        var createdAt = DateTimeOffset.UtcNow;
        var fileName = $"{FilePrefix}{createdAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}{FileExtension}";
        var fullPath = Path.Combine(directory, fileName);

        await using (var source = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            if (source is not SqliteConnection sqliteSource)
                throw new NotSupportedException("Backups are only supported for the SQLite provider.");

            // Pooling=False so the file handle is released on dispose, allowing retention pruning
            // to delete old backups (and the new file to be moved/restored) without a lingering lock.
            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Pooling = false
            }.ToString();

            await using var destination = new SqliteConnection(destinationConnectionString);
            await destination.OpenAsync(ct).ConfigureAwait(false);
            sqliteSource.BackupDatabase(destination);
        }

        var info = new BackupInfo(fileName, fullPath, new FileInfo(fullPath).Length, createdAt);
        _logger.LogInformation("Created backup {FileName} ({Size} bytes)", info.FileName, info.SizeBytes);

        Prune(directory);
        return info;
    }

    public IReadOnlyList<BackupInfo> ListBackups()
    {
        var directory = Path.GetFullPath(_options.Directory);
        if (!Directory.Exists(directory))
            return [];

        return new DirectoryInfo(directory)
            .EnumerateFiles($"{FilePrefix}*{FileExtension}")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new BackupInfo(f.Name, f.FullName, f.Length, new DateTimeOffset(f.CreationTimeUtc, TimeSpan.Zero)))
            .ToList();
    }

    private void Prune(string directory)
    {
        if (_options.RetentionCount <= 0) return;

        var stale = new DirectoryInfo(directory)
            .EnumerateFiles($"{FilePrefix}*{FileExtension}")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Skip(_options.RetentionCount)
            .ToList();

        foreach (var file in stale)
        {
            try
            {
                file.Delete();
                _logger.LogInformation("Pruned old backup {FileName}", file.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not prune old backup {FileName}", file.Name);
            }
        }
    }
}
