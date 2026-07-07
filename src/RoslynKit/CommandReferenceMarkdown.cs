using System.Text;

namespace RoslynKit;

/// <summary>
/// Renders the generated runtime command reference from the built-in command registry.
/// </summary>
public static class CommandReferenceMarkdown
{
    public const string RelativePath = ".agents/skills/roslynkit/references/commands.md";

    /// <summary>
    /// Renders deterministic markdown for the registered command names, usage strings, and options.
    /// </summary>
    public static string Render()
    {
        var builder = new StringBuilder();
        AppendLine(builder, "# RoslynKit Command Reference");
        AppendLine(builder);
        AppendLine(builder, "This reference lists command names, usage strings, and options exposed by the installed RoslynKit CLI. For emitted `id:` values and documentation-comment ID prefix meanings, see [references/output.md](output.md). Agent routing guidance remains in [SKILL.md](../SKILL.md).");
        AppendLine(builder);
        AppendLine(builder, "## Commands");
        AppendLine(builder);

        foreach (var command in BuiltinCommandRegistry.Commands)
        {
            builder.Append("- ").Append(CodeSpan(command.Name)).Append(": ").Append(command.Description);
            AppendLine(builder);
        }

        foreach (var command in BuiltinCommandRegistry.Commands)
        {
            AppendLine(builder);
            builder.Append("## ").Append(CodeSpan(command.Name));
            AppendLine(builder);
            AppendLine(builder);
            AppendLine(builder, command.Description);
            AppendLine(builder);
            AppendLine(builder, "### Usage");
            AppendLine(builder);
            AppendLine(builder, "```powershell");
            foreach (var usage in command.Usage)
            {
                AppendLine(builder, usage);
            }

            AppendLine(builder, "```");
            AppendLine(builder);
            AppendLine(builder, "### Options");
            AppendLine(builder);

            if (command.Options.Count == 0)
            {
                AppendLine(builder, "No options.");
                continue;
            }

            foreach (var option in command.Options)
            {
                AppendOption(builder, option);
            }
        }

        return builder.ToString();
    }

    private static void AppendOption(StringBuilder builder, OptionSpec option)
    {
        builder.Append("- ").Append(CodeSpan($"--{option.LongName}"));
        if (option.ShortName is { } shortName)
        {
            builder.Append(" / ").Append(CodeSpan($"-{shortName}"));
        }

        if (option.ValueName is { } valueName)
        {
            builder.Append(' ').Append(CodeSpan($"<{valueName}>"));
        }

        if (option.Required)
        {
            builder.Append(" (required)");
        }

        builder.Append(": ").Append(option.Description);
        AppendLine(builder);
    }

    private static string CodeSpan(string value)
    {
        return value.Contains('`') ? $"`` {value} ``" : $"`{value}`";
    }

    private static void AppendLine(StringBuilder builder, string value = "")
    {
        builder.Append(value);
        builder.Append('\n');
    }
}
