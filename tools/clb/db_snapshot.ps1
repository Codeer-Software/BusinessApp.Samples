<#
.SYNOPSIS
    稼働 DB の退避と復元。**稼働 DB と退避は消さず、上書きの前に必ず退避する。**

.DESCRIPTION
    自律作業で「壊れてもいい状態」を作るための道具である。取って・壊して・戻す、を
    確認を待たずに回せるようにする（docs/30_作業のルール.md §10・ADR-0046）。

      -Save     いまの稼働 DB の一貫した写しを LocalData/backup/<名前>.db に取る。
      -Restore  写しを稼働 DB へ戻す。**戻す前に現状を自動で退避**し、
                置き換える現物は消さずに LocalData/backup/_superseded/ へ改名して残す。
      -List     退避の一覧。
      -SelfTest この道具自身の検査（コミット前フックが流す）。稼働 DB には触れない。

    **なぜこの形なのか（VACUUM INTO で取る・止めてから戻す・何も消さない）は ADR-0046 が持つ。**
    ここに書くのは、**実装しないと分からないこと**だけである。

    **稼働 DB のパスは DB 自身に聞く**（PRAGMA database_list）。追跡外の設定ファイルを
    このスクリプトが解釈すると、書式が変わったときに黙ってずれる（_designer.ps1 と同じ考え）。

    **写しが健全かを、取ったときと戻す前の 2 回確かめる**（ATTACH して PRAGMA integrity_check）。
    一次情報が「VACUUM INTO は中断されると不完全で壊れた出力になりうる」と書いているためである
    （https://www.sqlite.org/lang_vacuum.html 確認日 2026-09-08）。

    **戻すときはサーバが止まっていることを求める。判定は 2 つある。**
      ① BusinessApp.Server のプロセスが居ないこと。
         **これが主である**——2026-09-08 の実測で、サーバ（PID あり）が動いている最中に
         本体を FileShare.None で開いたところ**成功した**。つまり②だけでは
         「サーバが動いている」を検出できない。
      ② DB ファイルと連れ（-wal / -shm / -journal）を排他で開けること。
         **いま書いている相手を捕まえるための保険である。何を捕まえられるかは未計測**——
         デザイナ exe で試していない。
    **現物を退ける直前に、もう一度確かめる**——最初の検査から時間が経つため。
    **それでも、検査を通った直後に開かれたら検出できない**（時間の穴は残る）。
    どのみち**戻したあとはサーバとデザイナの再起動が要る**——CLB は列定義を static に
    キャッシュするため（migrate.ps1 と同じ理由）。

    **パスの検査はすべて -LiteralPath で行う。** Test-Path の既定はワイルドカードを解釈するので、
    パスに [ ] が入ると「無い」と判定し、退避を飛ばして上書きだけ実行してしまう
    （trash.ps1 が先に塞いだ穴。docs/qa/03）。

    **稼働 DB とその連れ・退避は、何も消さない。** 置き換える現物は改名して残す。
    **例外は -SelfTest が自分で作った一時フォルダだけ**で、これは repo の外にあり、
    この道具が作ったものである（trash.ps1 の同じ断りに倣う——docs/30 §10 の規則は
    Claude が打つコマンドを縛るもので、道具が自分の後始末をすることまでは縛らない）。

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
    [switch]$SelfTest,
    [string]$Name,
    [string]$DataSource = 'BusinessAppSQLite'
)

$ErrorActionPreference = 'Stop'

$selected = @($Save, $Restore, $List, $SelfTest) | Where-Object { $_ }
if ($selected.Count -ne 1) { throw '-Save / -Restore / -List / -SelfTest のどれか 1 つを指定する。' }

. (Join-Path $PSScriptRoot '_designer.ps1')

$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:BackupDir = Join-Path $script:RepoRoot 'LocalData/backup'
$script:SupersededDir = Join-Path $script:BackupDir '_superseded'

# 稼働中のサーバの実行ファイル名。**dotnet run は apphost を起こす**ので dotnet.exe ではない。
$ServerProcessName = 'BusinessApp.Server'

# 本体のほかに SQLite が作るファイル。**戻すときはこれらも一緒に片付ける**——
# 古いジャーナルが残っていると、戻した本体にそれが再生されて壊れる。
$SidecarSuffixes = @('-wal', '-shm', '-journal')

