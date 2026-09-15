<#
.SYNOPSIS
    ワークツリーの稼働 DB を、本体の稼働 DB の写しで置き換える。

.DESCRIPTION
    常設のワークツリーは追跡外のファイルを開発者が先に写して作る（README「セットアップ」）。
    **そのうち稼働 DB の写しだけは、DDL を変えるたびにずれる**——コミット前フックの
    同値検査（migrate.ps1 -Verify）が赤くなり、そのたびに開発者を待つと
    常設ワークツリーの利点が消える。**この道具はそのずれだけを直す**
    （開発者の決定。2026-09-15。README の「git のワークツリーにも同じものが要る」）。

      -Update   本体の稼働 DB の写しで、ワークツリーの DB を置き換える。
                **置き換える現物は消さず、ワークツリーの中へ改名して残す。**
      -List     更新できるワークツリーの一覧。
      -SelfTest この道具自身の検査（コミット前フックが流す）。**稼働 DB には触れない。**

    **行き先の許否の正典はこの節である。** README・tools/README・docs/33 はここを指すだけにする
    （ADR-0046 が `guard_delete.py` の docstring を正典に置いたのと同じ作法）。

    **db_snapshot.ps1 に足さなかった。** ADR-0046 の決定 7 が「この道具はパスを受け取らない」
    ことを根拠に守りの免除を置いているので、そこへ行き先の引数を足すと
    **無検査の書き込み経路が 1 本開く**（README の敷衍。2026-09-15）。

    **cp では写さない。** 使用中の SQLite を複製すると壊れた写しができる（ADR-0046 の理由）。
    本体と同じく VACUUM INTO で取り、**取った直後と置いた直後の 2 回** integrity_check を通す。

    **行き先を許す条件は 3 つある。**
      ① `git worktree list` が返すワークツリーであること（git に登録されている）
      ② `.claude/worktrees/` の配下であること（README が決めた置き場）
      ③ **リンクを解いたうえで**、行き先が本体の稼働 DB でも本体の `LocalData/` の配下でもないこと

    **①②だけでは守れない。** ①だけだと repo の外に登録されたワークツリーへ書けてしまい、
    ②だけだと「それらしい名前のただのフォルダ」へ書けてしまう。
    **そして①②は字面の比較で、リンクを 1 本も見ていない**——
    `[System.IO.Path]::GetFullPath` はジャンクションもシンボリックリンクも追わない
    （2026-09-16 に実測。**Windows が既定で持つジャンクション**を渡すとそのまま返り、
    `ResolveLinkTarget($true)` だけが実体を返した）。
    **だから③を、解いたパスで別に見る。** `<ワークツリー>/LocalData` を本体へジャンクションすれば
    ①②は素通りするので、**③が無いと本体の稼働 DB がすり替わる。**

    **保護対象の正典は tools/claude/protected_paths.json で、ワークツリーの DB に行は無い。**
    ただし**機械の挙動は対称ではない**——`guard_delete.py` の `decide_write` は
    ワークツリーの中から走ったとき根を 2 つ持つので、**ワークツリーの中の Claude から見ると
    `<ワークツリーの根>/LocalData/db` として守られる**。本体側から見ると守られない。
    **「作り直せるから載せない」ではない**——ワークツリー側で入れた帳簿データは作り直せない。
    載せない理由は**この道具が退避して残すので消えないこと**である
    （載せる基準そのものは `protected_paths.json` の `_README_criteria` が持つ。ここに写さない）。

    **-SelfTest は一時フォルダを再帰削除する。** repo の外にあり、この道具が自分で作ったもので、
    `trash.ps1` と `db_snapshot.ps1` の同じ断りに倣う（ADR-0046 決定 4。
    docs/33 §1 の規則は Claude が打つコマンドを縛るもので、道具の後始末までは縛らない）。

.PARAMETER Worktree
    ワークツリーの名前（`.claude/worktrees/` の直下の名前）。
    **1 つしか無ければ省略できる。** 2 つ以上あるときは必須。

.PARAMETER DataSource
    designer.settings.json の DataSources の Name。

.EXAMPLE
    pwsh -NoProfile -File tools/clb/worktree_db.ps1 -List

.EXAMPLE
    pwsh -NoProfile -File tools/clb/worktree_db.ps1 -Update

.EXAMPLE
    pwsh -NoProfile -File tools/clb/worktree_db.ps1 -Update -Worktree work
