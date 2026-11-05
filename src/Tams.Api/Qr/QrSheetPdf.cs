namespace Tams.Api.Qr;

/// <summary>A4 grid of labels: QR (asset UUID payload), asset code, name.</summary>
public static class QrSheetPdf
{
    private const double Margin = 30, LabelH = 100, QrBox = 80;
    private const int Cols = 3;
    private static readonly int Rows = (int)((MiniPdf.PageHeight - 2 * Margin) / LabelH);

    public static byte[] Render(IReadOnlyList<(Guid Id, string AssetCode, string Name)> assets)
    {
        var pdf = new MiniPdf();
        var labelW = (MiniPdf.PageWidth - 2 * Margin) / Cols;
        var perPage = Cols * Rows;

        for (var i = 0; i < assets.Count; i++)
        {
            if (i % perPage == 0) pdf.NewPage();
            var slot = i % perPage;
            var x = Margin + slot % Cols * labelW;
            var y = Margin + slot / Cols * LabelH;
            var a = assets[i];

            pdf.StrokeRect(x, y, labelW, LabelH);

            var m = QrCodes.Encode(a.Id).ModuleMatrix; // includes the quiet zone, so the label stays scannable
            var mod = QrBox / m.Count;
            var qx = x + 6;
            var qy = y + (LabelH - QrBox) / 2;
            for (var r = 0; r < m.Count; r++)
                for (var c = 0; c < m.Count; c++)
                    if (m[r][c]) pdf.FillRect(qx + c * mod, qy + r * mod, mod, mod);

            var tx = qx + QrBox + 6;
            var textW = labelW - (tx - x) - 4;
            pdf.Text(tx, y + 38, 11, Fit(a.AssetCode, textW, 11), bold: true);
            var lines = Wrap(a.Name, textW, 8, 3);
            for (var l = 0; l < lines.Count; l++) pdf.Text(tx, y + 52 + l * 10, 8, lines[l]);
        }
        return pdf.ToBytes();
    }

    // Helvetica averages about 0.5 em per character; crude, but labels only need to not overflow.
    private static int MaxChars(double width, double size) => Math.Max(1, (int)(width / (size * 0.52)));

    private static string Fit(string s, double width, double size)
    {
        var max = MaxChars(width, size);
        return s.Length <= max ? s : s[..(max - 1)] + "...";
    }

    private static List<string> Wrap(string s, double width, double size, int maxLines)
    {
        var max = MaxChars(width, size);
        var lines = new List<string>();
        var cur = "";
        foreach (var word in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word.Length > max ? word[..max] : word;
            if (cur.Length == 0) cur = w;
            else if (cur.Length + 1 + w.Length <= max) cur += " " + w;
            else
            {
                lines.Add(cur);
                cur = w;
                if (lines.Count == maxLines) return lines;
            }
        }
        if (lines.Count < maxLines && cur.Length > 0) lines.Add(cur);
        return lines;
    }
}
