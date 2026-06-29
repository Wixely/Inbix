using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Services;

/// <summary>
/// Backup service for providers without an in-app backup mechanism (e.g. the JSON file/folder store,
/// where the files <i>are</i> the backup unit — copy/snapshot them with filesystem tooling). Reports
/// disabled and refuses on-demand backups.
/// </summary>
public sealed class NullBackupService : IBackupService
{
    public bool Enabled => false;

    public Task<BackupInfo> CreateBackupAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(
            "In-app backups are not available for this storage provider. Snapshot the data directory with filesystem tooling.");

    public IReadOnlyList<BackupInfo> ListBackups() => [];
}
