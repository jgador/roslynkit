using System.Collections;
using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace RoslynKit;

/// <summary>
/// Bridges to Roslyn's internal signature-help services for deterministic CLI payloads.
/// </summary>
internal static class RoslynSignatureHelpService
{
    private const string ExportProviderInterfaceName = "Microsoft.CodeAnalysis.Host.Mef.IMefHostExportProvider";
    private const string SignatureHelpServiceTypeName = "Microsoft.CodeAnalysis.SignatureHelp.SignatureHelpService";
    private const string SignatureHelpTriggerInfoTypeName = "Microsoft.CodeAnalysis.SignatureHelp.SignatureHelpTriggerInfo";
    private const string SignatureHelpTriggerReasonTypeName = "Microsoft.CodeAnalysis.SignatureHelp.SignatureHelpTriggerReason";

    private static readonly SymbolDisplayFormat SignatureLabelFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat ParameterLabelFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static async Task<ReflectedSignatureHelp?> GetSignatureHelpAsync(Document document, int position, CancellationToken cancellationToken)
    {
        try
        {
            var reflected = await TryGetReflectedSignatureHelpAsync(document, position, cancellationToken).ConfigureAwait(false);
            if (reflected is not null)
            {
                return reflected;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fall back to public semantic analysis when Roslyn's internal signature-help composition is unavailable.
        }

        return await GetFallbackSignatureHelpAsync(document, position, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ReflectedSignatureHelp?> TryGetReflectedSignatureHelpAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var service = GetSignatureHelpService(document.Project.Solution);
        if (service is null)
        {
            return null;
        }

        var serviceType = service.GetType();
        var featuresAssembly = serviceType.Assembly;
        var triggerInfoType = featuresAssembly.GetType(SignatureHelpTriggerInfoTypeName, throwOnError: true)!;
        var triggerReasonType = featuresAssembly.GetType(SignatureHelpTriggerReasonTypeName, throwOnError: true)!;
        var triggerReason = Enum.Parse(triggerReasonType, "InvokeSignatureHelpCommand");
        var triggerInfo = Activator.CreateInstance(
            triggerInfoType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: [triggerReason, null],
            culture: null)
            ?? throw new InvalidOperationException("Could not create Roslyn signature help trigger info.");

        var getSignatureHelpMethod = serviceType.GetMethod(
            "GetSignatureHelpAsync",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(Document), typeof(int), triggerInfoType, typeof(CancellationToken)],
            modifiers: null)
            ?? throw new InvalidOperationException("Could not locate Roslyn signature help service method.");

        var task = (Task)(getSignatureHelpMethod.Invoke(service, [document, position, triggerInfo, cancellationToken])
            ?? throw new InvalidOperationException("Roslyn signature help invocation returned null."));

        await task.ConfigureAwait(false);

        var result = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(task)
            ?? throw new InvalidOperationException("Roslyn signature help result was unavailable.");

        var bestItems = result.GetType().GetField("Item2", BindingFlags.Instance | BindingFlags.Public)?.GetValue(result);
        if (bestItems is null)
        {
            return null;
        }

        return ConvertResult(bestItems, cancellationToken);
    }

    private static async Task<ReflectedSignatureHelp?> GetFallbackSignatureHelpAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            return null;
        }

        var context = FindFallbackContext(root, semanticModel, position, cancellationToken);
        if (context is null || context.Symbols.Count == 0)
        {
            return null;
        }

        var signatures = context.Symbols
            .Select(symbol => CreateSignatureItem(symbol, cancellationToken))
            .ToArray();

        if (signatures.Length == 0)
        {
            return null;
        }

        var activeSignature = context.SelectedSymbol is null
            ? 0
            : Array.FindIndex(context.Symbols.ToArray(), symbol => SymbolEqualityComparer.Default.Equals(symbol, context.SelectedSymbol));
        if (activeSignature < 0)
        {
            activeSignature = 0;
        }

