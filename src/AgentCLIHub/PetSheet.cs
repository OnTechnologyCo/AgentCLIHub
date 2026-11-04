using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentCLIHub;

public sealed class PetSettings
{
    public bool Enabled { get; set; }
    public string FileName { get; set; } = "";
    public string Name { get; set; } = "";
    public int Size { get; set; } = 75;
    public bool FollowCursor { get; set; } = true;
    public bool Animate { get; set; } = true;
    public bool Desktop { get; set; }
    public double? DesktopX { get; set; }
    public double? DesktopY { get; set; }
    public double X { get; set; } = 1;
    public double Y { get; set; } = 1;

    public void Normalize()
    {
        Size = Math.Clamp(Size, 50, 150);
        X = double.IsFinite(X) ? Math.Clamp(X, 0, 1) : 1;
        Y = double.IsFinite(Y) ? Math.Clamp(Y, 0, 1) : 1;
        DesktopX = DesktopX is { } x && double.IsFinite(x) ? Math.Clamp(x, -1000000, 1000000) : null;
        DesktopY = DesktopY is { } y && double.IsFinite(y) ? Math.Clamp(y, -1000000, 1000000) : null;
    }

    public void SetOption(string key, string value)
    {
        bool OnOff() => value switch { "on" => true, "off" => false, _ => throw new ArgumentException("Expected on or off.") };
        switch (key)
        {
            case "location":
                Desktop = value switch { "desktop" => true, "app" => false, _ => throw new ArgumentException("Expected desktop or app.") }; break;
            case "visible": Enabled = OnOff(); break;
            case "cursor": FollowCursor = OnOff(); break;
            case "animation": Animate = OnOff(); break;
            case "size":
                if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) || size is < 50 or > 150)
                    throw new ArgumentException("Pet size must be 50–150.");
                Size = size; break;
            case "name":
                if (value.Length is 0 or > 120 || value.Any(char.IsControl)) throw new ArgumentException("Pet name must be 1–120 characters without control characters.");
                Name = value; break;
            default: throw new ArgumentException("Unknown pet option: location, size, visible, cursor, animation, name.");
        }
    }
}

public static class PetSheet
{
    public const int Width = 1536, Height = 2288, CellWidth = 192, CellHeight = 208;
    public const int MaxBytes = 20 * 1024 * 1024;
    public static IReadOnlyList<int> Counts { get; } = Array.AsReadOnly(new[] { 6, 8, 8, 4, 5, 8, 6, 6, 6, 8, 8 });
    public static IReadOnlyList<string> States { get; } = Array.AsReadOnly(new[]
        { "idle", "running-right", "running-left", "waving", "jumping", "failed", "waiting", "running", "review" });

    public static BitmapSource Decode(byte[] png)
    {
        if (png.Length is 0 or > MaxBytes) throw new InvalidDataException("PNG / WebP: 20 MiB 이하 / maximum 20 MiB.");
        // Check geometry before invoking a decoder, including for files changed outside the app.
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) != Width ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) != Height)
            throw new InvalidDataException("1536 × 2288 PNG가 필요합니다 / expected 1536 × 2288 PNG.");
        using var stream = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        if (bitmap.PixelWidth != Width || bitmap.PixelHeight != Height) throw new InvalidDataException("Invalid image dimensions.");
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        ValidatePixels(pixels);
        bitmap.Freeze();
        return bitmap;
    }

    public static void ValidatePixels(byte[] bgra)
    {
        if (bgra.Length != Width * Height * 4) throw new InvalidDataException("Invalid pixel buffer.");
        for (var row = 0; row < Counts.Count; row++)
            for (var col = 0; col < 8; col++)
            {
                var visible = 0;
                for (var y = row * CellHeight; y < (row + 1) * CellHeight; y++)
                    for (var x = col * CellWidth; x < (col + 1) * CellWidth; x++)
                        if (bgra[(y * Width + x) * 4 + 3] != 0) visible++;
                var used = col < Counts[row];
                if ((used && (visible == 0 || visible == CellWidth * CellHeight)) || (!used && visible != 0))
                    throw new InvalidDataException($"행/row {row + 1}, 칸/cell {col + 1}: " +
                        (used ? "캐릭터와 투명 배경이 필요합니다 / character and transparent background required."
                              : "빈 칸은 완전히 투명해야 합니다 / unused cells must be transparent."));
            }
    }

    public static BitmapSource Frame(BitmapSource sheet, int row, int column)
    {
        if (row < 0 || row >= Counts.Count || column < 0 || column >= Counts[row]) throw new ArgumentOutOfRangeException();
        var frame = new CroppedBitmap(sheet, new Int32Rect(column * CellWidth, row * CellHeight, CellWidth, CellHeight));
        frame.Freeze();
        return frame;
    }

    // Clockwise, with zero pointing up in screen coordinates.
    public static int Direction(double dx, double dy) =>
        ((int)Math.Round(Math.Atan2(dx, -dy) * 8 / Math.PI, MidpointRounding.AwayFromZero) % 16 + 16) % 16;

    public static string? StoredPath(PetSettings options)
    {
        var name = options.FileName ?? "";
        return name.Length == 36 && name.EndsWith(".png", StringComparison.Ordinal) &&
            Guid.TryParseExact(name[..32], "N", out _) ? Path.Combine(Settings.DataDir, "pets", name) : null;
    }
}
