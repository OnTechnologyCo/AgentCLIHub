# 실제 배포 도우미와 설치 스크립트의 결과 파일 권한을 격리된 임시 폴더에서 검사한다.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $HelperPath,
    [Parameter(Mandatory)][string] $CompilerPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$helper = (Resolve-Path -LiteralPath $HelperPath).Path
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$compileLog = Join-Path $artifacts 'installer-permission-compile.log'
$testLog = Join-Path $artifacts 'installer-permission-test.log'
& $CompilerPath "/DHelperPath=$helper" (Join-Path $root 'tests\InstallerChecks\ReportPermissions.iss') *> $compileLog
if ($LASTEXITCODE) { Get-Content -LiteralPath $compileLog -Tail 20; throw '권한 검사 프로그램 컴파일 실패' }
$testExe = Join-Path $artifacts 'installer-checks\ReportPermissions.exe'
$process = Start-Process -FilePath $testExe -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + $testLog + '"')) -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(30000)) { throw "권한 검사 프로그램 응답 없음. PID: $($process.Id)" }
if ($process.ExitCode -ne 0) { Get-Content -LiteralPath $testLog -Tail 30; throw "권한 검사 실패: $($process.ExitCode)" }
$passes = @(Select-String -LiteralPath $testLog -Pattern 'PASS ')
if ($passes.Count -ne 4) { throw '권한 검사 결과가 누락되었습니다.' }
$passes | ForEach-Object { $_.Line }
