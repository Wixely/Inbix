using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

/// <summary>Creates and lists consistent point-in-time backups of the database.</summary>
public interface IBackupService
{
    /// <summary>True when scheduled backups are enabled in configuration.</summary>
    bool Enabled { get; }

    /// <summary>Create a backup now and prune old backups per the retention policy.</summary>
    Task<BackupInfo> CreateBackupAsync(CancellationToken ct = default);

    /// <summary>List existing backups, newest first.</summary>
    IReadOnlyList<BackupInfo> ListBackups();
}
