using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using AgentCLIHub;

internal static class BrowserChecks
{
    public static int Run(byte[] png)
    {
        var result = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var profile = Path.Combine(root, "artifacts", "pet-checks", "profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            var web = new WebView2();
            var owner = new Window { Title = "AgentCLIHub isolated pet check", Width = 1050, Height = 800, Content = web };
            try
            {
                owner.Show();
                Console.WriteLine("Starting isolated WebView2 in " + profile);
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profile).WaitAsync(TimeSpan.FromSeconds(20));
                await web.EnsureCoreWebView2Async(env).WaitAsync(TimeSpan.FromSeconds(20));
                Console.WriteLine("WebView2 ready");
                web.CoreWebView2.SetVirtualHostNameToFolderMapping("agentclihub.local", Path.Combine(root, "web"), CoreWebView2HostResourceAccessKind.Deny);
                var ready = new TaskCompletionSource();
                var complete = new TaskCompletionSource();
                var options = new PetSettings();
                string? replaceId = null;
                var saved = 0; var imported = 0; var selected = 0; var removed = 0; var picked = 0;
                var thumbnail = Convert.ToBase64String(PetSheet.Thumbnail(PetSheet.Decode(png)));
                void Post(object payload) => web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
                void Conf() => Post(new { t = "conf", settings = new { UiLanguage = "ko", Pet = options }, catalog = new { }, detect = new { }, accounts = new object[0] });
                web.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    try
                    {
                        var m = JsonNode.Parse(e.WebMessageAsJson)!;
                        switch (m["t"]?.ToString())
                        {
                            case "ui.ready": Conf(); ready.TrySetResult(); break;
                            case "conf.get": Conf(); break;
                            case "pet.list":
                                Post(new { t = "pet.library", items = options.Items, selectedId = options.SelectedId });
                                foreach (var item in options.Items) Post(new { t = "pet.thumbnail", itemId = item.Id, file = item.FileName, data = thumbnail });
                                break;
                            case "pet.test.no-picker":
                                if (picked != 0) throw new Exception("Registration should open the library without a file picker");
                                break;
                            case "pet.pick":
                                replaceId = m["itemId"]?.ToString(); picked++;
                                Post(new { t = "pet.decode", id = "test", name = "fixture.png", mime = "image/png", data = Convert.ToBase64String(png) }); break;
                            case "pet.commit":
                                PetSheet.Decode(Convert.FromBase64String(m["data"]!.ToString())); imported++;
                                options.AddOrUpdate(Guid.NewGuid().ToString("N") + ".png", "Fixture " + imported, replaceId);
                                Post(new { t = "pet.settings", settings = options }); break;
                            case "pet.select":
                                options.Select(m["itemId"]!.ToString()); selected++;
                                Post(new { t = "pet.settings", settings = options }); break;
                            case "pet.remove":
                                options.Remove(m["itemId"]!.ToString()); removed++;
                                Post(new { t = "pet.settings", settings = options }); break;
                            case "pet.get": Post(new { t = "pet.preview", file = options.FileName, data = Convert.ToBase64String(png) }); break;
                            case "pet.save":
                                options = m["v"]!.Deserialize<PetSettings>()!; saved++;
                                Post(new { t = "pet.settings", settings = options }); break;
                            case "pet.test.done":
                                if (m["error"] is { } error) complete.TrySetException(new Exception(error.ToString()));
                                else complete.TrySetResult(); break;
                            case "pet.reject": complete.TrySetException(new Exception("Unexpected fixture rejection")); break;
                        }
                    }
                    catch (Exception ex) { complete.TrySetException(ex); }
                };
                web.CoreWebView2.Navigate("https://agentclihub.local/index.html");
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await web.CoreWebView2.ExecuteScriptAsync("""
                    (async () => {
                      const wait = ms => new Promise(r => setTimeout(r, ms));
                      const assert = (ok, text) => { if (!ok) throw new Error(text); };
                      const until = async fn => { for(let n=0;n<100;n++) { if(fn()) return; await wait(50); } throw new Error('UI response timeout'); };
                      try {
                        showConf(); await until(() => document.querySelector('[data-pet-import]'));
                        document.querySelector('[data-pet-import]').click();
                        await until(() => document.querySelector('dialog.pet-library[open]'));
                        assert(!document.querySelector('[data-pet-list] li'), 'empty library');
                        window.chrome.webview.postMessage({t:'pet.test.no-picker'});
                        const rows = () => [...document.querySelectorAll('[data-pet-list] li')];
                        const click = (id, action) => document.querySelector(`button[data-pet-id="${id}"][data-pet-action="${action}"]`).click();
                        const idle = () => !document.querySelector('[data-pet-add]').disabled;
                        document.querySelector('[data-pet-add]').click();
                        await until(() => rows().length === 1 && idle());
                        const first = rows()[0].dataset.petId;
                        document.querySelector('[data-pet-add]').click();
                        await until(() => rows().length === 2 && idle());
                        const second = rows()[1].dataset.petId;
                        assert(rows()[0].querySelector('strong').textContent === 'Fixture 1', 'second import preserves first pet');
                        await until(() => rows().every(row => row.querySelector('img')?.naturalWidth === 96));
                        click(first, 'select'); await until(() => idle() && rows()[0].classList.contains('selected'));
                        click(second, 'update'); await until(idle);
                        assert(rows().length === 2 && rows()[1].dataset.petId === second && rows()[1].querySelector('strong').textContent === 'Fixture 2', 'replacement keeps ID and name');
                        assert(rows()[0].classList.contains('selected'), 'inactive replacement keeps selection');
                        click(first, 'delete'); assert(rows().length === 2, 'delete requires confirmation');
                        click(first, 'cancel-delete'); assert(rows().length === 2, 'cancel preserves entry');
                        click(first, 'delete'); click(first, 'confirm-delete');
                        await until(() => rows().length === 1 && idle());
                        assert(rows()[0].dataset.petId === second && rows()[0].classList.contains('selected'), 'delete targets only selected entry and selects remaining');
                        document.querySelector('[data-pet-library-close]').click(); await until(() => !document.querySelector('dialog.pet-library'));
                        document.querySelector('[data-pet-import]').click(); await until(() => rows().length === 1);
                        document.querySelector('[data-pet-add]').click();
                        await until(() => rows().length === 2 && idle());
                        document.querySelector('[data-pet-library-close]').click(); await until(() => !document.querySelector('dialog.pet-library'));
                        await until(() => document.querySelector('[data-pet-name]').textContent === 'Fixture 4');
                        const canvas = document.querySelector('.pet-preview canvas');
                        await until(() => canvas.getContext('2d').getImageData(40,40,1,1).data[3] === 255);
                        const actions = document.querySelector('.pet-actions select');
                        assert(actions.options.length === 25, 'nine actions and sixteen directions');
                        for (const option of actions.options) {
                          actions.value = option.value; actions.dispatchEvent(new Event('change'));
                          assert(canvas.getContext('2d').getImageData(40,40,1,1).data[3] === 255, 'preview '+option.value);
                        }
                        const size = document.querySelector('[data-pet-key="Size"]'); size.value = '125'; size.dispatchEvent(new Event('change'));
                        await wait(100);
                        const mode = document.querySelector('[data-pet-key="Desktop"]'); mode.value = 'desktop'; mode.dispatchEvent(new Event('change'));
                        await wait(100); assert(mode.value === 'desktop', 'desktop mode preference');
                        const enabled = document.querySelector('[data-pet-key="Enabled"]'); enabled.click(); await wait(100);
                        assert(!enabled.checked, 'hidden preference');
                        applyLanguage('en');
                        assert(document.querySelector('[data-pet-import]').textContent === 'Import / pet library', 'English pet labels');
                        assert(document.querySelector('[data-pet-key="Desktop"]').selectedOptions[0].textContent === 'Desktop', 'English desktop mode and persisted selection');
                        const all = document.createElement('canvas'); all.width=1536; all.height=2288; const ctx=all.getContext('2d');
                        for(let r=0;r<11;r++) for(let c=0;c<PetFormat.counts[r];c++) { ctx.fillStyle='rgb(100,150,200)'; ctx.fillRect(c*192+30,r*208+30,100,140); }
                        const webp = await new Promise(r=>all.toBlob(r,'image/webp',1));
                        const normalized = await PetFormat.normalize(new Uint8Array(await webp.arrayBuffer()),'image/webp');
                        assert(PetFormat.inspect(normalized,'image/png').height===2288, 'WebP decoding and normalization');
                        ctx.clearRect(0,0,192,208);
                        const invalid=await new Promise(r=>all.toBlob(r,'image/png')); let rejected=false;
                        try { await PetFormat.normalize(new Uint8Array(await invalid.arrayBuffer()),'image/png'); } catch { rejected=true; }
                        assert(rejected, 'empty required frame rejected');
                        assert(document.querySelector('[data-pet-name]').textContent==='Fixture 4', 'invalid image preserves pet');
                        PetUI.active('one'); PetUI.terminal({t:'term.ready',id:'one'}); PetUI.terminal({t:'term.error',id:'other'});
                        document.querySelector('[data-pet-import]').click();
                        await until(() => rows().length === 2 && rows().every(row => row.querySelector('img')?.naturalWidth === 96));
                        assert(document.querySelector('.pet-library h2').textContent === 'My pets', 'English library title');
                        window.chrome.webview.postMessage({t:'pet.test.done'});
                      } catch(e) { window.chrome.webview.postMessage({t:'pet.test.done',error:String(e.stack||e)}); }
                    })();
                    """);
                await complete.Task.WaitAsync(TimeSpan.FromSeconds(30));
                if (saved != 3 || imported != 4 || selected != 1 || removed != 1 || options.Items.Count != 2 || !options.Desktop)
                    throw new Exception($"Expected gallery mutations, preserved collection and desktop mode: {imported}/{saved}/{selected}/{removed}/{options.Items.Count}");
                var output = Path.Combine(root, "artifacts", "pet-checks"); Directory.CreateDirectory(output);
                using (var screenshot = File.Create(Path.Combine(output, "gallery.png")))
                    await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
                Console.WriteLine("PASS: WebView2 gallery popup, multiple imports, 96x104 thumbnails, select, inactive replacement, delete/cancel, reopen, 25 previews, PNG/WebP decoder, invalid-frame rejection, settings and English UI");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                web.Dispose(); owner.Close(); app.Shutdown();
                try { Directory.Delete(profile, recursive: true); } catch { }
            }
        };
        app.Run(); return result;
    }
}
