using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Marconian.ResearchAgent.Models.Reporting;

namespace Marconian.ResearchAgent.Utilities;

public static class SourceCitationDeduplicator
{
    private static readonly Regex WhitespaceCollapseRegex = new("\\s+", RegexOptions.Compiled);

    public static IReadOnlyList<SourceCitation> Deduplicate(IEnumerable<SourceCitation> citations)
    {
        ArgumentNullException.ThrowIfNull(citations);

        var order = new List<string>();
        var map = new Dictionary<string, SourceCitation>(StringComparer.OrdinalIgnoreCase);

        foreach (var citation in citations)
        {
            if (citation is null)
            {
                continue;
            }

            string key = BuildKey(citation);
            if (!map.TryGetValue(key, out var existing))
            {
                map[key] = Normalize(citation);
                order.Add(key);
            }
            else
            {
                map[key] = Merge(existing, citation);
            }
        }

        return order.Select(key => map[key]).ToList();
    }

    public static string BuildKey(SourceCitation citation)
    {
        ArgumentNullException.ThrowIfNull(citation);

        string? normalizedUrl = NormalizeUrl(citation.Url);
        if (!string.IsNullOrEmpty(normalizedUrl))
        {
            return $"url::{normalizedUrl}";
        }

        string? normalizedId = NormalizeToken(citation.SourceId);
        if (!string.IsNullOrEmpty(normalizedId))
        {
            return $"id::{normalizedId}";
        }

        string? normalizedTitle = NormalizeText(citation.Title);
        if (!string.IsNullOrEmpty(normalizedTitle))
        {
            return $"title::{normalizedTitle}";
        }

        string? normalizedSnippet = NormalizeText(citation.Snippet);
        if (!string.IsNullOrEmpty(normalizedSnippet))
        {
            return $"snippet::{normalizedSnippet}";
        }

        return $"fallback::{Guid.NewGuid():N}";
    }

    private static SourceCitation Merge(SourceCitation existing, SourceCitation incoming)
    {
        string sourceId = !string.IsNullOrWhiteSpace(existing.SourceId)
            ? existing.SourceId
            : incoming.SourceId;

        string? title = ChooseText(existing.Title, incoming.Title);
        string? url = ChooseUrl(existing.Url, incoming.Url);
        string? snippet = ChooseSnippet(existing.Snippet, incoming.Snippet);

        return new SourceCitation(sourceId, title, url, snippet);
    }

    private static SourceCitation Normalize(SourceCitation citation)
    {
        string sourceId = citation.SourceId?.Trim() ?? string.Empty;
        string? title = TrimToNull(citation.Title);
        string? url = NormalizeDisplayedUrl(citation.Url);
        string? snippet = TrimToNull(citation.Snippet);
        return new SourceCitation(sourceId, title, url, snippet);
    }

    private static string? ChooseText(string? first, string? second)
    {
        string? normalizedFirst = TrimToNull(first);
        string? normalizedSecond = TrimToNull(second);

        if (normalizedFirst is null)
        {
            return normalizedSecond;
        }

        if (normalizedSecond is null)
        {
            return normalizedFirst;
        }

        return normalizedSecond.Length > normalizedFirst.Length ? normalizedSecond : normalizedFirst;
    }

    private static string? ChooseSnippet(string? first, string? second)
        => ChooseText(first, second);

    private static string? ChooseUrl(string? first, string? second)
    {
        string? resolvedFirst = NormalizeDisplayedUrl(first);
        string? resolvedSecond = NormalizeDisplayedUrl(second);

        if (resolvedFirst is null)
        {
            return resolvedSecond;
        }

        if (resolvedSecond is null)
        {
            return resolvedFirst;
        }

        if (string.Equals(resolvedFirst, resolvedSecond, StringComparison.OrdinalIgnoreCase))
        {
            return resolvedFirst;
        }

        // Prefer HTTPS over HTTP when the paths match.
        if (TryCreateUri(resolvedFirst, out var firstUri) && TryCreateUri(resolvedSecond, out var secondUri))
        {
            if (string.Equals(firstUri.Host, secondUri.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(firstUri.AbsolutePath, secondUri.AbsolutePath, StringComparison.Ordinal))
            {
                if (string.Equals(secondUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return resolvedSecond;
                }

                if (string.Equals(firstUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return resolvedFirst;
                }
            }
        }

        return resolvedFirst.Length >= resolvedSecond.Length ? resolvedFirst : resolvedSecond;
    }

    private static string? NormalizeDisplayedUrl(string? url)
    {
        string? normalized = NormalizeUrl(url);
        return normalized;
    }

    private static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };

        builder.Host = builder.Host.ToLowerInvariant();
        builder.Scheme = builder.Scheme.ToLowerInvariant();

        if (!builder.Path.Equals("/", StringComparison.Ordinal) && builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path = builder.Path.TrimEnd('/');
        }

        if (!string.IsNullOrEmpty(builder.Path) && !builder.Path.Equals("/", StringComparison.Ordinal))
        {
            builder.Path = builder.Path.ToLowerInvariant();
        }

        Uri normalizedUri = builder.Uri;
        return normalizedUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.Unescaped);
    }

    private static string? NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string collapsed = WhitespaceCollapseRegex.Replace(value, " ");
        return collapsed.Trim().ToLower(CultureInfo.InvariantCulture);
    }

    private static string? TrimToNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool TryCreateUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value ?? string.Empty, UriKind.Absolute, out var parsed))
        {
            uri = parsed;
            return true;
        }

        uri = default!;
        return false;
    }
}
