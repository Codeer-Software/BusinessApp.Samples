<#
.SYNOPSIS
    デザイナ exe の sql サブコマンドで SQL を実行し、結果 JSON を標準出力に返す。

.DESCRIPTION
    一時ファイルを作らない（docs/30_作業のルール.md §8）。
      - --out を省いて結果を標準出力で受ける
      - --query は ProcessStartInfo.ArgumentList で渡す
        （Start-Process -ArgumentList だと SQL 内の '...' が壊れて incomplete input になる）

    exe の呼び出しは _designer.ps1 に共通化してある。標準エラーは標準エラーへ流すので、
    標準出力はそのまま JSON として扱える。

    **DDL（CREATE / DROP / ALTER）はここでは流さない**（docs/33 §2・ADR-0064）。
    稼働 DB のスキーマは migrate.ps1 -Apply で当てる——記録（schema_migrations）が残る唯一の経路だからである。
    2026-09-16 に、適用済みのトリガをこの道具で手で作り直し、記録が残らなかった（docs/qa/04 の 2026-09-17）。
    通すのは次の 2 つだけ:
      - -File が Designer/ddl/ の正典で、**git に追跡され、HEAD から変わっていない**とき（DB を捨てて作り直すとき。ADR-0020 §6）
      - 印 <git-dir>/allow-ddl があるとき（-AllowDdlOnce -Reason で置く。check_frozen.py の allow-frozen-edit と同じ形で、
        **読んだら消す**ので 1 回しか効かない。exe が失敗しても印は戻らない——置き直すこと）
    判定は 1 パスで字句（'…'・"…"・/* */・-- 行末）を左から消してから、語で見る。**語彙は CREATE / DROP / ALTER の 3 つ**で、
    PRAGMA writable_schema・VACUUM INTO のような裏口は見ていない。schema_migrations への UPDATE / DELETE は DML なので通る
    （記録の訂正の経路——Designer/migrations/README.md）。過大に表明しない。

.PARAMETER Query
    実行する SQL。; 区切りで複数文を並べてよい。

.PARAMETER File
    SQL をファイルから読む（Query の代わり）。パスは呼び手の cwd から解決する。Designer/ddl の追跡ファイルを流すときに使う。

.PARAMETER DataSource
    designer.settings.json の DataSources の Name。

.PARAMETER AllowDdlOnce
    次の 1 回だけ DDL を通す印を <git-dir>/allow-ddl に置く。-Reason が要る（理由は印字され、通すときにもう一度印字される）。
    .git/ への書き込みはコマンド文字列では拒まれる（guard_delete.py）ので、この道具が自分で書く。

.PARAMETER Reason
    -AllowDdlOnce の理由（1 行）。

.PARAMETER SelfTest
    この道具自身の検査（コミット前フックが流す）。DB にも exe にも .git にも触れない。

.EXAMPLE
    pwsh -NoProfile -File tools/clb/sql.ps1 -Query "SELECT COUNT(*) FROM journal_entries;"

.EXAMPLE
    pwsh -NoProfile -File tools/clb/sql.ps1 -File Designer/ddl/005_journals.sql

.EXAMPLE
    pwsh -NoProfile -File tools/clb/sql.ps1 -AllowDdlOnce -Reason "正典からの作り直し（docs/33 §2）"
