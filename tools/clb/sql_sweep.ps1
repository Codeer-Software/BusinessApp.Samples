<#
.SYNOPSIS
    SQL ミューテーション——クエリの SQL を 1 箇所ずつ壊し、**赤にならない箇所を報告する**（ADR-0056）。

.DESCRIPTION
    「壊しても緑のままの箇所は、**誰もテストしていない箇所**である」を機械で言うための計器
    （docs/qa/05_観点網羅の計器.md §3 の A 案）。Stryker が変異させるのは C# のソースだけなので、
    別ファイルにあるクエリの SQL は計器の外にいた。

    変異点の列挙と注入の形の正典は **tools/clb/sql_mutate.py** である。
    このスクリプトは回すだけで、演算子も位置の数え方も知らない——
    駆動役に列挙を写すと、演算子が増えた日に写しだけが古くなる
    （制約ノックアウトで SchemaKnockout と knockout.ps1 を分けたのと同じ形）。

      （既定）        全モジュールを掃引して集計を出す
      -Only <名前...> 指定したモジュールだけ回す（カンマ区切り）
      -List           回さずに変異点の一覧だけ出す

    **判定は「そのモジュールの行動テストが赤になったか」だけである。**
    回すのは `<モジュール名>QueryTests` に絞る（ADR-0055 が名前で対応づけている）——
    全部のテストを回すと、**関係のないテストが代わりに赤くなって穴を隠す**。

    **注入は環境変数 SQL_MUTATION である**（TestDatabase.QuerySql が読む）。
    **ファイルを書き換えない**——途中で止めても、壊れた SQL が追跡下に残らない。

    **--no-build で回す。** Schema.Tests は coverlet.collector なのでこれが効く
    （coverlet.msbuild の他のプロジェクトでは全件が赤になる。ADR-0053 の実測）。

.NOTES
    **掃引 1 回は 5 分ほど**（実測は ADR-0056）。**コミット前フックには載せていない**——
    速さの問題ではなく、**生き残りが多いうちは毎回流しても誰も読まない**からである（同 決定 7）。
