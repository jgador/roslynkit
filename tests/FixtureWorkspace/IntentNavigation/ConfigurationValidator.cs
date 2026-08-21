namespace IntentNavigation;

/// <summary>
/// Validates configuration values before an application accepts a configuration snapshot.
/// </summary>
public sealed class ConfigurationValidator
{
    /// <summary>
    /// Validates configuration values without inspecting diagnostic reports or telemetry routes.
    /// </summary>
    public bool ValidateConfiguration(string configurationName)
    {
        return !string.IsNullOrWhiteSpace(configurationName);
    }
}
