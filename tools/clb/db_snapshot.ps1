<#
.SYNOPSIS
    稼働 DB の退避と復元。**何も消さず、上書きの前に必ず退避する。**

.DESCRIPTION
    自律作業で「壊れてもいい状態」を作るための道具である。取って・壊して・戻す、を
    確認を待たずに回せるようにする（docs/30_作業のルール.md §10）。

      -Save     いまの稼働 DB の一貫した写しを LocalData/backup/<名前>.db に取る。
      -Restore  写しを稼働 DB へ戻す。**戻す前に現状を自動で退避**し、
                置き換える現物は消さずに LocalData/backup/_superseded/ へ改名して残す。
      -List     退避の一覧。

    **写しは VACUUM INTO で取る。ファイルを cp しない。**
    SQLite は書き込み中のページを本体の外（ロールバックジャーナル、WAL なら -wal）に置くので、
    本体だけを複製すると**直前の更新が欠けた DB** ができる。しかも読めてしまうので、
    壊れたことに気づくのは「戻したのに数字が合わない」ときである。
    VACUUM INTO は 1 つのトランザクションで完結した写しを書き出すので、稼働中でも一貫している。

    **稼働 DB のパスは DB 自身に聞く**（PRAGMA database_list）。追跡外の設定ファイルを
    このスクリプトが解釈すると、書式が変わったときに黙ってずれる（_designer.ps1 と同じ考え）。

    **戻すときはサーバが止まっていることを求める。**
    判定は 2 つで、どちらか一方では足りない（2026-09-08 に実測）。
      ① BusinessApp.Server のプロセスが居ないこと。
         **稼働中でも DB ファイルは排他で開けてしまう**ので、ファイルのロックだけでは
         「サーバが動いている」を検出できない（接続を握りっぱなしにしていない）。
      ② DB ファイルを排他で開けること。デザイナ exe など、いま書いている相手を捕まえる。
    どのみち**戻したあとはサーバとデザイナの再起動が要る**——CLB は列定義を static に
    キャッシュするため（migrate.ps1 と同じ理由）。

.PARAMETER Name
    退避の名前。-Save では省略でき、そのとき snapshot_<日時> になる。-Restore では必須。

.PARAMETER DataSource
    designer.settings.json の DataSources の Name。

.EXAMPLE
    pwsh -NoProfile -File tools/clb/db_snapshot.ps1 -Save -Name before-migration-0023

.EXAMPLE
    pwsh -NoProfile -File tools/clb/db_snapshot.ps1 -Restore -Name before-migration-0023

.EXAMPLE
    pwsh -NoProfile -File tools/clb/db_snapshot.ps1 -List
#>
[CmdletBinding()]
param(
    [switch]$Save,
    [switch]$Restore,
    [switch]$List,
    [string]$Name,
    [string]$DataSource = 'BusinessAppSQLite'
)

$ErrorActionPreference = 'Stop'

$selected = @($Save, $Restore, $List) | Where-Object { $_ }
if ($selected.Count -ne 1) { throw '-Save / -Restore / -List のどれか 1 つを指定する。' }

. (Join-Path $PSScriptRoot '_designer.ps1')

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectRoot = Join-Path $repoRoot 'Designer/Design'
$backupDir = Join-Path $repoRoot 'LocalData/backup'
$supersededDir = Join-Path $backupDir '_superseded'

# 稼働中のサーバの実行ファイル名。**dotnet run は apphost を起こす**ので dotnet.exe ではない。
$ServerProcessName = 'BusinessApp.Server'

# 本体のほかに SQLite が作るファイル。**戻すときはこれらも一緒に片付ける**——
# 古いジャーナルが残っていると、戻した本体にそれが再生されて壊れる。
$SidecarSuffixes = @('-wal', '-shm', '-journal')

function Invoke-SqlQuery {
    param([Parameter(Mandatory)][string]$Sql)

    $exe = Get-DesignerExePath -RepoRoot $repoRoot
    $result = Invoke-Designer -Exe $exe -Arguments @('sql', $projectRoot, '--datasource', $DataSource, '--query', $Sql)
    # 終了コードの判定が先。失敗時の標準出力は JSON とは限らず、パース例外で本当の原因が隠れる。
    if ($result.ExitCode -ne 0) {
        $reason = $result.StandardOutput
        try { $reason = ($result.StandardOutput | ConvertFrom-Json).error } catch {}
        throw "SQL が失敗した: $reason"
    }
    return $result.StandardOutput | ConvertFrom-Json
}

function ConvertTo-SqlLiteral {
    param([Parameter(Mandatory)][string]$Value)
    # SQLite の文字列リテラルで特別なのは ' だけ（バックスラッシュはただの文字）。
    return "'" + $Value.Replace("'", "''") + "'"
}

function Get-LiveDbPath {
    $result = Invoke-SqlQuery 'PRAGMA database_list;'
    $main = @($result.results[0].rows | Where-Object { $_.name -eq 'main' })
    if ($main.Count -ne 1) { throw 'PRAGMA database_list が main を 1 つ返さなかった。' }
    $path = [string]$main[0].file
    if (-not $path) { throw '稼働 DB のパスを取れなかった（インメモリの接続先かもしれない）。' }
    return $path
}

function Assert-SnapshotName {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)
    # 区切り文字を弾く。名前がパスとして解釈されると、退避の外へ書き出せてしまう。
    if ($Value -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') {
        throw "名前は英数字で始まる 1〜64 文字（英数字・. _ -）にする: '$Value'"
    }
}

