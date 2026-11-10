using QRCoder;

namespace Tams.Api.Qr;

/// <summary>QR encoding. The payload is fixed by the PRD (section 7.1): tams:asset:&lt;asset uuid&gt;.</summary>
public static class QrCodes
{
    public static string Payload(Guid assetId) => $"tams:asset:{assetId}";

    public static QRCodeData Encode(Guid assetId)
    {
        using var gen = new QRCodeGenerator();
        // ECC Q: labels get scuffed, and the payload is short so the symbol stays small.
        return gen.CreateQrCode(Payload(assetId), QRCodeGenerator.ECCLevel.Q);
    }

    /// <summary>PNG of roughly <paramref name="size"/> pixels square, quiet zone included.</summary>
    public static byte[] Png(Guid assetId, int size)
    {
        var data = Encode(assetId);
        var ppm = Math.Max(1, size / data.ModuleMatrix.Count);
        return new PngByteQRCode(data).GetGraphic(ppm);
    }

    public static string Svg(Guid assetId, int size)
    {
        var data = Encode(assetId);
        return new SvgQRCode(data).GetGraphic(new System.Drawing.Size(size, size), "#000000", "#ffffff", drawQuietZones: true, SvgQRCode.SizingMode.WidthHeightAttribute);
    }
}
