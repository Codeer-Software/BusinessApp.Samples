# deploy.ps1 — CLB デザインプロジェクトを App.zip に固めて稼働サーバへ反映する
#
# デザイナ GUI の「送信」の代替。サーバの FileWatcher が *.zip を検知して hot-reload する。
# zip エントリ名はデザイナ独自形式（バックスラッシュ区切り・ディレクトリエントリ無し・
# app.clprj はルート直下・designer.settings*.json は含めない）を再現する。
#
# 使い方:
#   pwsh -NoProfile -File tools/clb/deploy.ps1
#   pwsh -NoProfile -File tools/clb/deploy.ps1 -Workspace <path> -Destination <path>
#
# 注意:
#   - 実行前に designcheck を通すこと（findingCount 0）。
#   - *.mod.cs（スクリプト）変更・DB スキーマ変更は deploy だけでは反映されない。サーバ再起動が必要。

param(
    [string]$Workspace,
    [string]$Destination
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $Workspace)   { $Workspace   = (Resolve-Path (Join-Path $repoRoot "Designer\Design")).Path }
if (-not $Destination) { $Destination = Join-Path $repoRoot "LocalData\designs\App.zip" }

if (-not (Test-Path (Join-Path $Workspace "app.clprj"))) {
    throw "app.clprj が見つかりません: $Workspace はデザインプロジェクトではありません"
}

$destDir = Split-Path -Parent $Destination
if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir | Out-Null }

# 含めるもの: app.clprj / app.css（あれば）/ Modules・PageFrames・Enums・Resources 配下の全ファイル
# 含めないもの: designer.settings*.json（接続設定）・.ide/（デザイナのマシン固有状態）・その他の作業ファイル
$entries = @()
foreach ($rootFile in @("app.clprj", "app.css")) {
    $p = Join-Path $Workspace $rootFile
    if (Test-Path $p) { $entries += @{ Path = $p; Name = $rootFile } }
}
foreach ($dir in @("Modules", "PageFrames", "Enums", "Resources")) {
    $dirPath = Join-Path $Workspace $dir
    if (-not (Test-Path $dirPath)) { continue }
    Get-ChildItem -Path $dirPath -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($Workspace.Length + 1)   # 例: Modules\Sub\X.mod.json
        $entries += @{ Path = $_.FullName; Name = $rel }       # 区切りは \ のまま入れる
    }
}

if ($entries.Count -eq 0) { throw "デプロイ対象のファイルがありません: $Workspace" }

# 一時ファイルに zip を作ってから移動する（FileWatcher が書きかけを拾わないように）
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("App_" + [Guid]::NewGuid().ToString("N") + ".zip")
$fs = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew)
try {
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($e in $entries) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $e.Path, $e.Name) | Out-Null
        }
    } finally { $zip.Dispose() }
} finally { $fs.Dispose() }

Move-Item -Force $tmp $Destination
Write-Host "deployed: $($entries.Count) entries -> $Destination"
$entries | ForEach-Object { Write-Host ("  " + $_.Name) }
