namespace Inbix.Core.Options;

/// <summary>Root configuration for Inbix. Bound from the "Inbix" configuration section.</summary>
public sealed class InbixOptions
{
    public const string SectionName = "Inbix";

    /// <summary>Domains this server accepts mail for. Recipients on any other domain are rejected.</summary>
    public string[] Domains { get; set; } = [];

    /// <summary>
    /// When true, the web host redirects HTTP→HTTPS, enables HSTS, honours forwarded proto
    /// headers (for reverse proxies), and marks the auth cookie Secure. Leave false when TLS is
    /// terminated upstream and Inbix is reached over plain HTTP on a private network.
    /// </summary>
    public bool RequireHttps { get; set; }

    public DatabaseOptions Database { get; set; } = new();
    public SmtpOptions Smtp { get; set; } = new();
    public ImapOptions Imap { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public AdminOptions Admin { get; set; } = new();
    public WorkerOptions Worker { get; set; } = new();
    public BackupOptions Backups { get; set; } = new();
    public DiagnosticsOptions Diagnostics { get; set; } = new();
    public JunkOptions Junk { get; set; } = new();

    /// <summary>
    /// When true and the database has no aliases yet, populate sample mailboxes and messages on
    /// startup (handy for demos/dev). Enables the catch-all so the sample catch-all mail is visible.
    /// </summary>
    public bool SeedSampleData { get; set; }
}

/// <summary>Database provider selection. SQLite is the default; an external DB can be slotted in later.</summary>
public sealed class DatabaseOptions
{
    /// <summary>
    /// Provider key. <c>"sqlite"</c> (default) is the embedded SQL database. <c>"json"</c> switches to a
    /// file/folder JSON store (no SQL engine) that tolerates network filesystems better — see
    /// <see cref="StorageOptions.JsonPath"/>.
    /// </summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>ADO.NET connection string for the selected provider (SQL providers only; ignored for "json").</summary>
    public string ConnectionString { get; set; } = "Data Source=./data/inbix.db";

    /// <summary>Apply pending migrations automatically on startup.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>
    /// SQLite journal mode (<c>PRAGMA journal_mode</c>). "WAL" (default) is fastest; with the default
    /// exclusive locking it also works on network filesystems. "DELETE" uses a rollback journal that
    /// relies only on POSIX file locks. Whitelisted at startup.
    /// </summary>
    public string JournalMode { get; set; } = "WAL";

    /// <summary>
    /// By <b>default</b> Inbix uses SQLite exclusive locking (<c>PRAGMA locking_mode=EXCLUSIVE</c> behind a
    /// single shared connection), so the database works on local disk <i>and</i> on a <b>network filesystem
    /// (NFS/SMB)</b> — where WAL's shared-memory <c>-shm</c> index is unavailable and SQLite would otherwise
    /// fail with "unable to open database file" / "locking protocol". Set this to <c>true</c> to instead use
    /// pooled, per-call connections for maximum read/write concurrency on <b>local disk</b>; do NOT enable it
    /// when the database is on a network filesystem. (SQLite on a network filesystem is still riskier than
    /// local storage — prefer keeping the live DB local and backing up to the share where possible.)
    /// </summary>
    public bool PooledConnections { get; set; }
}

public sealed class SmtpOptions
{
    /// <summary>Port to listen on. 25 in production; use a high port for local dev.</summary>
    public int Port { get; set; } = 25;

    /// <summary>Server name announced in the SMTP banner / EHLO response.</summary>
    public string ServerName { get; set; } = "inbix";

    /// <summary>Maximum accepted message size in bytes. Larger messages are rejected with 552.</summary>
    public long MaxMessageSizeBytes { get; set; } = 26_214_400; // 25 MiB

    /// <summary>Maximum number of concurrent SMTP sessions. Excess sessions are rejected at MAIL FROM. 0 disables.</summary>
    public int MaxConcurrentSessions { get; set; } = 50;

    /// <summary>Per-IP connection rate limit (connections per minute). Excess are rejected at MAIL FROM. 0 disables.</summary>
    public int MaxConnectionsPerMinutePerIp { get; set; }

    /// <summary>Optional path to a PFX certificate to enable STARTTLS. Empty disables TLS.</summary>
    public string CertificatePath { get; set; } = string.Empty;
    public string CertificatePassword { get; set; } = string.Empty;
}

/// <summary>
/// Read-only IMAP server so an internal mail client can browse stored mail. Its credentials are
/// SEPARATE from the admin login. <b>Disabled by default.</b> Designed for trusted internal networks
/// only — do not expose it to the internet (credentials are sent in plaintext unless TLS is configured).
/// </summary>
public sealed class ImapOptions
{
    /// <summary>Enable the read-only IMAP server. Disabled by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Port to listen on. 143 is the IMAP default (993 if you terminate TLS here).</summary>
    public int Port { get; set; } = 143;

