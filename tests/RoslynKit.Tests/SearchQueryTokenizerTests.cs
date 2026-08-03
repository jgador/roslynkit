namespace RoslynKit.Tests;

public sealed class SearchQueryTokenizerTests
{
    [Theory]
    [InlineData("WorkspaceDaemonSession", "workspacedaemonsession", "workspacedaemonsession", "workspace", "daemon", "session")]
    [InlineData("ABCParser", "abcparser", "abcparser", "abc", "parser")]
    [InlineData("Parser2Result", "parser2result", "parser2result", "parser", "2", "result")]
    [InlineData("ExecuteAsync", "executeasync", "executeasync", "execute", "async")]
    [InlineData("workspace_daemon_session", "workspacedaemonsession", "workspacedaemonsession", "workspace", "daemon", "session")]
    public void TokenizeIdentifier_PreservesNormalizedIdentifier_AndSplitsSearchParts(
        string identifier,
        string expectedNormalizedText,
        params string[] expectedTokens)
    {
        var result = SearchQueryTokenizer.TokenizeIdentifier(identifier);

        Assert.Equal(expectedNormalizedText, result.NormalizedText);
        Assert.Equal(expectedTokens, result.Tokens);
    }

    [Fact]
    public void TokenizeQuery_DiscardsEnglishFillerWords_AndRetainsConventionTerms()
    {
        var tokens = SearchQueryTokenizer.TokenizeQuery("How does the WorkspaceDaemonSession ExecuteAsync?");

        Assert.Equal(
            ["workspacedaemonsession", "workspace", "daemon", "session", "executeasync", "execute", "async"],
            tokens);
    }

    [Fact]
    public void TokenizeQuery_SplitsPunctuationAndDeduplicatesTokens_InFirstAppearanceOrder()
    {
        var tokens = SearchQueryTokenizer.TokenizeQuery("parser2-result, Parser2Result");

        Assert.Equal(["parser2", "parser", "2", "result", "parser2result"], tokens);
    }

    [Fact]
    public void TokenizeIdentifier_ReturnsNoTokens_WhenIdentifierContainsNoEnglishLettersOrDigits()
    {
        var result = SearchQueryTokenizer.TokenizeIdentifier("___--");

        Assert.Empty(result.Tokens);
        Assert.Equal(string.Empty, result.NormalizedText);
    }
}