#>
[CmdletBinding()]
param(
    [string]$Query,
    [string]$File,
    [string]$DataSource = 'BusinessAppSQLite',
    [switch]$AllowDdlOnce,
    [string]$Reason,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$script:MarkerName = 'allow-ddl'

# --- 純粋関数（-SelfTest が撃つ。DB・exe・git に触れない）

# 字句を左から 1 パスで消してから、DDL の語（CREATE / DROP / ALTER）があるかを見る。
# 2 パス（文字列 → コメント）にすると、コメント中の ' が文字列の始まりに読まれて後ろの DDL を隠す
# （"-- don't`nCREATE TABLE t(x);" が素通りした。2026-09-17 の自己レビュー）。
function Test-ContainsDdl {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Sql)
    $tokens = "'(?:[^']|'')*'|`"(?:[^`"]|`"`")*`"|/\*.*?\*/|--[^\r\n]*"
    $stripped = [regex]::Replace($Sql, $tokens, ' ', 'Singleline')
    return [regex]::IsMatch($stripped, '(?i)\b(CREATE|DROP|ALTER)\b')
}

# -File で DDL を流してよいのは Designer/ddl/ の直下と配下の .sql だけ（追跡・未変更かは呼び手が git で見て渡す）。
function Test-DdlAllowedFile {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$FullPath)
    $ddlRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'Designer/ddl')) + [System.IO.Path]::DirectorySeparatorChar
    $full = [System.IO.Path]::GetFullPath($FullPath)
    return $full.StartsWith($ddlRoot, [System.StringComparison]::OrdinalIgnoreCase) -and $full.EndsWith('.sql', [System.StringComparison]::OrdinalIgnoreCase)
}

# 関門の判定そのもの。戻りは @{ Allowed; Lines; ConsumeMarker }。
#   IsAllowedFile   … -File が Designer/ddl/ の正典で、追跡済み・HEAD から未変更
#   MarkerReason    … 印の中身（無ければ $null。空ファイルは ''）
function Resolve-DdlDecision {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Sql,
        [bool]$IsAllowedFile,
        [AllowNull()][object]$MarkerReason,   # [string] にすると $null が '' に化けて「印が空」と区別できない
        [string]$MarkerPath = '<git-dir>/allow-ddl'
    )
    if (-not (Test-ContainsDdl -Sql $Sql)) {
        return @{ Allowed = $true; Lines = @(); ConsumeMarker = $false }
    }
    if ($IsAllowedFile) {
        return @{ Allowed = $true; Lines = @('DDL を含むが、Designer/ddl/ の追跡済みの正典なので通す。'); ConsumeMarker = $false }
    }
    if ($null -ne $MarkerReason) {
        $MarkerReason = [string]$MarkerReason
        if ($MarkerReason.Trim().Length -eq 0) {
            return @{ Allowed = $false; ConsumeMarker = $true; Lines = @(
                "$MarkerPath の印はあったが理由が空だったので、消して拒む。-AllowDdlOnce -Reason で置き直すこと。") }
        }
        return @{ Allowed = $true; ConsumeMarker = $true; Lines = @(
            "$MarkerPath の印を読んで消した（理由: $($MarkerReason.Trim())）。この 1 回だけ DDL を通す（exe が失敗しても印は戻らない）。") }
    }
    return @{ Allowed = $false; ConsumeMarker = $false; Lines = @(
        'DDL（CREATE / DROP / ALTER）は sql.ps1 で流さない。稼働 DB のスキーマは migrate.ps1 -Apply で当てる（docs/33 §2・ADR-0064）。',
        "正典を流し直すなら -File Designer/ddl/<ファイル>（追跡済み・未変更のものだけ）。どうしても要るなら先に -AllowDdlOnce -Reason `"<理由>`" で印を置く（1 回だけ効く。探した場所: $MarkerPath）。") }
}

# --- 印（.git の中の 1 ファイル）

function Get-MarkerPath {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $gitDir = (& git -C $RepoRoot rev-parse --absolute-git-dir 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $gitDir) { return $null }
    return Join-Path $gitDir $script:MarkerName
}

# 印があれば中身（理由）を返し、その場で消す。無ければ $null。空ファイルは ''。
function Take-DdlMarker {
    param([Parameter(Mandatory)][string]$MarkerPath)
    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) { return $null }
    $reason = Get-Content -LiteralPath $MarkerPath -Raw
    Remove-Item -LiteralPath $MarkerPath -Force
    if ($null -eq $reason) { return '' }
    return [string]$reason
}

# -File の正典が追跡済みで HEAD から変わっていないか（git に聞く。純粋関数の外）。
function Test-TrackedUnchanged {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$FullPath)
    & git -C $RepoRoot ls-files --error-unmatch -- $FullPath 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { return $false }
    & git -C $RepoRoot diff --quiet HEAD -- $FullPath 2>$null
    return ($LASTEXITCODE -eq 0)
}

