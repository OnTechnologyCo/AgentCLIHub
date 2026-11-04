using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCLIHub;

public partial class MainWindow
{
    private PetWindow? _petWindow;
    private string? _petFile;
    private string? _petImport;
    private string _petImportName = "";
    private bool _petCommitting;
    private TaskCompletionSource<PetSettings>? _petImportCompletion;

    private void ApplyPet()
    {
        if (_closingStarted) return;
        var options = Settings.Load().Pet ?? new PetSettings();
        options.Normalize();
        var path = PetSheet.StoredPath(options);
        if (!options.Enabled || path is null)
        {
            _petWindow?.Close(); _petWindow = null; _petFile = null; return;
        }
        try
        {
            if (_petWindow is null || _petFile != options.FileName)
            {
                var bitmap = PetSheet.Decode(ReadPetBytes(path));
                _petWindow?.Close();
                var pet = new PetWindow(this, bitmap, options, position =>
                {
                    var c = Settings.Load(); c.Pet ??= new();
                    c.Pet.X = position.X; c.Pet.Y = position.Y;
                    c.Pet.DesktopX = position.DesktopX; c.Pet.DesktopY = position.DesktopY; c.Save();
                });
                pet.Closed += (_, _) =>
                {
                    if (_petWindow == pet) { _petWindow = null; _petFile = null; }
                };
                _petWindow = pet;
                _petFile = options.FileName;
            }
            else _petWindow.Configure(options);
        }
        catch (Exception ex)
        {
            _petWindow?.Close(); _petWindow = null; _petFile = null;
            PetError(ex);
        }
    }

    private static byte[] ReadPetBytes(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length is 0 or > PetSheet.MaxBytes) throw new InvalidDataException("20 MiB 이하 이미지를 선택하세요 / select an image up to 20 MiB.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }

    private async void HandlePetMessage(JsonNode message)
    {
        try
        {
            switch (message["t"]?.GetValue<string>())
            {
                case "pet.pick":
                {
                    if (_petImport is not null || _petCommitting) return;
                    var path = Dialogs.PickFile(this, UiText.Pick("펫 스프라이트 이미지", "Pet sprite sheet"),
                        "PNG / WebP (*.png;*.webp)|*.png;*.webp");
                    if (path is null) { Post(new { t = "pet.cancel" }); return; }
                    await ImportPetAsync(path);
                    break;
                }
                case "pet.commit":
                {
                    if (_petImport is null || _petCommitting || message["id"]?.GetValue<string>() != _petImport) return;
                    _petCommitting = true;
                    var importId = _petImport;
                    var importName = _petImportName;
                    var data = message["data"]?.GetValue<string>() ?? "";
                    if (data.Length > (PetSheet.MaxBytes + 2) / 3 * 4) throw new InvalidDataException("Image exceeds 20 MiB.");
                    var png = Convert.FromBase64String(data);
                    await System.Threading.Tasks.Task.Run(() => PetSheet.Decode(png));
                    // A timed-out or closed request must never commit later.
                    if (_closingStarted || _petImport != importId) return;
                    var c = Settings.Load(); c.Pet ??= new();
                    var oldPath = PetSheet.StoredPath(c.Pet);
                    c.Pet.FileName = Guid.NewGuid().ToString("N") + ".png";
                    c.Pet.Name = importName; c.Pet.Enabled = true;
                    var path = PetSheet.StoredPath(c.Pet)!;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    try { File.WriteAllBytes(path, png); PersistPet(c); }
                    catch { File.Delete(path); throw; }
                    if (oldPath is not null && oldPath != path) { try { File.Delete(oldPath); } catch { } }
                    ApplyPet(); PostPet();
                    _petImportCompletion?.TrySetResult(c.Pet);
                    break;
                }
                case "pet.reject":
                    if (!_petCommitting && message["id"]?.GetValue<string>() == _petImport)
                        _petImportCompletion?.TrySetException(new InvalidDataException(message["error"]?.GetValue<string>() ?? "Invalid pet image."));
                    break;
                case "pet.get":
                {
                    var c = Settings.Load();
                    if (PetSheet.StoredPath(c.Pet ?? new()) is { } path)
                        Post(new { t = "pet.preview", file = c.Pet!.FileName, data = Convert.ToBase64String(ReadPetBytes(path)) });
                    break;
                }
                case "pet.save":
                {
                    var c = Settings.Load(); c.Pet ??= new();
                    var v = message["v"];
                    c.Pet.Enabled = v?["Enabled"]?.GetValue<bool>() ?? c.Pet.Enabled;
                    c.Pet.Animate = v?["Animate"]?.GetValue<bool>() ?? c.Pet.Animate;
                    c.Pet.FollowCursor = v?["FollowCursor"]?.GetValue<bool>() ?? c.Pet.FollowCursor;
                    c.Pet.Size = v?["Size"]?.GetValue<int>() ?? c.Pet.Size;
                    c.Pet.Desktop = v?["Desktop"]?.GetValue<bool>() ?? c.Pet.Desktop;
                    if (message["resetPosition"]?.GetValue<bool>() == true)
                    {
                        if (c.Pet.Desktop) { c.Pet.DesktopX = null; c.Pet.DesktopY = null; }
                        else { c.Pet.X = 1; c.Pet.Y = 1; }
                    }
                    c.Pet.Normalize(); PersistPet(c); ApplyPet(); PostPet();
                    break;
                }
                case "pet.remove":
                {
                    if (_petImport is not null) throw new InvalidOperationException("Pet import is still running.");
                    var c = Settings.Load(); c.Pet ??= new();
                    var oldPath = PetSheet.StoredPath(c.Pet);
                    c.Pet = new PetSettings(); PersistPet(c); ApplyPet(); PostPet();
                    if (oldPath is not null) { try { File.Delete(oldPath); } catch { } }
                    break;
                }
                case "pet.play":
                    _petWindow?.Play(message["state"]?.GetValue<string>() ?? "idle");
                    break;
                case "pet.activity":
                    _petWindow?.Activity(message["state"]?.GetValue<string>() ?? "idle");
                    break;
            }
        }
        catch (Exception ex)
        {
            if (message["t"]?.GetValue<string>() == "pet.commit" && message["id"]?.GetValue<string>() == _petImport)
                _petImportCompletion?.TrySetException(ex);
            PetError(ex);
        }
    }

    private async Task<PetSettings> ImportPetAsync(string path)
    {
        if (_petImport is not null) throw new InvalidOperationException("Pet import is still running.");
        if (_closingStarted || View.CoreWebView2 is null) throw new InvalidOperationException("Pet importer is not ready.");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".png" or ".webp")) throw new InvalidDataException("PNG / WebP only.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<PetSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        _petImport = id; _petImportName = Path.GetFileName(path); _petImportCompletion = completion;
        try
        {
            var bytes = await System.Threading.Tasks.Task.Run(() => ReadPetBytes(path)).WaitAsync(TimeSpan.FromSeconds(5));
            if (_closingStarted) throw new InvalidOperationException("Application is closing.");
            Post(new { t = "pet.decode", id, name = _petImportName,
                mime = ext == ".png" ? "image/png" : "image/webp", data = Convert.ToBase64String(bytes) });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            if (_petImportCompletion == completion)
            {
                _petImport = null; _petCommitting = false; _petImportCompletion = null;
            }
        }
    }

