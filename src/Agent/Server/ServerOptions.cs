using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Server;

/// <summary>
/// Server mode (the default) against <c>BROWSER_ONLY_MODE=true</c>. In browser-only mode the agent is
/// exactly the stateless relay it always was: nothing under <see cref="DataDir"/> is touched, no
/// database is opened, no scheduler runs, and every profile and task endpoint answers
/// <c>404 SERVER_MODE_DISABLED</c>. In server mode users may keep a passkey-encrypted profile on the
/// server (see <c>Profiles/</c>) and, when <c>DATA_KEY</c> is set, scheduled tasks (see <c>Tasks/</c>).
/// <para>
/// Read from configuration (environment variables included) so <c>WebApplicationFactory</c> tests can
/// set every value with <c>UseSetting</c>. Never logs a key.
/// </para>
/// </summary>
public sealed record ServerOptions
{
    public bool BrowserOnly { get; init; }

    public bool ServerMode => !BrowserOnly;

    /// <summary>Where the SQLite database lives. <c>/data</c> in the image, <c>./data</c> otherwise.</summary>
    public string DataDir { get; init; } = "data";

    /// <summary>
    /// <c>DATA_KEY</c>: the server's own key (32 bytes). It seals what the server must read while the
    /// user is away: delegated accounts, task definitions, the task OpenRouter key. Unset = profiles
    /// still work, but delegation and scheduled tasks are off.
    /// </summary>
    public byte[]? DataKey { get; init; }

    /// <summary><c>DATA_KEY_PREVIOUS</c>: opens (never seals) data sealed before a rotation.</summary>
    public byte[]? DataKeyPrevious { get; init; }

    /// <summary>
    /// <c>PUBLIC_ORIGIN</c>, e.g. <c>https://mail-agent.example.com</c>: the passkey origin and relying
    /// party. Unset = taken from each request (after trusted forwarded headers).
    /// </summary>
    public Uri? PublicOrigin { get; init; }

    /// <summary>
    /// <c>PROFILE_MAIL_HOSTS</c>: when set, only accounts on these mail servers (host names, lower case)
    /// may be saved to a profile. Empty = any server the SSRF guard allows.
    /// </summary>
    public IReadOnlySet<string> ProfileMailHosts { get; init; } = new HashSet<string>();

    /// <summary><c>PROFILE_IDLE_DAYS</c>: a profile nobody has opened for this long is deleted.</summary>
    public int ProfileIdleDays { get; init; } = 180;

    /// <summary><c>MAX_PROFILES</c>: how many profiles this instance holds at most.</summary>
    public int MaxProfiles { get; init; } = 1000;

    /// <summary>
    /// <c>PROFILE_MAX_IDLE_MINUTES</c>: the longest session idle timeout a user may choose for their
    /// profile (Settings). Sessions still end at <c>SESSION_MAX_HOURS</c> regardless.
    /// </summary>
    public int ProfileMaxIdleMinutes { get; init; } = 480;

    /// <summary>The shortest idle timeout a profile may choose.</summary>
    public const int ProfileMinIdleMinutes = 5;

    public bool TasksEnabledSetting { get; init; } = true;

    /// <summary>Scheduled tasks need the server key: they run while nobody is there to unlock anything.</summary>
    public bool TasksEnabled => ServerMode && DataKey is not null && TasksEnabledSetting;

    public int TaskConcurrency { get; init; } = 2;
    public TimeSpan TaskTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public int TaskMaxToolRounds { get; init; } = 15;
    public TimeSpan TaskMinInterval { get; init; } = TimeSpan.FromMinutes(15);
    public int TasksPerProfile { get; init; } = 10;
    public int TaskRunRetention { get; init; } = 50;

    /// <summary>
    /// <c>ANALYSIS_MODEL</c>: the default model for <c>analyze_result</c> (large tool results), offered to the
    /// browser in <c>GET /api/config</c>; the user may pick another. Default <c>openai/gpt-6-luna</c>.
    /// </summary>
    public string AnalysisModel { get; init; } = DefaultAnalysisModel;

    public const string DefaultAnalysisModel = "openai/gpt-6-luna";

    /// <summary>
    /// <c>TASK_ANALYSIS_MODEL</c>: the analysis model for scheduled runs, billed to the task key. Defaults to
    /// <see cref="AnalysisModel"/>; <c>off</c> (or <c>none</c>) = no artifacts in runs, results clamped as before.
    /// </summary>
    public string? TaskAnalysisModel { get; init; } = DefaultAnalysisModel;

    /// <summary>OpenRouter's chat-completions endpoint by default; tests point it at a fake.</summary>
    public Uri LlmBaseUrl { get; init; } = new("https://openrouter.ai/api/v1/");

    public string DatabasePath => Path.Combine(Path.GetFullPath(DataDir), "smartermail-agent.db");