function Invoke-SelfTest {
    $failures = @()
    $samples = @(
        @{ Sql = 'SELECT COUNT(*) FROM accounts;'; Ddl = $false },
        @{ Sql = "UPDATE schema_migrations SET checksum = 'x' WHERE version = 38;"; Ddl = $false },
        @{ Sql = 'DELETE FROM schema_migrations WHERE version = 99;'; Ddl = $false },        # DML は通る（記録の訂正の経路）
        @{ Sql = 'SELECT created_at, dropped, altered FROM t WHERE alter_flag = 1;'; Ddl = $false },  # 語の一部・識別子
        @{ Sql = "SELECT * FROM t WHERE name = 'DROP TABLE t';"; Ddl = $false },     # 文字列リテラル
        @{ Sql = "SELECT 'it''s CREATE' FROM t;"; Ddl = $false },                     # '' のエスケープ
        @{ Sql = "SELECT '--' , '/*' , x'00' FROM t;"; Ddl = $false },                # リテラルの中のコメント記号
        @{ Sql = "-- CREATE TABLE in a comment`nSELECT 1;"; Ddl = $false },           # 行コメント
        @{ Sql = "/* ALTER TABLE */ SELECT 1;"; Ddl = $false },                       # ブロックコメント
        @{ Sql = "-- it's a note`nSELECT * FROM t WHERE a = 'x' AND name = 'CREATE';"; Ddl = $false },  # コメント中の '
        @{ Sql = 'CREATE TRIGGER trg_x BEFORE INSERT ON t BEGIN SELECT 1; END;'; Ddl = $true },
        @{ Sql = 'drop trigger if exists trg_x;'; Ddl = $true },                       # 小文字
        @{ Sql = 'ALTER TABLE t ADD COLUMN c TEXT;'; Ddl = $true },
        @{ Sql = "SELECT 1;`nCREATE INDEX ix ON t(c);"; Ddl = $true },                 # 2 文目
        @{ Sql = "SELECT 'x'; DROP TABLE t;"; Ddl = $true },                           # リテラルの後ろ
        @{ Sql = "-- don't`nCREATE TABLE t(x); -- won't"; Ddl = $true },              # コメント中の ' が DDL を隠さない
        @{ Sql = "/* it's */ DROP TABLE t; /* that's all */"; Ddl = $true },
        @{ Sql = "SELECT `"it''s`" FROM t; DROP TABLE t;"; Ddl = $true },              # 二重引用の識別子
        @{ Sql = "DROP TRIGGER IF EXISTS trg_a;`nCREATE TRIGGER trg_a BEFORE INSERT ON t BEGIN SELECT RAISE(ABORT, '「郵便番号」は数字 3 桁 ＋ 「-」 ＋ 数字 4 桁（123-4567）。'); END;"; Ddl = $true },  # 2026-09-16 の実例の形
        @{ Sql = "`u{FEFF}CREATE TABLE t(x);"; Ddl = $true },                          # BOM 前置
        @{ Sql = ''; Ddl = $false }
    )
    foreach ($s in $samples) {
        $got = Test-ContainsDdl -Sql $s.Sql
        if ($got -ne $s.Ddl) { $failures += "Test-ContainsDdl: 期待 $($s.Ddl) / 実際 $got — $($s.Sql)" }
    }

    $root = 'C:\repo'
    $paths = @(
        @{ Path = 'C:\repo\Designer\ddl\005_journals.sql'; Ok = $true },
        @{ Path = 'C:\repo\Designer\ddl\sub\x.sql'; Ok = $true },
        @{ Path = 'C:\repo\Designer\migrations\0039_x.sql'; Ok = $false },          # 適用は migrate.ps1 だけ
        @{ Path = 'C:\repo\LocalData\temp\postal_retrigger.sql'; Ok = $false },     # 2026-09-16 の実例
        @{ Path = 'C:\repo\Designer\ddl\..\migrations\0039_x.sql'; Ok = $false },   # .. で抜ける
        @{ Path = 'C:\repo\Designer\ddl\notes.txt'; Ok = $false },
        @{ Path = 'C:\repo\Designer\ddl2\x.sql'; Ok = $false }                       # 前方一致の取り違え
    )
    foreach ($p in $paths) {
        $got = Test-DdlAllowedFile -RepoRoot $root -FullPath $p.Path
        if ($got -ne $p.Ok) { $failures += "Test-DdlAllowedFile: 期待 $($p.Ok) / 実際 $got — $($p.Path)" }
    }

    # 配線（判定の本体）。部品だけ縛っても、分岐を消せば緑のまま通る。
    $ddl = 'DROP TABLE t;'
    $decisions = @(
        @{ Name = 'DDL なし';               Sql = 'SELECT 1;'; File = $false; Marker = $null;  Allowed = $true;  Consume = $false },
        @{ Name = 'DDL なし・印あり';       Sql = 'SELECT 1;'; File = $false; Marker = '理由'; Allowed = $true;  Consume = $false },  # SELECT が印を食わない
        @{ Name = 'DDL・正典';              Sql = $ddl;        File = $true;  Marker = $null;  Allowed = $true;  Consume = $false },
        @{ Name = 'DDL・印あり';            Sql = $ddl;        File = $false; Marker = '作り直し'; Allowed = $true; Consume = $true },
        @{ Name = 'DDL・印が空';            Sql = $ddl;        File = $false; Marker = '';     Allowed = $false; Consume = $true },
        @{ Name = 'DDL・印が空白だけ';      Sql = $ddl;        File = $false; Marker = "  `n"; Allowed = $false; Consume = $true },
        @{ Name = 'DDL・印なし';            Sql = $ddl;        File = $false; Marker = $null;  Allowed = $false; Consume = $false }
    )
    foreach ($d in $decisions) {
        $r = Resolve-DdlDecision -Sql $d.Sql -IsAllowedFile $d.File -MarkerReason $d.Marker
        if ($r.Allowed -ne $d.Allowed -or $r.ConsumeMarker -ne $d.Consume) {
            $failures += "Resolve-DdlDecision: $($d.Name) — 期待 Allowed=$($d.Allowed)/Consume=$($d.Consume) 実際 Allowed=$($r.Allowed)/Consume=$($r.ConsumeMarker)"
        }
        if (-not $r.Allowed -and $r.Lines.Count -eq 0) { $failures += "Resolve-DdlDecision: $($d.Name) — 拒むのに理由文が無い" }
    }

    # 印は読んだら消える。無ければ $null、空ファイルは ''。
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("sql-selftest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $markerChecks = 0
    try {
        $marker = Join-Path $tmp $script:MarkerName
        if ($null -ne (Take-DdlMarker -MarkerPath $marker)) { $failures += 'Take-DdlMarker: 無いのに値が返った' }; $markerChecks++
        Set-Content -LiteralPath $marker -Value 'テストの理由' -Encoding utf8
        $reason = Take-DdlMarker -MarkerPath $marker
        if (-not $reason -or $reason -notmatch 'テストの理由') { $failures += 'Take-DdlMarker: 理由が読めない' }; $markerChecks++
        if (Test-Path -LiteralPath $marker) { $failures += 'Take-DdlMarker: 読んだのに消していない' }; $markerChecks++
        [System.IO.File]::WriteAllText($marker, '')
        $empty = Take-DdlMarker -MarkerPath $marker
        if ($null -eq $empty -or $empty -ne '') { $failures += "Take-DdlMarker: 空の印は '' を返す（実際: $($null -eq $empty ? 'null' : "'$empty'")）" }; $markerChecks++
        if (Test-Path -LiteralPath $marker) { $failures += 'Take-DdlMarker: 空の印を消していない' }; $markerChecks++
    }
    finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Host "  NG $_" }
        Write-Host "sql.ps1 -SelfTest: $($failures.Count) 件失敗"
        return 1
    }
    Write-Host "sql.ps1 -SelfTest: OK（DDL の判定 $($samples.Count) 件・パス $($paths.Count) 件・配線 $($decisions.Count) 件・印 $markerChecks 件）"
    return 0
}

