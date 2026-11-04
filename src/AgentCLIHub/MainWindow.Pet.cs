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
                var pet = new PetWindow(this, bitmap, options, (x, y) =>
                {
                    var c = Settings.Load(); c.Pet ??= new();
                    c.Pet.X = x; c.Pet.Y = y; c.Save();
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
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext is not (".png" or ".webp")) throw new InvalidDataException("PNG / WebP only.");
                    _petImport = Guid.NewGuid().ToString("N");
                    _petImportName = Path.GetFileName(path);
                    var bytes = await System.Threading.Tasks.Task.Run(() => ReadPetBytes(path));
                    Post(new { t = "pet.decode", id = _petImport, name = _petImportName,
                        mime = ext == ".png" ? "image/png" : "image/webp", data = Convert.ToBase64String(bytes) });
                    break;
                }
                case "pet.commit":
                {
                    if (_petImport is null || message["id"]?.GetValue<string>() != _petImport) return;
                    _petCommitting = true;
                    _petImport = null;
                    var importName = _petImportName;
                    var data = message["data"]?.GetValue<string>() ?? "";
                    if (data.Length > (PetSheet.MaxBytes + 2) / 3 * 4) throw new InvalidDataException("Image exceeds 20 MiB.");
                    var png = Convert.FromBase64String(data);
                    await System.Threading.Tasks.Task.Run(() => PetSheet.Decode(png));
                    var c = Settings.Load(); c.Pet ??= new();
                    var oldPath = PetSheet.StoredPath(c.Pet);
                    c.Pet.FileName = Guid.NewGuid().ToString("N") + ".png";
                    c.Pet.Name = importName; c.Pet.Enabled = true;
                    var path = PetSheet.StoredPath(c.Pet)!;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    try { File.WriteAllBytes(path, png); PersistPet(c); }
                    catch { File.Delete(path); throw; }
                    _petCommitting = false;
                    if (oldPath is not null && oldPath != path) { try { File.Delete(oldPath); } catch { } }
                    ApplyPet(); PostPet();
                    break;
                }
                case "pet.reject":
                    if (message["id"]?.GetValue<string>() == _petImport) _petImport = null;
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
                    if (message["resetPosition"]?.GetValue<bool>() == true) { c.Pet.X = 1; c.Pet.Y = 1; }
                    c.Pet.Normalize(); PersistPet(c); ApplyPet(); PostPet();
                    break;
                }
                case "pet.remove":
                {
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
        catch (Exception ex) { _petImport = null; _petCommitting = false; PetError(ex); }
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
