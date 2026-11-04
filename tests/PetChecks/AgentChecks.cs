using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using AgentCLIHub;

internal static class AgentChecks
{
    public static int Run()
    {
        try
        {
            var guide = (string)typeof(AgentBrief).GetMethod("Text", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            if (!guide.Contains("펫 제작·수정·등록") || !guide.Contains("pet import") ||
                !guide.Contains("1536×2288") || !guide.Contains("출력하거나") || !guide.Contains("15칸") ||
                !guide.Contains("pet list") || !guide.Contains("update <id>") || !guide.Contains("이전 펫은 목록에 보존"))
                throw new Exception("Missing internal pet construction guide");
            var options = new PetSettings();
            var first = options.AddOrUpdate(Guid.NewGuid().ToString("N") + ".png", "first");
            var second = options.AddOrUpdate(Guid.NewGuid().ToString("N") + ".png", "second");
            var calls = 0;
            using var bridge = new BrowserBridge((cmd, args) =>
            {
                if (cmd != "pet") throw new Exception("Expected pet command");
                calls++;
                if (args[0] == "set") options.SetOption(args[1], args[2]);
                if (args[0] == "list") return Task.FromResult<object>(new { items = options.Items, selectedId = options.SelectedId });
                if (args[0] == "select") options.Select(args[1]);
                if (args[0] == "remove") options.Remove(args[1]);
                return Task.FromResult<object>(options);
            }, (_, _) => { });
            bridge.Start();
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var helper = Path.Combine(root, "src", "AgentCLIHub.Cli", "bin", "Release", "net9.0", "agentclihub-browser.exe");
            (int Exit, string Output) Call(string ticket, params string[] args)
            {
                var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment["AGENTCLIHUB_BROWSER_PIPE"] = bridge.PipeName;
                start.Environment[BrowserBridge.TicketEnv] = ticket;
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000)) { process.Kill(true); throw new Exception("Bridge command timeout"); }
                return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
            }
            var set = Call(bridge.Ticket, "pet", "set", "name", "별이 펫");
            if (set.Exit != 0 || JsonSerializer.Deserialize<PetSettings>(set.Output)?.Name != "별이 펫")
                throw new Exception("CLI must preserve Unicode and spaced arguments: " + set.Output);
            if (Call(bridge.Ticket, "pet", "set", "location", "desktop").Exit != 0 ||
                !JsonSerializer.Deserialize<PetSettings>(Call(bridge.Ticket, "pet", "status").Output)!.Desktop)
                throw new Exception("CLI settings/status round trip failed");
            var accepted = calls;
            if (Call("wrong-ticket", "pet", "set", "name", "unauthorized").Exit == 0 || calls != accepted || options.Name != "별이 펫")
                throw new Exception("Pet command bypassed bridge authentication");
            if (Call(bridge.Ticket, "pet", "set", "size", "999").Exit == 0 || options.Size != 75)
                throw new Exception("CLI did not report invalid pet settings");
            using var list = JsonDocument.Parse(Call(bridge.Ticket, "pet", "list").Output);
            if (list.RootElement.GetProperty("items").GetArrayLength() != 2 || list.RootElement.GetProperty("selectedId").GetString() != second.Id)
                throw new Exception("CLI library response lost stored entries");
            if (Call(bridge.Ticket, "pet", "select", first.Id).Exit != 0 || options.SelectedId != first.Id)
                throw new Exception("CLI select lost ID");
            accepted = calls;
            if (Call("wrong-ticket", "pet", "remove", first.Id).Exit == 0 || calls != accepted || options.Items.Count != 2)
                throw new Exception("Unauthorized collection deletion accepted");
            if (Call(bridge.Ticket, "pet", "remove", second.Id).Exit != 0 || options.Items.Count != 1 || options.SelectedId != first.Id)
                throw new Exception("CLI targeted delete lost other entry");
            Console.WriteLine("PASS: internal creation guide, CLI Unicode arguments, pet settings/status/list/select/remove, authentication and invalid settings");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
