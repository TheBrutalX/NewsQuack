using System.ComponentModel.DataAnnotations;

namespace NewsQuack.App.Configuration;

/// <summary>Validates that a string value is one of a fixed set of allowed values, compared case-insensitively.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class EnumLikeAttribute(params string[] allowedValues) : ValidationAttribute
{
    /// <summary>The allowed values, in the order they are listed in error messages.</summary>
    public IReadOnlyList<string> AllowedValues { get; } = allowedValues;

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null)
            return ValidationResult.Success;   // optional fields may be absent

        if (value is string s && AllowedValues.Any(v => string.Equals(v, s, StringComparison.OrdinalIgnoreCase)))
            return ValidationResult.Success;

        var valid = string.Join(", ", AllowedValues.Select(v => $"'{v}'"));
        return new ValidationResult($"Must be one of: {valid} (got '{value}').");
    }
}
