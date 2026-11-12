using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class HistoryBrowserChecks
{
    public static int Run()
    {
        var result = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var profile = Path.Combine(root, "artifacts", "resume-checks", "profile-" + Guid.NewGuid().ToString("N"));
            var web = new WebView2();
            var owner = new Window { Title = "AgentCLIHub isolated history check", Width = 1050, Height = 800, Content = web };
            try
            {
                Directory.CreateDirectory(profile); owner.Show();
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profile).WaitAsync(TimeSpan.FromSeconds(20));
                await web.EnsureCoreWebView2Async(env).WaitAsync(TimeSpan.FromSeconds(20));
                web.CoreWebView2.SetVirtualHostNameToFolderMapping("agentclihub.local", Path.Combine(root, "web"), CoreWebView2HostResourceAccessKind.Deny);
                var ready = new TaskCompletionSource(); var done = new TaskCompletionSource();
                var resumed = false;
                var first = Guid.NewGuid().ToString(); var second = Guid.NewGuid().ToString();
                void Post(object value) => web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value));
                var rows = new[] {
                    new { backend="claude_code", cli="Claude Code", id=first, cwd="D:\\Alpha", folder="Alpha", when="2026-11-05 10:10", title="Alpha question", account="alpha", accountName="회사", shared=false },
                    new { backend="claude_code", cli="Claude Code", id=second, cwd="D:\\Beta", folder="Beta", when="2026-11-05 10:09", title="Beta question", account="beta", accountName="개인", shared=false },
                    new { backend="claude_code", cli="Claude Code", id=Guid.NewGuid().ToString(), cwd="D:\\Shared", folder="Shared", when="2026-11-05 10:08", title="Shared question", account="", accountName="기본", shared=true }
                };
                web.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    try
                    {
                        var m = JsonNode.Parse(e.WebMessageAsJson)!;
                        switch (m["t"]?.ToString())
                        {
                            case "ui.ready": ready.TrySetResult(); break;
                            case "history.list": Post(new { t="history", items=rows }); break;
                            case "tab.resume":
                                if (m["account"]?.ToString() != "beta" || m["sessionId"]?.ToString() != second || m["cwd"]?.ToString() != "D:\\Beta")
                                    throw new Exception("Filtered history resumed the wrong account/session");
                                resumed=true; break;
                            case "history.test.done":
                                if (m["error"] is { } error) done.TrySetException(new Exception(error.ToString()));
                                else done.TrySetResult(); break;
                        }
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                };
                web.CoreWebView2.Navigate("https://agentclihub.local/index.html");
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await web.CoreWebView2.ExecuteScriptAsync("""
                    (async()=>{
                      const wait=ms=>new Promise(r=>setTimeout(r,ms));
                      const assert=(v,t)=>{if(!v)throw Error(t)};
                      const until=async f=>{for(let n=0;n<100;n++){if(f())return;await wait(30)}throw Error('history timeout')};
                      try {
                        document.querySelector('#resume').click();
                        await until(()=>document.querySelectorAll('#hlist .hrow').length===3);
                        const filter=document.querySelector('#haccount');
                        assert(filter.options.length===4,'all accounts and shared group');
                        filter.value=JSON.stringify(['claude_code','beta',false]);filter.dispatchEvent(new Event('change'));
                        assert(document.querySelectorAll('#hlist .hrow').length===1 && document.querySelector('#hlist .ti').textContent==='Beta question','account filter');
                        assert(document.querySelector('#hlist .cli').textContent.includes('개인'),'account badge');
                        document.querySelector('#hlist .hrow').click();
                        document.querySelector('#resume').click();
                        await until(()=>document.querySelectorAll('#hlist .hrow').length===1);
                        filter.value='';filter.dispatchEvent(new Event('change'));
                        const q=document.querySelector('#hq');q.value='회사';q.dispatchEvent(new Event('input'));
                        assert(document.querySelectorAll('#hlist .hrow').length===1 && document.querySelector('#hlist .ti').textContent==='Alpha question','search includes account names');
                        q.value='';q.dispatchEvent(new Event('input'));
                        applyLanguage('en');
                        assert(filter.options[0].textContent==='All accounts','English account filter');
                        assert([...document.querySelectorAll('#hlist .cli')].some(e=>e.textContent.includes('Shared history')),'unattributed history is not labeled default');
                        applyLanguage('ko');
                        CONF={settings:{UiLanguage:'ko'},catalog:{},detect:{claude_code:{found:true},codex_cli:{found:true},antigravity_cli:{found:true}},
                              accounts:[{backend:'claude_code',id:'',name:'기본',loggedIn:true},
                                        {backend:'codex_cli',id:'',name:'기본',loggedIn:true},
                                        {backend:'antigravity_cli',id:'',name:'기본',loggedIn:false},
                                        {backend:'antigravity_cli',id:'work',name:'회사',loggedIn:true}]};
                        USAGE['claude_code/']=[];
                        USAGE['codex_cli/']=[{label:'주',pct:44,reset:Math.floor(Date.now()/1000)+86400}];
                        USAGE['antigravity_cli/']=[{label:'5h',pct:1,reset:Math.floor(Date.now()/1000)+17000},{label:'주',pct:2,reset:Math.floor(Date.now()/1000)+500000}];
                        USAGE['antigravity_cli/work']=[];
                        renderUsage();
                        const usageLabels=[...document.querySelectorAll('#usage .acct .an')].map(e=>e.textContent);
                        assert(JSON.stringify(usageLabels)===JSON.stringify(['Claude','GPT','Gemini','Gemini·회사']),'Gemini label used instead of Antigravity: '+JSON.stringify(usageLabels));
                        assert(!document.querySelector('#usage').textContent.includes('Antigravity'),'bottom usage bar never displays Antigravity name');
                        const geminiCell=[...document.querySelectorAll('#usage .acct')][2];
                        assert(geminiCell.textContent.includes('5h')&&geminiCell.textContent.includes('1%')&&geminiCell.textContent.includes('주')&&geminiCell.textContent.includes('2%'),'Gemini 5h and weekly usage rendered identically');
                        const emptyWork=[...document.querySelectorAll('#usage .acct')][3];
                        assert(emptyWork.title.includes('사용량을 읽지 못했습니다'),'Korean fallback tooltip');
                        applyLanguage('en');
                        assert([...document.querySelectorAll('#usage .acct')][3].title.includes('Could not read usage'),'English fallback tooltip updates dynamically');
                        window.chrome.webview.postMessage({t:'history.test.done'});
                      } catch(e){window.chrome.webview.postMessage({t:'history.test.done',error:String(e.stack||e)})}
                    })();
                    """);
                await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (!resumed) throw new Exception("Resume was not dispatched");
                using var screenshot = File.Create(Path.Combine(root, "artifacts", "resume-checks", "history.png"));
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
                Console.WriteLine("PASS: WebView2 account list/filter/search, shared group, exact account/session resume, reopen and English labels");
                result=0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { web.Dispose(); owner.Close(); app.Shutdown(); try { Directory.Delete(profile,true); } catch { } }
        };
        app.Run(); return result;
    }
}
