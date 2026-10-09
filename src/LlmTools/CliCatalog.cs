using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace LlmTools;

/// <summary>
/// CLI 마다 **무엇을 받는가** — 모델 · 추론 강도 · 권한 항목.
///
/// **이 표는 여기 한 벌뿐이다.** 목록을 화면에 두고 검증을 호스트에 두면 반드시 어긋나고,
/// 어긋난 쪽은 "저장은 됐는데 CLI 가 인자를 거절해 터미널이 아예 안 열린다" 로만 보인다.
/// 그래서 목록도 검증도 여기서 나온다 — 화면은 <see cref="ForUi"/> 를 그대로 그린다.
///
/// **값은 CLI 마다 따로 저장하고, 각자 그 CLI 기준으로 검증한다.** 새 대화마다 CLI 를 고르므로
/// 한 칸을 같이 쓰면 claude 에서 고른 `opus` 가 `codex -m opus` 로 나가 뜨자마자 죽는다.
/// 그 판정이 <see cref="ClampModel"/> · <see cref="ClampEffort"/> · <see cref="ClampPermission"/> 다.
/// </summary>
public static class CliCatalog
{
    /// <summary>화면에 고정된 차례로 보여 줄 CLI.</summary>
    public static readonly string[] Backends = CliProfile.All.Select(p => p.Backend).ToArray();

    // ── 모델 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// claude 는 **별칭**을 받는다 — 최신 판을 자동으로 따라간다.
    /// 전체 이름(claude-opus-4-5-…)도 그대로 받으므로 이 목록은 편의이지 전부가 아니다.
    /// </summary>
    private static readonly string[] ClaudeModels = ["opus", "sonnet", "haiku", "fable"];

    /// <summary>이 CLI 가 고를 수 있는 모델. 빈 목록 = 우리가 아는 목록이 없다.</summary>
    public static IReadOnlyList<string> Models(string? backend) => Norm(backend) switch
    {
        "claude_code" => ClaudeModels,
        "codex_cli" => CodexModels().Select(m => m.Slug).ToArray(),
        _ => [],
    };

    /// <summary>
    /// 모델 목록이 **그 CLI 가 받는 전부인가**. 아니면 목록은 거들 뿐이라 검증에 못 쓴다.
    ///
    ///   codex  — 목록을 codex 자신이 준다(models_cache.json). 없는 슬러그는 거절된다 ⇒ 완전.
    ///   claude — 별칭은 편의다. 전체 모델 이름도 받는다 ⇒ 불완전.
    ///
    /// 불완전한 목록으로 값을 지우면 **사용자가 일부러 넣은 이름이 저장 한 번에 사라진다.**
    /// </summary>
    public static bool ModelsAreComplete(string? backend) =>
        Norm(backend) == "codex_cli" && CodexModels().Count > 0;

    // ── 추론 강도 ───────────────────────────────────────────────────────────

    /// <summary>보여 주는 차례. CLI 마다 받는 것만 이 차례로 걸러 낸다.</summary>
    private static readonly string[] EffortOrder = ["low", "medium", "high", "xhigh", "max", "ultra"];

    private static readonly string[] ClaudeEfforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// 이 CLI(·모델)가 받는 추론 강도. **모델까지 보는 이유는 codex 때문이다** —
    /// 같은 codex 안에서도 모델마다 다르다(실측: gpt-5.6-terra 는 ultra 까지,
    /// gpt-5.5 는 xhigh 까지). 모델을 안 주면 그 CLI 가 아는 것 전부를 합쳐 준다.
    ///
    /// **모르는 값은 목록에 안 넣는다.** CLI 에 넘기면 인자가 거절돼 대화가 시작조차 안 된다.
    /// </summary>
    public static IReadOnlyList<string> Efforts(string? backend, string? model = null) => Norm(backend) switch
    {
        "claude_code" => ClaudeEfforts,
        "codex_cli" => CodexEfforts(model),
        "antigravity_cli" => ["low", "medium", "high", "max"],
        _ => [],
    };

