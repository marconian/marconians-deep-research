using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Marconian.ResearchAgent.Models.Research;
using Marconian.ResearchAgent.Models.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Tracking;

public sealed class ResearchFlowTracker
{
    private readonly object _sync = new();
    private readonly ILogger<ResearchFlowTracker> _logger;
    private readonly Dictionary<string, BranchFlow> _branches = new();
    private readonly List<FlowNode> _nodes = new();
    private readonly List<FlowEdge> _edges = new();
    private readonly HashSet<string> _edgeKeys = new();

    private FlowNode? _sessionNode;
    private FlowNode? _planNode;
    private FlowNode? _synthesisNode;
    private FlowNode? _reportNode;
    private string? _diagramPath;
    private string? _sessionId;
    private int _nodeSequence;

    public ResearchFlowTracker(ILogger<ResearchFlowTracker>? logger = null)
    {
        _logger = logger ?? NullLogger<ResearchFlowTracker>.Instance;
    }

    public void ConfigureSession(string sessionId, string objective, string diagramPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(objective);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagramPath);

        lock (_sync)
        {
            ResetUnsafe();
            _sessionId = sessionId;
            _diagramPath = Path.GetFullPath(diagramPath);
            Directory.CreateDirectory(Path.GetDirectoryName(_diagramPath)!);

            _sessionNode = CreateNodeUnsafe($"Session {sessionId}" + (string.IsNullOrWhiteSpace(objective) ? string.Empty : $"\n{objective}"), NodeShape.Rounded);
            FlushUnsafe();
        }
    }

    public void RecordPlan(ResearchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        lock (_sync)
        {
            if (_sessionNode is null)
            {
                return;
            }

            _planNode ??= CreateNodeUnsafe($"Plan ready\nBranches: {plan.Branches.Count}", NodeShape.Diamond);
            AddEdgeUnsafe(_sessionNode.Id, _planNode.Id, "planning");

            foreach (var branch in plan.Branches)
            {
                RegisterBranchUnsafe(branch);
            }

            FlushUnsafe();
        }
    }

    public void RecordBranchStarted(string branchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);

        lock (_sync)
        {
            if (!_branches.TryGetValue(branchId, out var branch))
            {
                return;
            }

            branch.StatusNode ??= CreateNodeUnsafe($"Branch {branchId}\n{branch.Question}", NodeShape.Rectangle);
            var statusNode = branch.StatusNode;
            if (statusNode is null)
            {
                return;
            }

            var sourceId = _planNode?.Id ?? _sessionNode?.Id;
            if (string.IsNullOrEmpty(sourceId))
            {
                return;
            }

            AddEdgeUnsafe(sourceId, statusNode.Id, "dispatch");
            branch.LastNodeId = statusNode.Id;
            FlushUnsafe();
        }
    }

    public void RecordBranchNote(string branchId, string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        lock (_sync)
        {
            if (!_branches.TryGetValue(branchId, out var branch))
            {
                return;
            }

            var node = CreateNodeUnsafe(note, NodeShape.Note);
            var lastNodeId = branch.LastNodeId;
            if (!string.IsNullOrEmpty(lastNodeId))
            {
                AddEdgeUnsafe(lastNodeId, node.Id);
            }

            branch.LastNodeId = node.Id;
            FlushUnsafe();
        }
    }

    public void RecordToolExecution(string branchId, ToolExecutionResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        ArgumentNullException.ThrowIfNull(result);

        lock (_sync)
        {
            if (!_branches.TryGetValue(branchId, out var branch))
            {
                return;
            }

            string status = result.Success ? "✅" : "⚠️";
            string? text = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? result.Output?.Trim()
                : result.ErrorMessage;
            string label = $"{status} {result.ToolName}" + (string.IsNullOrWhiteSpace(text) ? string.Empty : $"\n{text!.Truncate(80)}");
            var node = CreateNodeUnsafe(label, NodeShape.Parallelogram);

            var lastNodeId = branch.LastNodeId;
            if (!string.IsNullOrEmpty(lastNodeId))
            {
                AddEdgeUnsafe(lastNodeId, node.Id, "tool");
            }

            branch.LastNodeId = node.Id;
            FlushUnsafe();
        }
    }

    public void RecordBranchCompleted(string branchId, bool success, string? summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);

        lock (_sync)
        {
            if (!_branches.TryGetValue(branchId, out var branch))
            {
                return;
            }

            string label = success ? "Branch success" : "Branch failed";
            if (!string.IsNullOrWhiteSpace(summary))
            {
                label += $"\n{summary.Trim().Truncate(120)}";
            }

            var node = CreateNodeUnsafe(label, success ? NodeShape.Rounded : NodeShape.Diamond);
            var lastNodeId = branch.LastNodeId;
            if (!string.IsNullOrEmpty(lastNodeId))
            {
                AddEdgeUnsafe(lastNodeId, node.Id, "result");
            }

            branch.LastNodeId = node.Id;
            branch.CompletedNodeId = node.Id;
            FlushUnsafe();
        }
    }

    public void RecordAggregation(int findingCount)
    {
        lock (_sync)
        {
            _synthesisNode ??= CreateNodeUnsafe($"Synthesis\nFindings: {findingCount}", NodeShape.Rounded);
            foreach (var branch in _branches.Values)
            {
                var completedNodeId = branch.CompletedNodeId;
                if (!string.IsNullOrEmpty(completedNodeId))
                {
                    AddEdgeUnsafe(completedNodeId, _synthesisNode.Id, "merge");
                }
            }

            FlushUnsafe();
        }
    }

    public void RecordReportDraft(string reportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);

        lock (_sync)
        {
            _reportNode ??= CreateNodeUnsafe("Report draft", NodeShape.Rectangle);
            if (_synthesisNode is not null)
            {
                AddEdgeUnsafe(_synthesisNode.Id, _reportNode.Id, "draft");
            }

            RecordNoteUnsafe($"Draft saved\n{Path.GetFileName(reportPath)}", _reportNode.Id);
            FlushUnsafe();
        }
    }

    public void RecordReportRevision(int passNumber, bool applied)
    {
        lock (_sync)
        {
            if (_reportNode is null)
            {
                return;
            }

            string label = applied ? $"Revision pass {passNumber}" : $"Revision pass {passNumber} (no changes)";
            var node = CreateNodeUnsafe(label, NodeShape.Rectangle);
            AddEdgeUnsafe(_reportNode.Id, node.Id, "revise");
            _reportNode = node;
            FlushUnsafe();
        }
    }

    public void RecordArtifacts(string reportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);

        lock (_sync)
        {
            var artifactNode = CreateNodeUnsafe("Artifacts persisted", NodeShape.Rounded);
            if (_reportNode is not null)
            {
                AddEdgeUnsafe(_reportNode.Id, artifactNode.Id, "store");
            }

            RecordNoteUnsafe($"Report: {Path.GetFileName(reportPath)}", artifactNode.Id);
            if (!string.IsNullOrEmpty(_diagramPath))
            {
                RecordNoteUnsafe($"Flow: {Path.GetFileName(_diagramPath)}", artifactNode.Id);
            }

            FlushUnsafe();
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            FlushUnsafe();
        }

        return Task.CompletedTask;
    }

    private void RegisterBranchUnsafe(ResearchBranchPlan branch)
    {
        if (_branches.ContainsKey(branch.BranchId))
        {
            return;
        }

        _branches.Add(branch.BranchId, new BranchFlow(branch.BranchId, branch.Question));
    }

    private FlowNode CreateNodeUnsafe(string label, NodeShape shape)
    {
        string id = $"N{++_nodeSequence}";
        string sanitized = Sanitize(label);
        var node = new FlowNode(id, sanitized, shape);
        _nodes.Add(node);
        return node;
    }

    private void RecordNoteUnsafe(string note, string sourceNodeId)
    {
        var node = CreateNodeUnsafe(note, NodeShape.Note);
        AddEdgeUnsafe(sourceNodeId, node.Id);
    }

    private void AddEdgeUnsafe(string? fromId, string toId, string? label = null)
    {
        if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId))
        {
            return;
        }

        string key = $"{fromId}->{toId}:{label}";
        if (_edgeKeys.Add(key))
        {
            _edges.Add(new FlowEdge(fromId, toId, label));
        }
    }

    private void FlushUnsafe()
    {
        if (string.IsNullOrEmpty(_diagramPath))
        {
            return;
        }

        try
        {
            string content = BuildMermaidUnsafe();
            File.WriteAllText(_diagramPath!, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to write flow diagram to {Path}.", _diagramPath);
        }
    }

    private string BuildMermaidUnsafe()
    {
        var builder = new StringBuilder();
        builder.AppendLine("```mermaid");
        builder.AppendLine("graph TD");

        foreach (var node in _nodes)
        {
            builder.AppendLine(node.ToMermaid());
        }

        foreach (var edge in _edges)
        {
            builder.AppendLine(edge.ToMermaid());
        }

        builder.AppendLine("```");
        return builder.ToString();
    }

    private void ResetUnsafe()
    {
        _branches.Clear();
        _nodes.Clear();
        _edges.Clear();
        _edgeKeys.Clear();
        _sessionNode = null;
        _planNode = null;
        _synthesisNode = null;
        _reportNode = null;
        _diagramPath = null;
        _sessionId = null;
        _nodeSequence = 0;
    }

    private static string Sanitize(string value)
    {
        return value
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "<br/>", StringComparison.Ordinal)
            .Replace("\"", "'", StringComparison.Ordinal);
    }

    private sealed record FlowNode(string Id, string Label, NodeShape Shape)
    {
        public string ToMermaid()
        {
            return Shape switch
            {
                NodeShape.Rounded => $"{Id}((\"{Label}\"))",
                NodeShape.Diamond => $"{Id}{'{'}\"{Label}\"{'}'}",
                NodeShape.Parallelogram => $"{Id}[/\"{Label}\"/]",
                NodeShape.Note => $"{Id}[\"{Label}\"]",
                _ => $"{Id}[\"{Label}\"]"
            };
        }
    }

    private sealed record FlowEdge(string FromId, string ToId, string? Label)
    {
        public string ToMermaid()
        {
            if (!string.IsNullOrEmpty(Label))
            {
                return $"{FromId} -->|{ResearchFlowTracker.Sanitize(Label)}| {ToId}";
            }

            return $"{FromId} --> {ToId}";
        }
    }

    private sealed class BranchFlow
    {
        public BranchFlow(string branchId, string question)
        {
            BranchId = branchId;
            Question = question;
        }

        public string BranchId { get; }

        public string Question { get; }

        public FlowNode? StatusNode { get; set; }

        public string? LastNodeId { get; set; }

        public string? CompletedNodeId { get; set; }
    }

    private enum NodeShape
    {
        Rectangle,
        Rounded,
        Diamond,
        Parallelogram,
        Note
    }
}

internal static class ResearchFlowTrackerExtensions
{
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        if (maxLength <= 1)
        {
            return "…";
        }

        return value[..(maxLength - 1)] + "…";
    }
}
