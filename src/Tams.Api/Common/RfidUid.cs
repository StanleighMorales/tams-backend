namespace Tams.Api.Common;

// schema-v1-decisions #6: tag and card UIDs are typed (or keyboard-wedge) text.
// Uppercase hex, no separators; normalise on input, reject anything else.
public static class RfidUid
{
    // ponytail: 4/7/10-byte UIDs; tighten to the actual reader's width if it is fixed.
    public const int MinLength = 8, MaxLength = 20;

    public static bool TryNormalize(string? input, out string uid)
    {
        uid = (input ?? "").Trim().Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
        return uid.Length is >= MinLength and <= MaxLength
            && uid.Length % 2 == 0
            && uid.All(Uri.IsHexDigit);
    }
}
