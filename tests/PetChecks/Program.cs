using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
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
            using var thumbStream = new MemoryStream(PetSheet.Thumbnail(decoded));
            var thumb = new PngBitmapDecoder(thumbStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Check(thumb.PixelWidth == 96 && thumb.PixelHeight == 104, "compact first-frame thumbnail");
            var legacyFile = Guid.NewGuid().ToString("N") + ".png";
            var library = JsonSerializer.Deserialize<PetSettings>(JsonSerializer.Serialize(new { FileName = legacyFile, Name = "기존 펫", Enabled = true, Size = 125, Desktop = true }))!;
            library.Normalize(); library.Normalize();
            Check(library.Items.Count == 1 && library.Find(library.SelectedId).FileName == legacyFile && library.Enabled && library.Desktop && library.Size == 125, "legacy migration preserves pet and preferences, is idempotent");
            var firstId = library.SelectedId;
            var added = library.AddOrUpdate(Guid.NewGuid().ToString("N") + ".png", "두 번째 펫");
            Check(library.Items.Count == 2 && library.SelectedId == added.Id && library.Find(firstId).FileName == legacyFile, "adding preserves old entry and selects new");
            var selectedFile = library.FileName;
            var replacement = Guid.NewGuid().ToString("N") + ".png";
            library.AddOrUpdate(replacement, "replacement.png", firstId);
            Check(library.Items.Count == 2 && library.Find(firstId).Name == "기존 펫" && library.Find(firstId).FileName == replacement && library.FileName == selectedFile, "replace inactive pet preserves identity/name/selection");
            library.Select(firstId); library.SetOption("name", "수정 펫");
            Check(library.FileName == replacement && library.Enabled && library.Find(firstId).Name == "수정 펫", "select and rename stored entry");
            var activeReplacement = Guid.NewGuid().ToString("N") + ".png";
            library.AddOrUpdate(activeReplacement, "ignored.png", firstId); library.Normalize();
            Check(library.Items.Count == 2 && library.FileName == activeReplacement && library.SelectedId == firstId, "replace active pet does not resurrect old file during normalization");
            library = JsonSerializer.Deserialize<PetSettings>(JsonSerializer.Serialize(library))!; library.Normalize();
            Check(library.Items.Count == 2 && library.SelectedId == firstId && library.Name == "수정 펫", "collection and selection survive settings round trip");
            var beforeBad = JsonSerializer.Serialize(library);
            foreach (var action in new Action[] { () => library.Select("missing"), () => library.Remove("missing"), () => library.AddOrUpdate(Guid.NewGuid().ToString("N") + ".png", "x", "missing"), () => library.AddOrUpdate("../outside.png", "x") })
            {
                try { action(); throw new Exception("Invalid library mutation accepted"); }
                catch (ArgumentException) { Check(JsonSerializer.Serialize(library) == beforeBad, "invalid library target leaves collection unchanged"); }
            }
            library.Remove(added.Id); library.Normalize();
            Check(library.Items.Count == 1 && library.SelectedId == firstId, "delete inactive only");
            var last = library.AddOrUpdate(Guid.NewGuid().ToString("N") + ".png", "last");
            library.Remove(last.Id); library.Normalize();
            Check(library.SelectedId == firstId && library.Enabled, "delete selected falls back to remaining pet");
            library.Remove(firstId); library.Normalize();
            Check(library.Items.Count == 0 && library.FileName == "" && library.SelectedId == "" && !library.Enabled, "delete last remains empty after normalization");
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
            Check(!JsonSerializer.Deserialize<Settings>("{}")!.Pet.Desktop, "old settings retain app mode");
            options.DesktopX = -800; options.DesktopY = 250;
            restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { Pet = options }))!;
            Check(restored.Pet.DesktopX == -800 && restored.Pet.DesktopY == 250, "desktop position round trip");
            options.DesktopX = double.NaN; options.DesktopY = double.PositiveInfinity; options.Normalize();
            Check(options.DesktopX is null && options.DesktopY is null, "invalid desktop positions reset");
            options.SetOption("location", "desktop"); options.SetOption("size", "100");
            options.SetOption("visible", "on"); options.SetOption("name", "별이 펫");
            Check(options.Desktop && options.Size == 100 && options.Enabled && options.Name == "별이 펫", "agent settings apply");
            var beforeInvalid = JsonSerializer.Serialize(options);
            foreach (var (key, value) in new[] { ("size", "49"), ("size", "151"), ("visible", "maybe"), ("location", "outside"), ("FileName", "../other.png") })
            {
                try { options.SetOption(key, value); throw new Exception("Invalid option accepted: " + key); }
                catch (ArgumentException) { Check(JsonSerializer.Serialize(options) == beforeInvalid, "invalid option leaves pet unchanged: " + key + value); }
            }
            options.Desktop = false;
            if (args.Contains("--bridge")) return AgentChecks.Run();
            if (args.Contains("--web")) return BrowserChecks.Run(png);
            if (args.Contains("--ui"))
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var owner = new Window { Width = 800, Height = 600, Title = "AgentCLIHub Pet UI Check", Background = Brushes.DimGray };
                owner.Show(); options.Enabled = true;
                var positionSaved = false;
                var pet = new PetWindow(owner, decoded, options, _ => positionSaved = true);
                foreach (var state in PetSheet.States) pet.Play(state);
                owner.WindowState = WindowState.Minimized; Check(!pet.IsVisible, "minimized pet hidden");
                owner.WindowState = WindowState.Normal;
                owner.Width = 600; owner.Height = 500;
                pet.Configure(options); Check(pet.IsVisible, "restored pet visible");
                Check(pet.Owner == owner && !pet.Topmost, "app mode remains owned and not topmost");
                var appX = options.X; var appY = options.Y;
                options.Desktop = true; pet.Configure(options);
                Check(pet.Owner is null && pet.Topmost && pet.IsVisible, "desktop unowned, topmost and visible");
                var desktopPosition = pet.PointToScreen(new Point());
                owner.Left += 40; owner.Top += 40; owner.Width += 20;
                Check((pet.PointToScreen(new Point()) - desktopPosition).Length < 2, "desktop independent of app move and resize");
                owner.WindowState = WindowState.Minimized;
                Check(pet.IsVisible, "desktop stays visible while app minimized");
                owner.Hide(); Check(pet.IsVisible, "desktop stays visible while app hidden");
                owner.Show(); owner.WindowState = WindowState.Normal;
                pet.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pet), 0, Key.Left) { RoutedEvent = Keyboard.KeyDownEvent });
                Check(positionSaved && options.DesktopX < desktopPosition.X, "desktop keyboard move saves physical position");
                Check(options.X == appX && options.Y == appY, "desktop motion preserves app position");
                options.DesktopX = -1000000; options.DesktopY = -1000000; pet.Configure(options);
                Check(options.DesktopX > -1000000 && options.DesktopY > -1000000, "off-screen position recovered");
                var desktopX = options.DesktopX; var desktopY = options.DesktopY;
                options.Desktop = false; pet.Configure(options);
                Check(pet.Owner == owner && !pet.Topmost, "switch back restores app ownership");
                owner.WindowState = WindowState.Minimized;
                Check(!pet.IsVisible, "app mode hides after switching back");
                options.Desktop = true; pet.Configure(options);
                Check(pet.IsVisible && options.DesktopX == desktopX && options.DesktopY == desktopY, "desktop restored while app minimized");
                var closed = false; pet.Closed += (_, _) => closed = true;
                owner.Close(); Check(closed, "unowned desktop pet closes with app"); app.Shutdown();
            }
            Console.WriteLine($"PASS: {checks} pet checks"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
