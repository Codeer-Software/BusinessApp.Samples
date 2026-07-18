# deploy.ps1 — Designer ワークスペースを App.zip に固めて LocalData\designs へ配置する
#
# デザイナ GUI の「送信」の代替。FileWatcher が *.zip を検知して hot-reload する。
# zip エントリ名はデザイナ独自形式（バックスラッシュ区切り・ディレクトリエントリ無し・
# app.clprj はルート直下・designer.settings*.json は含めない）を再現する。
# 実測根拠: GUI 製 App.zip のエントリ一覧（2026-07-05 確認）
#   Modules\AppUser.mod.json / PageFrames\Main.frm.json / Resources\lc_logo_256.png / app.clprj
#
# 使い方: pwsh -File deploy.ps1 [-Workspace <path>] [-Destination <path>]
# 注意: *.mod.cs（スクリプト）変更・DB スキーマ変更の反映にはサーバ再起動が必要。

param(
    [string]$Workspace = (Join-Path $PSScriptRoot "..\Designer" | Resolve-Path),
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\LocalData\designs\App.zip")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path (Join-Path $Workspace "app.clprj"))) {
    throw "app.clprj が見つかりません: $Workspace はデザインワークスペースではありません"
}

# 含めるもの: app.clprj / app.css(あれば) / Modules/ PageFrames/ Resources/ 配下の全ファイル
# 含めないもの: designer.settings*.json（接続設定）・その他の作業ファイル
$entries = @()
foreach ($rootFile in @("app.clprj", "app.css")) {
    $p = Join-Path $Workspace $rootFile
    if (Test-Path $p) { $entries += @{ Path = $p; Name = $rootFile } }
}
foreach ($dir in @("Modules", "PageFrames", "Resources")) {
    $dirPath = Join-Path $Workspace $dir
    if (-not (Test-Path $dirPath)) { continue }
    Get-ChildItem -Path $dirPath -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($Workspace.Length + 1)  # 例 Modules\Sub\X.mod.json
        $entries += @{ Path = $_.FullName; Name = $rel }      # 区切りは \ のまま入れる
    }
}

# 一時ファイルに zip を作ってから移動（FileWatcher が書きかけを拾わないように）
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
