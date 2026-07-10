using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Report;

/// <summary>Shared PDF structural assertions for the report tests. Latin1
/// decodes the whole byte array 1:1 (one char per byte) so string indices
/// line up exactly with the byte offsets recorded in the xref table.</summary>
internal static class PdfTestSupport
{
    public static string AsLatin1(byte[] pdfBytes) => Encoding.Latin1.GetString(pdfBytes);

    /// <summary>Validates the outer PDF envelope: header/footer markers, a
    /// parseable xref table whose every offset points at the matching
    /// "N 0 obj" header, and the catalog/page/font resources being present.</summary>
    public static void AssertValidStructure(byte[] pdfBytes)
    {
        var text = AsLatin1(pdfBytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF", text);

        var xrefStart = text.IndexOf("\nxref\n", StringComparison.Ordinal);
        Assert.True(xrefStart >= 0, "xref section not found");
        var trailerStart = text.IndexOf("\ntrailer\n", xrefStart, StringComparison.Ordinal);
        Assert.True(trailerStart >= 0, "trailer not found");

        var xrefSection = text.Substring(xrefStart + 1, trailerStart - xrefStart - 1);
        var lines = xrefSection.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var count = int.Parse(header[1], CultureInfo.InvariantCulture);

        for (var id = 1; id < count; id++)
        {
            var entryLine = lines[2 + id];
            var offset = int.Parse(entryLine.Substring(0, 10), CultureInfo.InvariantCulture);
            var expectedHeader = $"{id} 0 obj";
            Assert.True(offset + expectedHeader.Length <= text.Length, $"offset for object {id} out of range");
            Assert.Equal(expectedHeader, text.Substring(offset, expectedHeader.Length));
        }

        Assert.True(text.Contains("/Type /Catalog", StringComparison.Ordinal));
        Assert.True(text.Contains("/Type /Page", StringComparison.Ordinal));
        Assert.True(text.Contains("/F1", StringComparison.Ordinal));
        Assert.True(text.Contains("/F2", StringComparison.Ordinal));
    }

    /// <summary>Returns the object count from the xref table's "0 N" header (N,
    /// including the free object 0).</summary>
    public static int ObjectCount(byte[] pdfBytes)
    {
        var text = AsLatin1(pdfBytes);
        var xrefStart = text.IndexOf("\nxref\n", StringComparison.Ordinal);
        var trailerStart = text.IndexOf("\ntrailer\n", xrefStart, StringComparison.Ordinal);
        var xrefSection = text.Substring(xrefStart + 1, trailerStart - xrefStart - 1);
        var lines = xrefSection.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return int.Parse(header[1], CultureInfo.InvariantCulture);
    }

    /// <summary>Returns the text of the first PDF literal string ("(...) Tj")
    /// appearing after the given marker - useful for reading the value drawn
    /// immediately after a known label.</summary>
    public static string ExtractNextLiteralAfter(string content, string marker)
    {
        var idx = content.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"marker not found: {marker}");
        var openParen = content.IndexOf('(', idx + marker.Length);
        Assert.True(openParen >= 0, "no following literal string found");
        var closeParen = content.IndexOf(')', openParen);
        Assert.True(closeParen > openParen, "unterminated literal string");
        return content.Substring(openParen + 1, closeParen - openParen - 1);
    }

    /// <summary>Locates the one stream object without /Filter (the report's
    /// page content stream is stored uncompressed) and returns its raw
    /// text.</summary>
    public static string ExtractContentStream(byte[] pdfBytes)
    {
        var text = AsLatin1(pdfBytes);
        foreach (Match m in Regex.Matches(text, @"(\d+) 0 obj\n(.*?)\nstream\n", RegexOptions.Singleline))
        {
            var dict = m.Groups[2].Value;
            if (dict.Contains("/Filter", StringComparison.Ordinal))
            {
                continue;
            }
            if (!dict.Contains("/Length", StringComparison.Ordinal))
            {
                continue;
            }
            var streamStart = m.Index + m.Length;
            var endIdx = text.IndexOf("\nendstream", streamStart, StringComparison.Ordinal);
            if (endIdx < 0)
            {
                continue;
            }
            return text.Substring(streamStart, endIdx - streamStart);
        }
        throw new InvalidOperationException("content stream not found");
    }
}