function Get-SnapshotPath {
    param([Parameter(Mandatory)][string]$SnapshotName)
    return Join-Path $backupDir "$SnapshotName.db"
}

function Save-Snapshot {
    param([Parameter(Mandatory)][string]$SnapshotName)

    if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir | Out-Null }

    $destination = Get-SnapshotPath -SnapshotName $SnapshotName
    # **既にある退避は上書きしない。** VACUUM INTO 自身も既存ファイルを拒むが、
    # ここで先に断ると「どの名前が塞がっているか」を利用者の言葉で返せる。
    if (Test-Path $destination) {
        throw "同じ名前の退避が既にある: $destination（別の名前にする。退避は上書きしない）"
    }

    Invoke-SqlQuery "VACUUM INTO $(ConvertTo-SqlLiteral $destination);" | Out-Null
    if (-not (Test-Path $destination)) { throw "VACUUM INTO が写しを作らなかった: $destination" }
    return Get-Item $destination
}

function Assert-ServerStopped {
    param([Parameter(Mandatory)][string]$LiveDbPath)

    $running = @(Get-Process -Name $ServerProcessName -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        throw "$ServerProcessName が動いている（PID $($running.Id -join ', ')）。" +
              '戻す前に止めること。どのみち戻したあとに再起動が要る（CLB は列定義を static にキャッシュする）。'
    }

    if (-not (Test-Path $LiveDbPath)) { return }

    # 排他で開けるか。開けない相手（デザイナ exe など）が居るなら、書き換えてはいけない。
    try {
        $stream = [System.IO.File]::Open($LiveDbPath, 'Open', 'ReadWrite', 'None')
        $stream.Close()
    } catch {
        throw "稼働 DB を誰かが開いている: $LiveDbPath（$($_.Exception.Message)）"
    }
}

function Move-LiveAside {
    param([Parameter(Mandatory)][string]$LiveDbPath, [Parameter(Mandatory)][string]$Stamp)

    $destination = Join-Path $supersededDir $Stamp
    New-Item -ItemType Directory -Path $destination -Force | Out-Null

    $moved = @()
    foreach ($suffix in @('') + $SidecarSuffixes) {
        $source = $LiveDbPath + $suffix
        if (Test-Path $source) {
            Move-Item -Path $source -Destination (Join-Path $destination (Split-Path -Leaf $source))
            $moved += (Split-Path -Leaf $source)
        }
    }
    return [pscustomobject]@{ Directory = $destination; Files = $moved }
}

if ($Save) {
    if (-not $Name) { $Name = 'snapshot_' + (Get-Date -Format 'yyyyMMdd-HHmmss') }
    Assert-SnapshotName -Value $Name
    $item = Save-Snapshot -SnapshotName $Name
    Write-Host "退避した: $($item.FullName)（$([math]::Round($item.Length / 1KB)) KB）"
    exit 0
}

if ($List) {
    if (-not (Test-Path $backupDir)) { Write-Host "退避はまだ 1 つも無い: $backupDir"; exit 0 }

    $snapshots = @(Get-ChildItem -Path $backupDir -Filter '*.db' -File | Sort-Object LastWriteTime)
    if ($snapshots.Count -eq 0) {
        Write-Host "退避はまだ 1 つも無い: $backupDir"
    } else {
        $snapshots |
            Select-Object @{ n = '名前'; e = { $_.BaseName } },
                          @{ n = 'KB'; e = { [int][math]::Round($_.Length / 1KB) } },
                          @{ n = '取った日時'; e = { $_.LastWriteTime.ToString('yyyy/MM/dd HH:mm:ss') } } |
            Format-Table -AutoSize
    }

    if (Test-Path $supersededDir) {
        $aside = @(Get-ChildItem -Path $supersededDir -Directory)
        if ($aside.Count -gt 0) {
            Write-Host "置き換える前の現物が $($aside.Count) 組残っている: $supersededDir"
        }
    }
    exit 0
}

# --- -Restore
if (-not $Name) { throw '-Restore には -Name が要る（-List で名前を見る）。' }
Assert-SnapshotName -Value $Name

$snapshot = Get-SnapshotPath -SnapshotName $Name
if (-not (Test-Path $snapshot)) { throw "その名前の退避が無い: $snapshot（-List で名前を見る）" }

$livePath = Get-LiveDbPath
Assert-ServerStopped -LiveDbPath $livePath

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

# **戻す前に、いまの状態を必ず退避する。** 戻しすぎたことに気づいたときの戻り先である。
if (Test-Path $livePath) {
    $auto = Save-Snapshot -SnapshotName "before-restore_$stamp"
    Write-Host "戻す前の状態を退避した: $($auto.FullName)"
}

# **消さずに改名して残す。** ジャーナルを置き去りにすると、戻した本体に再生されて壊れる。
$aside = Move-LiveAside -LiveDbPath $livePath -Stamp $stamp
if ($aside.Files.Count -gt 0) {
    Write-Host "現物を退けた: $($aside.Directory)（$($aside.Files -join ', ')）"
}

Copy-Item -Path $snapshot -Destination $livePath
Write-Host "戻した: $Name → $livePath"
Write-Host 'サーバとデザイナを再起動すること（CLB は列定義を static にキャッシュする）。'
