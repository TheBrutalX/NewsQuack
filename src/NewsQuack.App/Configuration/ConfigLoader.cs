using System.Text.Json;

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

    /// <summary>Environment variable pointing at the config file (file location only — value overrides are a later task).</summary>
    public const string ConfigEnvironmentVariable = "NEWSQUACK_CONFIG";

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

    /// <summary>Reads one config file, parses and validates it. Never throws for expected I/O or JSON problems — they become friendly messages.</summary>
    public static ConfigLoadResult LoadAndValidate(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        // Filtered catch (not a catch-all): only read failures become friendly messages, fully converted here — no logging framework yet in this milestone.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(null, [$"Could not read config file '{path}': {ex.Message}"]);
        }

        try
        {
            var config = JsonSerializer.Deserialize<NewsquackConfig>(json);
            if (config is null)   // the JSON document was a literal null
                return new(null, [$"Config file '{path}' does not contain a JSON object."]);

            return new(config, ConfigValidator.Validate(config));
        }
        catch (JsonException ex)
        {
            // JsonException.Message carries the parser position (Path / LineNumber / BytePositionInLine) — no stack trace.
            return new(null, [$"Config file '{path}' is not valid JSON: {ex.Message}"]);
        }
    }
}