    /// <summary>Parses the settings; a malformed value fails startup with a message naming it.</summary>
    public static ServerOptions FromConfiguration(IConfiguration config, ILogger? logger = null)
    {
        var browserOnly = Bool(config, "BROWSER_ONLY_MODE", false);

        var rawKey = config["DATA_KEY"];
        var key = ResumeSealer.DecodeKey(rawKey);
        if (!string.IsNullOrWhiteSpace(rawKey) && key is null)
            throw new InvalidOperationException("DATA_KEY must be 32 bytes of base64 (openssl rand -base64 32).");

        var rawPrevious = config["DATA_KEY_PREVIOUS"];
        var previous = ResumeSealer.DecodeKey(rawPrevious);
        if (!string.IsNullOrWhiteSpace(rawPrevious) && previous is null)
            throw new InvalidOperationException("DATA_KEY_PREVIOUS must be 32 bytes of base64.");

        Uri? origin = null;
        if (config["PUBLIC_ORIGIN"] is { Length: > 0 } rawOrigin)
        {
            if (!Uri.TryCreate(rawOrigin.Trim(), UriKind.Absolute, out origin) ||
                origin.Scheme is not ("https" or "http") || origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query))
                throw new InvalidOperationException("PUBLIC_ORIGIN must be a bare origin such as https://mail-agent.example.com.");
        }

        var llm = config["LLM_BASE_URL"] is { Length: > 0 } rawLlm
            ? new Uri(rawLlm.EndsWith('/') ? rawLlm : rawLlm + "/", UriKind.Absolute)
            : new Uri("https://openrouter.ai/api/v1/");

        var analysisModel = config["ANALYSIS_MODEL"] is { Length: > 0 } rawAnalysis ? rawAnalysis.Trim() : DefaultAnalysisModel;
        var taskAnalysisModel = config["TASK_ANALYSIS_MODEL"]?.Trim() switch
        {
            null or "" => analysisModel,
            "off" or "none" => null,
            var model => model,
        };

        var options = new ServerOptions
        {
            BrowserOnly = browserOnly,
            DataDir = config["DATA_DIR"] is { Length: > 0 } dir ? dir : "data",
            DataKey = key,
            DataKeyPrevious = key is null ? null : previous,
            PublicOrigin = origin,
            ProfileMailHosts = (config["PROFILE_MAIL_HOSTS"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(h => h.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal),
            ProfileIdleDays = Int(config, "PROFILE_IDLE_DAYS", 180),
            MaxProfiles = Int(config, "MAX_PROFILES", 1000),
            ProfileMaxIdleMinutes = Math.Max(ProfileMinIdleMinutes, Int(config, "PROFILE_MAX_IDLE_MINUTES", 480)),
            TasksEnabledSetting = Bool(config, "TASKS_ENABLED", true),
            TaskConcurrency = Int(config, "TASK_CONCURRENCY", 2),
            TaskTimeout = TimeSpan.FromMinutes(Int(config, "TASK_TIMEOUT_MINUTES", 10)),
            TaskMaxToolRounds = Int(config, "TASK_MAX_TOOL_ROUNDS", 15),
            TaskMinInterval = TimeSpan.FromMinutes(Int(config, "TASK_MIN_INTERVAL_MINUTES", 15)),
            TasksPerProfile = Int(config, "TASKS_PER_PROFILE", 10),
            TaskRunRetention = Int(config, "TASK_RUN_RETENTION", 50),
            LlmBaseUrl = llm,
            AnalysisModel = analysisModel,
            TaskAnalysisModel = taskAnalysisModel,
        };

        if (logger is not null)
        {
            if (options.BrowserOnly)
                logger.LogInformation("Browser-only mode: nothing is stored on the server.");
            else
                logger.LogInformation("Server mode: profiles {Profiles}; scheduled tasks {Tasks}.",
                    "on", options.TasksEnabled ? "on" : options.DataKey is null ? "off (no DATA_KEY)" : "off (TASKS_ENABLED=false)");
        }

        return options;
    }

    /// <summary>Whether an account on <paramref name="baseUrl"/> may be saved to a profile here.</summary>
    public bool AllowsProfileHost(string baseUrl) =>
        ProfileMailHosts.Count == 0 ||
        (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && ProfileMailHosts.Contains(uri.Host.ToLowerInvariant()));

    private static bool Bool(IConfiguration config, string name, bool fallback) =>
        config[name]?.Trim().ToLowerInvariant() switch
        {
            null or "" => fallback,
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => throw new InvalidOperationException($"{name} must be true or false."),
        };

    private static int Int(IConfiguration config, string name, int fallback) =>
        config[name] is { Length: > 0 } raw
            ? int.TryParse(raw, out var value) && value > 0
                ? value
                : throw new InvalidOperationException($"{name} must be a positive whole number.")
            : fallback;
}
