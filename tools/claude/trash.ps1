#Requires -Version 7.0
<#
.SYNOPSIS
    ファイル・フォルダをごみ箱へ送る。**`rm` の代わりに使う唯一の削除コマンド**。

.DESCRIPTION
    完全削除せずごみ箱へ送るので、消してしまっても人が戻せる。
    だから開発者の確認を待たずに実行してよい。規則は docs/30_作業のルール.md §10、
    経緯は docs/decisions/0044-削除はごみ箱送りに一本化しrmを機械で止める.md。

    **「戻せる」には例外がある**——ごみ箱の容量を超えるものは Windows が完全削除するとされ、
    このスクリプトはそれを事前に知る手段を持たない（.NOTES）。

    守るものの正典は tools/claude/protected_paths.json の 1 ファイルで、
    このスクリプトと guard_delete.py の両方がそれを読む。**載せる基準もそのファイルが持つ。**

    ごみ箱から戻すのはエクスプローラで行う（復元の動詞は表示言語で名前が変わるとされ、
    機械で当てにできない——**未確認だが、外したときの損が大きいので危険側に倒した**）。

.PARAMETER Path
    削除するファイル・フォルダ。複数指定できる。
    **実在するものは文字どおりに扱い**、実在しないものだけワイルドカードとして展開する
    （`report[1].pdf` のような名前を glob と解釈して別のファイルを消さないため）。

.PARAMETER DryRun
    実際には消さず、何を消すかだけを表示する。

.PARAMETER SelfTest
    保護判定と本体の配線を検査する（コミット前フックが毎回流す。実測 2 秒）。
    **他のファイルには触れない**（削除は -DryRun でしか通さない）。
    自分の検体だけは一時フォルダに作って片づける。

.EXAMPLE
    pwsh -NoProfile -File tools/claude/trash.ps1 docs/下書き.md BusinessApp/obj

.EXAMPLE
    pwsh -NoProfile -File tools/claude/trash.ps1 -DryRun "Designer/Design/*.bak"

.NOTES
    Windows 専用（ごみ箱は Windows の機能で、本リポジトリ自体が Windows 前提である。
    前提は [README.md](../../README.md) の「セットアップ（新環境）」）。

    **未確認のまま危険側に倒しているもの**（実測も一次情報も取っていない。2026-09-07 時点）:
    ① ごみ箱の容量を超えるものは完全削除になるとされる。**事前に知る手段を持たないので拒めない**。
       手当ては正典の載せる基準（protected_paths.json の `_README_criteria`）に預けてある。
    ② ネットワーク・リムーバブルにはごみ箱が無いとされる。**固定ドライブかどうかで代用して拒む**
       （「固定ドライブ＝ごみ箱あり」の同値そのものは検証していない。ボリューム単位で
       ごみ箱を無効にした場合は拒めない）。

    **実測したもの**（2026-09-07）: 別のプロセスが FileShare.None で開いたファイルを
    UIOption.OnlyErrorDialogs で削除しようとすると、ダイアログは出ずに 1.4 秒で例外になった（1 回計測）。
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Path,

    [switch] $DryRun,

    [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows の既定のコンソール符号化は CP932 で、日本語の理由文が化ける。
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }

$script:CanonPath = Join-Path $PSScriptRoot 'protected_paths.json'

# ---------------------------------------------------------------- パスの比較

function ConvertTo-ComparablePath {
    param([string] $Value)
    # 実在しないパスでも正規化できる（区切りの統一・`..` の解決）。
    $full = [System.IO.Path]::GetFullPath($Value)
    return $full.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
}

