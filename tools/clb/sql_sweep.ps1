<#
.SYNOPSIS
    SQL ミューテーション——クエリの SQL を 1 箇所ずつ壊し、**気づけない箇所を報告する**（ADR-0056・ADR-0058）。

.DESCRIPTION
    「壊しても気づかれない箇所は、**誰もテストしていない箇所**である」を機械で言うための計器
    （docs/qa/05_観点網羅の計器.md §3）。Stryker が変異させるのは C# のソースだけなので、
    別ファイルにあるクエリの SQL は計器の外にいた。

    変異点の列挙と注入の形の正典は **tools/clb/sql_mutate.py** である。
    このスクリプトは回すだけで、演算子も位置の数え方も知らない——
    駆動役に列挙を写すと、演算子が増えた日に写しだけが古くなる
    （制約ノックアウトで SchemaKnockout と knockout.ps1 を分けたのと同じ形）。

    **殺し方が 2 つある。生成（変異点の列挙）は共通で、違いは判定だけである。**

      -Mode Tests （既定。qa/05 の A 案）
          **そのモジュールの行動テストが赤になったか**で殺す。測るのは
          「**実際のアサーションで見分けられるか**」——本当に知りたいほうである。
          注入は環境変数 SQL_MUTATION（TestDatabase.QuerySql が読む）。
          **1 点につきテストランナーを 1 回起動する**ので、掃引 1 回は分の単位でかかる。

      -Mode Rows （qa/05 の B 案）
          **行動テストが流した入力で行セットが変わったか**で殺す。測るのは**上界**——
          「いまのテストデータ・入力で**原理的に**見分けられるか」。
          注入は環境変数 SQL_MUTATION_PROBE（SqlMutationProbe が読む）。
          **1 モジュールにつきテストランナーを 1 回だけ起動する**（変異は 1 回の実行の中で全点当たる）。

      **Rows で生き残った点は、Tests でも必ず生き残る**（どの入力でも行が変わらないなら、
      どのアサーションも気づけない）。**つまり Rows の生き残りは Tests の生き残りの部分集合**で、
      **その差は「見えているのに誰も見ていない」＝アサーションの穴**である。
      **Rows の生き残りは検体かパラメータを足して潰す。差のほうは表明を足して潰す。**

      -Only <名前...> 指定したモジュールだけ回す（カンマ区切り）
      -List           回さずに変異点の一覧だけ出す

    **回すのは `<モジュール名>QueryTests` に絞る**（ADR-0055 が名前で対応づけている）——
    全部のテストを回すと、**関係のないテストが代わりに赤くなって穴を隠す**。

    **どちらのモードもファイルを書き換えない**——途中で止めても、壊れた SQL が追跡下に残らない。

    **内側の dotnet test は --no-build で回す**（ビルドはこのスクリプトが先に 1 回行う）。Schema.Tests は coverlet.collector なのでこれが効く
    （coverlet.msbuild の他のプロジェクトでは全件が赤になる。ADR-0053 の実測）。

.NOTES
    **Tests は分、Rows は秒で終わる**（実測値の正典は ADR-0058。ここには写さない——写した数は必ず腐る）。

    **-Mode Rows はコミット前フックに載っている**（開発者の決定。2026-09-20。ADR-0058 決定 9）。
    **-Mode Tests は載せていない**——分かかるので、流す契機は docs/31 §6 が持つ。

    **終了コードは 2 通りとも 1 である**——上限超過（読むべき生き残りがある）と、
    計器の故障（throw）を呼び出し側では区別できない。**印字の最後の行で見分ける。**
