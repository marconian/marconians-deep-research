using TiktokenSharp;

namespace Marconian.ResearchAgent.Utilities;

internal static class TokenUtilities
{
    private static readonly TikToken Tokenizer = TikToken.GetEncoding("cl100k_base");

    public static IReadOnlyList<int> Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Tokenizer.Encode(text).ToList();
    }

    public static string Decode(IEnumerable<int> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return Tokenizer.Decode(tokens.ToList());
    }

    public static int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Tokenizer.Encode(text).Count;
    }
}
