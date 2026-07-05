namespace RoslynKit;

/// <summary>
/// Represents one formatted section returned in a <c>quick-info</c> result.
/// </summary>
public sealed class QuickInfoSectionItem
{
    public QuickInfoSectionItem(string kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    /// <summary>
    /// Quick-info section kind, such as description or documentation.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Formatted section text returned by Roslyn quick-info.
    /// </summary>
    public string Text { get; }
}

/// <summary>
/// Represents one callable signature returned by <c>signature-help</c>.
/// </summary>
public sealed class SignatureHelpSignatureItem
{
    public SignatureHelpSignatureItem(
        string label,
        string documentation,
        bool isVariadic,
        IReadOnlyList<SignatureHelpParameterItem> parameters)
    {
        Label = label;
        Documentation = documentation;
        IsVariadic = isVariadic;
        Parameters = parameters;
    }

    /// <summary>
    /// Display label for the callable signature.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Documentation text associated with the signature, when Roslyn provides it.
    /// </summary>
    public string Documentation { get; }

    /// <summary>
    /// Indicates whether the signature accepts a variadic or <c>params</c> argument list.
    /// </summary>
    public bool IsVariadic { get; }

    /// <summary>
    /// Parameter entries rendered under this signature.
    /// </summary>
    public IReadOnlyList<SignatureHelpParameterItem> Parameters { get; }
}

/// <summary>
/// Represents one parameter entry inside a <c>signature-help</c> signature.
/// </summary>
public sealed class SignatureHelpParameterItem
{
    public SignatureHelpParameterItem(
        string name,
        string label,
        string documentation,
        bool isOptional)
    {
        Name = name;
        Label = label;
        Documentation = documentation;
        IsOptional = isOptional;
    }

    /// <summary>
    /// Parameter name reported by Roslyn.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Display label for the parameter, including type and modifiers when available.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Documentation text associated with the parameter, when Roslyn provides it.
    /// </summary>
    public string Documentation { get; }

    /// <summary>
    /// Indicates whether callers may omit this parameter.
    /// </summary>
    public bool IsOptional { get; }
}