#>
[CmdletBinding()]
param(
    [switch]$Update,
    [switch]$List,
    [switch]$SelfTest,
    [string]$Worktree,
    [string]$DataSource = 'BusinessAppSQLite'
)

$ErrorActionPreference = 'Stop'

$selected = @($Update, $List, $SelfTest) | Where-Object { $_ }
if ($selected.Count -ne 1) { throw '-Update / -List / -SelfTest のどれか 1 つを指定する。' }

. (Join-Path $PSScriptRoot '_designer.ps1')
. (Join-Path $PSScriptRoot '_sqlite.ps1')

$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# ワークツリーの置き場。**README がここと決めている**ので、道具も同じ 1 か所しか許さない。
$script:WorktreeArea = '.claude/worktrees'

# 稼働中のサーバの実行ファイル名。**dotnet run は apphost を起こす**ので dotnet.exe ではない。
$script:ServerProcessName = 'BusinessApp.Server'

function Show-Path {
    param([Parameter(Mandatory)][string]$Path)
    return Format-ForDisplay -FullPath $Path -RepoRoot $script:RepoRoot
}

# ------------------------------------------------------------------ パスの小道具

function Get-NormalizedPath {
    <#  `..` と `.` を畳み、区切りを揃え、末尾の区切りを落とす。
        **ファイルシステムを見ない**ので実在しないパスでも働く——だから検体で使える。
        **リンクは解かない。** 解くのは Resolve-RealPath の仕事である。 #>
    param([Parameter(Mandatory)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Resolve-RealPath {
    <#  **各段をたどってリンクを解く。** 途中のフォルダがジャンクションでも実体に着地する。
        実在しない末尾はそのまま継ぐ（まだ作っていない行き先も判定に掛けられるように）。

        **`[System.IO.Path]::GetFullPath` はリンクを追わない**ので、これが要る（2026-09-16 に実測）。
        **読めない段があっても止めない**——解けなかった段は字面のまま進む。 #>
    param([Parameter(Mandatory)][string]$Path)

    $full = Get-NormalizedPath $Path
    $parts = @()
    $probe = $full
    while ($true) {
        $parent = Split-Path -Parent $probe
        if (-not $parent -or $parent -eq $probe) { break }
        $parts = , (Split-Path -Leaf $probe) + $parts
        $probe = $parent
    }

    $resolved = $probe   # ドライブの根（または UNC の共有）
    foreach ($part in $parts) {
        $next = Join-Path $resolved $part
        if (Test-Path -LiteralPath $next) {
            try {
                $target = (Get-Item -LiteralPath $next -Force).ResolveLinkTarget($true)
                if ($target) { $next = $target.FullName }
            } catch {
                # 読めない段は字面のまま進む
            }
        }
        $resolved = $next
    }
    return Get-NormalizedPath $resolved
}

function Test-IsUnder {
    <#  $Child が $Parent の**配下**か。**同じパスは配下ではない**（false を返す）。 #>
    param(
        [Parameter(Mandatory)][string]$Child,
        [Parameter(Mandatory)][string]$Parent
    )
    $c = Get-NormalizedPath $Child
    $p = Get-NormalizedPath $Parent
    return $c.StartsWith($p + [System.IO.Path]::DirectorySeparatorChar,
                         [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-SamePath {
    param(
        [Parameter(Mandatory)][string]$Left,
        [Parameter(Mandatory)][string]$Right
    )
    return [string]::Equals((Get-NormalizedPath $Left), (Get-NormalizedPath $Right),
                            [System.StringComparison]::OrdinalIgnoreCase)
}

# ------------------------------------------------------------------ 判定（純関数）
#
# **どれもファイルシステムも git も見ない。** 呼ぶ側が集めて解いた事実だけを受け取る。
# こうしておくと、ワークツリーが 1 つも無い機でも検体で総当たりできる。

function New-Verdict {
    param([bool]$Ok, [AllowNull()][string]$Reason, [AllowNull()]$Value)
    return [pscustomobject]@{ Ok = $Ok; Reason = $Reason; Value = $Value }
}

function Select-UpdatableRoots {
    <#  登録されたワークツリーのうち、README が決めた置き場の配下だけを残す（本体は落ちる）。 #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Roots,
        [Parameter(Mandatory)][string]$Area
    )
    return @($Roots | Where-Object { Test-IsUnder -Child $_ -Parent $Area })
}

function Select-WorktreeVerdict {
    <#  名前から 1 本に絞る。**省略できるのは 1 つしか無いときだけ。** #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Candidates,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Name
    )

    if ($Candidates.Count -eq 0) {
        return New-Verdict $false '更新できるワークツリーが無い（開発者が作る。README のセットアップ）' $null
    }

    $names = @($Candidates | ForEach-Object { Split-Path -Leaf $_ })
    if (-not $Name) {
        if ($Candidates.Count -gt 1) {
            return New-Verdict $false "ワークツリーが $($Candidates.Count) つあるので -Worktree で選ぶ: $($names -join ', ')" $null
        }
        return New-Verdict $true $null $Candidates[0]
    }

    $matched = @($Candidates | Where-Object {
        [string]::Equals((Split-Path -Leaf $_), $Name, [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($matched.Count -eq 0) {
        return New-Verdict $false "その名前のワークツリーが無い: '$Name'（いまあるのは: $($names -join ', ')）" $null
    }
    if ($matched.Count -gt 1) {
        # **「無い」と言わない。** 葉が重なった 2 本を「無い」と言うと、理由文が嘘になる
        return New-Verdict $false "同じ名前のワークツリーが $($matched.Count) つある: '$Name'（置き場を整理する）" $null
    }
    return New-Verdict $true $null $matched[0]
}

function Get-DestinationVerdict {
    <#  **①②を見て、行き先の字面を導く。** 引数は**すべて解決済み**（Resolve-RealPath を通した）
        であることを名前で表明している。**③はここでは見ない**——
        導いた行き先をもう一度解いてから Get-ResolvedDestinationVerdict が見る。 #>
    param(
        [Parameter(Mandatory)][string]$ResolvedRepoRoot,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ResolvedWorktreeRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ResolvedRegisteredWorktrees,
        [Parameter(Mandatory)][string]$ResolvedLiveDbPath,
        [Parameter(Mandatory)][string]$Area
    )

    if ($ResolvedRegisteredWorktrees.Count -eq 0) {
        return New-Verdict $false 'git に登録されたワークツリーが 1 つも無い（開発者が作る。README のセットアップ）' $null
    }
    if (-not $ResolvedWorktreeRoot) {
        return New-Verdict $false 'ワークツリーを指定していない' $null
    }

    # ① git に登録されているか
    $registered = @($ResolvedRegisteredWorktrees | Where-Object { Test-SamePath -Left $_ -Right $ResolvedWorktreeRoot })
    if ($registered.Count -eq 0) {
        return New-Verdict $false 'git worktree list に無い（git が知らないフォルダには書かない）' $null
    }

    # **本体そのものを先に断る。** ①には本体も載っているので、ここを後ろに置くと
    # 「置き場の配下ではない」という当たっているが分かりにくい理由が返る。
    if (Test-SamePath -Left $ResolvedWorktreeRoot -Right $ResolvedRepoRoot) {
        return New-Verdict $false '本体のリポジトリを指している' $null
    }

    # ② README が決めた置き場の配下か
    if (-not (Test-IsUnder -Child $ResolvedWorktreeRoot -Parent $Area)) {
        return New-Verdict $false "$($script:WorktreeArea)/ の配下ではない（置き場は README が決めている）" $null
    }

    # 稼働 DB が repo の外に置かれている環境では、行き先を導けない
    if (-not (Test-IsUnder -Child $ResolvedLiveDbPath -Parent $ResolvedRepoRoot)) {
        return New-Verdict $false '稼働 DB が repo の外にあるので、ワークツリー側の在処を導けない（開発者に頼む）' $null
    }

    $relative = $ResolvedLiveDbPath.Substring((Get-NormalizedPath $ResolvedRepoRoot).Length + 1)
    return New-Verdict $true $null (Get-NormalizedPath (Join-Path $ResolvedWorktreeRoot $relative))
}

function Get-ResolvedDestinationVerdict {
    <#  **③。導いた行き先をリンクごと解いたうえで、本体に届いていないかを見る。**
        `<ワークツリー>/LocalData` を本体へジャンクションすると①②は字面なので素通りする。
        **ここだけが実体で比べている。** #>
    param(
        [Parameter(Mandatory)][string]$ResolvedRepoRoot,
        [Parameter(Mandatory)][string]$ResolvedWorktreeRoot,
        [Parameter(Mandatory)][string]$ResolvedDestination,
        [Parameter(Mandatory)][string]$ResolvedLiveDbPath
    )

    if (Test-SamePath -Left $ResolvedDestination -Right $ResolvedLiveDbPath) {
        return New-Verdict $false '行き先が本体の稼働 DB と同じである（リンクを解いて一致した）' $null
    }
    if (Test-IsUnder -Child $ResolvedDestination -Parent (Join-Path $ResolvedRepoRoot 'LocalData')) {
        return New-Verdict $false '行き先が本体の LocalData/ の配下である（リンクを解いて配下だった）' $null
    }
    if (-not (Test-IsUnder -Child $ResolvedDestination -Parent $ResolvedWorktreeRoot)) {
        return New-Verdict $false 'リンクを解くと、行き先がワークツリーの外だった' $null
    }
    return New-Verdict $true $null $ResolvedDestination
}

function Get-ServerVerdict {
    <#  **ワークツリーから起動したサーバが動いていないか。**
        本体のサーバは止めなくてよい——別の DB を握っているからである。

        **実行ファイルの在処が読めなかったプロセスは、断る側に倒す。**
        「排他で開けるか」に委ねられない——委ねた先（_sqlite.ps1 の Assert-NotInUse）が
        「サーバが動いている最中でも FileShare.None で開けた」と自分で記録している。
        **読めない相手を見逃すと、握られたまま DB を差し替えて「写した」と印字する。** #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][array]$Processes,
        [Parameter(Mandatory)][string]$ResolvedWorktreeRoot
    )

    $unreadable = @($Processes | Where-Object { -not $_.Readable })
    if ($unreadable.Count -gt 0) {
        return New-Verdict $false ("実行ファイルの在処を読めないサーバが居る（PID $($unreadable.Id -join ', ')）。" +
                                   'ワークツリーのものかを決められないので断る。止めてから打つこと。') $null
    }
    $inside = @($Processes | Where-Object { Test-IsUnder -Child $_.Path -Parent $ResolvedWorktreeRoot })
    if ($inside.Count -gt 0) {
        return New-Verdict $false "ワークツリーのサーバが動いている（PID $($inside.Id -join ', ')）。止めてから打つこと。" $null
    }
    return New-Verdict $true $null $null
}

# ------------------------------------------------------------------ 事実を集める

function Get-RegisteredWorktrees {
    <#  `git worktree list --porcelain` の `worktree ` 行を、**リンクを解いて**返す。
        **本体そのものも含めて返す**——除くのは判定の仕事である。 #>
    $output = & git -C $script:RepoRoot worktree list --porcelain 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git worktree list が失敗した: $output" }
    # **カンマで包まない。** 空のとき `,@()` は「空の配列を 1 つ含む配列」になり、
    # 呼ぶ側の Count が 1 になる（2026-09-16 に自己検査が捕まえた）。
    return @($output |
        Where-Object { $_ -like 'worktree *' } |
        ForEach-Object { Resolve-RealPath ($_.Substring('worktree '.Length)) })
}

function Get-ServerProcesses {
    <#  サーバのプロセスを `Id` / `Path` / `Readable` に畳む。**判定はしない。** #>
    return @(Get-Process -Name $script:ServerProcessName -ErrorAction SilentlyContinue | ForEach-Object {
        $path = $null
        try { $path = $_.Path } catch { $path = $null }
        [pscustomobject]@{
            Id       = $_.Id
            Path     = if ($path) { Resolve-RealPath $path } else { '' }
            Readable = [bool]$path
        }
    })
}

# ------------------------------------------------------------------ 入口（依存を差し替えられる形）

function Invoke-Update {
    <#  **`-Update` の全部。** 依存を引数で受けるので、自己検査が偽物を渡して端から端まで通せる
        （`self-review` スキル §9 の 5「入口を純関数に割り、終了コードと出る行を固定する」）。

        返すのは `Ok` / `Reason` / `Lines`。**印字は呼ぶ側がする。** #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Name,
        [Parameter(Mandatory)][scriptblock]$GetRegistered,
        [Parameter(Mandatory)][scriptblock]$GetProcesses,
        [Parameter(Mandatory)][scriptblock]$SqlRunner,
        [Parameter(Mandatory)][scriptblock]$Vacuum
    )

    $lines = @()

    $resolvedRepo = Resolve-RealPath $RepoRoot
    $area = Get-NormalizedPath (Join-Path $resolvedRepo $script:WorktreeArea)
    $registered = @(& $GetRegistered)
    $candidates = @(Select-UpdatableRoots -Roots $registered -Area $area)

    $picked = Select-WorktreeVerdict -Candidates $candidates -Name $Name
    if (-not $picked.Ok) {
        return [pscustomobject]@{ Ok = $false; Reason = $picked.Reason; Lines = $lines }
    }
    $worktreeRoot = $picked.Value

    $livePath = Resolve-RealPath (Get-LiveDbPath -SqlRunner $SqlRunner)

    $verdict = Get-DestinationVerdict -ResolvedRepoRoot $resolvedRepo `
                                      -ResolvedWorktreeRoot $worktreeRoot `
                                      -ResolvedRegisteredWorktrees $registered `
                                      -ResolvedLiveDbPath $livePath -Area $area
    if (-not $verdict.Ok) {
        return [pscustomobject]@{ Ok = $false; Reason = "行き先を許さない: $($verdict.Reason)"; Lines = $lines }
    }

    # **行き先の親が無ければ作らない。** 無いということは、開発者が追跡外のファイルを
    # 写していないということで、**DB だけ置いてもワークツリーは動かない**。
    $destinationDir = Split-Path -Parent $verdict.Value
    if (-not (Test-Path -LiteralPath $destinationDir -PathType Container)) {
        $why = "ワークツリーに稼働 DB の置き場が無い: $(Show-Path $verdict.Value)" +
               "（追跡外のファイルが写っていない。開発者に頼む——README のセットアップ）"
        return [pscustomobject]@{ Ok = $false; Reason = $why; Lines = $lines }
    }

    # ③ **リンクを解いてから、本体に届いていないかを見る。**
    $destination = Resolve-RealPath $verdict.Value
    $resolved = Get-ResolvedDestinationVerdict -ResolvedRepoRoot $resolvedRepo `
                                               -ResolvedWorktreeRoot $worktreeRoot `
                                               -ResolvedDestination $destination `
                                               -ResolvedLiveDbPath $livePath
    if (-not $resolved.Ok) {
        return [pscustomobject]@{ Ok = $false; Reason = "行き先を許さない: $($resolved.Reason)"; Lines = $lines }
    }

    $server = Get-ServerVerdict -Processes @(& $GetProcesses) -ResolvedWorktreeRoot $worktreeRoot
    if (-not $server.Ok) {
        return [pscustomobject]@{ Ok = $false; Reason = $server.Reason; Lines = $lines }
    }
    Assert-NotInUse -DbPath $destination -RepoRoot $RepoRoot

    # ワークツリー側で作業した形跡があれば言う。**止めはしない**——退避して残すので消えない
    if ((Test-FileExists $destination) -and (Test-FileExists $livePath)) {
        if ((Get-Item -LiteralPath $destination).LastWriteTimeUtc -gt (Get-Item -LiteralPath $livePath).LastWriteTimeUtc) {
            $lines += '注意: ワークツリーの DB のほうが新しい（そこで作業した形跡がある）。退避には残る。'
        }
    }

    # **先に写しを作ってから現物を退ける。** 逆にすると、写しで落ちたときに DB が無い状態で残る。
    $staged = $destination + '.incoming'
    if (Test-Path -LiteralPath $staged) {
        $why = "前回の残骸がある: $(Show-Path $staged)" +
               '（pwsh -NoProfile -File tools/claude/trash.ps1 <パス> で片づける）'
        return [pscustomobject]@{ Ok = $false; Reason = $why; Lines = $lines }
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $asideDir = Join-Path $worktreeRoot "LocalData/backup/_superseded/$stamp"
    $aside = $null
    try {
        & $Vacuum $staged
        Assert-DbSound -Path $staged -SqlRunner $SqlRunner -RepoRoot $RepoRoot

        # 退ける直前に、**プロセスとファイルの両方**をもう一度見る（最初の検査から時間が経っている）。
        # **ファイルだけでは足りない**——_sqlite.ps1 が「それだけでは動いているサーバを検出できない」と記録している。
        $again = Get-ServerVerdict -Processes @(& $GetProcesses) -ResolvedWorktreeRoot $worktreeRoot
        if (-not $again.Ok) { throw $again.Reason }
        Assert-NotInUse -DbPath $destination -RepoRoot $RepoRoot

        $aside = Move-FilesAside -DbPath $destination -Destination $asideDir
        if ($aside) {
            # **退けた先は、置き換えるより先に印字に積む。** 後ろに置くと、
            # 置いたあとの検算で落ちたときに「どこへ退けたか」が一度も出ない
            $lines += "前の DB を退けた: $(Show-Path $aside.Directory)（$($aside.Files -join ', ')）"
        }
        Move-Item -LiteralPath $staged -Destination $destination
        # **置いた直後の検算も try の中に置く。** 外に出すと、落ちたときに巻き戻しが走らず、
        # 壊れた写しが稼働位置に残る
        Assert-DbSound -Path $destination -SqlRunner $SqlRunner -RepoRoot $RepoRoot
    } catch {
        Restore-Aside -Aside $aside
        if (Test-Path -LiteralPath $staged) {
            # **-Force を使わない。** 前の失敗の証拠を黙って捨てないため。
            # **日時だけでは足りない**（Get-FreePath の理由。_sqlite.ps1）
            Move-Item -LiteralPath $staged -Destination (Get-FreePath -Path ($staged + ".failed.$stamp"))
        }
        return [pscustomobject]@{ Ok = $false; Reason = $_.Exception.Message; Lines = $lines }
    }

    $lines += "写した: 本体の稼働 DB → $(Show-Path $destination)"
    $lines += 'ワークツリーでサーバとデザイナを動かしていたなら再起動すること（CLB は列定義を static にキャッシュする）。'
    $lines += 'ワークツリーで migrate.ps1 -Verify を打って、スキーマが揃ったことを確かめること。'
    return [pscustomobject]@{ Ok = $true; Reason = $null; Lines = $lines }
}

function Invoke-List {
    <#  **`-List` の全部。** 0 本でも `Ok` を返す——**問い合わせなので「無い」も答え**である
        （`-Update` は仕事ができないので落とす。終了コードが割れているのは意図である）。 #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][scriptblock]$GetRegistered
    )

    $resolvedRepo = Resolve-RealPath $RepoRoot
    $area = Get-NormalizedPath (Join-Path $resolvedRepo $script:WorktreeArea)
    $candidates = @(Select-UpdatableRoots -Roots @(& $GetRegistered) -Area $area)

    $lines = @()
    if ($candidates.Count -eq 0) {
        $lines += "更新できるワークツリーは無い（$($script:WorktreeArea)/ の配下に 1 つも無い）。"
        $lines += '作るのは開発者である（README のセットアップ）。'
        return [pscustomobject]@{ Ok = $true; Lines = $lines; Rows = @() }
    }

    $rows = @($candidates | ForEach-Object {
        $dbDir = Join-Path $_ 'LocalData/db'
        $asideDir = Join-Path $_ 'LocalData/backup/_superseded'
        $leftovers = 0
        if (Test-Path -LiteralPath $dbDir) {
            foreach ($pattern in @('*.incoming', '*.failed.*')) {
                $leftovers += @(Get-ChildItem -LiteralPath $dbDir -Filter $pattern -File -ErrorAction SilentlyContinue).Count
            }
        }
        [pscustomobject]@{
            '名前' = Split-Path -Leaf $_
            '場所' = Show-Path $_
            '残骸' = $leftovers
            '退避' = if (Test-Path -LiteralPath $asideDir) {
                         @(Get-ChildItem -LiteralPath $asideDir -Directory -ErrorAction SilentlyContinue).Count
                     } else { 0 }
        }
    })
    $stuck = @($rows | Where-Object { $_.'残骸' -gt 0 })
    if ($stuck.Count -gt 0) {
        $lines += "残骸が残っているワークツリーが $($stuck.Count) つある（trash.ps1 で片づける）。"
    }
    return [pscustomobject]@{ Ok = $true; Lines = $lines; Rows = $rows }
}

# ------------------------------------------------------------------ 自己検査

. (Join-Path $PSScriptRoot '_worktree_db_selftest.ps1')

# ------------------------------------------------------------------ 呼び出し

if ($SelfTest) { exit (Invoke-SelfTest) }

$SqlRunner = {
    param([string]$Sql)
    Invoke-DesignerSql -RepoRoot $script:RepoRoot -DataSource $DataSource -Sql $Sql
}

if ($List) {
    $result = Invoke-List -RepoRoot $script:RepoRoot -GetRegistered { Get-RegisteredWorktrees }
    $result.Lines | ForEach-Object { Write-Host $_ }
    if ($result.Rows.Count -gt 0) { $result.Rows | Format-Table -AutoSize }
    exit 0
}

$result = Invoke-Update -RepoRoot $script:RepoRoot -Name $Worktree `
                        -GetRegistered { Get-RegisteredWorktrees } `
                        -GetProcesses { Get-ServerProcesses } `
                        -SqlRunner $SqlRunner `
                        -Vacuum { param([string]$To) & $SqlRunner "VACUUM INTO $(ConvertTo-SqlLiteral $To);" | Out-Null }
$result.Lines | ForEach-Object { Write-Host $_ }
if (-not $result.Ok) { throw $result.Reason }
exit 0