    private static IReadOnlyList<string> CodexEfforts(string? model)
    {
        var models = CodexModels();
        if (models.Count == 0) return ClaudeEfforts;   // 캐시를 못 읽었다 — 흔한 값으로 둔다

        var m = (model ?? "").Trim();
        var pick = m.Length > 0
            ? models.FirstOrDefault(x => string.Equals(x.Slug, m, StringComparison.OrdinalIgnoreCase))
            : null;

        var set = pick is not null
            ? new HashSet<string>(pick.Efforts, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(models.SelectMany(x => x.Efforts), StringComparer.OrdinalIgnoreCase);

        return EffortOrder.Where(set.Contains).ToArray();
    }

    // ── CLI 권한 ────────────────────────────────────────────────────────────

    /// <summary>CLI 자신의 승인 정책 항목 하나. 화면이 이대로 그리고, 저장이 이대로 검증한다.</summary>
    public sealed record PermField(string Key, string Label, string[] Choices);

    /// <summary>
    /// 이 CLI 가 받는 권한 항목.
    ///
    /// **claude 는 하나, codex 는 둘이다.** claude 의 `--permission-mode` 가 승인과 접근을
    /// 함께 정하는 데 비해 codex 는 샌드박스(`-s`)와 승인(`-a`)을 따로 받는다.
    /// 합쳐서 프리셋으로 접지 않는다 — 접은 이름과 실제로 걸리는 플래그가 갈라지면
    /// 화면이 거짓말을 하고, codex 가 값을 늘리면 여기까지 고쳐야 한다.
    ///
    /// 이름표에 CLI 이름을 안 붙인다 — 설정 화면이 CLI 마다 한 장씩 카드를 따로 그린다.
    /// </summary>
    public static IReadOnlyList<PermField> Permissions(string? backend) => Norm(backend) switch
    {
        "claude_code" =>
        [
            new("ClaudeCodePermissionMode", "권한",
                ["manual", "acceptEdits", "auto", "dontAsk", "plan", "bypassPermissions"]),
        ],
        "codex_cli" =>
        [
            new("CodexSandbox", "샌드박스",
                ["read-only", "workspace-write", "danger-full-access"]),
            // `untrusted` 는 뺐다 — codex 0.154 가 `-a untrusted` 를 인자 단계에서 거절한다(exit 2).
            // 목록에 남겨 두면 고르는 순간 Codex 탭이 새 대화·이어하기·복원 할 것 없이 뜨자마자 죽는다.
            new("CodexApproval", "승인",
                ["on-request", "never"]),
        ],
        "antigravity_cli" =>
        [
            new("AntigravityPermission", "권한", ["sandbox", "dangerously-skip-permissions"]),
        ],
        _ => [],
    };

    // ── 정리(clamp) ─────────────────────────────────────────────────────────

    /// <summary>
    /// 이 CLI 가 못 받는 모델이면 비운다(= CLI 기본값). **비운 사실은 말한다** —
    /// 조용히 바꾸면 사용자는 저장이 안 된 줄 알고 같은 값을 계속 다시 넣는다.
    /// 목록이 불완전한 CLI 에서는 아무것도 안 한다(<see cref="ModelsAreComplete"/> 참조).
    /// </summary>
    public static string ClampModel(string? backend, string? model, Action<string, string>? log = null)
    {
        var v = (model ?? "").Trim();
        if (v.Length == 0 || !ModelsAreComplete(backend)) return v;
        if (Models(backend).Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase))) return v;

