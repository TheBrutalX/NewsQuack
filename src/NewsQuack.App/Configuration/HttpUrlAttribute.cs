using System.ComponentModel.DataAnnotations;

namespace NewsQuack.App.Configuration;

/// <summary>Validates that a value is an absolute http(s) URL with a non-empty host (feed URLs, fallback image URL).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class HttpUrlAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null)
            return ValidationResult.Success;   // optional fields may be absent

        if (value is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.IdnHost.Length > 0)
        {
            return ValidationResult.Success;
        }

        return new ValidationResult($"Must be an http(s) URL with a host, e.g. https://example.com/feed.xml (got '{value}').");
    }
}
