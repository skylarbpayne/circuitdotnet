using System.ComponentModel.DataAnnotations;
using Circuit;

namespace Circuit.Interop.Tests.DocumentationExamples;

internal static class SignaturesValidationExample
{
    public static AgentSignature<ValidatedInput, ValidatedOutput> Create()
        => new AgentSignature<ValidatedInput, ValidatedOutput>(
                "validation.signature",
                "1.0.0",
                "Validated reply",
                "Return only the validated output.")
            .AddInputValidator(new SeverityValidator())
            .AddOutputValidator(new OutputValidator());

    internal sealed class ValidatedInput
    {
        [Required]
        public string Message { get; set; } = string.Empty;

        [Required]
        public string Severity { get; set; } = string.Empty;
    }

    internal sealed class ValidatedOutput
    {
        [Required]
        public string Summary { get; set; } = string.Empty;
    }

    private sealed class SeverityValidator : IContractValidator<ValidatedInput>
    {
        public IReadOnlyList<ValidationIssue> Validate(ValidatedInput value)
            => string.Equals(value.Severity, "low", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value.Severity, "high", StringComparison.OrdinalIgnoreCase)
                ? []
                : [new ValidationIssue("$.severity", "validation", "Severity must be low or high.")];
    }

    private sealed class OutputValidator : IContractValidator<ValidatedOutput>
    {
        public IReadOnlyList<ValidationIssue> Validate(ValidatedOutput value)
            => value.Summary.Length <= 200
                ? []
                : [new ValidationIssue("$.summary", "validation", "Summary must be 200 characters or fewer.")];
    }
}
