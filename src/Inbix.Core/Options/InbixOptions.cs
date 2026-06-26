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
    /// <summary>Provider key. Currently "sqlite". Reserved for "postgres"/"sqlserver" later.</summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>ADO.NET connection string for the selected provider.</summary>
    public string ConnectionString { get; set; } = "Data Source=./data/inbix.db";

    /// <summary>Apply pending migrations automatically on startup.</summary>
    public bool MigrateOnStartup { get; set; } = true;
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

public sealed class StorageOptions
{
    /// <summary>Directory where raw MIME messages and attachments are written.</summary>
    public string RawPath { get; set; } = "./data/raw";
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