#>
[CmdletBinding()]
param(
    # **殺し方。** Tests は行動テストの赤で、Rows は行セットの差分で殺す（上の .DESCRIPTION）。
    [ValidateSet('Tests', 'Rows')]
    [string]$Mode = 'Tests',

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
#                **痩せると生き残りも減り、上限だけの検査は緑になる。**
#   MaxSurvivors -Mode Tests の生き残りの**上限**（行動テストが赤にならなかった点）。
#   MaxUnseen    -Mode Rows の生き残りの**上限**（どの入力でも行セットが変わらなかった点）。
#                **必ず MaxSurvivors 以下になる**——上回ったら計器か検体の取り違えである。
#   MinUnseen    -Mode Rows の生き残りの**下限**。**上限だけでは足りない**——
#                比べ方が壊れて全点が「見分けた」に倒れると生き残りは 0 になり、
#                **上限も母数の下限も全部通って「見えない 0」という最良の報告が返る**。
#                **いま残っている点はどれも説明が付いている**（ADR-0058 の実測）ので、
#                **減った日は「直した」か「計器が壊れた」のどちらかで、必ず読む価値がある**。
#   Observations -Mode Rows で当てた入力の数の**下限**。**Points と同じ理由で要る**——
#                行動テストがクエリを流す回数が減れば、見分けられない点は静かに増え、
#                **上限だけの検査では「テストを消したから減った」を捕まえられない**。
#   MaxBroken    -Mode Rows で「SQL として成り立たなくなった点」の**上限**。
#                **壊れた点はどちらにも数えない**（ADR-0058 決定 5）ので、
#                **増えるほど生き残りが減って上限を通りやすくなる**。実測は 0 件である。
#
# **どちらの上限も、割れたら (a) テストや検体を足して戻すか (b) 生き残りを読んで理由とともに動かす。
# 黙って上げない。**
#
# **モジュールを足したら、ここにも行を足す。** 表と実在のモジュールの食い違いは下で両側から検査する
# （足し忘れも、消し忘れも、欄の書き忘れも、そこで止まる）。
#
# **変異点の総数の下限は `sql_mutate.py` の MINIMUM_REAL_POINTS も持っている**——
# あちらは**コミット前フックで毎回流れる**ぶん厳しい側なので、
# ここの `Points` を下げてもあちらが先に鳴る。**緩む向きには二重管理にならない。**
$ratchet = @{
    'GeneralLedger'           = @{ Points = 93; MaxSurvivors = 5; MaxUnseen = 5; MinUnseen = 5; Observations = 57; MaxBroken = 0 }
    'JournalBook'             = @{ Points = 81; MaxSurvivors = 3; MaxUnseen = 3; MinUnseen = 3; Observations = 47; MaxBroken = 0 }
    'JournalEntryList'        = @{ Points = 80; MaxSurvivors = 4; MaxUnseen = 4; MinUnseen = 4; Observations = 51; MaxBroken = 0 }
    'PartnerRegistrationList' = @{ Points = 22; MaxSurvivors = 3; MaxUnseen = 3; MinUnseen = 3; Observations = 24; MaxBroken = 0 }
}

# **表そのものを両側から検査する。** ここを飛ばすと、
# **欄を 1 つ書き忘れた行で下限が静かに消える**——PowerShell の `0 -lt $null` は False なので、
# **`$null` の下限はどんな値でも成立する**（上限側は `0 -gt $null` が True なので必ず赤くなり、
# **下限だけが危ない側に倒れる**という非対称になる）。
$required = @('Points', 'MaxSurvivors', 'MaxUnseen', 'MinUnseen', 'Observations', 'MaxBroken')
foreach ($name in @($ratchet.Keys)) {
    foreach ($field in $required) {
        $value = $ratchet[$name][$field]
        if ($null -eq $value -or $value -isnot [int] -or $value -lt 0) {
            throw "ラチェットの $name に $field が無いか、非負の整数でない。下限が黙って消えるので止める。"
        }
    }
    if ($ratchet[$name].MaxUnseen -gt $ratchet[$name].MaxSurvivors) {
        # **Rows の生き残りは Tests の生き残りの部分集合である**（ADR-0058 決定 6）。
        # **上回る値を書けたということは、計器か検体の取り違えである。**
        throw "ラチェットの $name は MaxUnseen が MaxSurvivors を超えている（部分集合の関係が崩れている）。"
    }
    if ($ratchet[$name].MinUnseen -gt $ratchet[$name].MaxUnseen) {
        throw "ラチェットの $name は MinUnseen が MaxUnseen を超えている。"
    }
}

# モードごとに違うのは**判定に使う上限の欄と、報告の語**だけである。
$limitKey = if ($Mode -eq 'Rows') { 'MaxUnseen' } else { 'MaxSurvivors' }
$killedLabel = if ($Mode -eq 'Rows') { '見分けた  ' } else { '殺した    ' }
$survivedLabel = if ($Mode -eq 'Rows') { '見えない  ' } else { '生き残り  ' }

# **モジュールはサイドカーの実体から数える。** 名前の表を持つと、足した日に写しだけが古くなる。
# **`QueryBehaviorCoverageTests.母数はモジュール定義の側から数えている` が、この数え方と
# モジュール定義（*.mod.json）側の数え方が同じ集合を返すことを毎回表明している**ので、
# クエリ項目を別名にしたモジュールが静かに抜けることは無い。
$allFiles = @(Get-ChildItem -Path $modulesRoot -Recurse -Filter '*.Query.sql' | Sort-Object Name)
if ($allFiles.Count -eq 0) {
    throw "クエリの SQL が 1 本も見つからない（探した先は $modulesRoot）。"
}

# **表と実在のモジュールを両側から突き合わせる。** 片側だけ見ると、
# **改名した日に「新しい名前は表に無いので下限 1」と「古い名前の行が死んだまま残る」が同時に起き、
# どちらも赤くならない**（先例の SchemaKnockout.TestsThatReadSchemaText は
# 「書いた名前が実在するか」を SchemaKnockoutTests が見張っている）。
$allModules = @($allFiles | ForEach-Object { $_.Name.Replace('.Query.sql', '') })
$missing = @($allModules | Where-Object { -not $ratchet.ContainsKey($_) })
$stale = @($ratchet.Keys | Where-Object { $allModules -notcontains $_ })
if ($missing -or $stale) {
    throw ("ラチェットの表と実在のモジュールが食い違っている。" +
        "表に無いモジュール: $($missing -join ' / ')。実在しない行: $($stale -join ' / ')。")
}

$sqlFiles = $allFiles
if ($Only) {
    $names = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

    # **1 つでも当たらない名前があれば止める。** 当たった分だけ回して成功で返すと、
    # **片方だけ打ち間違えた回に「見えない 0」が返る**（全部間違えたときしか気づけない形にしない）。
    $unknown = @($names | Where-Object { $allModules -notcontains $_ })
    if ($unknown) {
        throw "-Only に知らないモジュール名がある: $($unknown -join ' / ')。実在するのは $($allModules -join ' / ') である。"
    }

    $sqlFiles = @($allFiles | Where-Object { $names -contains $_.Name.Replace('.Query.sql', '') })
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
                # **Rows は変異点をまとめて子プロセスへ渡す**ので、行そのものも持っておく。
                Raw = $_
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
    $floor = $ratchet[$module].Points
    if ($counted -lt $floor) {
        $shrunk += "$module は変異点が $counted 個しかない（下限 $floor）"
    }
}
if ($shrunk) {
    throw ("変異点の母数が痩せている。掃引は当てにならないので止める。`n  " + ($shrunk -join "`n  "))
}

Write-Host "[sql-sweep] ビルド（-Mode $Mode）"
dotnet build $tests --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) {
    # **ビルドの失敗を見逃すと、全点が非 0 で終わって「生き残り 0」という最良の報告になる。**
    throw 'ビルドに失敗した。掃引は当てにならないので止める。'
}

