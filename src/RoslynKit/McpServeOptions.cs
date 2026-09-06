namespace RoslynKit;

/// <summary>
/// Bounds the repository workers owned by one standard-input/output server connection.
/// </summary>
internal sealed record McpServeOptions(int MaxWorkspaces = 4, bool AutomaticRestore = true)
{
    public static McpServeOptions FromCommand(ParsedCommand command)
    {
        return new McpServeOptions(
            command.OptionalInt("max-workspaces", defaultValue: 4, minimumValue: 1),
            command.Optional("restore") != "false");
    }
}
