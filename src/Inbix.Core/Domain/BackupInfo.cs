namespace Inbix.Core.Domain;

/// <summary>Metadata about a stored backup file.</summary>
public sealed record BackupInfo(string FileName, string Path, long SizeBytes, DateTimeOffset CreatedAt);