$killed = @{}
$survived = @{}
$broken = @{}
$survivors = [System.Collections.Generic.List[string]]::new()
foreach ($module in $modules) { $killed[$module] = 0; $survived[$module] = 0; $broken[$module] = 0 }
$started = Get-Date

function Format-Point {
    param($Point)

    return "{0} #{1} {2} {3} 行 '{4}'->'{5}'" -f `
        $Point.Module, $Point.Index, $Point.Operator, $Point.Line, $Point.From, $Point.To
}

# **入口で 3 つとも消す。** 中断した回の残骸が同じシェルに残っていると、
# **もう一方のモードで「何も壊していないのに赤い」という誤った診断になる**
# （A 案の対照実験は、B 案の観測が投げた赤を「行動テストが 1 本も無い」と読む）。
$env:SQL_MUTATION = $null
$env:SQL_MUTATION_PROBE = $null
$env:SQL_MUTATION_PROBE_REPORT = $null

if ($Mode -eq 'Tests') {
    # **対照実験は、モジュールごとに回す。** まとめて 1 回だと
    # **行動テストが 1 本も無いモジュールを見逃す**——そのモジュールでは `--filter` が 1 件も当たらず、
    # ランナーが非 0 で終わるので、**全点が「殺した」に化ける**
    # （最良の報告が返るのが、この手の道具で最も危ない壊れ方である）。
    Write-Host "[sql-sweep] 対照実験（何も壊さずに 1 巡。$($modules.Count) モジュール / $($points.Count) 点）"
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

    try {
        $index = 0
        foreach ($point in $points) {
            $index++
            $label = Format-Point $point

            $env:SQL_MUTATION = $point.Spec
            # **そのモジュールの行動テストだけを回す。** 他まで回すと、関係のないテストの赤が穴を隠す。
            dotnet test $tests --no-build --nologo -v q --filter "FullyQualifiedName~$($point.Module)QueryTests" 2>&1 | Out-Null
            $red = $LASTEXITCODE -ne 0

            # **注入が当たらなかった赤は、上のカナリアで既に止めてある。**
            # ここで文言を見る形は採らない——`-v q` は例外の本文を印字しないので、
            # **見ているつもりで素通りする**（実測）。
            if ($red) {
                $killed[$point.Module]++
                Write-Host ("[{0,4}/{1}] {2}{3}" -f $index, $points.Count, $killedLabel, $label)
            }
            else {
                $survived[$point.Module]++
                $survivors.Add($label)
                Write-Host ("[{0,4}/{1}] {2}{3}" -f $index, $points.Count, $survivedLabel, $label)
            }
        }
    }
    finally {
        # **必ず消す。** 空文字を入れても「立っている」と読む実装がありうるので、`$null` で消す。
        $env:SQL_MUTATION = $null
    }
}
else {
    # **Rows——1 モジュールにつき 1 回だけ回す。**
    # 変異は行動テストの実行の中で全点当たり、当てた入力（＝行動テストがクエリを流した接続と引数）で
    # 行セットが変わるかを SqlMutationProbe が数える。**入力コーパスを別に持たない**のがこの案の要である
    # （ADR-0058 決定 1）。
    # **対照実験にあたるものを、先に 1 回だけ回す。**
    # **A 案は「当てる前から赤くないか」を対照実験で見た**が、Rows の危ない側は
    # **比べ方が壊れて全点が「見分けた」に倒れる**ほうである（ADR-0058 決定 7）。
    # **それを撃っているのは計器そのものの検体で、掃引は `--filter` でそれを除外して回す**——
    # だから**ここで 1 回だけ、明示的に回す**（1 秒。SKILL §9-3）。
    Write-Host '[sql-sweep] 計器そのものの検体（比べ方が生きているか）'
    dotnet test $tests --no-build --nologo -v q --filter 'FullyQualifiedName~SqlMutationProbeTests' 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw '計器そのものの検体（SqlMutationProbeTests）が赤い。比べ方が生きていることを言えないので止める。'
    }

    $probeRoot = Join-Path $repoRoot 'LocalData' 'temp' 'sql-probe'
    New-Item -ItemType Directory -Force -Path $probeRoot | Out-Null

    foreach ($module in $modules) {
        $mine = @($points | Where-Object { $_.Module -eq $module })
        $report = Join-Path $probeRoot "$module.tsv"

        # **前の回の報告を必ず無効にする。** 残したまま流すと、
        # **テストが 1 度も走らなかった回に、前の回の「生き残り 0」がそのまま通る**
        # ——この手の道具で最も危ない壊れ方である。
        Set-Content -LiteralPath $report -Value '前の回の報告（この行が残っていれば、今回の掃引は届いていない）'

        Write-Host "[sql-sweep] $module（$($mine.Count) 点）"
        try {
            $env:SQL_MUTATION_PROBE = ($mine | ForEach-Object { $_.Raw }) -join "`n"
            $env:SQL_MUTATION_PROBE_REPORT = $report
            dotnet test $tests --no-build --nologo -v q --filter "FullyQualifiedName~$($module)QueryTests" 2>&1 | Out-Null
            $ran = $LASTEXITCODE
        }
        finally {
            $env:SQL_MUTATION_PROBE = $null
            $env:SQL_MUTATION_PROBE_REPORT = $null
        }

        if ($ran -ne 0) {
            throw "$module の行動テストが赤い。**当てる前から赤い**か、**計器が投げた**（原本と変異点がずれている・同じ SQL を 2 度流して行が違う）。掃引は当てにならないので止める。"
        }

        $head = @{}
        $outcome = @{}
        foreach ($line in @(Get-Content -LiteralPath $report)) {
            if (-not $line) { continue }
            $columns = $line -split "`t"
            if ($columns.Count -ne 2) {
                throw "$module の報告の欄が 2 つでない: '$line'（掃引が届いていない可能性がある）"
            }
            if ($columns[0] -match '^\d+$') { $outcome[[int]$columns[0]] = $columns[1] }
            else { $head[$columns[0]] = $columns[1] }
        }

        if ($head['module'] -cne $module) {
            throw "$module の報告が別のモジュール（$($head['module'])）のものである。"
        }
        if ([int]$head['points'] -ne $mine.Count -or $outcome.Count -ne $mine.Count) {
            throw "$module の報告の点の数が合わない（報告 $($head['points']) / 行 $($outcome.Count) / 渡した $($mine.Count)）。"
        }

        # **当てた入力の数にも下限を置く。** 行動テストがクエリを流さなくなれば、
        # **見分けられない点は静かに増える**——増える向きなので上限で気づけはするが、
        # **理由が「テストを消した」なのか「SQL を変えた」なのかは、この下限だけが言える。**
        $observed = [int]$head['observations']
        $floor = $ratchet[$module].Observations
        if ($observed -lt $floor) {
            throw "$module に当てた入力が $observed 通りしかない（下限 $floor）。行動テストがクエリを流さなくなっていないか。"
        }

        Write-Host ("           入力 {0} 通り" -f $observed)
        foreach ($point in $mine) {
            $label = Format-Point $point
            switch ($outcome[$point.Index]) {
                'killed' { $killed[$module]++ }
                'broken' {
                    # **SQL として成り立たなくなった点は、どちらにも数えない**（qa/05 §3-2）。
                    # 実物がそう壊れれば QueryModuleTests が全 SQL を流して捕まえる。
                    $broken[$module]++
                    Write-Host ("           壊れた    {0}" -f $label)
                }
                'survived' {
                    $survived[$module]++
                    $survivors.Add($label)
                }
                default { throw "$module の報告に知らない判定がある: '$($outcome[$point.Index])'" }
            }
        }
    }
}

