using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace NewsQuack.App.Configuration;

/// <summary>
/// Discovers, reads and parses the v2 config file. Pure functions — no stderr writes and no Environment.Exit here;
/// Program.cs owns the "print every message to stderr + exit 1" contract so tests can assert on the messages.
/// </summary>
public static class ConfigLoader
{
    /// <summary>Config file name searched in the working directory.</summary>
    public const string DefaultFileName = "newsquack.json";

    /// <summary>Default config location inside the Docker container.</summary>
    public const string ContainerConfigPath = "/config/newsquack.json";

    /// <summary>Environment variable pointing at the config file (file location only — value overrides use <see cref="EnvironmentVariablePrefix"/>).</summary>
    public const string ConfigEnvironmentVariable = "NEWSQUACK_CONFIG";

    /// <summary>
    /// Prefix of every env var that overrides a config value (frozen by T-003); sections are separated by "__" per .NET convention,
    /// e.g. NEWSQUACK_INSTANCES__0__DISCORD__WEBHOOKURL → Instances[0].Discord.WebhookUrl.
    /// </summary>
    public const string EnvironmentVariablePrefix = "NEWSQUACK_";

    /// <summary>The ordered list of locations the boot process searches; first hit wins.</summary>
    public static IReadOnlyList<string> GetSearchPaths(IReadOnlyList<string> args, Func<string?> environmentVariableReader)
    {
        var paths = new List<string>(4);

        if (args.Count > 0 && !string.IsNullOrWhiteSpace(args[0]))
            paths.Add(args[0]);   // first CLI argument is treated as a path

        var envPath = environmentVariableReader();
        if (!string.IsNullOrWhiteSpace(envPath))
            paths.Add(envPath);   // not null here: IsNullOrWhiteSpace has [NotNullWhen(false)]

        paths.Add($"./{DefaultFileName}");
        paths.Add(ContainerConfigPath);

        return paths;
    }

    /// <summary>The first searched location that exists, or null when the file is missing everywhere.</summary>
    public static string? FindFirstExisting(IReadOnlyList<string> searchPaths) =>
        searchPaths.FirstOrDefault(File.Exists);

    /// <summary>Friendly single-message result for "no config file found anywhere".</summary>
    public static ConfigLoadResult MissingFile(IReadOnlyList<string> searchPaths) =>
        new(null,
        [
            $"No config file found. Searched: {string.Join(", ", searchPaths)}. " +
            $"For Docker, mount your config: -v /host/path/{DefaultFileName}:{ContainerConfigPath} (or set {ConfigEnvironmentVariable})."
        ]);

    /// <summary>Reads one config file with env var overrides applied, parses and validates it. Never throws for expected I/O or JSON problems — they become friendly messages.</summary>
    public static ConfigLoadResult LoadAndValidate(string path) =>
        LoadAndValidate(path, ReadProcessEnvironmentVariables());

    /// <summary>
    /// Same as <see cref="LoadAndValidate(string)"/>, but with an injectable environment snapshot so tests can control the NEWSQUACK_* variables.
    /// </summary>
    public static ConfigLoadResult LoadAndValidate(string path, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        try
        {
            // Opened before anything else: a missing/unreadable file throws here (same friendly message as T-002's File.ReadAllText).
            // The stream must stay open until Build() — the JSON parse happens there. A FileStream resolves relative paths against the
            // working directory, exactly like discovery (AddJsonFile would resolve them against AppContext.BaseDirectory instead).
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);

            // Standard .NET configuration pipeline (T-003): the JSON file is the first source; the NEWSQUACK_-prefixed env vars are added
            // after it, so env always wins over the file. The bound result goes through exactly the same POCOs + total validator as T-002.
            var builder = new ConfigurationBuilder();
            builder.AddJsonStream(fileStream);
            builder.AddInMemoryCollection(BuildOverrideVariables(environmentVariables));

            var configuration = builder.Build();   // JSON parse happens here (raw JsonException / FormatException — see the catches below)

            var config = configuration.Get<NewsquackConfig>();
            if (config is null)   // cannot happen: the binder constructs the root object (a non-object JSON root already threw above); kept for flow-state completeness
                return new(null, [$"Config file '{path}' does not contain a JSON object."]);

            return new(config, ConfigValidator.Validate(config));
        }
        // Filtered catches (not catch-all): only expected I/O / JSON / binding problems become friendly messages, fully converted here — no logging framework yet in this milestone.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(null, [$"Could not read config file '{path}': {ex.Message}"]);
        }
        catch (JsonException ex)
        {
            // JsonException.Message carries the parser position (Path / LineNumber / BytePositionInLine) — no stack trace.
            return new(null, [$"Config file '{path}' is not valid JSON: {ex.Message}"]);
        }
        catch (FormatException ex) when (ex.Message.Contains("Top-level JSON element", StringComparison.Ordinal))
        {
            // A literal null / array / scalar at the root — same friendly message as T-002's "does not contain a JSON object".
            return new(null, [$"Config file '{path}' does not contain a JSON object."]);
        }
        catch (FormatException ex)
        {
            // Valid JSON the configuration pipeline still rejects, e.g. duplicate keys ("A duplicate key 'X' was found.").
            return new(null, [$"Config file '{path}' could not be loaded: {ex.Message}"]);
        }
        catch (InvalidOperationException ex)
        {
            // A value that cannot be converted to the schema type, e.g. "Health": { "Port": "abc" } — the message names the exact config path; no stack trace.
            return new(null, [$"Invalid value in config file '{path}': {ex.Message}"]);
        }
    }

    /// <summary>The real process environment as a plain snapshot (the default for <see cref="LoadAndValidate(string)"/>).</summary>
    private static Dictionary<string, string?> ReadProcessEnvironmentVariables()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key!] = entry.Value as string;   // keys are always strings from GetEnvironmentVariables

        return variables;
    }

    /// <summary>
    /// The NEWSQUACK_-prefixed env vars as configuration keys: prefix stripped, "__" replaced with the .NET section separator —
    /// exactly what AddEnvironmentVariables(prefix: "NEWSQUACK_") would contribute after the JSON source. Empty values are dropped:
    /// an empty-string env var means "not set", so the file value (or schema default) wins (T-003 rule).
    /// </summary>
    private static IReadOnlyCollection<KeyValuePair<string, string?>> BuildOverrideVariables(IReadOnlyDictionary<string, string?> environmentVariables)
    {
        var overrides = new List<KeyValuePair<string, string?>>();

        foreach (var (name, value) in environmentVariables)
        {
            if (!name.StartsWith(EnvironmentVariablePrefix, StringComparison.Ordinal))
                continue;

            var key = name[EnvironmentVariablePrefix.Length..];
            if (key.Length == 0)          // bare "NEWSQUACK_" — no path behind the prefix
                continue;

            if (string.IsNullOrEmpty(value))   // empty value = not set → file/default wins
                continue;

            overrides.Add(new KeyValuePair<string, string?>(key.Replace("__", ConfigurationPath.KeyDelimiter), value));
        }

        return overrides;
    }
}
