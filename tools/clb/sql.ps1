<#
.SYNOPSIS
    デザイナ exe の sql サブコマンドで SQL を実行し、結果 JSON を標準出力に返す。

.DESCRIPTION
    一時ファイルを作らない（docs/15_実装の原則.md §5）。
      - --out を省いて結果を標準出力で受ける
      - --query は ProcessStartInfo.ArgumentList で渡す
        （Start-Process -ArgumentList だと SQL 内の '...' が壊れて incomplete input になる）

    exe の呼び出しは _designer.ps1 に共通化してある。標準エラーは標準エラーへ流すので、
    標準出力はそのまま JSON として扱える。

.PARAMETER Query
    実行する SQL。; 区切りで複数文を並べてよい。

.PARAMETER File
    SQL をファイルから読む（Query の代わり）。Designer/ddl の追跡ファイルを流すときに使う。

.PARAMETER DataSource
    designer.settings.json の DataSources の Name。

.EXAMPLE
    pwsh -NoProfile -File tools/clb/sql.ps1 -Query "SELECT COUNT(*) FROM journal_entries;"

.EXAMPLE
    pwsh -NoProfile -File tools/clb/sql.ps1 -File Designer/ddl/005_journals.sql
#>
[CmdletBinding()]
param(
    [string]$Query,
    [string]$File,
    [string]$DataSource = 'BusinessAppSQLite'
)

$ErrorActionPreference = 'Stop'

if (-not $Query -and -not $File) { throw '-Query か -File のどちらかが要る。' }
if ($Query -and $File) { throw '-Query と -File は同時に指定できない。' }

. (Join-Path $PSScriptRoot '_designer.ps1')

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectRoot = Join-Path $repoRoot 'Designer/Design'
$exe = Get-DesignerExePath -RepoRoot $repoRoot

$arguments = @('sql', $projectRoot, '--datasource', $DataSource)
if ($Query) {
    $arguments += @('--query', $Query)
} else {
    $arguments += @('--file', (Resolve-Path $File).Path)
}

$result = Invoke-Designer -Exe $exe -Arguments $arguments
Write-Output $result.StandardOutput
exit $result.ExitCode