    /// <summary>IMAP login username. Independent of the admin login. Defaults to "admin".</summary>
    public string Username { get; set; } = "admin";

    /// <summary>
    /// Plaintext IMAP password (default "admin"). Convenient for internal use, but prefer
    /// <see cref="PasswordHash"/>. Ignored when <see cref="PasswordHash"/> is set.
    /// </summary>
    public string Password { get; set; } = "admin";

    /// <summary>
    /// PBKDF2 password hash (format "pbkdf2-sha256$iterations$salt$key"). Generate with
    /// <c>dotnet run --project src/Inbix.Web -- hash-password &lt;password&gt;</c>. Takes precedence over <see cref="Password"/>.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Optional PFX certificate to serve IMAP over TLS (implicit TLS on connect). Empty = plaintext.</summary>
    public string CertificatePath { get; set; } = string.Empty;
    public string CertificatePassword { get; set; } = string.Empty;

    /// <summary>Maximum concurrent IMAP client connections.</summary>
    public int MaxConcurrentSessions { get; set; } = 20;

    /// <summary>
    /// When true, deleting a message in an IMAP client (<c>\Deleted</c> + EXPUNGE, or moving to Trash)
    /// <b>permanently removes it from Inbix</b> — the row, raw MIME and attachments. Off by default: the
    /// mailbox is read-only, so client deletes don't affect the server. Enabling this allows real data loss
    /// from a mail client.
    /// </summary>
    public bool AllowDelete { get; set; }
}

public sealed class StorageOptions
{
    /// <summary>Directory where raw MIME messages and attachments are written.</summary>
    public string RawPath { get; set; } = "./data/raw";

    /// <summary>
    /// Root directory for the JSON file/folder store (used when <c>Database:Provider = "json"</c>).
    /// Aliases become folders and each email is a single JSON file underneath. Safe to place on a
    /// network filesystem: every write is an atomic temp-file + rename, so a crash damages at most one
    /// file rather than a whole database.
    /// </summary>
    public string JsonPath { get; set; } = "./data/store";

    /// <summary>
    /// How long a single file write/move is retried before giving up, in seconds. Network filesystems
    /// throw transient IO errors (e.g. ESTALE "stale file handle") that succeed on a retry; writes block
    /// and retry with backoff up to this many seconds to avoid corrupting a file.
    /// </summary>
    public int WriteRetrySeconds { get; set; } = 5;
}

public sealed class BackupOptions
{
    /// <summary>Enable scheduled backups. On-demand backups via the API work regardless.</summary>
    public bool Enabled { get; set; }

    /// <summary>Directory where backup files are written.</summary>
    public string Directory { get; set; } = "./data/backups";

    /// <summary>Hours between scheduled backups.</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>Number of most-recent backups to keep; older ones are pruned.</summary>
    public int RetentionCount { get; set; } = 7;
}

public sealed class WorkerOptions
{
    /// <summary>How often the MIME parser polls for unparsed messages.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>Number of unparsed messages to claim per poll.</summary>
    public int BatchSize { get; set; } = 20;
}

/// <summary>Junk inbox retention/cleanup settings.</summary>
public sealed class JunkOptions
{
    /// <summary>Days a message stays in Junk before the cleanup job deletes it (by junked-at time).</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Hours between Junk cleanup runs. Set to 0 or less to run only once at startup.</summary>
    public int CleanupIntervalHours { get; set; } = 24;
}

/// <summary>Settings for the diagnostics / status page.</summary>
public sealed class DiagnosticsOptions
{
    /// <summary>
    /// URL of a plain-text "what is my public IP" service, used to compare the public IP against
    /// the MX target. Set empty to skip the outbound public-IP lookup.
    /// </summary>
    public string PublicIpLookupUrl { get; set; } = "https://checkip.amazonaws.com";

    /// <summary>
    /// How often the background diagnostics run repeats. The first run happens a few seconds after
    /// startup; subsequent runs every this many hours. Set to 0 or less to run only once at startup.
    /// </summary>
    public int IntervalHours { get; set; } = 6;
}

/// <summary>Admin UI / API authentication.</summary>
public sealed class AdminOptions
{
    /// <summary>Admin login username. Defaults to "admin" when left empty.</summary>
    public string Username { get; set; } = "admin";

    /// <summary>
    /// Plaintext admin password. Convenient for local/dev via env var or user-secrets, but prefer
    /// <see cref="PasswordHash"/> in production. Ignored when <see cref="PasswordHash"/> is set.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// PBKDF2 password hash (format "pbkdf2-sha256$iterations$salt$key"). Generate with
    /// <c>dotnet run --project src/Inbix.Web -- hash-password &lt;password&gt;</c>. Takes precedence over <see cref="Password"/>.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// When set, the API also accepts this value in the "X-Api-Key" header (for programmatic access
    /// without a login cookie). The browser UI always uses cookie authentication.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
