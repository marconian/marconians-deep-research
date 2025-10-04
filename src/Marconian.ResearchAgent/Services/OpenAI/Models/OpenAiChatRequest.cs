using System.Text.Json.Nodes;

namespace Marconian.ResearchAgent.Services.OpenAI.Models;

public sealed record OpenAiChatRequest(
    string SystemPrompt,
    IReadOnlyList<OpenAiChatMessage> Messages,
    string DeploymentName,
    int MaxOutputTokens = 800,
    float? Temperature = null,
    float? TopP = null,
    OpenAiChatJsonSchemaFormat? JsonSchemaFormat = null);

public sealed record OpenAiChatMessage(string Role, string Content);

public sealed record OpenAiChatJsonSchemaFormat(string SchemaName, JsonNode Schema, bool Strict = true);