#>
[CmdletBinding()]
param(
    [string[]]$Only,
    [switch]$List,

    # **モジュールごとの上限を無視して、全体の上限 1 つで判定する**（初回の測定用）。
    # **常用しない**——`-Only` が常に赤くなり、exit code を読まない習慣ができる。
    [int]$MaxSurvivors = -1
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$tests = Join-Path $repoRoot 'BusinessApp' 'BusinessApp.Schema.Tests' 'BusinessApp.Schema.Tests.csproj'
$mutate = Join-Path $PSScriptRoot 'sql_mutate.py'
$modulesRoot = Join-Path $repoRoot 'Designer' 'Design' 'Modules'

# **モジュールごとのラチェット。値の正典はここである**（knockout.ps1 と同じ作法）。
#
#   Points       変異点の数の**下限**。**母数が静かに痩せる経路がある**——
#                閉じない引用符が 1 つ入れば伏せ字が以降を全部飲み、
#                キーワードの大文字小文字が変われば演算子が当たらなくなる。
#                **痩せると生き残りも減り、上限だけの関門は緑になる。**
#   MaxSurvivors 生き残りの**上限**。割れたら (a) テストを足して戻すか
#                (b) 生き残りを読んで理由とともに動かす。**黙って上げない。**
#
# **モジュールを足したら、ここにも行を足す。** 表に無いモジュールは
# 「点 1 以上・生き残り 0」で判定するので、**足した日に必ず赤くなる**。
$ratchet = @{
    'GeneralLedger'           = @{ Points = 93; MaxSurvivors = 18 }
    'JournalBook'             = @{ Points = 81; MaxSurvivors = 4 }
    'JournalEntryList'        = @{ Points = 80; MaxSurvivors = 56 }
    'PartnerRegistrationList' = @{ Points = 22; MaxSurvivors = 3 }
}

# **モジュールはサイドカーの実体から数える。** 名前の表を持つと、足した日に写しだけが古くなる。
# **`QueryBehaviorCoverageTests.母数はモジュール定義の側から数えている` が、この数え方と
# モジュール定義（*.mod.json）側の数え方が同じ集合を返すことを毎回表明している**ので、
# クエリ項目を別名にしたモジュールが静かに抜けることは無い。
$sqlFiles = @(Get-ChildItem -Path $modulesRoot -Recurse -Filter '*.Query.sql' | Sort-Object Name)
if ($Only) {
    $names = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $sqlFiles = @($sqlFiles | Where-Object { $names -contains $_.Name.Replace('.Query.sql', '') })
}

if ($sqlFiles.Count -eq 0) {
    # **0 本は緑ではない。** 名前を打ち間違えたまま「生き残り 0」を報告させない。
    throw 'クエリの SQL が 1 本も見つからない。-Only の値と Modules/ の中身を確かめること。'
}

$points = @(foreach ($file in $sqlFiles) {
        $module = $file.Name.Replace('.Query.sql', '')
        python $mutate spec $file.FullName $module | Where-Object { $_ } | ForEach-Object {
            $columns = $_ -split "`t"
            if ($columns.Count -ne 6) {
                # **欄が割れたら止める。** 原文に TAB や改行が混ざると 1 行が 2 行に割れ、
                # **幽霊の点（位置が空）が静かに混ざって「殺した」に倒れる**
                # （PowerShell の `[int]''` は例外ではなく 0 を返す）。
                throw "spec の欄が 6 つでない（$($columns.Count) 欄）: $_"
            }
            [pscustomobject]@{
                Module = $module; Index = [int]$columns[0]; Operator = $columns[1]
                Line = [int]$columns[2]; From = $columns[3]; To = $columns[4]; Spec = $columns[5]
            }
        }
        if ($LASTEXITCODE -ne 0) { throw "変異点を数えられなかった（$($file.Name)）。" }
    })

if ($points.Count -eq 0) {
    throw '変異点が 1 つも無い。sql_mutate.py の演算子表を確かめること。'
}

if ($List) {
    $points | Format-Table Module, Index, Operator, Line, From, To -AutoSize
    exit 0
}

# **母数の下限を先に見る。** 痩せた母数のまま掃引すると、
# **生き残りが減って上限を通り、最良の報告が返る**。
$modules = @($sqlFiles | ForEach-Object { $_.Name.Replace('.Query.sql', '') })
$shrunk = @()
foreach ($module in $modules) {
    $counted = @($points | Where-Object { $_.Module -eq $module }).Count
    $floor = if ($ratchet.ContainsKey($module)) { $ratchet[$module].Points } else { 1 }
    if ($counted -lt $floor) {
        $shrunk += "$module は変異点が $counted 個しかない（下限 $floor）"
    }
}
if ($shrunk) {
    throw ("変異点の母数が痩せている。掃引は当てにならないので止める。`n  " + ($shrunk -join "`n  "))
}

Write-Host '[sql-sweep] ビルド'
dotnet build $tests --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) {
    # **ビルドの失敗を見逃すと、全点が非 0 で終わって「生き残り 0」という最良の報告になる。**
    throw 'ビルドに失敗した。掃引は当てにならないので止める。'
}

# **対照実験は、モジュールごとに回す。** まとめて 1 回だと
# **行動テストが 1 本も無いモジュールを見逃す**——そのモジュールでは `--filter` が 1 件も当たらず、
# ランナーが非 0 で終わるので、**全点が「殺した」に化ける**
# （最良の報告が返るのが、この手の道具で最も危ない壊れ方である）。
Write-Host "[sql-sweep] 対照実験（何も壊さずに 1 巡。$($modules.Count) モジュール / $($points.Count) 点）"
$env:SQL_MUTATION = $null
foreach ($module in $modules) {
    dotnet test $tests --no-build --nologo -v q --filter "FullyQualifiedName~$($module)QueryTests" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "$module の行動テストが、何も壊していないのに緑でない（1 本も無いのかもしれない）。掃引の判定（赤＝見張られている）が成り立たないので止める。"
    }
}

