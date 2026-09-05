<#
.SYNOPSIS
    デザイナ exe の designcheck を実行し、結果 JSON を標準出力に返す。

.DESCRIPTION
    designcheck は --out を省くと何も出力しない（終了コードのみ）ので、結果ファイルだけは避けられない。
    そこで **固定パスを 1 つ上書きし続ける**（増やさない・消さない。docs/15_実装の原則.md §5）。
    exe の呼び出しは _designer.ps1 に共通化してある。

    designcheck の緑は「読み込める」までの保証でしかない。
    計算・状態遷移・見た目は必ず実機（ブラウザ）で確認する（CLAUDE.md §4-1）。

.EXAMPLE
    pwsh -NoProfile -File tools/clb/designcheck.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_designer.ps1')

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectRoot = Join-Path $repoRoot 'Designer/Design'
$exe = Get-DesignerExePath -RepoRoot $repoRoot

# 結果の置き場は固定。毎回上書きするので増えないし、消す必要もない。
$resultPath = Join-Path ([System.IO.Path]::GetTempPath()) 'clb-designcheck.json'

$result = Invoke-Designer -Exe $exe -Arguments @('designcheck', $projectRoot, '--out', $resultPath)
if ($result.StandardOutput) { Write-Output $result.StandardOutput }
if (Test-Path $resultPath) { Get-Content $resultPath -Raw }
exit $result.ExitCode
