using System.Globalization;
using System.Text;

namespace Tams.Api.Qr;

/// <summary>
/// Just enough PDF to print label grids: filled rectangles and Helvetica text on A4 pages.
/// Hand-written so the QR sheet needs no PDF dependency; the QR is drawn as vector squares.
/// </summary>
public sealed class MiniPdf
{
    public const double PageWidth = 595, PageHeight = 842; // A4 in points

    private readonly List<StringBuilder> _pages = [];

    private StringBuilder Cur => _pages[^1];

    public void NewPage() => _pages.Add(new StringBuilder());

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Filled black rectangle; y is measured from the top of the page.</summary>
    public void FillRect(double x, double y, double w, double h) =>
        Cur.Append(CultureInfo.InvariantCulture, $"{N(x)} {N(PageHeight - y - h)} {N(w)} {N(h)} re f\n");

    public void StrokeRect(double x, double y, double w, double h) =>
        Cur.Append(CultureInfo.InvariantCulture, $"0.5 w 0.7 G {N(x)} {N(PageHeight - y - h)} {N(w)} {N(h)} re S 0 G\n");

    public void Text(double x, double y, double size, string text, bool bold = false) =>
        Cur.Append(CultureInfo.InvariantCulture,
            $"BT /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(PageHeight - y)} Td ({Escape(text)}) Tj ET\n");

    // Standard fonts are WinAnsi; anything outside Latin-1 printable becomes '?'.
    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else if (c is >= ' ' and <= '~' or >= ' ' and <= 'ÿ') sb.Append(c);
            else sb.Append('?');
        }
        return sb.ToString();
    }

    public byte[] ToBytes()
    {
        if (_pages.Count == 0) NewPage();
        var latin1 = Encoding.Latin1;
        var objs = new List<string>(); // object bodies, ids are 1-based
        // 1 catalog, 2 pages, 3 F1, 4 F2, then page/content pairs from 5
        objs.Add("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, _pages.Count).Select(i => $"{5 + i * 2} 0 R"));
        objs.Add($"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>");
        objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < _pages.Count; i++)
        {
            var content = _pages[i].ToString();
            objs.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] " +
                     $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {6 + i * 2} 0 R >>");
            objs.Add($"<< /Length {latin1.GetByteCount(content)} >>\nstream\n{content}endstream");
        }

        using var ms = new MemoryStream();
        void Write(string s) => ms.Write(latin1.GetBytes(s));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objs.Count; i++)
        {
            offsets.Add(ms.Position);
            Write($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        var xref = ms.Position;
        Write($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
