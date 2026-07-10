using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nexus.Service.Diagnostics.Report;

/// <summary>
/// Minimal hand-rolled PDF 1.4 object writer. No external PDF library: this is
/// the entire wire format needed for a single fixed-layout report page.
///
/// Objects are addressed by an integer id handed out via <see cref="Reserve"/>
/// before their dictionary text is known, so a dict that references another
/// object (Page -> Parent, Pages -> Kids) can be built regardless of
/// definition order. <see cref="Build"/> then emits the header, every object
/// in id order, and a correct xref table + trailer.
/// </summary>
public sealed class PdfWriter
{
    private readonly List<(string Dict, byte[]? Stream)> _slots = new();

    /// <summary>Reserves the next object id without defining its content yet.</summary>
    public int Reserve()
    {
        _slots.Add((string.Empty, null));
        return _slots.Count;
    }

    /// <summary>Defines a fully-formed object in one call for objects with no forward reference.</summary>
    public int Add(string dict, byte[]? stream = null)
    {
        var id = Reserve();
        Define(id, dict, stream);
        return id;
    }

    public void Define(int id, string dict, byte[]? stream = null)
    {
        _slots[id - 1] = (dict, stream);
    }

    /// <summary>Assembles the final PDF byte stream: header, every defined
    /// object, xref table, trailer. Throws if a reserved id was never defined.</summary>
    public byte[] Build(int catalogId, int? infoId)
    {
        using var ms = new MemoryStream();
        void WriteAscii(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
        }

        WriteAscii("%PDF-1.4\n");

        var count = _slots.Count + 1;
        var offsets = new long[count];
        for (var i = 0; i < _slots.Count; i++)
        {
            var id = i + 1;
            offsets[id] = ms.Position;
            var (dict, stream) = _slots[i];
            if (string.IsNullOrEmpty(dict))
            {
                throw new InvalidOperationException($"pdf object {id} was reserved but never defined");
            }

            WriteAscii($"{id} 0 obj\n{dict}\n");
            if (stream is not null)
            {
                WriteAscii("stream\n");
                ms.Write(stream, 0, stream.Length);
                WriteAscii("\nendstream\n");
            }
            WriteAscii("endobj\n");
        }

        var xrefOffset = ms.Position;
        WriteAscii($"xref\n0 {count}\n");
        WriteAscii("0000000000 65535 f \n");
        for (var id = 1; id < count; id++)
        {
            WriteAscii(offsets[id].ToString("D10", System.Globalization.CultureInfo.InvariantCulture));
            WriteAscii(" 00000 n \n");
        }

        WriteAscii("trailer\n");
        var trailer = new StringBuilder();
        trailer.Append("<< /Size ").Append(count).Append(" /Root ").Append(catalogId).Append(" 0 R");
        if (infoId is int iid)
        {
            trailer.Append(" /Info ").Append(iid).Append(" 0 R");
        }
        trailer.Append(" >>\n");
        WriteAscii(trailer.ToString());
        WriteAscii("startxref\n");
        WriteAscii(xrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteAscii("\n%%EOF");

        return ms.ToArray();
    }
}
