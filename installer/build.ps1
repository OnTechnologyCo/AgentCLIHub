# Agent CLI Hub 설치본 만들기.
#
#   pwsh installer\build.ps1              # Release, win-x64, 자체 포함
#   pwsh installer\build.ps1 -SkipPublish # 이미 publish 해 뒀을 때 설치본만 다시
#
# 결과: artifacts\setup\AgentCLIHub-Setup-<버전>.exe
#
# **자체 포함으로 낸다.** 설치받는 PC 에 .NET 9 데스크톱 런타임이 없어도 돌아야 한다 —
# 회사 PC 에 런타임을 따로 깔게 하면 그 단계에서 대부분 멈춘다.
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$app     = Join-Path $root 'src\LlmTools\LlmTools.csproj'
$helper  = Join-Path $root 'src\LlmTools.Cli\LlmTools.Cli.csproj'
$publish = Join-Path $root 'artifacts\publish'
$setup   = Join-Path $root 'artifacts\setup'
$iss     = Join-Path $PSScriptRoot 'AgentCLIHub.iss'

# ISCC 는 PATH 에 없는 것이 보통이다(사용자 폴더 설치가 기본이라 ProgramFiles 에도 없다).
function Find-ISCC {
    $c = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { return $p }
    }
    throw "ISCC.exe 를 찾지 못했습니다. Inno Setup 6 을 설치하세요 — https://jrsoftware.org/isdl.php"
}

if (-not $SkipPublish) {
    if (Test-Path $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }

    # 도우미를 먼저 복원한다. 앱이 publish 도중 이 프로젝트를 RID 를 물려 부르는데,
    # 그때는 복원이 안 돌아 자산 파일이 없다고 멈춘다.
    Write-Host '== 도우미 복원 =='
    & dotnet restore $helper -r $Runtime
    if ($LASTEXITCODE) { throw "복원 실패 ($LASTEXITCODE)" }

    Write-Host '== 배포본 만들기 =='
    & dotnet publish $app -c $Configuration -r $Runtime --self-contained true -o $publish
    if ($LASTEXITCODE) { throw "publish 실패 ($LASTEXITCODE)" }
}

# 셋 다 없으면 설치본이 조용히 반쪽이 된다 — 깔리고 나서야 드러나므로 여기서 막는다.
foreach ($need in @('AgentCLIHub.exe', 'web\index.html', 'agent\agentclihub-browser.exe', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'licenses\xterm-6.0.0-MIT.txt', 'licenses\addon-fit-0.11.0-MIT.txt', 'licenses\WebView2-1.0.4078.44-LICENSE.txt', 'licenses\WebView2-1.0.4078.44-NOTICE.txt', 'licenses\ProtectedData-9.0.0-LICENSE.txt', 'licenses\ProtectedData-9.0.0-THIRD-PARTY-NOTICES.txt', 'licenses\component-provenance.json')) {
    if (-not (Test-Path (Join-Path $publish $need))) { throw "배포본에 $need 이(가) 없습니다." }
}

New-Item -ItemType Directory -Force -Path $setup | Out-Null

Write-Host '== 설치본 컴파일 =='
$iscc = Find-ISCC
& $iscc "/DPublishDir=$publish" $iss
if ($LASTEXITCODE) { throw "ISCC 실패 ($LASTEXITCODE)" }

Get-ChildItem $setup -Filter '*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    ForEach-Object { "`n완성: $($_.FullName)  ({0:N0} MB)" -f ($_.Length / 1MB) }
