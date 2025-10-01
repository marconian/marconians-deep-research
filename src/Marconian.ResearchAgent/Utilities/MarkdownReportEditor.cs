using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Marconian.ResearchAgent.Utilities;

public sealed class MarkdownReportEditor
{
    private readonly List<string> _lines;

    private MarkdownReportEditor(IEnumerable<string> lines)
    {
        _lines = lines.ToList();
    }

    public static MarkdownReportEditor FromContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        return new MarkdownReportEditor(lines);
    }

    public static MarkdownReportEditor FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        return FromContent(content);
    }

    public IReadOnlyList<string> Lines => _lines;

    public string ToContent()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < _lines.Count; i++)
        {
            if (i > 0)
            {
                builder.AppendLine();
            }

            builder.Append(_lines[i]);
        }

        return builder.ToString();
    }

    public void ReplaceLine(int lineNumber, string content)
    {
        if (!TryGetIndex(lineNumber, out int index))
        {
            return;
        }

        _lines[index] = content ?? string.Empty;
    }

    public void InsertAfter(int lineNumber, string content)
    {
        if (!TryGetIndex(lineNumber, out int index))
        {
            index = Math.Max(0, _lines.Count - 1);
            _lines.Insert(index + 1, content ?? string.Empty);
            return;
        }

        _lines.Insert(index + 1, content ?? string.Empty);
    }

    public void InsertBefore(int lineNumber, string content)
    {
        if (!TryGetIndex(lineNumber, out int index))
        {
            if (lineNumber <= 1)
            {
                _lines.Insert(0, content ?? string.Empty);
            }
            else
            {
                _lines.Add(content ?? string.Empty);
            }

            return;
        }

        _lines.Insert(index, content ?? string.Empty);
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllText(path, ToContent());
    }

    public string EnumerateLines()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < _lines.Count; i++)
        {
            builder.Append(i + 1).Append(':').Append(' ').AppendLine(_lines[i]);
        }

        return builder.ToString();
    }

    private bool TryGetIndex(int lineNumber, out int index)
    {
        if (lineNumber <= 0)
        {
            index = -1;
            return false;
        }

        index = lineNumber - 1;
        if (index >= _lines.Count)
        {
            index = _lines.Count - 1;
            return false;
        }

        return true;
    }
}
