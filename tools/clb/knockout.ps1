<#
.SYNOPSIS
    制約ノックアウト——DDL の制約を 1 つずつ外し、**赤にならない制約を報告する**（ADR-0053）。

.DESCRIPTION
    「全部緑のまま生き残った制約は、**誰もテストしていない制約**である」を機械で言うための計器
    （docs/qa/05_観点網羅の計器.md §4）。カバレッジも Stryker も C# のソースしか見ないので、
    DDL の制約とトリガは計器の外にいた。

    外す点の一覧・外し方・殺し手から外すテストの正典は **BusinessApp.TestSupport の
    SchemaKnockout** である。このスクリプトは回すだけで、何も知っていない——
    駆動役に列挙を写すと、DDL の書き方が増えた日に写しだけが古くなる。

      （既定）      全点を掃引して集計を出す。生き残りがラチェットを超えたら 1 で終わる
      -Only <点...> 指定した点だけ回す（カンマ区切り）。**テストを足した点が「殺せた」に変わることを確かめる**のに使う
      -Kind <種類>  その種類だけ回す（Trigger / Index / Check / Unique / ForeignKey）
      -List         回さずに点の一覧だけ出す

    **判定は「テストが赤になったか」だけである。** 何が赤になったかは見ない——
    理由はどうあれ、外したことに気づいたテストが 1 本でもあれば、その制約は見張られている。

    **「DDL が流せなくなった赤」は殺せたに数えない**（ADR-0053 決定 5）。
    テストが見張っているから赤いのではないからである。一覧の 4 列目がそれを持つ。

    **回すのは Schema.Tests だけである**（同 決定 3）。サーバ側まで回すと、
    アプリ層のテストが代わりに赤くなって**砦の穴を隠す**——DB の制約はアプリを迂回しても
    効く最後の砦（ADR-0004）なので、それを直接撃つテストが要る。

    **--no-build で回す。** Schema.Tests は coverlet.collector なのでこれが効く。
    coverlet.msbuild を使う他のテストプロジェクトでは --no-build が
    **シンボル不一致で全件を赤にする**ので、掃引に混ぜてはいけない（ADR-0053 の実測）。

.NOTES
    **1 回の掃引は長い**（実測は ADR-0053 の実測節）。コミット前フックには載せていない（同 決定 7）。
#>
[CmdletBinding()]
param(
    [string[]]$Only,
    [ValidateSet('Trigger', 'Index', 'Check', 'Unique', 'ForeignKey')]
    [string]$Kind,
    [switch]$List,

    # **ラチェットの上限。値の正典はここである**（ADR-0053 の決定 6。ADR-0012 §8 の Stryker が
    # pre-commit に値を持つのと同じ作法）。割れたら (a) テストを足して戻すか (b) 生き残りを読んで
    # 理由とともに動かす。**黙って上げない。**
    #
    # **他の制約に包まれていて、どんなテストを書いても殺せないものの数である**
    # （一覧と「なぜ包まれているか」の証明は qa/02 のラウンド 88・151・152）。
    # **下げるには DDL から冗長な制約を消すことになる**ので、次に触る回の判断に送ってある。
    #
    # **2026-09-23 に 7 から 10 へ動かした**（qa/02 のラウンド 152 に一覧と理由）——
    # **税率の表の `version` の UNIQUE が 1 本増え**、
    # **2026-09-13 の掃引の時点で数えていなかった 2 本**（`partner_invoice_registrations` と
    # `transition_purchase_rates` の自然キーの UNIQUE）**が表に出たため**である。
    #
    # **-Only / -Kind で絞ったときは 0 に落とす**（下で上書きする）——
    # 絞った掃引は「足したテストがその点を殺せるか」を見る用途で、
    # **全点の上限をそのまま当てると、生き残っても成功で返ってしまう**。
    [int]$MaxSurvivors = 10
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$cli = Join-Path $repoRoot 'BusinessApp' 'BusinessApp.KnockoutCli'
$tests = Join-Path $repoRoot 'BusinessApp' 'BusinessApp.Schema.Tests' 'BusinessApp.Schema.Tests.csproj'

Write-Host '[knockout] ビルド'
foreach ($project in @($tests, (Join-Path $cli 'BusinessApp.KnockoutCli.csproj'))) {
    dotnet build $project --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        # **ビルドの失敗を見逃すと、全点が非 0 で終わって「生き残り 0」という最良の報告になる。**
        throw "ビルドに失敗した（$project）。掃引は当てにならないので止める。"
    }
}