# **カナリア——「原文を原文で置き換える」変異を 1 つずつ当てる。**
#
# **位置がずれていれば `TestDatabase.Mutate` が投げ、そのモジュールの全点が赤くなる**——
# 掃引はそれを**「全部殺した」＝生き残り 0** と読み、**ラチェットを通って成功で返る**。
# **当たったかを確かめる守りが、そのまま最良の報告を作ってしまう。**
#
# **文言では見分けられない。** `dotnet test -v q` はテスト名しか印字せず、
# 例外の本文は出ない（実測）。**verbosity を上げて本文を拾う形は、書式に依る。**
#
# **だから「何も変えない変異」を打つ。** 位置と原文が合っていれば SQL は 1 文字も変わらず、
# テストは緑のまま。**合っていなければ投げて赤くなる**——ここで止める。
Write-Host '[sql-sweep] カナリア（何も変えない変異を 1 つずつ当てる）'
foreach ($module in $modules) {
    $first = @($points | Where-Object { $_.Module -eq $module })[0]
    $parts = $first.Spec -split ':', 5
    $env:SQL_MUTATION = "$($parts[0]):$($parts[1]):$($parts[2]):$($parts[4]):$($parts[4])"
    dotnet test $tests --no-build --nologo -v q --filter "FullyQualifiedName~$($module)QueryTests" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $env:SQL_MUTATION = $null
        throw "$module に「何も変えない変異」を当てたのにテストが赤い。**注入が別の場所を指している**（数える側と読む側で BOM か改行が食い違っているか、SQL を直したあと掃引を流し直していない）。掃引は当てにならないので止める。"
    }
}
$env:SQL_MUTATION = $null

$killed = @{}
$survived = @{}
$survivors = [System.Collections.Generic.List[string]]::new()
foreach ($module in $modules) { $killed[$module] = 0; $survived[$module] = 0 }
$started = Get-Date

try {
    $index = 0
    foreach ($point in $points) {
        $index++
        $label = "{0} #{1} {2} {3} 行 '{4}'->'{5}'" -f `
            $point.Module, $point.Index, $point.Operator, $point.Line, $point.From, $point.To

        $env:SQL_MUTATION = $point.Spec
        # **そのモジュールの行動テストだけを回す。** 他まで回すと、関係のないテストの赤が穴を隠す。
        dotnet test $tests --no-build --nologo -v q --filter "FullyQualifiedName~$($point.Module)QueryTests" 2>&1 | Out-Null
        $red = $LASTEXITCODE -ne 0

        # **注入が当たらなかった赤は、上のカナリアで既に止めてある。**
        # ここで文言を見る形は採らない——`-v q` は例外の本文を印字しないので、
        # **見ているつもりで素通りする**（実測）。
        if ($red) {
            $killed[$point.Module]++
            Write-Host ("[{0,4}/{1}] 殺した    {2}" -f $index, $points.Count, $label)
        }
        else {
            $survived[$point.Module]++
            $survivors.Add($label)
            Write-Host ("[{0,4}/{1}] 生き残り  {2}" -f $index, $points.Count, $label)
        }
    }
}
finally {
    # **必ず消す。** 空文字を入れても「立っている」と読む実装がありうるので、`$null` で消す。
    $env:SQL_MUTATION = $null
}

$elapsed = (Get-Date) - $started
$totalKilled = ($killed.Values | Measure-Object -Sum).Sum
$totalSurvived = ($survived.Values | Measure-Object -Sum).Sum

Write-Host ''
Write-Host ("[sql-sweep] 殺した {0} / 生き残り {1} / 所要 {2:mm\:ss}" -f $totalKilled, $totalSurvived, $elapsed)
foreach ($module in $modules) {
    $limit = if ($ratchet.ContainsKey($module)) { $ratchet[$module].MaxSurvivors } else { 0 }
    Write-Host ("           {0,-24} 殺した {1,3} / 生き残り {2,3}（上限 {3}）" -f `
            $module, $killed[$module], $survived[$module], $limit)
}

if ($survivors.Count -gt 0) {
    Write-Host ''
    Write-Host '生き残り（このテストでは壊れても気づけない箇所）:'
    $survivors | ForEach-Object { Write-Host "  $_" }
}

# **判定はモジュールごと。** 全体の 1 つの数で見ると、
# **`-Only` で 1 本だけ回したときに全体の上限が当たって、生き残っても成功で返る**。
$over = @()
if ($MaxSurvivors -ge 0) {
    if ($totalSurvived -gt $MaxSurvivors) { $over += "全体で $totalSurvived 件（上限 $MaxSurvivors）" }
}
else {
    foreach ($module in $modules) {
        $limit = if ($ratchet.ContainsKey($module)) { $ratchet[$module].MaxSurvivors } else { 0 }
        if ($survived[$module] -gt $limit) {
            $over += "$module が $($survived[$module]) 件（上限 $limit）"
        }
    }
}

if ($over) {
    Write-Host ''
    Write-Host "[sql-sweep] 生き残りが上限を超えた: $($over -join ' / ')" -ForegroundColor Red
    exit 1
}

exit 0
