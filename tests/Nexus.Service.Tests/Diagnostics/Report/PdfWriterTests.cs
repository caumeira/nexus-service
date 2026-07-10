using System.Text;
using Nexus.Service.Diagnostics.Report;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Report;

public class PdfWriterTests
{
    private static byte[] BuildDocumentWithTwoFonts()
    {
        var pdf = new PdfWriter();
        var pagesId = pdf.Reserve();
        var catalogId = pdf.Reserve();
        var fontId = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var fontBoldId = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        var pageId = pdf.Reserve();
        var contentBytes = Encoding.ASCII.GetBytes("BT /F1 12 Tf 40 700 Td (Hello) Tj ET\n");
        var contentId = pdf.Add($"<< /Length {contentBytes.Length} >>", contentBytes);

        pdf.Define(pagesId, $"<< /Type /Pages /Kids [{pageId} 0 R] /Count 1 >>");
        pdf.Define(catalogId, $"<< /Type /Catalog /Pages {pagesId} 0 R >>");
        pdf.Define(pageId,
            $"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontId} 0 R /F2 {fontBoldId} 0 R >> >> /Contents {contentId} 0 R >>");

        return pdf.Build(catalogId, infoId: null);
    }

    [Fact]
    public void Build_ProducesStructurallyValidDocument()
    {
        var bytes = BuildDocumentWithTwoFonts();
        PdfTestSupport.AssertValidStructure(bytes);
    }

    [Fact]
    public void Build_ContentStream_ContainsTheTextOperator()
    {
        var bytes = BuildDocumentWithTwoFonts();
        var content = PdfTestSupport.ExtractContentStream(bytes);
        Assert.Contains("(Hello) Tj", content);
    }

    [Fact]
    public void Reserve_ThenNeverDefined_ThrowsOnBuild()
    {
        var pdf = new PdfWriter();
        var danglingId = pdf.Reserve();
        var catalogId = pdf.Add($"<< /Type /Catalog /Pages {danglingId} 0 R >>");

        Assert.Throws<System.InvalidOperationException>(() => pdf.Build(catalogId, infoId: null));
    }
}
