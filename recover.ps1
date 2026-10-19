# AgentCLIHub — 탭 중복 사고 수습.
#
#   pwsh -File ".\recover.ps1"
#
# 무엇이 있었나: 껍데기(index.html)가 다시 뜨면 ui.ready 가 또 오는데, 그때
# RestoreTabs 가 무방비로 한 번 더 돌아 설정에 적힌 탭이 한 벌씩 더 생겼다.
# 세 번 겹쳐 탭이 6개 → 18개가 됐고, CLI 탭 4개가 12개가 되어 claude 가 같은
# 폴더에서 12개 떠 ~/.claude.json 을 동시에 쓰다 그 파일을 잘라 놓았다.
# 그래서 "대화 이어서 하기" 를 누르면 로그인부터 다시 하라고 나온다.
#
# **AgentCLIHub 를 완전히 닫은 뒤에 실행하라.** 떠 있으면 닫을 때 지금 화면의
# 탭 목록(중복된 것)을 그대로 다시 써서 이 스크립트가 한 일이 지워진다.

$ErrorActionPreference = 'Stop'

if (Get-Process -Name AgentCLIHub,LlmTools -ErrorAction SilentlyContinue) {
    Write-Host "AgentCLIHub 가 아직 떠 있습니다. 창을 닫고 다시 실행하세요." -ForegroundColor Red
    exit 1
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

# ── 1. 중복된 탭 목록 정리 ──────────────────────────────────────────────
# 같은 탭(종류·주소·폴더·CLI 가 모두 같은 것)은 **처음 것만 남긴다.**
$cfgPath = "$env:LOCALAPPDATA\AgentCLIHub\settings.json"
if (Test-Path $cfgPath) {
    Copy-Item $cfgPath "$cfgPath.dup-$stamp"
    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    $before = $cfg.Tabs.Count

    $seen = [System.Collections.Generic.HashSet[string]]::new()
    $cfg.Tabs = @($cfg.Tabs | Where-Object {
        $seen.Add("$($_.Kind)|$($_.Url)|$($_.Cwd)|$($_.Backend)")
    })

    $cfg | ConvertTo-Json -Depth 8 | Set-Content $cfgPath -Encoding UTF8
    Write-Host "탭 정리: $before개 -> $($cfg.Tabs.Count)개  (백업: settings.json.dup-$stamp)" -ForegroundColor Green
} else {
    Write-Host "settings.json 이 없습니다 — 건너뜁니다." -ForegroundColor Yellow
}

# ── 2. 잘려 나간 Claude Code 설정 되돌리기 ──────────────────────────────
# 토큰(.credentials.json)은 멀쩡하다. 잘린 것은 .claude.json 쪽이고,
# 거기 hasCompletedOnboarding·projects 가 통째로 빠져 처음 켠 것처럼 보인다.
$live = "$env:USERPROFILE\.claude.json"
$cand = "$env:USERPROFILE\.claude.json.restore-candidate"

if (-not (Test-Path $cand)) {
    Write-Host ".claude.json 복구본이 없습니다 — 건너뜁니다." -ForegroundColor Yellow
} elseif (Get-Process claude, node -ErrorAction SilentlyContinue) {
    Write-Host "claude 프로세스가 아직 살아 있습니다. 전부 끝난 뒤 다시 실행하세요." -ForegroundColor Red
} else {
    $a = Get-Content $live -Raw | ConvertFrom-Json
    $b = Get-Content $cand -Raw | ConvertFrom-Json

    # **계정이 같은지 확인하고 덮는다.** 다른 계정 설정을 덮어씌우면 남의 자리로 들어간다.
    if ($a.oauthAccount.accountUuid -ne $b.oauthAccount.accountUuid) {
        Write-Host "복구본의 계정이 지금 계정과 다릅니다 — 덮지 않습니다." -ForegroundColor Red
    } elseif ($a.hasCompletedOnboarding) {
        Write-Host ".claude.json 이 이미 정상입니다 — 덮지 않습니다." -ForegroundColor Green
    } else {
        Copy-Item $live "$live.broken-$stamp"
        Copy-Item $cand $live -Force
        Write-Host ".claude.json 복구 완료 (백업: .claude.json.broken-$stamp)" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "다음: artifacts\setup 폴더의 최신 AgentCLIHub-Setup 설치 파일을 설치하세요." -ForegroundColor Cyan
Write-Host "그 판부터는 껍데기가 다시 떠도 탭이 두 벌이 되지 않습니다." -ForegroundColor Cyan
