using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NewsQuack.App.Configuration;

/// <summary>Validates that a value is a Discord webhook URL: https://(discord|discordapp).com/api/webhooks/&lt;numeric id&gt;/&lt;token&gt;.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class DiscordWebhookUrlAttribute : ValidationAttribute
{
    private static readonly Regex WebhookPattern = new(
        @"^https://(discord|discordapp)\.com/api/webhooks/\d+/.+$", RegexOptions.Compiled);

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null)
            return ValidationResult.Success;   // a missing value is reported by [Required]

        if (value is string url && WebhookPattern.IsMatch(url))
            return ValidationResult.Success;

        // The webhook token is a secret: the echoed value goes through WebhookMasker so no error message ever contains it in full (T-003).
        var shown = value is string s ? WebhookMasker.Mask(s) : Convert.ToString(value, CultureInfo.InvariantCulture);
        return new ValidationResult(
            $"Must be a Discord webhook URL of the form https://discord.com/api/webhooks/<id>/<token> (got '{shown}').");
    }
}
