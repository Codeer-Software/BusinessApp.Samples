# refresh-ai-workspace.ps1（ルート版ラッパ）
#
# 目的: Designer ワークスペースが自前で持つ同名スクリプト
#       （Designer/.claude/refresh-ai-workspace.ps1。デザイナが生成・更新する）を、
#       正しい作業ディレクトリで呼び出す。
#
# なぜラッパが要るか:
#   デザイナ生成版は 'ClaudeCodeForDesigner/_ai_refresh.stamp' や '<Project>/app.clprj' を
#   **相対パス**で参照する。Claude Code のフックはリポジトリルートを CWD として実行されるため、
#   ルートから直接呼ぶと必ず空振りし、デザイナや拡張ライブラリを再ビルドしても
#   ClaudeCodeForDesigner/（フィールドカタログ等の生成リファレンス）が更新されない。
#   ここで Designer/ に移動してから呼ぶことで、生成版に手を入れずに済ませる
#   （Designer/.claude/ はデザイナが再生成する領域なので触らない）。
#
# 使い方（フックから）:
#   powershell -NoProfile -ExecutionPolicy Bypass -File ".claude/refresh-ai-workspace.ps1" "<デザイナexeのパス>" "Design"
#
# 常に exit 0（セッション／プロンプトをブロックしない）。

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Project = 'Design'
)

$ErrorActionPreference = 'SilentlyContinue'

$workspace = Join-Path $PSScriptRoot '..\Designer'
if (-not (Test-Path -LiteralPath $workspace)) { exit 0 }

$inner = Join-Path $workspace '.claude\refresh-ai-workspace.ps1'
if (-not (Test-Path -LiteralPath $inner)) { exit 0 }

Push-Location -LiteralPath $workspace
try {
    & $inner -Exe $Exe -Project $Project
}
finally {
    Pop-Location
}

exit 0
