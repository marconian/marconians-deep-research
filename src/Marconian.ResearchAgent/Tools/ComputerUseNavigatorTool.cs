using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.ComputerUse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Tools;

public sealed class ComputerUseNavigatorTool : ITool
{
    private readonly IComputerUseExplorer _explorer;
    private readonly ILogger<ComputerUseNavigatorTool> _logger;

    public ComputerUseNavigatorTool(IComputerUseExplorer explorer, ILogger<ComputerUseNavigatorTool>? logger = null)
    {
        _explorer = explorer ?? throw new ArgumentNullException(nameof(explorer));
        _logger = logger ?? NullLogger<ComputerUseNavigatorTool>.Instance;
    }

    public string Name => "ComputerUseNavigator";

    public string Description => "Opens a URL in the computer-use browser, scrolls through the page, and returns a concise summary of the findings.";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Parameters.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url))
        {
            url = context.Instruction;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = "No URL provided for computer-use exploration."
            };
        }

        string? objective = null;
        if (context.Parameters.TryGetValue("objective", out var objectiveValue) && !string.IsNullOrWhiteSpace(objectiveValue))
        {
            objective = objectiveValue.Trim();
        }

        try
        {
            _logger.LogInformation("Starting computer-use exploration for {Url}.", url);
            ComputerUseExplorationResult exploration = await _explorer
                .ExploreAsync(url, objective, cancellationToken)
                .ConfigureAwait(false);

            string summary = BuildSummary(exploration);
            var metadata = new Dictionary<string, string>
            {
                ["requestedUrl"] = url
            };

            if (!string.IsNullOrWhiteSpace(exploration.FinalUrl))
            {
                metadata["finalUrl"] = exploration.FinalUrl!;
            }

            if (!string.IsNullOrWhiteSpace(exploration.PageTitle))
            {
                metadata["pageTitle"] = exploration.PageTitle!;
            }

            if (exploration.Transcript.Count > 0)
            {
                metadata["transcript"] = string.Join(" | ", exploration.Transcript.TakeLast(6));
            }

            if (exploration.FlaggedResources.Count > 0)
            {
                metadata["flaggedCount"] = exploration.FlaggedResources.Count.ToString(CultureInfo.InvariantCulture);
            }

            string citationTitle = exploration.PageTitle ?? exploration.FinalUrl ?? url;
            var citation = new SourceCitation(
                $"cux:{Guid.NewGuid():N}",
                citationTitle,
                exploration.FinalUrl ?? url,
                summary.Length > 280 ? summary[..280] + "…" : summary);

            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = true,
                Output = summary,
                Metadata = metadata,
                Citations = { citation },
                FlaggedResources = exploration.FlaggedResources.ToList()
            };
        }
        catch (ComputerUseSearchBlockedException blocked)
        {
            _logger.LogWarning(blocked, "Computer-use exploration was blocked for {Url}.", url);
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = $"Computer-use exploration blocked: {blocked.Message}",
                Metadata = new Dictionary<string, string>
                {
                    ["requestedUrl"] = url,
                    ["blockReason"] = blocked.Message
                }
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Computer-use exploration failed for {Url}.", url);
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = $"Computer-use exploration failed: {ex.Message}",
                Metadata = new Dictionary<string, string>
                {
                    ["requestedUrl"] = url
                }
            };
        }
    }

    private static string BuildSummary(ComputerUseExplorationResult exploration)
    {
        if (!string.IsNullOrWhiteSpace(exploration.StructuredSummaryJson))
        {
            return exploration.StructuredSummaryJson!.Trim();
        }

        if (exploration.Transcript.Count == 0)
        {
            return "No summary was produced by the exploration session.";
        }

        var builder = new StringBuilder();
        foreach (string segment in exploration.Transcript.TakeLast(3))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(segment.Trim());
        }

        return builder.Length == 0
            ? "No summary was produced by the exploration session."
            : builder.ToString();
    }
}
