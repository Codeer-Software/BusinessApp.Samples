<#
.SYNOPSIS
    worktree_db.ps1 の自己検査（`Invoke-SelfTest`）。

.DESCRIPTION
    **本体と分けてある**のは、worktree_db.ps1 が目安の長さを超えたからである。
    **単体では動かない**——worktree_db.ps1 が読み込んだ関数を使う。

    **稼働 DB には触れない。** 判定はすべて純関数で総当たりし、
    ファイルを動かす部分と `-Update` の端から端までは、**repo の外の一時フォルダ**に
    偽の repo を組んで通す（依存は引数で差し替える）。

    **一時フォルダは再帰削除する。** repo の外にあり、この検査が自分で作ったもので、
    `trash.ps1` と `db_snapshot.ps1` の同じ断りに倣う（ADR-0046 決定 4）。
#>

function Invoke-SelfTest {
    # **NG はためてから最後に印字する。** カナリア（下）が NG 行を画面に残さないため。
    $script:selfTestMessages = [System.Collections.ArrayList]::new()
    $script:selfTestChecks = 0
    function Fail([string]$Message) { [void]$script:selfTestMessages.Add($Message) }
    function Note { $script:selfTestChecks++ }

    function Remove-TreeWithLinks {
        <#  **ジャンクションを含むフォルダは再帰削除できない**（2026-09-16 に実測。
            「Access to the path 'link' is denied.」）。**リンクを先に外してから消す。** #>
        param([Parameter(Mandatory)][string]$Path)
        if (-not (Test-Path -LiteralPath $Path)) { return }
        foreach ($item in @(Get-ChildItem -LiteralPath $Path -Recurse -Force -Directory -ErrorAction SilentlyContinue)) {
            if ($item.LinkTarget) { [System.IO.Directory]::Delete($item.FullName, $false) }
        }
        [System.IO.Directory]::Delete($Path, $true)
    }

    # ============================================================ 判定（純関数）
    #
    # 検体のパスは**実在しない**。判定はファイルシステムを見ないので、これで総当たりできる。

    $repo = Get-NormalizedPath 'C:/probe/repo'
    $area = Get-NormalizedPath 'C:/probe/repo/.claude/worktrees'
    $live = Get-NormalizedPath 'C:/probe/repo/LocalData/db/app.db'
    $work = Get-NormalizedPath 'C:/probe/repo/.claude/worktrees/work'
    $all = @($repo, $work)

    function Destination($worktreeRoot, $registered, $livePath = $live) {
        Note
        return Get-DestinationVerdict -ResolvedRepoRoot $repo -ResolvedWorktreeRoot $worktreeRoot `
                                      -ResolvedRegisteredWorktrees $registered `
                                      -ResolvedLiveDbPath $livePath -Area $area
    }

    # --- 対照実験。**まず「通るはずのもの」が通ることを確かめる**
    #     （§9 の 3。これが無いと、全部断る実装でも緑になる）
    $expectedDestination = Get-NormalizedPath 'C:/probe/repo/.claude/worktrees/work/LocalData/db/app.db'
    $ok = Destination $work $all
    if (-not $ok.Ok) {
        Fail "通すはずの行き先を断った: $($ok.Reason)"
    } elseif (-not (Test-SamePath -Left $ok.Value -Right $expectedDestination)) {
        Fail "導いた行き先が違う: $($ok.Value)"
    }

    # 末尾の区切り・区切りの向き・大文字小文字が違っても、同じ 1 つと読む
    foreach ($spelling in @("$work\", ($work -replace '\\', '/'), $work.ToUpperInvariant())) {
        if (-not (Destination $spelling $all).Ok) { Fail "同じワークツリーの別の綴りを断った: $spelling" }
    }

    # --- ①② で断るもの。**理由まで見る**（別の理由で落ちても「断った」に見えるため）。
    #     期待する字面はここに書き下す——実装の文字列を読むと、壊したとき期待値も一緒に壊れる
    $denials = @(
        @{ 名 = 'ワークツリーが 1 つも無い'; 根 = $work; 表 = @(); 印 = '1 つも無い' },
        @{ 名 = '指定が空'; 根 = ''; 表 = $all; 印 = '指定していない' },
        @{ 名 = 'git が知らない'; 根 = (Get-NormalizedPath 'C:/probe/repo/.claude/worktrees/ghost')
           表 = $all; 印 = 'git worktree list に無い' },
        @{ 名 = '置き場の外'; 根 = (Get-NormalizedPath 'C:/probe/elsewhere/work')
           表 = @($repo, (Get-NormalizedPath 'C:/probe/elsewhere/work')); 印 = '配下ではない' },
        @{ 名 = '本体そのもの'; 根 = $repo; 表 = $all; 印 = '本体のリポジトリ' },
        @{ 名 = '置き場そのもの'; 根 = $area; 表 = @($repo, $area); 印 = '配下ではない' }
    )
    foreach ($case in $denials) {
        $v = Destination $case.根 $case.表
        if ($v.Ok) { Fail "断るはずの行き先を通した: $($case.名)" }
        elseif ($v.Reason -notlike "*$($case.印)*") { Fail "「$($case.名)」を別の理由で断っている: $($v.Reason)" }
        elseif ($v.Value) { Fail "断ったのに行き先を返した: $($case.名)" }
    }

    $outside = Destination $work $all (Get-NormalizedPath 'C:/elsewhere/app.db')
    if ($outside.Ok) { Fail '稼働 DB が repo の外にあるのに行き先を導いた' }
    elseif ($outside.Reason -notlike '*repo の外*') { Fail "別の理由で断っている: $($outside.Reason)" }

    # --- ③ 解いたパスで見る。**ここが本体へ届くリンクを止める唯一の段**である
    function Resolved($destination, $worktreeRoot = $work, $livePath = $live) {
        Note
        return Get-ResolvedDestinationVerdict -ResolvedRepoRoot $repo -ResolvedWorktreeRoot $worktreeRoot `
                                              -ResolvedDestination $destination -ResolvedLiveDbPath $livePath
    }
    if (-not (Resolved $expectedDestination).Ok) { Fail '③ が、通すはずの行き先を断った' }

    $resolvedDenials = @(
        @{ 名 = '解くと本体の稼働 DB'; 先 = $live; 印 = '本体の稼働 DB と同じ' },
        @{ 名 = '解くと本体の LocalData の下'; 先 = (Get-NormalizedPath 'C:/probe/repo/LocalData/db/other.db')
           印 = '本体の LocalData/ の配下' },
        @{ 名 = '解くとワークツリーの外'; 先 = (Get-NormalizedPath 'C:/probe/somewhere/app.db')
           印 = 'ワークツリーの外' }
    )
    foreach ($case in $resolvedDenials) {
        $v = Resolved $case.先
        if ($v.Ok) { Fail "③ が断るはずの行き先を通した: $($case.名)" }
        elseif ($v.Reason -notlike "*$($case.印)*") { Fail "③ の「$($case.名)」が別の理由で断っている: $($v.Reason)" }
    }

    # --- ワークツリーの絞り込みと名前の解決（**通すべきものを断る側**も見る）
    $other = Get-NormalizedPath 'C:/probe/repo/.claude/worktrees/other'
    $nested = Get-NormalizedPath 'C:/probe/repo/.claude/worktrees/team/work'

    $roots = @(Select-UpdatableRoots -Roots @($repo, $work, $other) -Area $area)
    Note
    if ($roots.Count -ne 2) { Fail "絞り込みが 2 本のはずが $($roots.Count) 本" }
    foreach ($item in $roots) {
        if ($item -isnot [string]) { Fail "絞り込みが文字列でないものを返した（配列を包んでいる）: $($item.GetType().Name)" }
    }
    if (@($roots | Where-Object { Test-SamePath -Left $_ -Right $repo }).Count -gt 0) {
        Fail '絞り込みに本体が残っている'
    }
    if (@(Select-UpdatableRoots -Roots @() -Area $area).Count -ne 0) { Fail '空を渡したのに 0 本にならない' }

    $picks = @(
        @{ 名 = '0 本'; 候補 = @(); 名前 = ''; 可 = $false; 印 = '更新できるワークツリーが無い' },
        @{ 名 = '1 本・省略'; 候補 = @($work); 名前 = ''; 可 = $true; 期待 = $work },
        @{ 名 = '2 本・省略'; 候補 = @($work, $other); 名前 = ''; 可 = $false; 印 = '-Worktree で選ぶ' },
        @{ 名 = '2 本・名前で'; 候補 = @($work, $other); 名前 = 'other'; 可 = $true; 期待 = $other },
        @{ 名 = '名前が違う'; 候補 = @($work); 名前 = 'nope'; 可 = $false; 印 = 'その名前のワークツリーが無い' },
        @{ 名 = '大文字小文字'; 候補 = @($work); 名前 = 'WORK'; 可 = $true; 期待 = $work },
        @{ 名 = '葉が重なる'; 候補 = @($work, $nested); 名前 = 'work'; 可 = $false; 印 = '同じ名前のワークツリーが 2 つ' }
    )
    foreach ($case in $picks) {
        Note
        $v = Select-WorktreeVerdict -Candidates $case.候補 -Name $case.名前
        if ($v.Ok -ne $case.可) { Fail "ワークツリーの選択が違う: $($case.名)（理由: $($v.Reason)）" }
        elseif ($case.可 -and -not (Test-SamePath -Left $v.Value -Right $case.期待)) {
            Fail "選んだワークツリーが違う: $($case.名) → $($v.Value)"
        } elseif (-not $case.可 -and $v.Reason -notlike "*$($case.印)*") {
            Fail "「$($case.名)」が別の理由で断っている: $($v.Reason)"
        }
    }

    # --- サーバの判定。**在処を読めない相手は断る側に倒す**
    $servers = @(
        @{ 名 = '1 つも居ない'; 列 = @(); 可 = $true },
        @{ 名 = '本体のサーバだけ'; 可 = $true
           列 = @([pscustomobject]@{ Id = 1; Path = (Get-NormalizedPath 'C:/probe/repo/bin/BusinessApp.Server.exe'); Readable = $true }) },
        @{ 名 = 'ワークツリーのサーバ'; 可 = $false; 印 = 'ワークツリーのサーバが動いている'
           列 = @([pscustomobject]@{ Id = 2; Path = (Join-Path $work 'bin/BusinessApp.Server.exe'); Readable = $true }) },
        @{ 名 = '在処が読めない'; 可 = $false; 印 = '読めないサーバが居る'
           列 = @([pscustomobject]@{ Id = 3; Path = ''; Readable = $false }) }
    )
    foreach ($case in $servers) {
        Note
        $v = Get-ServerVerdict -Processes $case.列 -ResolvedWorktreeRoot $work
        if ($v.Ok -ne $case.可) { Fail "サーバの判定が違う: $($case.名)（理由: $($v.Reason)）" }
        elseif (-not $case.可 -and $v.Reason -notlike "*$($case.印)*") {
            Fail "「$($case.名)」が別の理由で断っている: $($v.Reason)"
        }
    }

    # ============================================================ 実物を触る側
    #
    # **repo の外に偽の repo を組む。** 稼働 DB にも本物のワークツリーにも触れない。

    $sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("worktree_db_selftest_" + [guid]::NewGuid().ToString('N'))
    $savedRepoRoot = $script:RepoRoot
    try {
        # --- リンクを解けるか。**実物のジャンクションを 1 本張って確かめる**
        #     （`GetFullPath` はこれを追わない。2026-09-16 に実測）
        $realDir = Join-Path $sandbox 'real'
        $linkDir = Join-Path $sandbox 'link'
        [System.IO.Directory]::CreateDirectory($realDir) | Out-Null
        New-Item -ItemType Junction -Path $linkDir -Target $realDir -ErrorAction Stop | Out-Null
        Note
        if (-not (Test-SamePath -Left (Resolve-RealPath $linkDir) -Right $realDir)) {
            Fail "ジャンクションを解けていない: $(Resolve-RealPath $linkDir)"
        }
        Note
        if (-not (Test-SamePath -Left (Resolve-RealPath (Join-Path $linkDir 'まだ無い.db')) `
                                -Right (Join-Path $realDir 'まだ無い.db'))) {
            Fail '実在しない末尾を継げていない（途中のジャンクションが解けていない）'
        }
        Note
        if (Test-SamePath -Left ([System.IO.Path]::GetFullPath($linkDir)) -Right $realDir) {
            Fail 'GetFullPath がジャンクションを解いてしまった（この道具の前提が変わった。Resolve-RealPath を見直すこと）'
        }

        # --- 退避と巻き戻し。**元にも退け先にも角括弧を置く**
        #     （Test-Path と New-Item の既定はこれをワイルドカードと読む）
        $dbDir = Join-Path $sandbox 'db[1]'
        [System.IO.Directory]::CreateDirectory($dbDir) | Out-Null
        $target = Join-Path $dbDir 'app.db'
        $asideRoot = Join-Path $sandbox 'aside[2]'

        Note
        if ($null -ne (Move-FilesAside -DbPath $target -Destination (Join-Path $asideRoot 'none'))) {
            Fail '現物が無いのに退けたと言った'
        }
        if (Test-Path -LiteralPath (Join-Path $asideRoot 'none')) { Fail '現物が無いのに退け先のフォルダを作った' }

        # **期待する連れの名前はここに書き下す。** 実装のリストを読んで作ると、
        # リストを縮めたときに検体も一緒に縮んで同語反復になる
        $expectedSidecars = @('-wal', '-shm', '-journal')
        $extra = @($script:SqliteSidecarSuffixes | Where-Object { $_ -notin $expectedSidecars })
        if ($extra.Count -gt 0) { Fail "連れが増えている（検体にも足すこと）: $($extra -join ', ')" }

        Set-Content -LiteralPath $target -Value 'old' -NoNewline
        foreach ($suffix in $expectedSidecars) { Set-Content -LiteralPath ($target + $suffix) -Value $suffix -NoNewline }

        $expectedAside = Join-Path $asideRoot 'probe'
        $aside = Move-FilesAside -DbPath $target -Destination $expectedAside
        Note
        if ($null -eq $aside) {
            Fail '現物があるのに退けなかった（角括弧のパスを見失っている）'
        } else {
            # **退け先は検体が書いた字面と突き合わせる。** 実装が返した値だけを使うと、
            # 組み立てを壊しても返り値が同じように壊れて釣り合う
            if (-not (Test-SamePath -Left $aside.Directory -Right $expectedAside)) {
                Fail "退け先が検体の字面と違う: $($aside.Directory)"
            }
            $expectedFiles = @('app.db') + @($expectedSidecars | ForEach-Object { 'app.db' + $_ })
            if (@(Compare-Object $aside.Files $expectedFiles).Count -ne 0) {
                Fail "退けたファイルの一覧が違う: $($aside.Files -join ', ')"
            }
            foreach ($name in $expectedFiles) {
                if (-not (Test-FileExists (Join-Path $expectedAside $name))) { Fail "退けた先に $name が無い（消してしまっている）" }
            }
        }
        foreach ($suffix in @('') + $expectedSidecars) {
            if (Test-FileExists ($target + $suffix)) { Fail "退けたはずの $suffix が残っている" }
        }

        Restore-Aside -Aside $aside
        Note
        foreach ($suffix in @('') + $expectedSidecars) {
            if (-not (Test-FileExists ($target + $suffix))) { Fail "巻き戻しで $suffix が戻っていない" }
        }
        if ((Get-Content -LiteralPath $target -Raw) -ne 'old') { Fail '巻き戻した本体の中身が違う' }

        # --- 使用中の相手。**掴み方を 2 通り試す。**
        #     締めて掴む相手だけで試すと、**こちらの開き方をどれだけ緩めても緑のまま**になる
        foreach ($share in @('None', 'ReadWrite')) {
            Note
            $held = [System.IO.File]::Open($target, 'Open', 'ReadWrite', $share)
            try {
                $threw = $false
                try { Assert-NotInUse -DbPath $target -RepoRoot $sandbox } catch { $threw = $true }
                if (-not $threw) { Fail "掴まれている DB を「使われていない」と言った（相手の共有は $share）" }
            } finally { $held.Close() }
        }
        Note
        $threw = $false
        try { Assert-NotInUse -DbPath $target -RepoRoot $sandbox } catch { $threw = $true }
        if ($threw) { Fail '誰も掴んでいない DB を「使われている」と言った' }

        # --- `-Update` を端から端まで。**依存を偽物に差し替えて通す**
        $fakeRepo = Join-Path $sandbox 'repo'
        $fakeLiveDir = Join-Path $fakeRepo 'LocalData/db'
        $fakeWork = Join-Path $fakeRepo '.claude/worktrees/w'
        [System.IO.Directory]::CreateDirectory($fakeLiveDir) | Out-Null
        [System.IO.Directory]::CreateDirectory((Join-Path $fakeWork 'LocalData/db')) | Out-Null
        $fakeLive = Join-Path $fakeLiveDir 'app.db'
        Set-Content -LiteralPath $fakeLive -Value 'LIVE' -NoNewline
        $script:RepoRoot = $fakeRepo

        # **偽物は「何回目か」を数える。** 写しの検算（1 回目）と置いたあとの検算（2 回目）を
        # 撃ち分けられないと、**後者を消しても緑のまま**になる（2026-09-16 のノックアウトで実測）。
        $script:probeIntegrityCalls = 0
        $script:probeBadIntegrityAt = 0
        $fakeSql = {
            param([string]$Sql)
            if ($Sql -like 'PRAGMA database_list*') {
                return [pscustomobject]@{ results = @([pscustomobject]@{
                    rows = @([pscustomobject]@{ name = 'main'; file = $fakeLive }) }) }
            }
            if ($Sql -like '*integrity_check*') {
                $script:probeIntegrityCalls++
                $verdict = if ($script:probeIntegrityCalls -eq $script:probeBadIntegrityAt) { 'page 3 is never used' } else { 'ok' }
                return [pscustomobject]@{ results = @([pscustomobject]@{
                    columns = @('integrity_check'); rows = @([pscustomobject]@{ integrity_check = $verdict }) }) }
            }
            throw "検体が知らない SQL: $Sql"
        }
        $fakeVacuum = { param([string]$To) Copy-Item -LiteralPath $fakeLive -Destination $To }
        $noServers = { @() }

        # **プロセスは波で返す。** 1 波目は誰も居ない・2 波目でワークツリーのサーバが上がる、を作ると
        # 「退ける直前の再検査」を消したときに赤くできる
        $script:probeProcessWaves = @()
        $wavedProcesses = {
            if ($script:probeProcessWaves.Count -eq 0) { return @() }
            $wave = $script:probeProcessWaves[0]
            if ($script:probeProcessWaves.Count -gt 1) {
                $script:probeProcessWaves = @($script:probeProcessWaves[1..($script:probeProcessWaves.Count - 1)])
            }
            return $wave
        }

        function Update($name = '', $registered = $null, $processes = $null, $vacuum = $null) {
            Note
            if ($null -eq $registered) { $registered = { @((Resolve-RealPath $fakeRepo), (Resolve-RealPath $fakeWork)) } }
            if ($null -eq $processes) { $processes = $noServers }
            if ($null -eq $vacuum) { $vacuum = $fakeVacuum }
            return Invoke-Update -RepoRoot $fakeRepo -Name $name -GetRegistered $registered `
                                 -GetProcesses $processes -SqlRunner $fakeSql -Vacuum $vacuum
        }

        # 初回。**出る行まで見る**（§9 の 5）
        $r = Update
        if (-not $r.Ok) {
            Fail "-Update が通らなかった: $($r.Reason)"
        } else {
            $copied = Join-Path $fakeWork 'LocalData/db/app.db'
            if (-not (Test-FileExists $copied)) { Fail '-Update が緑なのに DB が置かれていない' }
            elseif ((Get-Content -LiteralPath $copied -Raw) -ne 'LIVE') { Fail '置かれた DB の中身が本体と違う' }
            if (@($r.Lines | Where-Object { $_ -like '写した:*' }).Count -ne 1) {
                Fail "「写した」の行が 1 行ではない: $($r.Lines -join ' / ')"
            }
            if (@($r.Lines | Where-Object { $_ -like '*migrate.ps1 -Verify*' }).Count -ne 1) {
                Fail '揃ったかを確かめる案内が出ていない'
            }
            if (@($r.Lines | Where-Object { $_ -like '前の DB を退けた:*' }).Count -ne 0) {
                Fail '初回なのに「退けた」と言っている'
            }
        }

        # 2 回目。**退避の経路を通り、退けた先が出る**
        $r = Update
        if (-not $r.Ok) { Fail "2 回目の -Update が通らなかった: $($r.Reason)" }
        elseif (@($r.Lines | Where-Object { $_ -like '前の DB を退けた:*' }).Count -ne 1) {
            Fail "2 回目に「退けた」の行が出ていない: $($r.Lines -join ' / ')"
        }

        # **ジャンクションで本体へ届く形。** ①②は素通りするので、③が止めるはずである
        $trap = Join-Path $fakeRepo '.claude/worktrees/trap'
        [System.IO.Directory]::CreateDirectory($trap) | Out-Null
        New-Item -ItemType Junction -Path (Join-Path $trap 'LocalData') -Target (Join-Path $fakeRepo 'LocalData') -ErrorAction Stop | Out-Null
        $withTrap = { @((Resolve-RealPath $fakeRepo), (Resolve-RealPath $fakeWork), (Resolve-RealPath $trap)) }
        $before = Get-Content -LiteralPath $fakeLive -Raw
        $r = Update 'trap' $withTrap
        if ($r.Ok) { Fail 'ジャンクションで本体へ届く行き先を通した' }
        elseif ($r.Reason -notlike '*本体の稼働 DB と同じ*') { Fail "ジャンクションを別の理由で断っている: $($r.Reason)" }
        if ((Get-Content -LiteralPath $fakeLive -Raw) -ne $before) { Fail '本体の稼働 DB が書き換わった' }

        # 写しで落ちたら、現物を戻し、残骸に日時を付けて残す
        $r = Update '' $null $null { param([string]$To) throw '写しに失敗した（検体）' }
        if ($r.Ok) { Fail '写しが落ちたのに緑を返した' }
        $copied = Join-Path $fakeWork 'LocalData/db/app.db'
        if (-not (Test-FileExists $copied)) { Fail '写しが落ちたのに現物が戻っていない' }

        # ワークツリーのサーバが動いていたら断る
        $r = Update '' $null { @([pscustomobject]@{ Id = 9; Path = (Join-Path (Resolve-RealPath $fakeWork) 'x.exe'); Readable = $true }) }
        if ($r.Ok) { Fail 'ワークツリーのサーバが動いているのに通した' }

        # **写しの検算（1 回目）が落ちたら、現物に触れずに止まる**
        $copied = Join-Path $fakeWork 'LocalData/db/app.db'
        Set-Content -LiteralPath $copied -Value 'WORKTREE' -NoNewline
        $script:probeIntegrityCalls = 0
        $script:probeBadIntegrityAt = 1
        $r = Update
        $script:probeBadIntegrityAt = 0
        if ($r.Ok) { Fail '写しが壊れているのに通した' }
        elseif ($r.Reason -notlike '*壊れている*') { Fail "写しの検算を別の理由で断っている: $($r.Reason)" }
        if ((Get-Content -LiteralPath $copied -Raw) -ne 'WORKTREE') { Fail '写しが壊れていたのに現物を触った' }

        # **置いたあとの検算（2 回目）が落ちたら、現物を戻し、残骸を日時つきで残す**
        $script:probeIntegrityCalls = 0
        $script:probeBadIntegrityAt = 2
        $r = Update
        $script:probeBadIntegrityAt = 0
        if ($r.Ok) { Fail '置いた写しが壊れているのに緑を返した' }
        elseif ($r.Reason -notlike '*壊れている*') { Fail "置いたあとの検算を別の理由で断っている: $($r.Reason)" }
        if (-not (Test-FileExists $copied)) { Fail '置いたあとの検算が落ちたのに現物が戻っていない' }
        elseif ((Get-Content -LiteralPath $copied -Raw) -ne 'WORKTREE') { Fail '巻き戻した現物の中身が違う' }
        $failed = @(Get-ChildItem -LiteralPath (Split-Path -Parent $copied) -Filter '*.failed.*' -File -ErrorAction SilentlyContinue)
        Note
        if ($failed.Count -eq 0) { Fail '失敗した写しが残骸として残っていない' }
        foreach ($item in $failed) { [System.IO.File]::Delete($item.FullName) }

        # **退ける直前の再検査。** 1 波目は誰も居ないが、2 波目でサーバが上がる
        $script:probeProcessWaves = @(
            @(),
            @([pscustomobject]@{ Id = 8; Path = (Join-Path (Resolve-RealPath $fakeWork) 'x.exe'); Readable = $true })
        )
        $r = Update '' $null $wavedProcesses
        if ($r.Ok) { Fail '退ける直前にサーバが上がったのに通した' }
        elseif ($r.Reason -notlike '*ワークツリーのサーバが動いている*') {
            Fail "退ける直前の再検査が別の理由で断っている: $($r.Reason)"
        }
        if ((Get-Content -LiteralPath $copied -Raw) -ne 'WORKTREE') { Fail '再検査で止めたのに現物が入れ替わっている' }
        $script:probeProcessWaves = @()
        foreach ($item in @(Get-ChildItem -LiteralPath (Split-Path -Parent $copied) -Filter '*.failed.*' -File -ErrorAction SilentlyContinue)) {
            [System.IO.File]::Delete($item.FullName)
        }

        # --- `-List`。0 本と 1 本の両方
        Note
        $l = Invoke-List -RepoRoot $fakeRepo -GetRegistered { @((Resolve-RealPath $fakeRepo)) }
        if ($l.Rows.Count -ne 0) { Fail '0 本なのに行を返した' }
        elseif (@($l.Lines | Where-Object { $_ -like '*無い*' }).Count -eq 0) { Fail '0 本のときに何も言っていない' }
        Note
        $l = Invoke-List -RepoRoot $fakeRepo -GetRegistered { @((Resolve-RealPath $fakeRepo), (Resolve-RealPath $fakeWork)) }
        if ($l.Rows.Count -ne 1) { Fail "1 本のはずが $($l.Rows.Count) 行" }

        # --- カナリア。**Fail が数えられ、終了コードに届くこと**を確かめる
        #     （数え損ねると、NG を印字しながら exit 0 でフックを通ってしまう）
        Note
        $saved = $script:selfTestMessages.Count
        Fail 'カナリア'
        if ($script:selfTestMessages.Count -ne $saved + 1) {
            Write-Host 'NG  Fail が数えられていない（この検査自身が壊れている）'
            return 1
        }
        $script:selfTestMessages.RemoveAt($script:selfTestMessages.Count - 1)
    } finally {
        $script:RepoRoot = $savedRepoRoot
        Remove-TreeWithLinks -Path $sandbox   # 一時フォルダの後始末（repo の外）
    }

    $failed = $script:selfTestMessages.Count
    foreach ($message in $script:selfTestMessages) { Write-Host "NG  $message" }
    if ($failed -eq 0) {
        Write-Host "worktree_db: すべて期待どおり（検体 $($script:selfTestChecks) 点）"
    } else {
        Write-Host "worktree_db: $failed 件が期待と違う"
    }
    return $failed
}
