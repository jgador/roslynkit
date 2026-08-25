namespace IntentNavigation;

/// <summary>
/// Starts diagnostic-report validation through the interface boundary before telemetry publication.
/// </summary>
public sealed class DiagnosticBootstrap
{
    private readonly IDiagnosticReportValidator validator = new DiagnosticReportValidator();

    /// <summary>
    /// Routes the supplied diagnostic report through the selected validator.
    /// </summary>
    public DiagnosticValidationResult Publish(DiagnosticReport report)
    {
        return validator.ValidateDiagnosticReport(report);
    }
}
