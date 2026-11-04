using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentCLIHub;

internal static class Program
{
    private static int checks;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (InvalidDataException) { checks++; return; }
        throw new Exception("Not rejected: " + name);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Check(PetSheet.Counts.Sum() == 73 && PetSheet.States.Count == 9, "73 frames, 9 states");
            var pixels = new byte[PetSheet.Width * PetSheet.Height * 4];
            for (var row = 0; row < 11; row++) for (var col = 0; col < PetSheet.Counts[row]; col++)
                for (var y = 24; y < 184; y++) for (var x = 32; x < 160; x++)
                {
                    var p = ((row * 208 + y) * 1536 + col * 192 + x) * 4;
                    pixels[p] = (byte)(col * 25 + 30); pixels[p + 1] = (byte)(row * 20 + 20); pixels[p + 2] = 180; pixels[p + 3] = 255;
                }
            var image = BitmapSource.Create(1536, 2288, 96, 96, PixelFormats.Bgra32, null, pixels, 1536 * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream(); encoder.Save(stream); var png = stream.ToArray();
            var decoded = PetSheet.Decode(png);
            Check(decoded.IsFrozen, "decoded bitmap frozen for UI use");
            for (var row = 0; row < 11; row++) for (var col = 0; col < PetSheet.Counts[row]; col++)
            {
                var frame = PetSheet.Frame(decoded, row, col); var pixel = new byte[4];
                frame.CopyPixels(new Int32Rect(40, 40, 1, 1), pixel, 4, 0);
                Check(pixel[0] == col * 25 + 30 && pixel[1] == row * 20 + 20, $"crop {row}:{col}");
            }
            Reject(() => PetSheet.Decode([]), "empty file");
            Reject(() => PetSheet.Decode(new byte[PetSheet.MaxBytes + 1]), "oversized file");
            var wrong = (byte[])png.Clone(); wrong[18] = 0;
            Reject(() => PetSheet.Decode(wrong), "wrong dimensions before decoding");
            Reject(() => PetSheet.ValidatePixels(new byte[pixels.Length]), "missing required frames");
            var alphaAtUnused = (6 * 192) * 4 + 3; pixels[alphaAtUnused] = 1;
            Reject(() => PetSheet.ValidatePixels(pixels), "nonempty unused cell"); pixels[alphaAtUnused] = 0;
            for (var y = 0; y < 208; y++) for (var x = 0; x < 192; x++) pixels[(y * 1536 + x) * 4 + 3] = 255;
            Reject(() => PetSheet.ValidatePixels(pixels), "opaque required cell");
            for (var direction = 0; direction < 16; direction++)
            {
                var angle = direction * Math.PI / 8;
                Check(PetSheet.Direction(Math.Sin(angle), -Math.Cos(angle)) == direction, "look " + direction);
            }
            var options = new PetSettings { FileName = "../../settings.json", Size = 1000, X = double.NaN, Y = -2 };
            Check(PetSheet.StoredPath(options) is null, "path traversal rejected"); options.Normalize();
            Check(options.Size == 150 && options.X == 1 && options.Y == 0, "settings clamp");
            options.FileName = Guid.NewGuid().ToString("N") + ".png";
            Check(PetSheet.StoredPath(options)!.EndsWith(options.FileName), "valid owned filename");
            var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { Pet = options }))!;
            Check(restored.Pet.FileName == options.FileName && restored.Pet.X == options.X, "pet settings round trip");
            Check(!JsonSerializer.Deserialize<Settings>("{}")!.Pet.Enabled, "old settings default hidden");
            if (args.Contains("--web")) return BrowserChecks.Run(png);
            if (args.Contains("--ui"))
            {
                var app = new Application();
                var owner = new Window { Width = 800, Height = 600, Title = "AgentCLIHub Pet UI Check", Background = Brushes.DimGray };
                owner.Show(); options.Enabled = true;
                var pet = new PetWindow(owner, decoded, options, (_, _) => { });
                foreach (var state in PetSheet.States) pet.Play(state);
                owner.WindowState = WindowState.Minimized; Check(!pet.IsVisible, "minimized pet hidden");
                owner.WindowState = WindowState.Normal;
                owner.Width = 600; owner.Height = 500;
                pet.Configure(options); Check(pet.IsVisible, "restored pet visible");
                pet.Close(); owner.Close(); app.Shutdown();
            }
            Console.WriteLine($"PASS: {checks} pet checks"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
