# db-restore.ps1 — db-snapshot.ps1 で取ったスナップショットへ実行環境データを巻き戻す
#
# 使い方:
#   pwsh -NoProfile -File Designer/tools/db-restore.ps1 -Snapshot 20260817_120000_baseline
#   pwsh -NoProfile -File Designer/tools/db-restore.ps1 -Snapshot latest -StopServer
#
# 既定ではサーバが 5085 で稼働していたら中断する（SQLite のファイルロックと
# 稼働中プロセスのキャッシュで、戻したつもりが戻らない事故を防ぐため）。
# -StopServer を付けると listener を落としてから戻す。**戻したあとサーバは自分で起動し直すこと。**
#
# 巻き戻す前の状態は自動で pre-restore スナップショットとして退避する（取り違えの保険）。

param(
    [Parameter(Mandatory = $true)][string]$Snapshot,
    [switch]$StopServer
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$dbPath   = Join-Path $repoRoot "LocalData\db\business-app_v1.db"
$storages = Join-Path $repoRoot "LocalData\storages"
$snapRoot = Join-Path $repoRoot "LocalData\backup\snapshots"

if (-not (Test-Path $snapRoot)) { throw "スナップショットが 1 つもありません: $snapRoot" }

if ($Snapshot -eq "latest") {
    $src = Get-ChildItem -Path $snapRoot -Directory | Sort-Object Name | Select-Object -Last 1
    if (-not $src) { throw "スナップショットが 1 つもありません" }
} else {
    $src = Get-Item (Join-Path $snapRoot $Snapshot) -ErrorAction SilentlyContinue
    if (-not $src) { throw "見つかりません: $Snapshot（一覧は db-snapshot.ps1 -List）" }
}

$srcDb = Join-Path $src.FullName "db\business-app_v1.db"
if (-not (Test-Path $srcDb)) { throw "スナップショットに DB がありません: $srcDb" }

$serverPid = Get-NetTCPConnection -LocalPort 5085 -State Listen -ErrorAction SilentlyContinue |
             Select-Object -First 1 -ExpandProperty OwningProcess
if ($serverPid) {
    if (-not $StopServer) {
        throw "サーバが稼働中 (PID $serverPid)。停止してから実行するか -StopServer を付けること"
    }
    Stop-Process -Id $serverPid -Force
    Start-Sleep -Milliseconds 1500
    Write-Host "stopped server (PID $serverPid)"
}

# 巻き戻す前の状態を保険で退避
& (Join-Path $PSScriptRoot "db-snapshot.ps1") -Name "pre-restore" -Note "db-restore 直前の自動退避" | Out-Null

# DB 本体。古い WAL/SHM が残っていると戻した DB と食い違うので必ず消す
foreach ($suffix in @("-wal", "-shm")) {
    $side = $dbPath + $suffix
    if (Test-Path $side) { Remove-Item $side -Force }
}
Copy-Item $srcDb $dbPath -Force
foreach ($suffix in @("-wal", "-shm")) {
    $side = Join-Path $src.FullName ("db\business-app_v1.db" + $suffix)
    if (Test-Path $side) { Copy-Item $side ($dbPath + $suffix) -Force }
}

# 添付ファイル
$srcStorages = Join-Path $src.FullName "storages"
if (Test-Path $srcStorages) {
    if (Test-Path $storages) { Remove-Item $storages -Recurse -Force }
    Copy-Item $srcStorages $storages -Recurse -Force
}

Write-Host "restored: $($src.Name)"
$manifest = Join-Path $src.FullName "manifest.json"
if (Test-Path $manifest) {
    $m = Get-Content $manifest -Raw | ConvertFrom-Json
    Write-Host ("  取得 {0} / HEAD {1} / 添付 {2} 件 / {3}" -f $m.TakenAt, $m.GitHead, $m.StorageFiles, $m.Note)
}
Write-Host "  サーバは停止したままなので、必要なら起動し直すこと:"
Write-Host "  dotnet run --project BusinessApp/BusinessApp.Server --launch-profile http"