if ($SelfTest) { exit (Invoke-SelfTest) }

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectRoot = Join-Path $repoRoot 'Designer/Design'

# --- 印を置く（-AllowDdlOnce）
if ($AllowDdlOnce) {
    if ($Query -or $File) { throw '-AllowDdlOnce は単独で指定する（印を置くだけ。DDL は次の呼び出しで流す）。' }
    if (-not $Reason -or $Reason.Trim().Length -eq 0) { throw '-AllowDdlOnce には -Reason（1 行）が要る。理由の無い印は置かない。' }
    $markerPath = Get-MarkerPath -RepoRoot $repoRoot
    if (-not $markerPath) { throw 'git の管理下ではないので印を置く場所が無い。' }
    [System.IO.File]::WriteAllText($markerPath, $Reason.Trim() + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "印を置いた: $markerPath（理由: $($Reason.Trim())）。次の 1 回の DDL だけ通る。"
    exit 0
}

if (-not $Query -and -not $File) { throw '-Query か -File のどちらかが要る。' }
if ($Query -and $File) { throw '-Query と -File は同時に指定できない。' }

. (Join-Path $PSScriptRoot '_designer.ps1')

# --- DDL の関門（docs/33 §2・ADR-0064）
$filePath = if ($File) { (Resolve-Path $File).Path } else { $null }
$sqlText = if ($Query) { $Query } else { Get-Content -LiteralPath $filePath -Raw }
$isAllowedFile = $false
if ($filePath -and (Test-DdlAllowedFile -RepoRoot $repoRoot -FullPath $filePath)) {
    $isAllowedFile = Test-TrackedUnchanged -RepoRoot $repoRoot -FullPath $filePath
}
$markerPath = Get-MarkerPath -RepoRoot $repoRoot
$markerReason = $null
$shownMarkerPath = if ($markerPath) { $markerPath } else { '（git の管理下ではないので印の置き場が無い）' }
if ($markerPath -and (Test-ContainsDdl -Sql $sqlText) -and -not $isAllowedFile) {
    # 印は「DDL を流そうとしたとき」にだけ読む（SELECT が印を食わないように）
    $markerReason = Take-DdlMarker -MarkerPath $markerPath
}
$decision = Resolve-DdlDecision -Sql $sqlText -IsAllowedFile $isAllowedFile -MarkerReason $markerReason -MarkerPath $shownMarkerPath
foreach ($line in $decision.Lines) { Write-Host $line }
if (-not $decision.Allowed) { exit 1 }

$exe = Get-DesignerExePath -RepoRoot $repoRoot

$arguments = @('sql', $projectRoot, '--datasource', $DataSource)
if ($Query) {
    $arguments += @('--query', $Query)
} else {
    $arguments += @('--file', $filePath)
}

$result = Invoke-Designer -Exe $exe -Arguments $arguments
Write-Output $result.StandardOutput
exit $result.ExitCode
