<#
.SYNOPSIS
    デザイナ exe の designcheck を実行し、結果 JSON を標準出力に返す。

.DESCRIPTION
    designcheck は --out を省くと何も出力しない（終了コードのみ）ので、結果ファイルだけは避けられない。
    そこで **固定パスを 1 つ上書きし続ける**（増やさない・消さない。CLAUDE.md §3-2-1）。
    デザイナ exe は WinExe なので Process.WaitForExit() で待つ。

    designcheck の緑は「読み込める」までの保証でしかない。
    計算・状態遷移・見た目は必ず実機（ブラウザ）で確認する（CLAUDE.md §4-1）。

.EXAMPLE
    pwsh -NoProfile -File tools/clb/designcheck.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectRoot = Join-Path $repoRoot 'Designer/Design'

$localEnv = Join-Path $repoRoot 'Designer/LocalEnvironment.md'
if (-not (Test-Path $localEnv)) { throw "Designer/LocalEnvironment.md が無い。DesignerExePath を書いておくこと。" }

$line = Select-String -Path $localEnv -Pattern '^DesignerExePath:\s*(.+)$' | Select-Object -First 1
if (-not $line) { throw 'LocalEnvironment.md に DesignerExePath: の行が無い。' }
$exe = $line.Matches[0].Groups[1].Value.Trim()
if (-not (Test-Path $exe)) { throw "デザイナ exe が見つからない: $exe" }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
# 結果の置き場は固定。毎回上書きするので増えないし、消す必要もない。
$resultPath = Join-Path ([System.IO.Path]::GetTempPath()) 'clb-designcheck.json'

$psi.ArgumentList.Add('designcheck')
$psi.ArgumentList.Add($projectRoot)
$psi.ArgumentList.Add('--out')
$psi.ArgumentList.Add($resultPath)

$proc = [System.Diagnostics.Process]::Start($psi)
$stdout = $proc.StandardOutput.ReadToEnd()
$stderr = $proc.StandardError.ReadToEnd()
$proc.WaitForExit()

if ($stdout) { Write-Output $stdout }
if ($stderr) { Write-Output "STDERR: $stderr" }
if (Test-Path $resultPath) { Get-Content $resultPath -Raw }
exit $proc.ExitCode