$points = @(dotnet run --project $cli --no-build | Where-Object { $_ } | ForEach-Object {
        $columns = $_ -split "`t"
        [pscustomobject]@{ Name = $columns[0]; Kind = $columns[1]; Where = $columns[2]; Applies = $columns[3] -eq 'yes' }
    })

# **`pwsh -File` は引数を文字列 1 本で渡す**（カンマで配列にはならない）ので、ここで割る。
# 割らないと、名前が 1 つも当たらないまま「外す点が 1 つも無い」で止まる。
if ($Only) {
    $only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $points = @($points | Where-Object { $only -contains $_.Name })
}
if ($Kind) { $points = @($points | Where-Object { $_.Kind -eq $Kind }) }

if ($points.Count -eq 0) {
    # **0 点は緑ではない。** 名前を打ち間違えたまま「生き残り 0」を報告させない。
    # **`throw` で落とす**——`$ErrorActionPreference = 'Stop'` のもとでは `Write-Error` も
    # 終端エラーになり、続く `exit` に到達しない（どちらも 1 になって書き分けられない）。
    throw '外す点が 1 つも無い。-Only / -Kind の値を確かめること。'
}

if ($List) {
    $points | Format-Table -AutoSize
    exit 0
}

if ($PSBoundParameters.ContainsKey('Only') -or $PSBoundParameters.ContainsKey('Kind')) {
    if (-not $PSBoundParameters.ContainsKey('MaxSurvivors')) { $MaxSurvivors = 0 }
}

$filter = (dotnet run --project $cli --no-build -- --filter).Trim()
Write-Host "[knockout] $($points.Count) 点 / 殺し手から外す: $filter"

# **対照実験。** 何も外さずに 1 巡して緑でなければ、以降の赤は「テストが見張っている」証拠にならない。
# フィルタ式の綴り違いも、ランナーの異常終了も、素の赤も、**すべて「全点殺せた」に倒れる**。
Write-Host '[knockout] 対照実験（何も外さずに 1 巡）'
$env:SCHEMA_KNOCKOUT = ''
dotnet test $tests --no-build --nologo -v q --filter $filter 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw '何も外していないのにテストが赤い。掃引の判定（赤＝見張られている）が成り立たないので止める。'
}

$killed = [System.Collections.Generic.List[string]]::new()
$survived = [System.Collections.Generic.List[string]]::new()
$notApplied = [System.Collections.Generic.List[string]]::new()
$started = Get-Date

try {
    $index = 0
    foreach ($point in $points) {
        $index++
        if (-not $point.Applies) {
            $notApplied.Add($point.Name)
            Write-Host ("[{0,3}/{1}] {2} — DDL が流せない（集計の外）" -f $index, $points.Count, $point.Name)
            continue
        }

        $env:SCHEMA_KNOCKOUT = $point.Name
        dotnet test $tests --no-build --nologo -v q --filter $filter 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $survived.Add($point.Name)
            Write-Host ("[{0,3}/{1}] {2} — 生き残り（誰も見張っていない）" -f $index, $points.Count, $point.Name) -ForegroundColor Yellow
        }
        else {
            $killed.Add($point.Name)
            Write-Host ("[{0,3}/{1}] {2} — 殺せた" -f $index, $points.Count, $point.Name)
        }
    }
}
finally {
    # **必ず消す。** 残すと、この後に流す普通のテストが「制約を 1 つ欠いた DB」で緑になる。
    $env:SCHEMA_KNOCKOUT = ''
}

$elapsed = (Get-Date) - $started
Write-Host ''
Write-Host ("[knockout] 殺せた {0} / 生き残り {1} / DDL が流せない {2}（{3:hh\:mm\:ss}）" -f `
        $killed.Count, $survived.Count, $notApplied.Count, $elapsed)

if ($survived.Count -gt 0) {
    Write-Host ''
    Write-Host '生き残り（この制約を外してもテストは全部緑だった）:'
    $survived | ForEach-Object { Write-Host "  $_" }
}

if ($survived.Count -gt $MaxSurvivors) {
    throw "生き残りが $($survived.Count) 件で、上限 $MaxSurvivors を超えている。テストを足すか、理由とともに -MaxSurvivors を動かすこと。"
}

exit 0
