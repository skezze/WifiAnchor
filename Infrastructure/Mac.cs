using System.Globalization;

namespace WifiAnchor.Infrastructure;

internal static class Mac
{
    public static bool IsValid(string? value) => Parse(value).Length == 6;

    public static string Normalize(string? value)
    {
        byte[] bytes = Parse(value);
        return bytes.Length == 6 ? Format(bytes) : "";
    }

    public static string Format(byte[] bytes) =>
        string.Join(":", bytes.Select(b => b.ToString("x2")));

    public static byte[] Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<byte>();

        string[] parts = value.Trim().Split(
            new[] { ':', '-' },
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 6)
            return Array.Empty<byte>();

        var result = new byte[6];

        for (int i = 0; i < 6; i++)
        {
            if (parts[i].Length is < 1 or > 2 ||
                !byte.TryParse(
                    parts[i],
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out result[i]))
            {
                return Array.Empty<byte>();
            }
        }

        return result;
    }
}
