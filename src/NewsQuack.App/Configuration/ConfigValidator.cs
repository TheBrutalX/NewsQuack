using System.ComponentModel.DataAnnotations;

namespace NewsQuack.App.Configuration;

/// <summary>
/// Total validator for the v2 config schema: walks the whole object graph (root → instances → feeds/discord → global sections)
/// and collects every error into one list of friendly messages — never fails fast. Every message names the exact JSON path,
/// what is wrong and what is expected; no exception text, no stack traces.
/// Each property is validated through its DataAnnotations attributes via Validator.TryValidateProperty (per property rather than
/// per object because ValidationResult.MemberName is not public, so only a per-property call can preserve the exact JSON path).
/// </summary>
public static class ConfigValidator
{
    /// <summary>Validates the whole config graph and returns every error (empty list = valid).</summary>
    public static IReadOnlyList<string> Validate(NewsQuackConfig config)
    {
        var errors = new List<string>();

        var instances = config.Instances;
        if (instances is null || instances.Count == 0)
            errors.Add("Instances: at least one instance is required — add an \"Instances\" array with at least one instance object.");

        var idOccurrences = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        if (instances is not null)
        {
            for (var i = 0; i < instances.Count; i++)
            {
                var prefix = $"Instances[{i}]";
                var instance = instances[i];

                if (instance is null)
                {
                    errors.Add($"{prefix}: each entry must be a JSON object describing an instance (got null).");
                    continue;
                }

                ValidateProperty(instance, nameof(InstanceConfig.Id), $"{prefix}.Id", errors);
                ValidateProperty(instance, nameof(InstanceConfig.CheckIntervalMinutes), $"{prefix}.CheckIntervalMinutes", errors);

                if (!string.IsNullOrEmpty(instance.Id))
                    AddOccurrence(idOccurrences, instance.Id, i);

                ValidateFeeds(instance, prefix, errors);

                if (instance.Discord is null)
                    errors.Add($"{prefix}.Discord: the Discord target is required in v1.0 (the only notification target) — add a \"Discord\" object with at least a \"WebhookUrl\".");
                else
                    ValidateDiscord(instance.Discord, $"{prefix}.Discord", errors);
            }

            foreach (var group in idOccurrences.Where(g => g.Value.Count > 1))
            {
                var paths = string.Join(" and ", group.Value.Select(ix => $"Instances[{ix}].Id"));
                errors.Add($"{paths}: duplicate instance id '{group.Key}' — every instance must have a unique Id.");
            }
        }

        if (config.Http is { } http)
        {
            ValidateProperty(http, nameof(HttpSectionConfig.FeedTimeoutSeconds), "Http.FeedTimeoutSeconds", errors);
            ValidateProperty(http, nameof(HttpSectionConfig.DiscordTimeoutSeconds), "Http.DiscordTimeoutSeconds", errors);
            ValidateProperty(http, nameof(HttpSectionConfig.ImageTimeoutSeconds), "Http.ImageTimeoutSeconds", errors);
            ValidateProperty(http, nameof(HttpSectionConfig.BlockPrivateNetworkRedirects), "Http.BlockPrivateNetworkRedirects", errors);
        }

        if (config.Health is { } health)
        {
            ValidateProperty(health, nameof(HealthSectionConfig.Enabled), "Health.Enabled", errors);
            ValidateProperty(health, nameof(HealthSectionConfig.Port), "Health.Port", errors);
        }

        if (config.Logging is { } logging)
            ValidateProperty(logging, nameof(LoggingSectionConfig.Level), "Logging.Level", errors);

        return errors;
    }

    private static void ValidateDiscord(DiscordTargetConfig discord, string path, List<string> errors)
    {
        ValidateProperty(discord, nameof(DiscordTargetConfig.WebhookUrl), $"{path}.WebhookUrl", errors);
        ValidateProperty(discord, nameof(DiscordTargetConfig.Mode), $"{path}.Mode", errors);
        ValidateProperty(discord, nameof(DiscordTargetConfig.Color), $"{path}.Color", errors);
        ValidateProperty(discord, nameof(DiscordTargetConfig.FallbackImageUrl), $"{path}.FallbackImageUrl", errors);
        ValidateProperty(discord, nameof(DiscordTargetConfig.MinSendIntervalSeconds), $"{path}.MinSendIntervalSeconds", errors);   // T-048: without this line the [Range(1, 60)] attribute is never checked (explicit per-property validation)
    }

    /// <summary>Validates one property through its DataAnnotations attributes and prefixes every message with the JSON path.</summary>
    private static void ValidateProperty<T>(T node, string propertyName, string jsonPath, List<string> errors) where T : class
    {
        var value = node.GetType().GetProperty(propertyName)?.GetValue(node);

        // value may be null on purpose: [Required] reports it and TryValidateProperty accepts null values.
        var results = new List<ValidationResult>();
        Validator.TryValidateProperty(value!, new ValidationContext(node) { MemberName = propertyName }, results);

        foreach (var result in results)
            errors.Add($"{jsonPath}: {result.ErrorMessage}");
    }

    private static void ValidateFeeds(InstanceConfig instance, string prefix, List<string> errors)
    {
        var feeds = instance.Feeds;
        if (feeds is null || feeds.Count == 0)
        {
            errors.Add($"{prefix}.Feeds: at least one feed is required — add a \"Feeds\" array with at least one {{ \"Url\": ... }} entry.");
            return;
        }

        var urlOccurrences = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var j = 0; j < feeds.Count; j++)
        {
            var feed = feeds[j];
            if (feed is null)
            {
                errors.Add($"{prefix}.Feeds[{j}]: each entry must be a JSON object with a \"Url\" (got null).");
                continue;
            }

            ValidateProperty(feed, nameof(FeedConfig.Url), $"{prefix}.Feeds[{j}].Url", errors);

            var url = feed.Url;
            if (!string.IsNullOrEmpty(url))
                AddOccurrence(urlOccurrences, url, j);
        }

        foreach (var group in urlOccurrences.Where(g => g.Value.Count > 1))
        {
            var paths = string.Join(" and ", group.Value.Select(ix => $"{prefix}.Feeds[{ix}].Url"));
            errors.Add($"{paths}: duplicate feed URL '{group.Key}' — feed URLs must be unique within an instance.");
        }
    }

    private static void AddOccurrence(Dictionary<string, List<int>> occurrences, string key, int index)
    {
        if (occurrences.TryGetValue(key, out var indexes))
            indexes.Add(index);
        else
            occurrences[key] = [index];
    }
}