        log?.Invoke($"'{Norm(backend)}' 는 모델 '{v}' 를 받지 않습니다 — 기본 모델로 둡니다.", "w");
        return "";
    }

    /// <summary>이 CLI·모델이 못 받는 추론 강도면 비운다.</summary>
    public static string ClampEffort(string? backend, string? model, string? effort,
                                     Action<string, string>? log = null)
    {
        var v = (effort ?? "").Trim();
        if (v.Length == 0) return "";

        var list = Efforts(backend, model);
        if (list.Count == 0) return "";
        if (list.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase))) return v;

        var where = (model ?? "").Trim().Length > 0 ? $"'{Norm(backend)} · {model!.Trim()}'" : $"'{Norm(backend)}'";
        log?.Invoke($"{where} 는 추론 강도 '{v}' 를 받지 않습니다 — 기본값으로 둡니다.", "w");
        return "";
    }

    /// <summary>
    /// 권한 값 하나를 **그 항목이 속한 CLI 기준**으로 정리한다.
    ///
    /// 지금 고른 CLI 는 안 본다. 안 쓰는 쪽 값도 **지우지 않고 그대로 저장하기 때문이다** —
    /// CLI 를 되돌렸을 때 전에 걸어 둔 것이 살아 있어야 한다(오갈 때마다 초기화되면 못 쓴다).
    /// 저장은 되는데 검증은 안 하면, 그 값이 나중에 CLI 를 되돌린 날 그대로 커맨드라인으로 나간다.
    ///
    /// 모르는 값은 빈 값으로 떨어뜨린다 — 오타를 그대로 넘기면 CLI 가 인자를 거절해
    /// 터미널이 아예 안 열리고, 화면에는 이유가 안 보인다.
    /// </summary>
    public static string ClampPermission(string key, string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return "";
        var f = Backends.SelectMany(Permissions).FirstOrDefault(x => x.Key == key);
        if (f is null) return "";
        return f.Choices.FirstOrDefault(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    // ── 화면으로 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 화면이 그대로 쓸 표. CLI → { models · modelsComplete · efforts · effortsByModel ·
    /// modelKey · effortKey · perm }.
    /// `effortsByModel` 은 codex 에만 있다 — 모델을 고르면 강도 목록이 따라 바뀐다.
    /// `modelKey` · `effortKey` 는 그 CLI 의 값이 담기는 설정 칸 이름이다(<see cref="CliProfile.ModelKey"/>).
    /// </summary>
    public static Dictionary<string, object> ForUi(IReadOnlyList<AntigravityModels.Model>? antigravityModels = null)
    {
        var o = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in Backends)
        {
            Dictionary<string, string[]>? byModel = null;
            if (Norm(b) == "codex_cli")
            {
                byModel = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in CodexModels())
                    byModel[m.Slug] = EffortOrder.Where(e => m.Efforts.Contains(e, StringComparer.OrdinalIgnoreCase))
                                                 .ToArray();
            }

            var p = CliProfile.ForOrDefault(b);
            o[b] = new
            {
                display = p.Display,
                memoryFile = p.MemoryFile,
                models = p.Kind == CliKind.Antigravity ? (antigravityModels ?? []).Select(m => m.Id).ToArray() : Models(b),
                modelLabels = p.Kind == CliKind.Antigravity ? (antigravityModels ?? []).ToDictionary(m => m.Id, m => m.Name) : null,
                modelsComplete = ModelsAreComplete(b),
                efforts = Efforts(b),
                effortsByModel = byModel,
                modelKey = p.ModelKey,
                effortKey = p.EffortKey,
                pathKey = p.PathKey,
                supportsAccounts = p.SupportsAccounts,
                supportsTransfer = p.SupportsTransfer,
                perm = Permissions(b).Select(f => new { key = f.Key, label = f.Label, choices = f.Choices }).ToArray(),
            };
        }
        return o;
    }

    // ── codex 모델 캐시 ─────────────────────────────────────────────────────

    /// <summary>codex 가 아는 모델 하나. 강도가 모델마다 다르므로 같이 들고 다닌다.</summary>
    public sealed record CodexModel(string Slug, string[] Efforts);

    private static readonly object _lock = new();
    private static string _file = "";
    private static DateTime _stamp;
    private static IReadOnlyList<CodexModel> _codex = [];

    /// <summary>
    /// codex 가 캐시해 둔 모델 목록(`$CODEX_HOME/models_cache.json`).
    ///
    /// **목록을 소스에 박지 않는 이유**는 claude 쪽과 반대다. claude 는 별칭을 받아 최신 판을
    /// 자동으로 따라가지만, codex 는 실제 슬러그(gpt-5.6-terra 등)를 받는다 — 박아 두면
    /// 다음 모델이 나온 날 설정에서 고를 수가 없다. 못 읽으면 빈 목록이고,
    /// 그때는 <see cref="ModelsAreComplete"/> 가 false 라 검증도 안 한다.
    ///
    /// 파일이 바뀌면 다시 읽는다 — codex 가 새 모델을 받아 오면 앱을 껐다 켜지 않아도 따라온다.
    /// </summary>
    public static IReadOnlyList<CodexModel> CodexModels()
    {
        try
        {
            var file = Path.Combine(CliProfile.CodexHome, "models_cache.json");
            if (!File.Exists(file)) return [];

            var stamp = File.GetLastWriteTimeUtc(file);
            lock (_lock)
            {
                // **경로까지 본다.** 시각만 보면 CODEX_HOME 을 옮겼을 때(검사·다중 프로필)
                // 앞 홈의 목록이 그대로 남는다 — 두 파일의 기록 시각이 같을 수 있다.
                if (stamp == _stamp && file == _file) return _codex;

                var arr = JsonNode.Parse(File.ReadAllText(file))?["models"] as JsonArray;
                var list = new List<CodexModel>();
                foreach (var m in arr ?? [])
                {
                    // 화면에 내보내는 것만 담는다. 내부용 슬러그(리뷰 전용 등)를 고르면 답이 안 나온다.
                    if (m?["visibility"]?.GetValue<string>() is { } vis &&
                        !string.Equals(vis, "list", StringComparison.OrdinalIgnoreCase)) continue;
                    if (m?["slug"]?.GetValue<string>() is not { Length: > 0 } slug) continue;
                    if (list.Any(x => string.Equals(x.Slug, slug, StringComparison.OrdinalIgnoreCase))) continue;

                    var efforts = (m["supported_reasoning_levels"] as JsonArray ?? [])
                        .Select(e => e?["effort"]?.GetValue<string>() ?? "")
                        .Where(e => e.Length > 0)
                        .ToArray();

                    list.Add(new CodexModel(slug, efforts));
                }

                _file = file;
                _stamp = stamp;
                _codex = list;
                return _codex;
            }
        }
        catch { return []; }
    }

    private static string Norm(string? backend) => (backend ?? "").Trim().ToLowerInvariant();
}