function Test-PathWithin {
    <#  $Child が $Ancestor と同じか、その配下か。
        **区切りまで含めて比べる**——文字列の前方一致だけだと LocalData が LocalDataX に当たる。 #>
    param([string] $Child, [string] $Ancestor)

    $c = ConvertTo-ComparablePath $Child
    $a = ConvertTo-ComparablePath $Ancestor
    if ($c -ieq $a) { return $true }
    return $c.StartsWith($a + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-DriveRoot {
    param([string] $Value)
    $full = [System.IO.Path]::GetFullPath($Value)
    return $full -ieq [System.IO.Path]::GetPathRoot($full)
}

function Get-RepoRoots {
    <#  守る範囲の起点。**worktree から実行されたときは本体のリポジトリも守る**——
        稼働 DB や環境設定は本体側にあり、worktree 側の複製を守っても意味が無い。 #>
    $primary = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $roots = @($primary)
    try {
        $common = git -C $primary rev-parse --path-format=absolute --git-common-dir 2>$null
        if ($LASTEXITCODE -eq 0 -and $common) {
            $mainRoot = Split-Path -Parent (@($common)[0])
            if ($mainRoot -and -not (Test-PathWithin -Child $mainRoot -Ancestor $primary)) {
                $roots += $mainRoot
            }
        }
    }
    catch { }  # git が無くても本体は動く（守る範囲が狭まるだけ）
    return $roots
}

# ------------------------------------------------------------ 守るものの正典

function Get-ProtectedEntries {
    if (-not (Test-Path -LiteralPath $script:CanonPath)) {
        throw "守るものの正典が読めない: $script:CanonPath。読めないまま消すことはしない。"
    }
    $entries = @((Get-Content -LiteralPath $script:CanonPath -Raw -Encoding UTF8 | ConvertFrom-Json).protected)
    if ($entries.Count -eq 0) {
        throw "守るものの正典が空である: $script:CanonPath。読めないまま消すことはしない。"
    }
    # **形の検査は読み込みの時点で行う**（後回しにすると、後段が先に例外死して検査に届かない）。
    foreach ($entry in $entries) {
        $hasFields = $entry.PSObject.Properties['path'] -and $entry.PSObject.Properties['why']
        if (-not $hasFields -or -not $entry.path -or -not $entry.why) {
            throw "守るものの正典に path または why の無い行がある: $($entry | ConvertTo-Json -Compress)"
        }
    }
    return $entries
}

function Resolve-LinkTargetPath {
    <#  シンボリックリンク・ジャンクションの実体を返す（リンクでなければ $null）。
        **実体も保護判定にかける**——保護対象を指すリンクを消させないため。 #>
    param([string] $FullPath)
    try {
        $target = (Get-Item -LiteralPath $FullPath -Force -ErrorAction Stop).ResolveLinkTarget($true)
        if ($target) { return $target.FullName }
    }
    catch { }
    return $null
}

function Get-BlockReason {
    <#  消してはいけない相手なら理由を返す。消してよいなら $null を返す。 #>
    param([string] $Target)

    if (Test-DriveRoot $Target) {
        return 'ドライブの直下（ボリュームの根）は削除しない'
    }

    foreach ($candidate in @($Target, (Resolve-LinkTargetPath $Target))) {
        if (-not $candidate) { continue }
        foreach ($root in $script:RepoRoots) {
            foreach ($entry in $script:Protected) {
                $protectedPath = Join-Path $root $entry.path
                if (Test-PathWithin -Child $candidate -Ancestor $protectedPath) {
                    return $entry.why
                }
                # 保護対象を**内側に含む**フォルダごとの削除も止める（ルートの削除は .git を巻き込む）。
                if (Test-PathWithin -Child $protectedPath -Ancestor $candidate) {
                    return "$($entry.why)——これを内側に含むフォルダごとの削除になる"
                }
            }
        }
    }
    return $null
}

# ------------------------------------------------------------------ 表示・削除

function Format-ForDisplay {
    param([string] $FullPath)
    $root = ConvertTo-ComparablePath $script:RepoRoots[0]
    if (Test-PathWithin -Child $FullPath -Ancestor $root) {
        $rel = (ConvertTo-ComparablePath $FullPath).Substring($root.Length).TrimStart('\', '/')
        if ($rel) { return $rel }
    }
    return $FullPath
}

function Expand-Target {
    <#  引数 1 つを絶対パスへ展開する。
        **戻り値を `, @(...)` で包まない**——呼び出し側が `@()` で受けるので、
        包むと「配列を 1 個だけ含む配列」になり、2 つのパスが 1 本の文字列に潰れる。 #>
    param([string] $Raw)

    # **実在するものは文字どおりに扱う。**
    # `report[1].pdf` を glob と解釈すると、実在する report1.pdf のほうを消す。
    $literal = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Raw)
    if (Test-Path -LiteralPath $literal) { return $literal }

    if ([System.Management.Automation.WildcardPattern]::ContainsWildcardCharacters($Raw)) {
        # -Force が無いと隠しファイルが落ち、「消したつもりで残る」のに成功と報告される。
        return @(Get-Item -Path $Raw -Force -ErrorAction SilentlyContinue) | ForEach-Object { $_.FullName }
    }
    return $literal  # 実在しない。呼び出し側が「見つからない」として報告する
}

function Test-RecycleBinAvailable {
    <#  ごみ箱のある置き場か。**ネットワーク・リムーバブルでは完全削除になる**ので、
        「戻せるから確認不要」という前提が崩れる。そこには当てさせない。 #>
    param([string] $FullPath)
    try {
        $root = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($FullPath))
        if (-not $root) { return $false }
        return ([System.IO.DriveInfo]::new($root)).DriveType -eq [System.IO.DriveType]::Fixed
    }
    catch { return $false }
}

function Move-ToRecycleBin {
    param([string] $FullPath)

    $ui = [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs
    $recycle = [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin

    if (Test-Path -LiteralPath $FullPath -PathType Container) {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($FullPath, $ui, $recycle)
    }
    else {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($FullPath, $ui, $recycle)
    }

    # 例外を出さずに消し残す経路（共有違反など）があるので、消えたことを確かめる。
    if (Test-Path -LiteralPath $FullPath) {
        throw '削除を呼んだが、まだ残っている（別のプロセスが掴んでいる可能性がある）'
    }
}

# ------------------------------------------------------------------- 自己検査

function Invoke-SelfTest {
    <#  **この表がこのスクリプトの保護の仕様である。**
        守る道具にテストが無いと、書き換えたときに「止めているつもりで素通り」になる。

        期待値は真偽ではなく**理由の字**にしてある——真偽 1 ビットに潰すと、
        保護の種類が互いに区別できず、`Test-DriveRoot` を丸ごと消しても
        「保護対象を内側に含む」枝が代わりに鳴って緑になる。

        結果は $script:SelfTestFailed に置く。**戻り値にしない**——
        PowerShell の関数は Write-Output した行も戻り値に混ぜて返すので、
        `exit (Invoke-SelfTest)` と書くと理由が画面から消え、終了コードも壊れる。 #>

    $failed = 0
    $root = $script:RepoRoots[0]
    $driveRoot = [System.IO.Path]::GetPathRoot($root)
    $outside = Join-Path ([System.IO.Path]::GetTempPath()) 'claude-trash-selftest'

    # Expect: $null なら止めない。文字列ならその字が理由に含まれること。
    $cases = @(
        # 保護対象そのものと、その配下
        @{ Target = (Join-Path $root 'LocalData'); Expect = 'LocalData/' }
        @{ Target = (Join-Path $root 'LocalData/db/x.db'); Expect = 'LocalData/' }
        @{ Target = (Join-Path $root 'Designer/LocalEnvironment.md'); Expect = 'LocalEnvironment.md' }
        @{ Target = (Join-Path $root '.claude/settings.local.json'); Expect = '.claude/settings.local.json' }
        @{ Target = (Join-Path $root 'Designer/.claude/settings.local.json'); Expect = 'Designer/.claude' }
        @{ Target = (Join-Path $root '.git/config'); Expect = '.git/' }
        @{ Target = (Join-Path $root 'BusinessApp/BusinessApp.Server/appsettings.Development.json'); Expect = 'appsettings.Development.json' }
        @{ Target = (Join-Path $root 'Designer/Design/designer.settings.Development.json'); Expect = 'designer.settings.Development.json' }
        # 保護対象を内側に含むもの（**ドライブ直下と区別できているか**）
        @{ Target = $root; Expect = '内側に含む' }
        @{ Target = (Join-Path $root 'Designer'); Expect = '内側に含む' }
        @{ Target = $driveRoot; Expect = 'ドライブの直下' }
        # 名前が前方一致するだけのものは止めない（区切りまで見ているか）
        @{ Target = (Join-Path $root 'LocalDataX'); Expect = $null }
        @{ Target = (Join-Path $root '.gitignore'); Expect = $null }
        @{ Target = (Join-Path $root '.gitattributes'); Expect = $null }
        # 追跡ファイル・生成物・リポジトリの外は止めない（git で戻せる／捨ててよいもの）
        @{ Target = (Join-Path $root 'docs/README.md'); Expect = $null }
        @{ Target = (Join-Path $root 'BusinessApp/BusinessApp.Server/obj'); Expect = $null }
        @{ Target = (Join-Path $root 'BusinessApp/BusinessApp.Server/appsettings.json'); Expect = $null }
        @{ Target = $outside; Expect = $null }
    )

    foreach ($case in $cases) {
        $actual = Get-BlockReason -Target $case.Target
        $ok = if ($null -eq $case.Expect) { $null -eq $actual } else { $actual -and $actual.Contains($case.Expect) }
        if (-not $ok) {
            $failed++
            Write-Output "NG  期待『$($case.Expect)』/ 実際『$actual』: $($case.Target)"
        }
    }

    # 正典の各行が、実際に止める形になっているか（綴り違い・組み立ての取りこぼしを拾う）。
    foreach ($entry in $script:Protected) {
        $reason = Get-BlockReason -Target (Join-Path $root $entry.path)
        if (-not $reason -or -not $reason.Contains($entry.why)) {
            $failed++
            Write-Output "NG  正典に載っているのに止まらない: $($entry.path)"
        }
    }

    # ワイルドカードに見える名前の実在ファイルを、文字どおりに扱えるか。
    # **取り違えると「隣の実在ファイル」を消す**——`report[1].pdf` を glob と読むと report1.pdf に当たる。
    # 検体は一時フォルダに作って必ず片づける（DryRun すら通さないので、他は何も触らない）。
    $fixture = Join-Path ([System.IO.Path]::GetTempPath()) 'claude-trash-selftest-fixture'
    $literal = Join-Path $fixture 'report[1].pdf'
    try {
        New-Item -ItemType Directory -Force -Path $fixture | Out-Null
        Set-Content -LiteralPath (Join-Path $fixture 'report1.pdf') -Value 'decoy' -Encoding utf8
        Set-Content -LiteralPath $literal -Value 'literal' -Encoding utf8
        $resolved = @(Expand-Target -Raw $literal)
        if ($resolved.Count -ne 1 -or $resolved[0] -ne $literal) {
            $failed++
            Write-Output "NG  ワイルドカードに見える実在ファイルを取り違えた: $($resolved -join ' / ')"
        }
    }
    finally {
        # **ここだけ完全削除でよい**——自分が作った検体であり、ごみ箱へ送ると
        # コミットのたびに開発者のごみ箱が汚れる。§10 の規則は Claude が打つコマンドを縛るもので、
        # 道具の内側で自分の後始末をすることまでは縛らない。
        Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
    }

    # ごみ箱の無い置き場を拒むか（ネットワークパスは DriveInfo が投げる）。
    # **この検体を消すと `Test-RecycleBinAvailable` の判定を殺す変異が緑になる。**
    # ただし本体の呼び出し側（$probes は固定ドライブしか当てられない）までは守っていない。
    # 行末の印は架空のホスト名・共有名に対する秘密検査の免除で、**この 1 行だけに効く**。
    if (Test-RecycleBinAvailable '\\server\share\x.txt') {  # lint-secrets:ignore
        $failed++
        Write-Output 'NG  ごみ箱の無い置き場（UNC）を「ごみ箱あり」と判定した'
    }

    # **本体の配線も動かす。** 判定関数だけを検査すると、
    # 拒否のブロックごと消しても緑になる（関門を殺す最短の変異がそれである）。
    # **期待する字は、集計行に出ない字にする。**「拒否 0 件・見つからない等 1 件」という
    # 最後の 1 行がどの語も含むので、語だけを探すと何をどう壊しても緑になる。
    $probes = @(
        @{ Args = @('-DryRun', (Join-Path $root 'LocalData')); Expect = '消すと戻せない'; Code = 1 }
        @{ Args = @('-DryRun', (Join-Path $root 'docs/README.md')); Expect = 'DryRun      :'; Code = 0 }
        @{ Args = @('-DryRun', (Join-Path $root 'この名前のファイルは無いはず.txt')); Expect = '見つからない:'; Code = 1 }
        @{ Args = @('-DryRun', '   '); Expect = '空白だけ'; Code = 1 }
    )
    foreach ($probe in $probes) {
        $out = (& pwsh -NoProfile -File $PSCommandPath @($probe.Args) 2>&1 | Out-String)
        $code = $LASTEXITCODE
        if ($code -ne $probe.Code -or -not $out.Contains($probe.Expect)) {
            $failed++
            Write-Output "NG  本体の配線: 期待『$($probe.Expect)』終了コード $($probe.Code) / 実際 $code : $($out.Trim())"
        }
    }

    if ($failed -eq 0) {
        Write-Output "trash: 保護判定 $($cases.Count) 件・正典 $($script:Protected.Count) 行・配線 $($probes.Count) 件、すべて期待どおり"
    }
    else {
        Write-Output "trash: $failed 件が期待と違う"
    }
    $script:SelfTestFailed = if ($failed -gt 0) { 1 } else { 0 }
}

# ----------------------------------------------------------------------- 本体

# **`@()` で受ける**——要素 1 個の配列は戻り値で文字列に潰れ、`[0]` が 1 文字目になる。
$script:RepoRoots = @(Get-RepoRoots)
$script:Protected = Get-ProtectedEntries

if ($SelfTest) {
    $script:SelfTestFailed = 1
    Invoke-SelfTest
    exit $script:SelfTestFailed
}

if (-not $Path -or $Path.Count -eq 0) {
    Write-Output @'
使い方: pwsh -NoProfile -File tools/claude/trash.ps1 [-DryRun] <パス> [<パス> ...]

ファイル・フォルダをごみ箱へ送る（完全削除しない）。rm の代わりに使う。
  -DryRun    何を消すかだけを表示する
  -SelfTest  保護判定と配線を検査する（自分の検体以外には触れない）
'@
    exit 2
}

$onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)
if (-not $onWindows) {
    Write-Output 'trash: ごみ箱は Windows の機能である。本リポジトリは Windows 前提なので、他の OS 向けの経路は用意していない。'
    exit 2
}
Add-Type -AssemblyName 'Microsoft.VisualBasic'

$moved = 0
$blocked = 0
$failures = 0

foreach ($raw in $Path) {
    # 空白だけ・ドライブ名だけの引数は**カレントディレクトリに化ける**ので、名指しで拒む。
    if ([string]::IsNullOrWhiteSpace($raw) -or $raw -match '^[A-Za-z]:$') {
        Write-Output "拒否        : 『$raw』— 空白だけ、またはドライブ名だけの引数はカレントディレクトリに化ける"
        $blocked++
        continue
    }

    try {
        $targets = @(Expand-Target -Raw $raw)
    }
    catch {
        # **1 件の失敗で全体を落とさない**（最後の集計が出ないのがいちばん困る）。
        Write-Output "失敗        : $raw — パスを解決できない: $($_.Exception.Message)"
        $failures++
        continue
    }

    if ($targets.Count -eq 0) {
        Write-Output "一致なし    : $raw"
        $failures++
        continue
    }

    foreach ($target in $targets) {
        try {
            $display = Format-ForDisplay -FullPath $target

            $reason = Get-BlockReason -Target $target
            if ($reason) {
                Write-Output "拒否        : $display — $reason"
                $blocked++
                continue
            }

            if (-not (Test-Path -LiteralPath $target)) {
                Write-Output "見つからない: $display"
                $failures++
                continue
            }

            if (-not (Test-RecycleBinAvailable -FullPath $target)) {
                Write-Output "拒否        : $display — ごみ箱の無い置き場（ネットワーク・リムーバブル）で、完全削除になる"
                $blocked++
                continue
            }

            $kind = if (Test-Path -LiteralPath $target -PathType Container) { 'フォルダ' } else { 'ファイル' }

            if ($DryRun) {
                Write-Output "DryRun      : $display （$kind）"
                continue
            }

            Move-ToRecycleBin -FullPath $target
            Write-Output "ごみ箱へ    : $display （$kind）"
            $moved++
        }
        catch {
            $message = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
            Write-Output "失敗        : $target — $message"
            $failures++
        }
    }
}

if ($DryRun) {
    Write-Output "（DryRun。何も消していない。拒否 $blocked 件・見つからない等 $failures 件）"
}
else {
    Write-Output "ごみ箱へ $moved 件・拒否 $blocked 件・失敗 $failures 件"
}

exit ($(if (($blocked + $failures) -gt 0) { 1 } else { 0 }))
