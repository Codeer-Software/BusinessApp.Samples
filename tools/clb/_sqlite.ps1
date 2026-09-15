<#
.SYNOPSIS
    稼働 DB を触る道具が共有する小道具（db_snapshot.ps1・worktree_db.ps1）。

.DESCRIPTION
    **同じ事実を 2 か所に置かない**（docs/20_実装の原則.md §4）。
    2026-09-16 に worktree_db.ps1 を足すとき、db_snapshot.ps1 から出した——
    そのまま書くと Format-ForDisplay が**3 本目の写しになり**（trash.ps1 に 1 本ある。
    docs/README.md の保留リストが「次にどちらかを触るとき」と置いていた）、
    Test-FileExists・ConvertTo-SqlLiteral・退避と巻き戻しが 2 本目になるためである。

    **ここに置くのは「どの道具でも同じもの」だけ**である。
    **行き先を許すかどうかの判断は、それぞれの道具が持つ**——
    db_snapshot.ps1 は行き先を持たず、worktree_db.ps1 は .DESCRIPTION に正典を置いている。

    **tools/claude/trash.ps1 の Format-ForDisplay は写しのまま残る。**
    あちらは SQLite に触れず tools/claude/ にあるので、この 1 本を読ませると
    無関係な依存ができる。**2 本に減った**ことを docs/README.md の保留に書いてある。
#>

# 本体のほかに SQLite が作るファイル。**片付けるときはこれらも一緒に扱う**——
# 古いジャーナルが残っていると、置いた本体にそれが再生されて壊れる。
$script:SqliteSidecarSuffixes = @('-wal', '-shm', '-journal')

