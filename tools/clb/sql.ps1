<#
.SYNOPSIS
    デザイナ exe の sql サブコマンドで SQL を実行し、結果 JSON を標準出力に返す。

.DESCRIPTION
    一時ファイルを作らない（CLAUDE.md §3-2-1）。
      - --out を省いて結果を標準出力で受ける
      - --query は ProcessStartInfo.ArgumentList で渡す
        （Start-Process -ArgumentList だと SQL 内の '...' が壊れて incomplete input になる）

    デザイナ exe は WinExe なので、PowerShell の & 演算子だと待たずに戻る。
    Process.WaitForExit() で待つ。

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

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectRoot = Join-Path $repoRoot 'Designer/Design'

# デザイナ exe のパスは Git 追跡外の LocalEnvironment.md にある（CLAUDE.md §5）。
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

$psi.ArgumentList.Add('sql')
$psi.ArgumentList.Add($projectRoot)
$psi.ArgumentList.Add('--datasource')
$psi.ArgumentList.Add($DataSource)
if ($Query) {
    $psi.ArgumentList.Add('--query')
    $psi.ArgumentList.Add($Query)
} else {
    $psi.ArgumentList.Add('--file')
    $psi.ArgumentList.Add((Resolve-Path $File).Path)
}

$proc = [System.Diagnostics.Process]::Start($psi)
$stdout = $proc.StandardOutput.ReadToEnd()
$stderr = $proc.StandardError.ReadToEnd()
$proc.WaitForExit()

Write-Output $stdout
if ($stderr) { Write-Output "STDERR: $stderr" }
exit $proc.ExitCode
