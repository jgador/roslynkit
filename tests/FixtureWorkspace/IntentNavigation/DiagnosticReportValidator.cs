namespace IntentNavigation;

/// <summary>
/// Validates diagnostic reports and routes rejected reports to manual review before telemetry publication.
/// </summary>
public sealed partial class DiagnosticReportValidator : IDiagnosticReportValidator
{
    // Leading line comment: this declaration owns the diagnostic-report routing decision.
    /* Leading block comment: fatal diagnostics must not reach telemetry automatically. */
    /// <summary>
    /// Rejects fatal diagnostic reports and accepts ordinary reports for telemetry publication.
    /// </summary>
    public DiagnosticValidationResult ValidateDiagnosticReport(DiagnosticReport report)
    {
        // Body line comment: fatal diagnostics are narrowed to manual review.
        /* Body block comment: routine diagnostics can continue to telemetry. */
        if (report.Message.Contains("fatal", StringComparison.OrdinalIgnoreCase))
        {
            return new DiagnosticValidationResult(false, "manual-review");
        }

        return new DiagnosticValidationResult(true, "telemetry");
    } // Trailing line comment: preserve the selected route for the caller.
}
