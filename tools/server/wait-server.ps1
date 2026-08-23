# wait-server.ps1 — 開発サーバの起動を待つ
#
# 使い方: pwsh -NoProfile -File tools/server/wait-server.ps1 [-TimeoutSec 60]
# 終了コード: 0 = 起動確認 / 1 = タイムアウト

param(
    [string]$Url = "http://localhost:5085",
    [int]$TimeoutSec = 60
)

$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { Write-Host "server up: $Url"; exit 0 }
    } catch { Start-Sleep -Seconds 3 }
}
Write-Host "server not up after ${TimeoutSec}s: $Url"
exit 1