function Format-ForDisplay {
    <#  表示用に repo からの相対へ畳む。**畳めないものはそのまま返さない。**
        絶対パスにはユーザー名が入り、貼ると公開リポジトリへ混ざる（CLAUDE.md §5）。 #>
    param(
        [Parameter(Mandatory)][string]$FullPath,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $root = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
    $full = [System.IO.Path]::GetFullPath($FullPath)
    if ($full.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar,
                         [System.StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($root.Length + 1).Replace('\', '/')
    }
    # repo の外（稼働 DB の置き場を移した環境）では、親を伏せて名前だけ出す。
    return '…/' + (Split-Path -Leaf $full)
}

function Test-FileExists {
    <#  **必ず -LiteralPath で見る。** Test-Path の既定はワイルドカードを解釈するので、
        パスに [ ] が入ると「無い」と判定し、退避を飛ばして上書きだけ実行してしまう
        （trash.ps1 が先に塞いだ穴。docs/qa/03）。 #>
    param([Parameter(Mandatory)][string]$Path)
    return Test-Path -LiteralPath $Path -PathType Leaf
}

function ConvertTo-SqlLiteral {
    param([Parameter(Mandatory)][string]$Value)
    # SQLite の文字列リテラルで特別なのは ' だけ（バックスラッシュはただの文字）。
    return "'" + $Value.Replace("'", "''") + "'"
}

function Get-FreePath {
    <#  まだ誰も使っていない名前を返す。**既にあるものを上書きしない**ための小道具で、
        塞がっていたら `-2`・`-3`… と足していく。

        **日時だけの名前では足りない**——同じ秒に 2 度退けると衝突し、
        「既に存在するファイルを作成することはできません」で死ぬ（2026-09-16 に検体が捕まえた）。 #>
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $Path }
    for ($n = 2; $n -lt 1000; $n++) {
        $candidate = "$Path-$n"
        if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    throw "空いている名前が見つからない: $Path"
}

function Get-DbFamily {
    <#  本体と、実在する連れのパスを並べて返す。無ければ空の配列。 #>
    param([Parameter(Mandatory)][string]$DbPath)

    $found = @()
    foreach ($suffix in @('') + $script:SqliteSidecarSuffixes) {
        $path = $DbPath + $suffix
        if (Test-FileExists $path) { $found += $path }
    }
    # **カンマで包まない。** 空のとき `,@()` は「空の配列を 1 つ含む配列」になり、
    # 呼ぶ側の Count が 1 になる（2026-09-16 に自己検査が捕まえた）。
    return $found
}

function Assert-DbSound {
    <#  写しが SQLite として読めるか。**ATTACH して integrity_check を通す。**
        Test-Path だけでは、0 バイトや途中で切れた出力が素通りする。
        一次情報が「VACUUM INTO は中断されると不完全で壊れた出力になりうる」と書いている
        （https://www.sqlite.org/lang_vacuum.html 確認日 2026-09-08）。 #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][scriptblock]$SqlRunner,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $shown = Format-ForDisplay -FullPath $Path -RepoRoot $RepoRoot
    if (-not (Test-FileExists $Path)) { throw "写しが無い: $shown" }
    if ((Get-Item -LiteralPath $Path).Length -eq 0) { throw "写しが空である: $shown" }

    $literal = ConvertTo-SqlLiteral $Path
    $result = & $SqlRunner "ATTACH DATABASE $literal AS probe; PRAGMA probe.integrity_check; DETACH DATABASE probe;"
    $rows = @($result.results | Where-Object { $_.columns -contains 'integrity_check' })
    if ($rows.Count -ne 1) { throw "写しの検査結果を読めなかった: $shown" }
    $verdict = [string]$rows[0].rows[0].integrity_check
    if ($verdict -ne 'ok') { throw "写しが壊れている（$verdict）: $shown" }
}

function Get-LiveDbPath {
    <#  **稼働 DB のパスは DB 自身に聞く**（PRAGMA database_list）。追跡外の設定ファイルを
        道具が解釈すると、書式が変わったときに黙ってずれる（_designer.ps1 と同じ考え）。 #>
    param([Parameter(Mandatory)][scriptblock]$SqlRunner)

    $result = & $SqlRunner 'PRAGMA database_list;'
    $main = @($result.results[0].rows | Where-Object { $_.name -eq 'main' })
    if ($main.Count -ne 1) { throw 'PRAGMA database_list が main を 1 つ返さなかった。' }
    $path = [string]$main[0].file
    if (-not $path) { throw '稼働 DB のパスを取れなかった（インメモリの接続先かもしれない）。' }
    return $path
}

function Assert-NotInUse {
    <#  本体と連れを排他で開けるか。開けない相手が居るなら、書き換えてはいけない。

        **これは保険であって主ではない。** 2026-09-08 の実測で、サーバ（PID あり）が
        動いている最中に本体を FileShare.None で開いたところ**成功した**——
        つまりこれだけでは「サーバが動いている」を検出できない。
        **プロセスを見る側は道具ごとに違う**（本体の稼働 DB は名前で、ワークツリーの
        写しは実行ファイルの在処で見分ける）ので、それぞれの道具が持つ。 #>
    param(
        [Parameter(Mandatory)][string]$DbPath,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    foreach ($path in (Get-DbFamily -DbPath $DbPath)) {
        try {
            $stream = [System.IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
            $stream.Close()
        } catch {
            throw "誰かが開いている: $(Format-ForDisplay -FullPath $path -RepoRoot $RepoRoot)（$($_.Exception.Message)）"
        }
    }
}

function Move-FilesAside {
    <#  本体と連れを `<行き先>/` へ改名して退ける。**1 つも無ければフォルダも作らない。**
        途中で落ちたら、動かした分を戻してから投げ直す——本体だけ消えて古いジャーナルが残る、
        という一番危ない形を作らないため。 #>
    param(
        [Parameter(Mandatory)][string]$DbPath,
        [Parameter(Mandatory)][string]$Destination
    )

    $sources = @(Get-DbFamily -DbPath $DbPath)
    if ($sources.Count -eq 0) { return $null }

    # **退け先そのものを空き名にする。** 呼ぶ側は日時で名前を作るので、
    # 同じ秒に 2 度退けると衝突する。**何も上書きしないのがこの道具の約束**である
    $Destination = Get-FreePath -Path $Destination

    # **読む側と書く側で、角括弧の扱いを揃えるために .NET で作る。**
    # ただし**穴を塞いだのではない**——`New-Item -ItemType Directory -Path 'x/aside[2]/probe' -Force` は
    # 角括弧つきのフォルダをそのまま作れる（2026-09-16 に実測）。**読む側の Test-Path とは違う。**
    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null

    $moved = @()
    try {
        foreach ($source in $sources) {
            $target = Join-Path $Destination (Split-Path -Leaf $source)
            # **Move-Item の -Destination はリテラルにできない。** .NET の側で動かす
            [System.IO.File]::Move($source, $target)
            $moved += , @($target, $source)
        }
    } catch {
        foreach ($pair in $moved) { Move-Item -LiteralPath $pair[0] -Destination $pair[1] }
        throw
    }
    return [pscustomobject]@{
        Directory = $Destination
        Moved     = $moved
        Files     = @($sources | ForEach-Object { Split-Path -Leaf $_ })
    }
}

function Restore-Aside {
    <#  退けたものを元の場所へ戻す（置き換えが途中で落ちたときの巻き戻し）。 #>
    param($Aside)
    if (-not $Aside) { return }
    foreach ($pair in $Aside.Moved) {
        if (Test-FileExists $pair[0]) { [System.IO.File]::Move($pair[0], $pair[1], $true) }
    }
}
