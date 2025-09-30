namespace Marconian.ResearchAgent.Models.Files;

public sealed class DocumentAnalysisResult
{
    public string Text { get; set; } = string.Empty;

    public List<string> KeyPhrases { get; set; } = new();

    public string? Title { get; set; }

    public Dictionary<string, string> Metadata { get; set; } = new();
}