    private object PetStatus()
    {
        var options = Settings.Load().Pet ?? new();
        return new { settings = options, imagePath = PetSheet.StoredPath(options), importing = _petImport is not null,
            format = new { width = PetSheet.Width, height = PetSheet.Height, cellWidth = PetSheet.CellWidth,
                cellHeight = PetSheet.CellHeight, maxBytes = PetSheet.MaxBytes, frames = 73,
                rowCounts = PetSheet.Counts, states = PetSheet.States, lookDirections = 16 } };
    }

    private async Task<object> AgentPetCommand(string[] args)
    {
        if (_closingStarted) throw new InvalidOperationException("Application is closing.");
        var command = args.Length == 0 ? "status" : args[0];
        string Arg(int index) => args.Length > index ? args[index] : throw new ArgumentException("Missing pet command argument.");
        switch (command)
        {
            case "status": return PetStatus();
            case "import":
                if (args.Length != 2 || !Path.IsPathFullyQualified(Arg(1))) throw new ArgumentException("pet import <absolute PNG/WebP path>");
                await ImportPetAsync(Arg(1));
                return PetStatus();
            case "export":
            {
                if (args.Length != 2 || !Path.IsPathFullyQualified(Arg(1)) || !Arg(1).EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("pet export <new absolute PNG path>");
                var path = PetSheet.StoredPath(Settings.Load().Pet ?? new()) ?? throw new InvalidOperationException("No pet registered.");
                var bytes = ReadPetBytes(path); PetSheet.Decode(bytes);
                using var output = new FileStream(Arg(1), FileMode.CreateNew, FileAccess.Write);
                output.Write(bytes);
                return new { path = Arg(1), bytes = bytes.Length };
            }
            case "set":
            case "reset-position":
            {
                if (_petImport is not null) throw new InvalidOperationException("Pet import is still running.");
                var settings = Settings.Load(); settings.Pet ??= new();
                if (command == "set")
                {
                    if (args.Length != 3) throw new ArgumentException("pet set <location|size|visible|cursor|animation|name> <value>");
                    settings.Pet.SetOption(Arg(1), Arg(2));
                }
                else if (settings.Pet.Desktop) { settings.Pet.DesktopX = null; settings.Pet.DesktopY = null; }
                else { settings.Pet.X = 1; settings.Pet.Y = 1; }
                PersistPet(settings); ApplyPet(); PostPet(); return PetStatus();
            }
            case "play":
                if (args.Length != 2 || !PetSheet.States.Contains(Arg(1))) throw new ArgumentException("Unknown pet action. Use pet status for supported states.");
                if (_petWindow is null) throw new InvalidOperationException("Register and show a pet first.");
                _petWindow.Play(Arg(1)); return new { state = Arg(1) };
            default: throw new ArgumentException("pet status | import <path> | export <new PNG path> | set <option> <value> | reset-position | play <state>");
        }
    }

    private static void PersistPet(Settings settings)
    {
        settings.Save();
        if (JsonSerializer.Serialize(Settings.Load().Pet) != JsonSerializer.Serialize(settings.Pet))
            throw new IOException("펫 설정을 저장하지 못했습니다 / could not save pet settings.");
    }

    private void PostPet() => Post(new { t = "pet.settings", settings = Settings.Load().Pet });
    private void PetError(Exception ex) => Post(new { t = "pet.error", message = ex.Message });
}