$elapsed = (Get-Date) - $started
$totalKilled = ($killed.Values | Measure-Object -Sum).Sum
$totalSurvived = ($survived.Values | Measure-Object -Sum).Sum
$totalBroken = ($broken.Values | Measure-Object -Sum).Sum

Write-Host ''
Write-Host ("[sql-sweep] {0}{1} / {2}{3} / 壊れた {4} / 所要 {5:mm\:ss}" -f `
        $killedLabel.Trim(), $totalKilled, $survivedLabel.Trim(), $totalSurvived, $totalBroken, $elapsed)
foreach ($module in $modules) {
    Write-Host ("           {0,-24} {1}{2,3} / {3}{4,3}（上限 {5}）" -f `
            $module, $killedLabel.Trim(), $killed[$module], $survivedLabel.Trim(), $survived[$module],
        $ratchet[$module][$limitKey])
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
        $limit = $ratchet[$module][$limitKey]
        if ($survived[$module] -gt $limit) {
            $over += "$module が $($survived[$module]) 件（上限 $limit）"
        }
    }
}

# **Rows は下側も見る。** **上限だけでは「全点を見分けた」が最良の報告として通る**——
# 比べ方が壊れたときに生き残りは 0 になり、母数の下限も入力の下限も全部緑のままである
# （ADR-0058 決定 6・7）。**壊れた点の上限も同じ理由で要る**——
# **壊れた点はどちらにも数えないので、増えるほど生き残りが減る。**
if ($Mode -eq 'Rows' -and $MaxSurvivors -lt 0) {
    foreach ($module in $modules) {
        if ($survived[$module] -lt $ratchet[$module].MinUnseen) {
            $over += "$module が $($survived[$module]) 件しかない（下限 $($ratchet[$module].MinUnseen)）。潰したのなら下限を下げる"
        }
        if ($broken[$module] -gt $ratchet[$module].MaxBroken) {
            $over += "$module の壊れた点が $($broken[$module]) 件（上限 $($ratchet[$module].MaxBroken)）"
        }
    }
}

if ($over) {
    Write-Host ''
    Write-Host "[sql-sweep] ラチェットを割った: $($over -join ' / ')" -ForegroundColor Red
    exit 1
}

exit 0
