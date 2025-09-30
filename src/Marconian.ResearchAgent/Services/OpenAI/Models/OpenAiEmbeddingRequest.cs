namespace Marconian.ResearchAgent.Services.OpenAI.Models;

public sealed record OpenAiEmbeddingRequest(
    string DeploymentName,
    string InputText);
