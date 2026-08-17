# db-snapshot.ps1 — 実行環境データ（DB＋添付ファイル）のスナップショットを取る
#
# バグ狩り・修正作業の前後で「この操作で何が変わったか」を機械的に比較できるようにする。
# 保存先は LocalData\backup\snapshots\<yyyyMMdd_HHmmss>_<Name>\（LocalData 配下は Git 追跡外）。
#
# 使い方:
#   pwsh -NoProfile -File Designer/tools/db-snapshot.ps1 -Name baseline
#   pwsh -NoProfile -File Designer/tools/db-snapshot.ps1 -Name before-BUG-0059 -Note "C10 修正の直前"
#   pwsh -NoProfile -File Designer/tools/db-snapshot.ps1 -List
#
# 復元は db-restore.ps1。
#
# 注意: サーバ稼働中でもコピー自体は取れるが、書き込みの最中だと中途半端な状態を掴む。
#       WAL/SHM が残っている場合は一緒に退避する（SQLite の未チェックポイント分を落とさないため）。

param(
    [string]$Name = "manual",
    [string]$Note = "",
    [switch]$List
)

$ErrorActionPreference = "Stop"

$repoRoot  = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$dbPath    = Join-Path $repoRoot "LocalData\db\business-app_v1.db"
$storages  = Join-Path $repoRoot "LocalData\storages"
$snapRoot  = Join-Path $repoRoot "LocalData\backup\snapshots"

if ($List) {
    if (-not (Test-Path $snapRoot)) { Write-Host "スナップショットはまだありません"; return }
    Get-ChildItem -Path $snapRoot -Directory | Sort-Object Name | ForEach-Object {
        $manifest = Join-Path $_.FullName "manifest.json"
        $note = ""
        if (Test-Path $manifest) { $note = (Get-Content $manifest -Raw | ConvertFrom-Json).Note }
        $size = (Get-ChildItem -Path $_.FullName -Recurse -File | Measure-Object -Property Length -Sum).Sum
        "{0,-40} {1,8:N0} KB  {2}" -f $_.Name, ($size / 1KB), $note
    }
    return
}

if (-not (Test-Path $dbPath)) { throw "DB が見つかりません: $dbPath" }

# 名前に使えない文字を落とす（ディレクトリ名になるため）
$safeName = ($Name -replace '[^\w\-\.]', '_')
$stamp    = (Get-Date).ToString("yyyyMMdd_HHmmss")
$dest     = Join-Path $snapRoot "${stamp}_${safeName}"

New-Item -ItemType Directory -Path (Join-Path $dest "db") -Force | Out-Null

# DB 本体＋WAL/SHM（あれば）
Copy-Item $dbPath (Join-Path $dest "db") -Force
foreach ($suffix in @("-wal", "-shm")) {
    $side = $dbPath + $suffix
    if (Test-Path $side) { Copy-Item $side (Join-Path $dest "db") -Force }
}

# 添付ファイル（FileField の実体）。DB だけ戻すと参照が孤児になるので対で退避する
$storageCount = 0
if (Test-Path $storages) {
    Copy-Item $storages (Join-Path $dest "storages") -Recurse -Force
    $storageCount = (Get-ChildItem -Path $storages -Recurse -File).Count
}

$serverPid = Get-NetTCPConnection -LocalPort 5085 -State Listen -ErrorAction SilentlyContinue |
             Select-Object -First 1 -ExpandProperty OwningProcess
$gitHead = (& git -C $repoRoot rev-parse --short HEAD 2>$null)

$manifest = [ordered]@{
    Name          = $safeName
    Note          = $Note
    TakenAt       = (Get-Date).ToString("s")
    GitHead       = "$gitHead"
    ServerRunning = [bool]$serverPid
    DbBytes       = (Get-Item $dbPath).Length
    StorageFiles  = $storageCount
}
$manifest | ConvertTo-Json | Set-Content -Path (Join-Path $dest "manifest.json") -Encoding UTF8

$total = (Get-ChildItem -Path $dest -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("snapshot: {0}  ({1:N0} KB / 添付 {2} 件)" -f (Split-Path $dest -Leaf), ($total / 1KB), $storageCount)
if ($serverPid) { Write-Host "  警告: サーバ稼働中 (PID $serverPid) に取得した。書き込み中の状態を掴んでいる可能性がある" }
Write-Host "  復元: pwsh -NoProfile -File Designer/tools/db-restore.ps1 -Snapshot $(Split-Path $dest -Leaf)"
