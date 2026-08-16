<#
.SYNOPSIS
  会計整合性の不変条件 SQL をまとめて実行し、違反があるものだけを報告する。

.DESCRIPTION
  このフォルダの *.sql を名前順に流す。各 SQL は「合格なら 0 行・違反なら違反内容の行」を返す
  約束になっているので、返った行数がそのまま違反件数になる。

  読み取り専用。SELECT しか流さない（DB を書き換えるチェックは置かない）。

.PARAMETER DesignerExe
  デザイナ exe のパス。省略時は Designer/LocalEnvironment.md の DesignerExePath: 行から読む。

.PARAMETER DataSource
  designer.settings.json の DataSources 名。既定は BusinessAppSQLite。
  sql CLI は AllowCliSqlAccess:true のデータソースにしか繋がらない（安全境界）。

.PARAMETER OutDir
  結果 JSON の出力先。省略時は一時フォルダに作る（リポジトリは汚さない）。

.PARAMETER Only
  ファイル名に対するワイルドカード。例: -Only 'A*' で仕訳系だけ流す。

.PARAMETER ShowRows
  違反があったチェックについて、違反行の中身も表示する。

.EXAMPLE
  pwsh -File docs/qa/20_不変条件SQL/run_all.ps1
.EXAMPLE
  pwsh -File docs/qa/20_不変条件SQL/run_all.ps1 -Only 'C*' -ShowRows

.OUTPUTS
  終了コード 0 = 全件合格 / 1 = 違反あり / 2 = 実行エラーあり
#>
[CmdletBinding()]
param(
    [string]$DesignerExe,
    [string]$DataSource = 'BusinessAppSQLite',
    [string]$OutDir,
    [string]$Only = '*',
    [switch]$ShowRows
)

$ErrorActionPreference = 'Stop'

$checkDir   = $PSScriptRoot
$repoRoot   = (Resolve-Path (Join-Path $checkDir '..' '..' '..')).Path
$designRoot = Join-Path $repoRoot 'Designer' 'Design'

# --- デザイナ exe の解決（絶対パスはリポジトリに書かない。マシン固有ファイルから読む） ---
if (-not $DesignerExe) {
    $envFile = Join-Path $repoRoot 'Designer' 'LocalEnvironment.md'
    if (-not (Test-Path $envFile)) {
        throw "Designer/LocalEnvironment.md が見つかりません。-DesignerExe でパスを渡してください。"
    }
    $m = Select-String -Path $envFile -Pattern '^\s*DesignerExePath:\s*(.+?)\s*$' | Select-Object -First 1
    if (-not $m) {
        throw "LocalEnvironment.md に DesignerExePath: 行がありません。-DesignerExe でパスを渡してください。"
    }
    $DesignerExe = $m.Matches[0].Groups[1].Value
}
if (-not (Test-Path $DesignerExe)) { throw "デザイナ exe が見つかりません: $DesignerExe" }
if (-not (Test-Path $designRoot))  { throw "デザインプロジェクトが見つかりません: $designRoot" }

if (-not $OutDir) {
    $OutDir = Join-Path ([System.IO.Path]::GetTempPath()) ("invariant-sql-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$files = Get-ChildItem -Path $checkDir -Filter '*.sql' | Where-Object { $_.Name -like $Only } | Sort-Object Name
if ($files.Count -eq 0) { throw "実行対象の SQL がありません（-Only '$Only'）。" }

Write-Host ""
Write-Host "不変条件チェック  対象 $($files.Count) 本 / データソース $DataSource"
Write-Host "結果 JSON: $OutDir"
Write-Host ("-" * 78)

$pass = @(); $fail = @(); $err = @()

foreach ($f in $files) {
    $name    = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
    $outJson = Join-Path $OutDir ($name + '.json')

    & $DesignerExe sql $designRoot --datasource $DataSource --file $f.FullName --out $outJson 2>&1 | Out-Null
    $code = $LASTEXITCODE

    if ($code -ne 0 -or -not (Test-Path $outJson)) {
        $err += [pscustomobject]@{ Name = $name; Detail = "sql CLI が終了コード $code で失敗" }
        Write-Host ("  [ERROR] {0}" -f $name) -ForegroundColor Magenta
        continue
    }

    try {
        $json = Get-Content -LiteralPath $outJson -Raw -Encoding utf8 | ConvertFrom-Json
    } catch {
        $err += [pscustomobject]@{ Name = $name; Detail = "結果 JSON を解釈できない: $($_.Exception.Message)" }
        Write-Host ("  [ERROR] {0}" -f $name) -ForegroundColor Magenta
        continue
    }

    $rows = 0
    foreach ($r in @($json.results)) { $rows += [int]$r.rowCount }

    if ($rows -eq 0) {
        $pass += $name
        Write-Host ("  [OK]    {0}" -f $name) -ForegroundColor DarkGray
    } else {
        $fail += [pscustomobject]@{ Name = $name; Violations = $rows; Json = $outJson }
        Write-Host ("  [NG]    {0}  違反 {1} 件" -f $name, $rows) -ForegroundColor Yellow
        if ($ShowRows) {
            foreach ($r in @($json.results)) {
                foreach ($row in @($r.rows)) { Write-Host ("          " + ($row | ConvertTo-Json -Compress)) }
            }
        }
    }
}

Write-Host ("-" * 78)
Write-Host ("合格 {0} 本 / 違反 {1} 本 / 実行エラー {2} 本" -f $pass.Count, $fail.Count, $err.Count)

if ($fail.Count -gt 0) {
    Write-Host ""
    Write-Host "違反のあったチェック:" -ForegroundColor Yellow
    $fail | ForEach-Object { Write-Host ("  {0}  {1} 件  → {2}" -f $_.Name, $_.Violations, $_.Json) }
}
if ($err.Count -gt 0) {
    Write-Host ""
    Write-Host "実行エラー:" -ForegroundColor Magenta
    $err | ForEach-Object { Write-Host ("  {0}  {1}" -f $_.Name, $_.Detail) }
}
Write-Host ""

if ($err.Count  -gt 0) { exit 2 }
if ($fail.Count -gt 0) { exit 1 }
exit 0