# ------------------------------------------------------------------ 小道具

function Format-ForDisplay {
    <#  表示用に repo からの相対へ畳む。**畳めないものはそのまま返す。**
        絶対パスにはユーザー名が入り、貼ると公開リポジトリへ混ざる（CLAUDE.md §5）。 #>
    param([Parameter(Mandatory)][string]$FullPath)

    $root = [System.IO.Path]::GetFullPath($script:RepoRoot).TrimEnd('\', '/')
    $full = [System.IO.Path]::GetFullPath($FullPath)
    if ($full.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar,
                         [System.StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($root.Length + 1).Replace('\', '/')
    }
    # repo の外（稼働 DB の置き場を移した環境）では、親を伏せて名前だけ出す。
    return '…/' + (Split-Path -Leaf $full)
}

function Test-FileExists {
    param([Parameter(Mandatory)][string]$Path)
    return Test-Path -LiteralPath $Path -PathType Leaf
}

function ConvertTo-SqlLiteral {
    param([Parameter(Mandatory)][string]$Value)
    # SQLite の文字列リテラルで特別なのは ' だけ（バックスラッシュはただの文字）。
    return "'" + $Value.Replace("'", "''") + "'"
}

function Invoke-Sql {
    param([Parameter(Mandatory)][string]$Sql)
    # 中身は _designer.ps1 が持つ（migrate.ps1 と同じ 1 つを使う。docs/20 §4）。
    return Invoke-DesignerSql -RepoRoot $script:RepoRoot -DataSource $DataSource -Sql $Sql
}

function Get-LiveDbPath {
    $result = Invoke-Sql 'PRAGMA database_list;'
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
    return Join-Path $script:BackupDir "$SnapshotName.db"
}

function Assert-SnapshotSound {
    <#  写しが SQLite として読めるか。**ATTACH して integrity_check を通す。**
        Test-Path だけでは、0 バイトや途中で切れた出力が素通りする。 #>
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-FileExists $Path)) { throw "写しが無い: $(Format-ForDisplay $Path)" }
    if ((Get-Item -LiteralPath $Path).Length -eq 0) {
        throw "写しが空である: $(Format-ForDisplay $Path)"
    }

    $literal = ConvertTo-SqlLiteral $Path
    $result = Invoke-Sql "ATTACH DATABASE $literal AS probe; PRAGMA probe.integrity_check; DETACH DATABASE probe;"
    $rows = @($result.results | Where-Object { $_.columns -contains 'integrity_check' })
    if ($rows.Count -ne 1) { throw "写しの検査結果を読めなかった: $(Format-ForDisplay $Path)" }
    $verdict = [string]$rows[0].rows[0].integrity_check
    if ($verdict -ne 'ok') {
        throw "写しが壊れている（$verdict）: $(Format-ForDisplay $Path)"
    }
}

function Save-Snapshot {
    param([Parameter(Mandatory)][string]$SnapshotName)

    if (-not (Test-Path -LiteralPath $script:BackupDir)) {
        New-Item -ItemType Directory -Path $script:BackupDir | Out-Null
    }

    $destination = Get-SnapshotPath -SnapshotName $SnapshotName
    # **既にある退避は上書きしない。** VACUUM INTO 自身も既存ファイルを拒むが、
    # ここで先に断ると「どの名前が塞がっているか」を言葉で返せる。
    if (Test-Path -LiteralPath $destination) {
        throw "同じ名前の退避が既にある: $(Format-ForDisplay $destination)（別の名前にする。退避は上書きしない）"
    }

    Invoke-Sql "VACUUM INTO $(ConvertTo-SqlLiteral $destination);" | Out-Null
    Assert-SnapshotSound -Path $destination
    return Get-Item -LiteralPath $destination
}

function Assert-NotInUse {
    param([Parameter(Mandatory)][string]$LiveDbPath)

    $running = @(Get-Process -Name $ServerProcessName -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        throw "$ServerProcessName が動いている（PID $($running.Id -join ', ')）。" +
              '戻す前に止めること。どのみち戻したあとに再起動が要る（CLB は列定義を static にキャッシュする）。'
    }

    # 本体と連れを排他で開けるか。開けない相手（デザイナ exe など）が居るなら、書き換えてはいけない。
    foreach ($suffix in @('') + $SidecarSuffixes) {
        $path = $LiveDbPath + $suffix
        if (-not (Test-FileExists $path)) { continue }
        try {
            $stream = [System.IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
            $stream.Close()
        } catch {
            throw "稼働 DB を誰かが開いている: $(Format-ForDisplay $path)（$($_.Exception.Message)）"
        }
    }
}

function Move-LiveAside {
    <#  本体と連れを `_superseded/<日時>/` へ改名して退ける。**1 つも無ければフォルダも作らない。**
        途中で落ちたら、動かした分を戻してから投げ直す——本体だけ消えて古いジャーナルが残る、
        という一番危ない形を作らないため。 #>
    param([Parameter(Mandatory)][string]$LiveDbPath, [Parameter(Mandatory)][string]$Stamp)

    $sources = @()
    foreach ($suffix in @('') + $SidecarSuffixes) {
        $path = $LiveDbPath + $suffix
        if (Test-FileExists $path) { $sources += $path }
    }
    if ($sources.Count -eq 0) { return $null }

    $destination = Join-Path $script:SupersededDir $Stamp
    New-Item -ItemType Directory -Path $destination -Force | Out-Null

    $moved = @()
    try {
        foreach ($source in $sources) {
            $target = Join-Path $destination (Split-Path -Leaf $source)
            Move-Item -LiteralPath $source -Destination $target
            $moved += , @($target, $source)
        }
    } catch {
        foreach ($pair in $moved) { Move-Item -LiteralPath $pair[0] -Destination $pair[1] }
        throw
    }
    return [pscustomobject]@{
        Directory = $destination
        Moved     = $moved
        Files     = @($sources | ForEach-Object { Split-Path -Leaf $_ })
    }
}

function Restore-Aside {
    <#  退けたものを元の場所へ戻す（復元が途中で落ちたときの巻き戻し）。 #>
    param($Aside)
    if (-not $Aside) { return }
    foreach ($pair in $Aside.Moved) {
        if (Test-FileExists $pair[0]) { Move-Item -LiteralPath $pair[0] -Destination $pair[1] -Force }
    }
}

# ------------------------------------------------------------------ 自己検査

function Invoke-SelfTest {
    <#  **稼働 DB には触れない。** 名前の検査と、ファイルを動かす手順だけを一時フォルダで確かめる。

        **SQL を要する部分はここでは扱えない。** VACUUM INTO と integrity_check を通した
        -Save・-Restore の実機確認は ADR-0046 が記録している。
        **書き込みが走っている最中の -Save は未計測である**——「稼働中でも一貫している」は
        一次情報（sqlite.org）に依っており、この repo で測ってはいない。 #>
    $failed = 0
    function Fail([string]$Message) { $script:selfTestFailed++; Write-Host "NG  $Message" }
    $script:selfTestFailed = 0

    # --- 名前の検査。**通す形と断る形の両方を置く**（片方だけだと素通りに気づけない）
    $valid = @('a', 'probe-01', 'before-restore_20260908-205101', 'A.b_c-1', ('x' * 64))
    $invalid = @('', '-lead', '_lead', '.lead', '../escape', 'a/b', 'a\b', 'あ', 'a b', ('x' * 65))
    foreach ($name in $valid) {
        try { Assert-SnapshotName -Value $name } catch { Fail "通すはずの名前を断った: '$name'" }
    }
    foreach ($name in $invalid) {
        $rejected = $false
        try { Assert-SnapshotName -Value $name } catch { $rejected = $true }
        if (-not $rejected) { Fail "断るはずの名前を通した: '$name'" }
    }

    # --- ファイルを動かす手順。**角括弧を含むパスで試す**——
    # Test-Path の既定はこれをワイルドカードと読むので、退避を飛ばして上書きだけ走る穴になる。
    $sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("db_snapshot_selftest_" + [guid]::NewGuid().ToString('N'))
    $originalBackup = $script:BackupDir
    $originalSuperseded = $script:SupersededDir
    try {
        $script:BackupDir = Join-Path $sandbox 'backup'
        $script:SupersededDir = Join-Path $script:BackupDir '_superseded'
        $dbDir = Join-Path $sandbox 'db[1]'
        New-Item -ItemType Directory -Path $dbDir -Force | Out-Null
        $live = Join-Path $dbDir 'app.db'

        # 現物が 1 つも無ければ、空のフォルダを作らない
        if ($null -ne (Move-LiveAside -LiveDbPath $live -Stamp 'none')) { Fail '現物が無いのに退けたと言った' }
        if (Test-Path -LiteralPath (Join-Path $script:SupersededDir 'none')) {
            Fail '現物が無いのに _superseded のフォルダを作った'
        }

        # 本体と連れを、すべて退ける。
        # **期待する連れの名前は、ここに書き下す。** 実装の $SidecarSuffixes を読んで作ると、
        # リストを縮めたときに検体も一緒に縮んで同語反復になる（自己レビューで実測。2026-09-08）。
        $expectedSidecars = @('-wal', '-shm', '-journal')
        $extra = @($SidecarSuffixes | Where-Object { $_ -notin $expectedSidecars })
        if ($extra.Count -gt 0) { Fail "連れが増えている（検体にも足すこと）: $($extra -join ', ')" }

        Set-Content -LiteralPath $live -Value 'body' -NoNewline
        foreach ($suffix in $expectedSidecars) {
            Set-Content -LiteralPath ($live + $suffix) -Value $suffix -NoNewline
        }
        $aside = Move-LiveAside -LiveDbPath $live -Stamp 'probe'
        if ($null -eq $aside) { Fail '現物があるのに退けなかった（角括弧のパスを見失っている）' }
        foreach ($suffix in @('') + $expectedSidecars) {
            if (Test-FileExists ($live + $suffix)) { Fail "退けたはずの $suffix が残っている" }
            if (-not (Test-FileExists (Join-Path $aside.Directory (Split-Path -Leaf ($live + $suffix))))) {
                Fail "退けた先に $suffix が無い（消してしまっている）"
            }
        }

        # 巻き戻すと、すべて元の場所へ戻る
        Restore-Aside -Aside $aside
        foreach ($suffix in @('') + $expectedSidecars) {
            if (-not (Test-FileExists ($live + $suffix))) { Fail "巻き戻しで $suffix が戻っていない" }
        }
        if ((Get-Content -LiteralPath $live -Raw) -ne 'body') { Fail '巻き戻した本体の中身が違う' }

        # 途中で落ちたら、動かした分をすべて戻す。**連れの 1 つを掴んで Move-Item を失敗させる。**
        # ここが働かないと、本体だけ消えて古いジャーナルが残る——一番危ない形になる。
        Set-Content -LiteralPath $live -Value 'body' -NoNewline
        foreach ($suffix in $expectedSidecars) {
            Set-Content -LiteralPath ($live + $suffix) -Value $suffix -NoNewline
        }
        $locked = [System.IO.File]::Open($live + $expectedSidecars[1], 'Open', 'ReadWrite', 'None')
        try {
            $threw = $false
            try { Move-LiveAside -LiveDbPath $live -Stamp 'partial' | Out-Null } catch { $threw = $true }
            if (-not $threw) { Fail '掴まれている連れがあるのに、退けたと言った' }
        } finally {
            $locked.Close()
        }
        foreach ($suffix in @('') + $expectedSidecars) {
            if (-not (Test-FileExists ($live + $suffix))) {
                Fail "途中で落ちたのに $suffix が戻っていない（巻き戻しが働いていない）"
            }
        }

        # 壊れた写しを断る（0 バイト）。**理由まで見る**——別の理由で落ちても「断った」に見えるため。
        $empty = Join-Path $sandbox 'empty.db'
        New-Item -ItemType File -Path $empty | Out-Null
        $message = $null
        try { Assert-SnapshotSound -Path $empty } catch { $message = $_.Exception.Message }
        if (-not $message) { Fail '空の写しを健全だと言った' }
        elseif ($message -notlike '*空*') { Fail "空の写しを別の理由で断っている: $message" }
    } finally {
        $script:BackupDir = $originalBackup
        $script:SupersededDir = $originalSuperseded
        if (Test-Path -LiteralPath $sandbox) {
            [System.IO.Directory]::Delete($sandbox, $true)  # 一時フォルダの後始末（repo の外）
        }
    }

    $failed = $script:selfTestFailed
    if ($failed -eq 0) { Write-Host 'db_snapshot: すべて期待どおり' } else { Write-Host "db_snapshot: $failed 件が期待と違う" }
    return $failed
}

# ------------------------------------------------------------------ 入口

if ($SelfTest) { exit (Invoke-SelfTest) }

if ($Save) {
    if (-not $Name) { $Name = 'snapshot_' + (Get-Date -Format 'yyyyMMdd-HHmmss') }
    Assert-SnapshotName -Value $Name
    $item = Save-Snapshot -SnapshotName $Name
    Write-Host "退避した: $(Format-ForDisplay $item.FullName)（$([int][math]::Round($item.Length / 1KB)) KB）"
    exit 0
}

if ($List) {
    if (-not (Test-Path -LiteralPath $script:BackupDir)) {
        Write-Host "退避はまだ 1 つも無い: $(Format-ForDisplay $script:BackupDir)"
        exit 0
    }

    $snapshots = @(Get-ChildItem -LiteralPath $script:BackupDir -Filter '*.db' -File | Sort-Object LastWriteTime)
    if ($snapshots.Count -eq 0) {
        Write-Host "退避はまだ 1 つも無い: $(Format-ForDisplay $script:BackupDir)"
    } else {
        $snapshots |
            Select-Object @{ n = '名前'; e = { $_.BaseName } },
                          @{ n = 'KB'; e = { [int][math]::Round($_.Length / 1KB) } },
                          @{ n = '取った日時'; e = { $_.LastWriteTime.ToString('yyyy/MM/dd HH:mm:ss') } } |
            Format-Table -AutoSize
    }

    if (Test-Path -LiteralPath $script:SupersededDir) {
        $aside = @(Get-ChildItem -LiteralPath $script:SupersededDir -Directory)
        if ($aside.Count -gt 0) {
            Write-Host "置き換える前の現物が $($aside.Count) 組残っている: $(Format-ForDisplay $script:SupersededDir)"
        }
    }
    exit 0
}

# --- -Restore
if (-not $Name) { throw '-Restore には -Name が要る（-List で名前を見る）。' }
Assert-SnapshotName -Value $Name

$snapshot = Get-SnapshotPath -SnapshotName $Name
if (-not (Test-FileExists $snapshot)) {
    throw "その名前の退避が無い: $(Format-ForDisplay $snapshot)（-List で名前を見る）"
}

$livePath = Get-LiveDbPath
Assert-NotInUse -LiveDbPath $livePath

# **戻す前に、いまの状態を必ず退避する。** 戻しすぎたことに気づいたときの戻り先である。
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if (Test-FileExists $livePath) {
    $auto = Save-Snapshot -SnapshotName "before-restore_$stamp"
    Write-Host "戻す前の状態を退避した: $(Format-ForDisplay $auto.FullName)"
}

# **戻す写しが健全かを、現物に触れる前に確かめる。** SQL を使うので、稼働 DB がまだ在る間に行う。
Assert-SnapshotSound -Path $snapshot

# **先に複製を作ってから現物を退ける。** 逆にすると、複製で落ちたときに稼働 DB が無い状態で残る。
$staged = $livePath + '.restoring'
if (Test-Path -LiteralPath $staged) { throw "前回の復元の残骸がある: $(Format-ForDisplay $staged)" }
Copy-Item -LiteralPath $snapshot -Destination $staged

$aside = $null
try {
    Assert-NotInUse -LiveDbPath $livePath   # 退ける直前にもう一度（最初の検査から時間が経っている）
    $aside = Move-LiveAside -LiveDbPath $livePath -Stamp $stamp
    Move-Item -LiteralPath $staged -Destination $livePath
} catch {
    Restore-Aside -Aside $aside
    if (Test-Path -LiteralPath $staged) { Move-Item -LiteralPath $staged -Destination ($staged + '.failed') -Force }
    throw
}

if ($aside) {
    Write-Host "現物を退けた: $(Format-ForDisplay $aside.Directory)（$($aside.Files -join ', ')）"
}
Write-Host "戻した: $Name → $(Format-ForDisplay $livePath)"
Write-Host 'サーバとデザイナを再起動すること（CLB は列定義を static にキャッシュする）。'
Write-Host 'そのあと migrate.ps1 -Verify を打つこと（古い退避を戻すとスキーマが巻き戻る）。'