        return new ReflectedSignatureHelp(
            context.ApplicableSpan,
            activeSignature,
            context.ActiveParameter,
            signatures);
    }

    private static ReflectedSignatureHelp ConvertResult(object bestItems, CancellationToken cancellationToken)
    {
        var resultType = bestItems.GetType();
        var applicableSpan = (TextSpan)(resultType.GetProperty("ApplicableSpan", BindingFlags.Instance | BindingFlags.Public)?.GetValue(bestItems)
            ?? throw new InvalidOperationException("Roslyn signature help span was unavailable."));
        var activeParameter = (int)(resultType.GetProperty("SemanticParameterIndex", BindingFlags.Instance | BindingFlags.Public)?.GetValue(bestItems)
            ?? throw new InvalidOperationException("Roslyn signature help parameter index was unavailable."));
        var selectedItemIndex = (int?)resultType.GetProperty("SelectedItemIndex", BindingFlags.Instance | BindingFlags.Public)?.GetValue(bestItems);
        var items = (IEnumerable?)(resultType.GetProperty("Items", BindingFlags.Instance | BindingFlags.Public)?.GetValue(bestItems))
            ?? throw new InvalidOperationException("Roslyn signature help items were unavailable.");

        var signatures = new List<SignatureHelpSignatureItem>();
        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            var itemType = item.GetType();
            var parameters = new List<SignatureHelpParameterItem>();
            var parameterObjects = (IEnumerable?)(itemType.GetProperty("Parameters", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item));
            if (parameterObjects is not null)
            {
                foreach (var parameter in parameterObjects)
                {
                    if (parameter is null)
                    {
                        continue;
                    }

                    var parameterType = parameter.GetType();
                    parameters.Add(new SignatureHelpParameterItem(
                        (string?)parameterType.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public)?.GetValue(parameter) ?? string.Empty,
                        parameter.ToString() ?? string.Empty,
                        InvokeDocumentationFactory(parameterType.GetProperty("DocumentationFactory", BindingFlags.Instance | BindingFlags.Public)?.GetValue(parameter), cancellationToken),
                        (bool)(parameterType.GetProperty("IsOptional", BindingFlags.Instance | BindingFlags.Public)?.GetValue(parameter) ?? false)));
                }
            }

            signatures.Add(new SignatureHelpSignatureItem(
                item.ToString() ?? string.Empty,
                InvokeDocumentationFactory(itemType.GetProperty("DocumentationFactory", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item), cancellationToken),
                (bool)(itemType.GetProperty("IsVariadic", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item) ?? false),
                parameters));
        }

        return new ReflectedSignatureHelp(
            applicableSpan,
            GetActiveSignature(signatures, selectedItemIndex, activeParameter),
            activeParameter,
            signatures);
    }

    private static int GetActiveSignature(IReadOnlyList<SignatureHelpSignatureItem> signatures, int? selectedItemIndex, int activeParameter)
    {
        if (selectedItemIndex.HasValue)
        {
            return selectedItemIndex.Value;
        }

        for (var index = 0; index < signatures.Count; index++)
        {
            var signature = signatures[index];
            if (signature.IsVariadic || signature.Parameters.Count > activeParameter)
            {
                return index;
            }
        }

        return 0;
    }

    private static object? GetSignatureHelpService(Solution solution)
    {
        var hostServices = solution.Workspace.Services.HostServices;
        var exportProviderInterface = hostServices.GetType().GetInterface(ExportProviderInterfaceName);
        if (exportProviderInterface is null)
        {
            return null;
        }

        var signatureHelpServiceType = typeof(QuickInfoService).Assembly.GetType(SignatureHelpServiceTypeName, throwOnError: false);

        if (signatureHelpServiceType is null)
        {
            return null;
        }

        var getExportsMethod = exportProviderInterface
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name.Contains("GetExports", StringComparison.Ordinal) && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1);
        var exports = (IEnumerable?)(getExportsMethod.MakeGenericMethod(signatureHelpServiceType).Invoke(hostServices, null));
        if (exports is null)
        {
            return null;
        }

        foreach (var export in exports)
        {
            if (export is null)
            {
                continue;
            }

            return export.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)?.GetValue(export);
        }

        return null;
    }

    private static FallbackSignatureHelpContext? FindFallbackContext(SyntaxNode root, SemanticModel semanticModel, int position, CancellationToken cancellationToken)
    {
        if (root.FullSpan.IsEmpty)
        {
            return null;
        }

        var tokenPosition = Math.Clamp(position, root.FullSpan.Start, root.FullSpan.End);
        if (tokenPosition == root.FullSpan.End)
        {
            tokenPosition--;
        }

        var token = root.FindToken(tokenPosition);
        var nodes = token.Parent?.AncestorsAndSelf() ?? Enumerable.Empty<SyntaxNode>();
        foreach (var node in nodes)
        {
            if (TryCreateFallbackContext(node, semanticModel, position, cancellationToken, out var context))
            {
                return context;
            }
        }

        return null;
    }

    private static bool TryCreateFallbackContext(
        SyntaxNode node,
        SemanticModel semanticModel,
        int position,
        CancellationToken cancellationToken,
        out FallbackSignatureHelpContext? context)
    {
        context = node switch
        {
            ArgumentListSyntax argumentList when argumentList.Parent is not null => CreateFallbackContext(argumentList, argumentList.Parent, semanticModel, position, cancellationToken),
            AttributeArgumentListSyntax attributeArgumentList when attributeArgumentList.Parent is AttributeSyntax attribute => CreateFallbackContext(attributeArgumentList, attribute, semanticModel, position, cancellationToken),
            _ => null,
        };

        return context is not null;
    }

    private static FallbackSignatureHelpContext? CreateFallbackContext(
        BaseArgumentListSyntax argumentList,
        SyntaxNode owner,
        SemanticModel semanticModel,
        int position,
        CancellationToken cancellationToken)
    {
        var applicableSpan = GetApplicableSpan(argumentList);
        var activeParameter = CountSeparators(argumentList.Arguments.GetSeparators(), position);

        return owner switch
        {
            InvocationExpressionSyntax invocation => CreateFallbackContext(
                applicableSpan,
                activeParameter,
                semanticModel.GetSymbolInfo(invocation, cancellationToken)),
            ObjectCreationExpressionSyntax objectCreation => CreateFallbackContext(
                applicableSpan,
                activeParameter,
                semanticModel.GetSymbolInfo(objectCreation, cancellationToken)),
            ImplicitObjectCreationExpressionSyntax implicitObjectCreation => CreateFallbackContext(
                applicableSpan,
                activeParameter,
                semanticModel.GetSymbolInfo(implicitObjectCreation, cancellationToken)),
            ConstructorInitializerSyntax constructorInitializer => CreateFallbackContext(
                applicableSpan,
                activeParameter,
                semanticModel.GetSymbolInfo(constructorInitializer, cancellationToken)),
            ElementAccessExpressionSyntax elementAccess => CreateFallbackContext(
                applicableSpan,
                activeParameter,
                semanticModel.GetSymbolInfo(elementAccess, cancellationToken)),
            _ => null,
        };
    }

    private static FallbackSignatureHelpContext? CreateFallbackContext(
        AttributeArgumentListSyntax argumentList,
        AttributeSyntax attribute,
        SemanticModel semanticModel,
        int position,
        CancellationToken cancellationToken)
    {
        var applicableSpan = TextSpan.FromBounds(argumentList.OpenParenToken.Span.End, argumentList.CloseParenToken.SpanStart);
        var activeParameter = CountSeparators(argumentList.Arguments.GetSeparators(), position);
        return CreateFallbackContext(applicableSpan, activeParameter, semanticModel.GetSymbolInfo(attribute, cancellationToken));
    }

    private static FallbackSignatureHelpContext? CreateFallbackContext(TextSpan applicableSpan, int activeParameter, SymbolInfo symbolInfo)
    {
        var symbols = GetCallableSymbols(symbolInfo).ToArray();
        if (symbols.Length == 0)
        {
            return null;
        }

        return new FallbackSignatureHelpContext(applicableSpan, activeParameter, symbols, NormalizeCallableSymbol(symbolInfo.Symbol));
    }

    private static IEnumerable<ISymbol> GetCallableSymbols(SymbolInfo symbolInfo)
    {
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        var symbol = NormalizeCallableSymbol(symbolInfo.Symbol);
        if (symbol is not null && seen.Add(symbol))
        {
            yield return symbol;
        }

        foreach (var candidate in symbolInfo.CandidateSymbols)
        {
            var normalized = NormalizeCallableSymbol(candidate);
            if (normalized is not null && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static ISymbol? NormalizeCallableSymbol(ISymbol? symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => method,
            IPropertySymbol property when property.IsIndexer => property,
            _ => null,
        };
    }

    private static SignatureHelpSignatureItem CreateSignatureItem(ISymbol symbol, CancellationToken cancellationToken)
    {
        return symbol switch
        {
            IMethodSymbol method => new SignatureHelpSignatureItem(
                method.ToDisplayString(SignatureLabelFormat),
                method.GetDocumentationCommentXml(cancellationToken: cancellationToken) ?? string.Empty,
                method.Parameters.LastOrDefault()?.IsParams == true,
                method.Parameters.Select(CreateParameterItem).ToArray()),
            IPropertySymbol property => new SignatureHelpSignatureItem(
                property.ToDisplayString(SignatureLabelFormat),
                property.GetDocumentationCommentXml(cancellationToken: cancellationToken) ?? string.Empty,
                false,
                property.Parameters.Select(CreateParameterItem).ToArray()),
            _ => throw new InvalidOperationException($"Unsupported signature-help symbol kind '{symbol.Kind}'."),
        };
    }

    private static SignatureHelpParameterItem CreateParameterItem(IParameterSymbol parameter)
    {
        return new SignatureHelpParameterItem(
            parameter.Name,
            parameter.ToDisplayString(ParameterLabelFormat),
            string.Empty,
            parameter.IsOptional);
    }

    private static int CountSeparators(IEnumerable<SyntaxToken> separators, int position)
    {
        var count = 0;
        foreach (var separator in separators)
        {
            if (separator.SpanStart >= position)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static TextSpan GetApplicableSpan(BaseArgumentListSyntax argumentList)
    {
        return argumentList switch
        {
            ArgumentListSyntax parenthesized => TextSpan.FromBounds(parenthesized.OpenParenToken.Span.End, parenthesized.CloseParenToken.SpanStart),
            BracketedArgumentListSyntax bracketed => TextSpan.FromBounds(bracketed.OpenBracketToken.Span.End, bracketed.CloseBracketToken.SpanStart),
            _ => argumentList.Span,
        };
    }

    private static string InvokeDocumentationFactory(object? documentationFactory, CancellationToken cancellationToken)
    {
        if (documentationFactory is not Delegate delegateValue)
        {
            return string.Empty;
        }

        return delegateValue.DynamicInvoke(cancellationToken) is IEnumerable<TaggedText> taggedText
            ? string.Concat(taggedText.Select(part => part.Text))
            : string.Empty;
    }
}

/// <summary>
/// Captures the reflected Roslyn signature-help result before JSON projection.
/// </summary>
internal sealed record ReflectedSignatureHelp(
    TextSpan ApplicableSpan,
    int ActiveSignature,
    int ActiveParameter,
    IReadOnlyList<SignatureHelpSignatureItem> Signatures);

/// <summary>
/// Captures the public-semantic fallback context for signature help.
/// </summary>
internal sealed record FallbackSignatureHelpContext(
    TextSpan ApplicableSpan,
    int ActiveParameter,
    IReadOnlyList<ISymbol> Symbols,
    ISymbol? SelectedSymbol);
