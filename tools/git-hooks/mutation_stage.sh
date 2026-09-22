#!/bin/sh
#
# ミューテーションテストの段（ADR-0012 §8）。**コミット前フックから呼ばれる。**
#
# **段をファイルに切り出してあるのは、検体から流せるようにするためである**——
# `docs_only.py --selftest` が、`dotnet` を切り株に差し替えてこのファイルを実際に流し、
# **飛ばす回に 1 つも呼ばれないこと**と**流す回に 5 つとも呼ばれること**を見る（ADR-0068）。
# **字を読むだけの検査では、判定の反転（`!` を 1 つ足す）も、5 段のうち 4 段を消すのも素通りした。**
#
# カバレッジ 100% でも「壊したら気づくか」は別問題で、実際にここから実漏れが 3 件出た。
# スコアの下限は「現状の生き残り（許容ノイズ）」のすぐ下に置くラチェットで、割れたら
# (a) テストを足して戻すか (b) 生き残りを読んで許容と判断した理由とともに下限を下げる。黙って下げない。
set -e

# **呼び出し側の cwd に頼らない**——段はファイルとして独立している
cd "$(git rev-parse --show-toplevel)"

# **docs の文書だけを変えた回は、この段を流さない**（ADR-0068。開発者の決定。2026-09-22）。
# **飛ばすのはこの段だけである**——`dotnet test`（38 秒）と行セットの掃引（10 秒）は合わせて 48 秒で、
# **判定が壊れたときに鳴るものとして残す**ほうが得である。
# **判定は `docs_only.py` が持ち、飛ばす回もその理由を印字する**（黙って飛ばさない）。
if python tools/git-hooks/docs_only.py; then
    echo "[pre-commit]    → この段は流さない（ADR-0068。ほかの段はすべて流れている）"
    exit 0
fi

(cd BusinessApp/BusinessApp.AccountingCore.Tests \
    && dotnet stryker --project BusinessApp.AccountingCore.csproj --break-at 84 --threshold-low 84 --reporter progress)
(cd BusinessApp/BusinessApp.AccountingCore.Server.Tests \
    && dotnet stryker --project BusinessApp.AccountingCore.Server.csproj --break-at 94 --threshold-low 94 --reporter progress)
# 取引先部品と共有インフラ（ADR-0025 §6）。**分割でこの段を持ち出し忘れると、
# テストは全部緑のままこの 3 つだけが検査の外へ落ちる。**
# 実測は取引先部品 93.85・そのサーバ層 97.37・共有インフラ 95.59（2026-09-21）。
# ラチェットなので下限を取引先部品 89 → 92・そのサーバ層 94 → 95 に上げた（共有インフラは下の理由で 93 のまま）。
# ADR-0012 §8 のラチェット（現状の生き残りのすぐ下）で、実運用では 1〜3 点の余裕を取っている。
# **上の会計コアは 76 → 84**（実測 87.90・タイムアウト 2 件。3.9 点の余裕は、実測が 1 か月で 11 点上がった直後で
# 揺れ幅が読めないのと、タイムアウト 2 件のぶん）。**生き残りの内訳と採否の正典は qa/02 のラウンド 144**——
# 会計コアは境界の 1 件が実漏れで、試験例を足して倒した（qa/03 の L-61）。
# **上げた理由を「文言まで表明したから」と書かない**——§8 は文言の変異を許容ノイズと決めている
# （qa/02 R28-18）。上がったのは実測がそうだったからである。
#
# **下限は実測より 1〜3 点下に置いてある。** タイムアウトした変異は「倒した」に数えられるが、
# 何がタイムアウトするかは機械の負荷で変わる。実際に ServerSupport は同じコードで
# 96.00 と 94.00 の両方を出した（フックの中＝テスト直後の負荷が高いときに下振れした）。
# 実測のすぐ下に置くと、コードを 1 行も変えていないのにコミットが落ちる。
# **タイムアウトが多いものほど余裕を広く取る**（2026-09-21 の実測では会計コアが 2 件で他は 0 件。
# ServerSupport は 2026-08 に 8 件出たことがある）。
(cd BusinessApp/BusinessApp.Partners.Tests \
    && dotnet stryker --project BusinessApp.Partners.csproj --break-at 92 --threshold-low 92 --reporter progress)
(cd BusinessApp/BusinessApp.Partners.Server.Tests \
    && dotnet stryker --project BusinessApp.Partners.Server.csproj --break-at 95 --threshold-low 95 --reporter progress)
(cd BusinessApp/BusinessApp.ServerSupport.Tests \
    && dotnet stryker --project BusinessApp.ServerSupport.csproj --break-at 93 --threshold-low 93 --reporter progress)
