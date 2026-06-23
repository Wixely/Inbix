namespace Inbix.Core.Domain;

/// <summary>An attachment extracted from a message. Bytes live on the raw store at StoragePath.</summary>
public sealed class Attachment
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public string? Filename { get; set; }
    public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
}
