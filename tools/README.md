---
title: tools — 開発スクリプト
status: current
scope: 全体
audience: [開発]
updated: 2026-09-22
supersedes: []
related: [../docs/README.md]
---
# tools — 開発スクリプト

リポジトリの開発・検証に使う自作スクリプト置き場。**すべて Git 追跡下**なので、
絶対パス・ユーザー名・秘密情報を書かないこと（パスは `$PSScriptRoot` からの相対で解決する）。

> 依存パッケージ（Playwright の `npm install` 等）はここではなく `Designer/tools/`（Git 追跡外）に置く。
> これは CLB デザインワークスペースの規約（`Designer/CLAUDE.md`）に従うため。

## 一覧

| スクリプト | 用途 |
|---|---|
| [`clb/deploy.ps1`](clb/deploy.ps1) | `Designer/Design` を zip 化して `LocalData/designs/App.zip` に配置する（デザイナ GUI「送信」の代替。FileWatcher が hot-reload） |
| [`claude/trash.ps1`](claude/trash.ps1) | **ファイル・フォルダをごみ箱へ送る。`rm` の代わりに使う唯一の削除コマンド**（[ADR-0044](../docs/decisions/0044-削除はごみ箱送りに一本化しrmを機械で止める.md)・[33 §1](../docs/33_失わないためのルール.md)）。複数指定・ワイルドカード・`-DryRun` に対応する。**絶対パスへ解決してから保護対象を拒む**。`-SelfTest` で保護判定を検査する（コミット前フックが毎回流す） |
| [`claude/guard_delete.py`](claude/guard_delete.py) | **失うことを止める** PreToolUse フック。**削除にあたるコマンドは当たり先によらず拒み、代わりに `trash.ps1` を使えと理由文で示す**。**保護対象への `Write`（全上書き）も拒む**（`Edit` は照合があるので拒まない）。**保護対象の名前が出る上書きのコマンドも拒む**。`--selftest` は仕様表に加えて、`settings.json` の `deny`・`ask`・`allow` を正典と突き合わせる（**`allow` へ道具を足したら `ALLOWED_TOOLS` にも足す**）。コミット前フックが毎回流す |
| [`claude/normalize_eol.py`](claude/normalize_eol.py) | **作業コピーの改行を LF に保つ。** 既定は PostToolUse フック（`Write` などが書いた直後に直す）、`--check` はコミット前フックの段、`--fix` は残っているものを直す、`--selftest` は判定の検査。**リポジトリに入る中身は `.gitattributes` が正規化する**ので常に LF で、**ずれるのは作業コピーだけ**である。**`.cs` の中の CR は `CSharpStyleTests` が別に見る**（[ADR-0021 §4-3](../docs/decisions/0021-CSharpは読みやすさを損なわない範囲で最新の記法に揃える.md)。**あちらは単独の CR も拾うが `.cs` だけ**、こちらは**全追跡ファイルだが CRLF と混在だけ**——**どちらも他方の上位集合ではない**） |
| [`claude/protected_paths.json`](claude/protected_paths.json) | **削除と上書きから守るものの正典。** `trash.ps1` と `guard_delete.py` が同じこの 1 ファイルを読む（**載せる基準と読み方はファイル冒頭の `_README`** が持つ） |
| [`clb/worktree_db.ps1`](clb/worktree_db.ps1) | **ワークツリーの稼働 DB を、本体の写しで置き換える**（[README](../README.md) の「ワークツリーにも同じものが要る」）。`-Update` / `-List` / `-SelfTest`（コミット前フックが毎回流す）。**行き先を許す条件の正典は、この道具の `.DESCRIPTION`** |
| [`clb/_sqlite.ps1`](clb/_sqlite.ps1) | **`worktree_db.ps1` と `db_snapshot.ps1` が共有する小道具**（稼働 DB のパス・写しの健全性・退避と巻き戻し・使用中かの判定・空き名探し・表示用のパス畳み）。**単体では動かない** |
| [`clb/db_snapshot.ps1`](clb/db_snapshot.ps1) | **稼働 DB の退避と復元**（[ADR-0046](../docs/decisions/0046-稼働DBの退避と復元を戻せる道具に閉じる.md)・[33 §1](../docs/33_失わないためのルール.md)）。`-Save` / `-Restore` / `-List`。**写しは `VACUUM INTO` で取る**（ファイルの複製は、古い内容と新しい内容が混ざった**壊れた写し**になりうる）。**何も消さず、戻す前に必ず現状を退避する**ので、`trash.ps1` と同じく確認を待たずに実行してよい |
| [`server/wait-server.ps1`](server/wait-server.ps1) | 開発サーバ（`http://localhost:5085`）の起動を待つ |
| [`clb/sql.ps1`](clb/sql.ps1) | `sql` CLI のラッパ。結果 JSON を標準出力に **UTF-8（BOM 無し）で**返し（Bash 道具からそのまま読める。2026-09-21 まで shift_jis で化けていた）、**一時ファイルを作らない**。**DDL（`CREATE`・`DROP`・`ALTER`）は拒む**——通すのは `-File Designer/ddl/*`（追跡済み・未変更）と、`-AllowDdlOnce -Reason "<理由>"` で置いた印（次の 1 回だけ）。判定は純粋関数で、`-SelfTest` は**配線ごと**撃つ（分岐を消しても鳴る）。**なぜ拒むかは [docs/33 §2](../docs/33_失わないためのルール.md)、経緯は [ADR-0064](../docs/decisions/0064-稼働DBのスキーマはmigrateだけで動かし適用の記録はコミットごとに突き合わせる.md)**。`-SelfTest` あり |
| [`clb/migrate.ps1`](clb/migrate.ps1) | **DB マイグレーションのランナー**（ADR-0020）。`-Adopt` / `-Apply` / `-Status` / `-Verify`。**`-Verify` はスキーマの同値・未適用の有無・適用済みのチェックサムの 3 つを見る**（コミット前フックが流す。[ADR-0064](../docs/decisions/0064-稼働DBのスキーマはmigrateだけで動かし適用の記録はコミットごとに突き合わせる.md)）。書き方は [`Designer/migrations/README`](../Designer/migrations/README.md) |
| [`clb/designcheck.ps1`](clb/designcheck.ps1) | `designcheck` のラッパ。結果は固定パスに上書きし続ける |
| [`clb/lint_design.py`](clb/lint_design.py) | **CLB デザインの静的検査**。`designcheck` が緑でも壊れるもの（[qa/01](../docs/qa/01_CLB静かな失敗.md)）のうち JSON とスクリプトで判るものを検出する。`--selftest` で**検査そのものを検査する**——個々の検査を殺す／error を warn に格下げする／`main()` の配線を消す・指摘の受け皿を渡さない／印字と終了コードを壊す／検体を減らす／**言うべき直し方を薄める**／**母数の枝を殺す**、のどれでも鳴る。**通し数は書かない**（足すたびに腐る。[qa/01 §0](../docs/qa/01_CLB静かな失敗.md) と同じ理由）。最後のふたつは**実デザインに対する対照実験**である——検体は「その形なら鳴る」しか言わないので、**枝を殺すと本番で数える読みが減ること**まで見る |
| [`clb/knockout.ps1`](clb/knockout.ps1) | **制約ノックアウト**（[ADR-0053](../docs/decisions/0053-制約ノックアウトはDDLを1つずつ外し振る舞いのテストだけで赤になるかを見る.md)・[qa/05 §4](../docs/qa/05_観点網羅の計器.md)）。**DDL の制約を 1 つずつ外し、`Schema.Tests` が赤にならない制約＝誰もテストしていない制約を報告する**。`-Only` / `-Kind` / `-List`。**時間がかかるのでコミット前フックには載せていない**——流す回は [ADR-0053 決定 7](../docs/decisions/0053-制約ノックアウトはDDLを1つずつ外し振る舞いのテストだけで赤になるかを見る.md)（**開発者の決定。2026-09-20**。もとは Claude の判断）。外す点と外し方の正典は `BusinessApp.TestSupport` の `SchemaKnockout` で、このスクリプトは回すだけ |
| [`clb/sql_mutate.py`](clb/sql_mutate.py) | **SQL の変異点を数え、1 つだけ当てて書き出す**（[ADR-0056](../docs/decisions/0056-SQLミューテーションはクエリのSQLを1箇所ずつ壊し行動テストだけで赤になるかを見る.md)・[qa/05 §3](../docs/qa/05_観点網羅の計器.md)）。`list` / `spec` / `show` / `mask` / `audit` / `selftest`。**注記と文字列リテラルの中は数えない**（当てると等価ミュータントばかり増える）。**掃引はしない——数えて印字するだけ** |
| [`clb/sql_sweep.ps1`](clb/sql_sweep.ps1) | **SQL ミューテーションの掃引**（同 ADR と [ADR-0058](../docs/decisions/0058-行セットの差分で殺す掃引は入力コーパスを持たず行動テストが流した入力をその場で当てる.md)）。**クエリの SQL を 1 箇所ずつ壊し、気づけない箇所を報告する**。**殺し方が 2 つある**——`-Mode Tests`（既定。A 案。**行動テストが赤になったか**）と `-Mode Rows`（B 案。**行動テストが流した入力で行セットが変わったか**）。**2 つの違いと使い分けは ADR-0058**（`-Only` / `-List`。注入はどちらも環境変数でファイルを書き換えない）。**`-Mode Rows` はコミット前フックに載っている**（[ADR-0058 決定 9](../docs/decisions/0058-行セットの差分で殺す掃引は入力コーパスを持たず行動テストが流した入力をその場で当てる.md)。**開発者の決定。2026-09-20。旧 Q-27**）。**`-Mode Tests` は分かかるので載せていない**。変異点の正典は `sql_mutate.py` で、このスクリプトは回すだけ |
| [`clb/which_gates.py`](clb/which_gates.py) | **この回に流す計器を、差分から決めて印字する**（名前の `gates` はコミット前フックの段のこと。[81 §1](../docs/81_略語と記号.md) の「関門」ではない）（[31 §6](../docs/31_検証のルール.md) の判定を機械に当てたもの）。**流さない。決めて印字するだけ**——掃引は分かかるので、ここからも起こさない。**「流さない」も必ず印字する**（黙ると「言われなかったから流さなくてよい」に倒れる）。`--base` / `--selftest`。**未追跡の新しいファイルも見る**——`git diff` だけだと、クエリの SQL を新しく足した回に「触っていない」と報告する |
| [`clb/scaffold_module.py`](clb/scaffold_module.py) | モジュール定義の足場作り。生成後は `Design/Modules/*.mod.json` が正典 |
| [`git-hooks/pre-commit`](git-hooks/pre-commit) | コミット前の検証。`git config core.hooksPath tools/git-hooks` で有効にする |
| [`git-hooks/pre-merge-commit`](git-hooks/pre-merge-commit) | **マージが自動でコミットするときに git が呼ぶフック**。木がブランチ先端と同じなら、稼働 DB の同値検査だけを流す（[ADR-0067](../docs/decisions/0067-マージコミットは木がブランチ先端と同じなら木から決まる段を流さない.md)。下の「マージが自動でコミットするとき」） |
| [`git-hooks/docs_only.py`](git-hooks/docs_only.py) | **このコミットがミューテーションの段の入力を 1 つも変えていないか**を判定する（[ADR-0068](../docs/decisions/0068-docsの文書だけを変えた回はミューテーションの段を流さない.md)）。0 を返した回は、コミット前フックが**その段だけ**を飛ばす。**判定は足し算**（変わったパスが全部**不活性**＝段の入力になりえないなら飛ばす）で、**判定できないときは流す**。**`docs/` の下でも、テストが読む 2 本は不活性ではない**——その一覧が腐らないよう、`--selftest` が **C# の側を走査して突き合わせる**。検体は**本物の `git add` で索引を作って**撃ち、**段の配線は `dotnet` を切り株に差し替えて実際に流して見る** |
| [`git-hooks/mutation_stage.sh`](git-hooks/mutation_stage.sh) | **ミューテーションテストの段の中身**（5 プロジェクト）。**ファイルに切り出してあるのは、検体から流せるようにするため**である（同 ADR の帰結） |
| [`git-hooks/_gitenv.py`](git-hooks/_gitenv.py) | **フックの中から別のリポジトリで git を撃つための環境**（`GIT_DIR`・`GIT_INDEX_FILE`・`GITHEAD_*` を落とす）。上の 2 つの検体が使う。**単体では動かない** |
| [`git-hooks/pre_merge_commit_selftest.py`](git-hooks/pre_merge_commit_selftest.py) | **`pre-merge-commit` の判定の検体**（コミット前フックが毎回流す）。**使い捨てのリポジトリで本物の `git merge` に呼ばせ**、稼働 DB の同値検査（`pwsh`）と全段への委譲（`pre-commit`）に切り株を置いて、**流れたかどうかまで見る**。`--hook <写し>` で壊した複製を撃てる（[31 §1](../docs/31_検証のルール.md)）。**フックを直接呼ぶ検体は、git に作れない場合だけ**（マージの外・先端の木を引けない・木を書けない） |
| [`docs/lint_docs.py`](docs/lint_docs.py) | **ドキュメント規約の検査**（[docs/00 §6](../docs/00_ドキュメント規約/README.md)）。フロントマター・リンク切れ・索引の突合・**current でない文書へのコード参照**・**`current` の本文から `superseded` へのリンク**・**節への参照の指し先に節が実在するか**・**リンクの札と行き先の文書番号が一致するか**・**`updated:` の鮮度**（作業ツリーと履歴の両方）・**条項を [80 §3](../docs/80_参照法令一覧.md) の記法で書いているか**・**日付で発効する条番号の切替が残っていないか**（30 日前までは件数を印字するだけ、30 日前から warn、発効日以後は error。[ADR-0043](../docs/decisions/0043-日付で発効する条番号の切替を機械の関門に置き除外は行の印で表す.md)）。`--selftest` で検査そのものを検査する。**検査項目の正典は [00 §6](../docs/00_ドキュメント規約/README.md) の表**で、ここは道具の一覧である |
| [`docs/doclint/`](docs/doclint/__init__.py) | 上の中身。`model.py`（設定値・`Doc`・git・読み込み）／`checks.py`（検査の本数は数えない。**正典は `ALL_CHECKS`** で、`selftest.py` が突合する）／`selftest.py`（`lint_docs.py` 自身の検査）。**入口は `lint_docs.py` のまま** |
| [`docs/lint_secrets.py`](docs/lint_secrets.py) | **公開リポジトリ向けの混入検査**。追跡ファイルに絶対パス・ユーザー名・接続文字列・API キー・秘密鍵が無いかを検査する |
| [`docs/lint_secrets_allow.txt`](docs/lint_secrets_allow.txt) | 上記の誤検知抑制リスト |

## PDF のテキスト抽出（制度リサーチで使う）

国税庁などの一次情報は PDF が多い。**リポジトリに仮想環境は作らず**、uv の一時環境で読む
（パッケージは uv のキャッシュに入り、グローバル環境を汚さない。開発者の方針 2026-08-25）:

```powershell
# 例: 抽出スクリプトを書いて uv で走らせる（pypdf は実行のたびに一時環境へ解決される）
uv run --with pypdf --with cryptography python <スクリプト.py> <対象.pdf> <出力.txt> <開始ページ> <終了ページ>
```

**`--with cryptography` を落とさない。** 国税庁の一問一答は **AES で暗号化された PDF** で、
pypdf だけだと**全ページが `cryptography>=3.1 is required for AES algorithm` で抽出できない**
（2026-09-16 に電帳一問一答の 55 ページ全部で実測）。
**このとき出るのはページごとの例外なので、「スキャン画像だから本文が取れない」と誤認しやすい**
——[32 §3](../docs/32_調査のルール.md) が「取れなければ未確認として残す」と言っている相手は
**本当に画像のページ**であって、これではない。

抽出スクリプト自体は使い捨てなのでスクラッチパッドに置く（[30 §8](../docs/30_作業のルール.md)）。
Python の依存が恒常的に増えてきたら、そのとき `pyproject.toml` ＋ uv 管理の venv へ移行を検討する。

## よく使うコマンド

```powershell
# 消す（ごみ箱へ送る。rm は使わない。docs/33 §1）。複数指定・ワイルドカード可
pwsh -NoProfile -File tools/claude/trash.ps1 <パス> [<パス> ...]
pwsh -NoProfile -File tools/claude/trash.ps1 -DryRun <パス>        # 何が消えるかだけ見る

# 稼働 DB を退避する・戻す・一覧する（docs/33 §1）
pwsh -NoProfile -File tools/clb/db_snapshot.ps1 -Save -Name <名前>     # 省略すると snapshot_<日時>
pwsh -NoProfile -File tools/clb/db_snapshot.ps1 -Restore -Name <名前>  # サーバを止めてから
pwsh -NoProfile -File tools/clb/db_snapshot.ps1 -List

# ワークツリーの稼働 DB を、本体の写しで更新する（DDL を変えたあと）
pwsh -NoProfile -File tools/clb/worktree_db.ps1 -List
pwsh -NoProfile -File tools/clb/worktree_db.ps1 -Update [-Worktree <名前>]  # 1 つだけなら省略可

# デザインを稼働サーバへ反映（designcheck を通してから実行する）
pwsh -NoProfile -File tools/clb/deploy.ps1

# サーバ起動（別ターミナル）
dotnet run --project BusinessApp/BusinessApp.Server --launch-profile http

# サーバ停止（*.mod.cs / DB スキーマを変えたあとの再起動と、-Restore の前に要る）
Get-NetTCPConnection -LocalPort 5085 -State Listen -ErrorAction SilentlyContinue |
  Select-Object -ExpandProperty OwningProcess -Unique | ForEach-Object { Stop-Process -Id $_ -Force }

# 起動待ち
pwsh -NoProfile -File tools/server/wait-server.ps1 -TimeoutSec 60

# 公開前チェック（コミット前に流す）
python tools/docs/lint_secrets.py
python tools/docs/lint_secrets.py --staged

# CLB デザインの検査（designcheck の後に流す）
pwsh -NoProfile -File tools/clb/designcheck.ps1
python tools/clb/lint_design.py

# DB を触る（一時ファイルを作らない）
pwsh -NoProfile -File tools/clb/sql.ps1 -Query "SELECT COUNT(*) FROM accounts;"
pwsh -NoProfile -File tools/clb/sql.ps1 -File Designer/ddl/005_journals.sql
```

**ごみ箱と退避の扱い**（いつ使ってよいか・何を守るかの規則は [docs/33 §1](../docs/33_失わないためのルール.md)）。

- **ごみ箱から戻すのはエクスプローラで行う。** `trash.ps1` に復元機能は無い
- **`-Name` は半角英数で始まる 64 文字以内**（`[0-9A-Za-z][0-9A-Za-z._-]*`）。日本語・空白・`/` は断る。
  **同名の退避があれば上書きせず断る**
- **`db_snapshot.ps1` はパスを受け取る引数を持たない。** だから `guard_delete.py` は、この道具への言及では
  保護対象の名前を探さない（[ADR-0046](../docs/decisions/0046-稼働DBの退避と復元を戻せる道具に閉じる.md) の決定 7）——
  `-Name` に保護対象と同じ字を書いても確認は出ない
- **待ち受けは IPv4 と IPv6 で 2 行返る。** 同じプロセスなので `-Unique` で 1 つに畳む
  （畳まないと 2 度目の `Stop-Process` が「そんなプロセスは無い」と鳴く）
- **`worktree_db.ps1` はパスを受け取らない**（受け取るのはワークツリーの**名前**だけ）。行き先は稼働 DB の在処から導く——**引数で行き先を指せる形にすると、無検査の書き込み経路が 1 本開く**
- **ワークツリーの DB は [protected_paths.json](claude/protected_paths.json) に行が無い**（載せる基準は同ファイルの `_README_criteria`。ここに写さない）。**理由と、機械の側の非対称は [docs/33 §1](../docs/33_失わないためのルール.md) が持つ**
- **`-Restore` はサーバを止めてから**（道具が断る）。**どのみち戻したあとはサーバとデザイナの再起動が要る**——
  CLB は列定義を static にキャッシュするため
- **戻したら `migrate.ps1 -Verify` を打つ。** 古い退避を戻すとスキーマが巻き戻り、適用済みの記録と食い違う

**コミット前フックが次の段を上から順に流す**（`tools/git-hooks/pre-commit`。段の正典はこの表）。
**段数は数えない**（[ADR-0012 §7](../docs/decisions/0012-テスト方針とカバレッジのゲート.md)。数えると足すたびに全部書き直すことになる。スクリプトも実行時に数える）。

| 段 | 中身 |
|---|---|
| **凍結ファイルの検査** | `check_frozen.py --selftest` → `check_frozen.py`（**凍結されたファイルの変更・削除・改名**。適用済みマイグレーションと `baseline/`。[ADR-0020](../docs/decisions/0020-スキーマは現在形の正典で持ち変更は差分で配る.md)） |
| **失うことを止める道具の自己検査** | 失うことを止める道具の自己検査（`guard_delete.py --selftest`・`trash.ps1 -SelfTest`・`db_snapshot.ps1 -SelfTest`・`worktree_db.ps1 -SelfTest`・`sql.ps1 -SelfTest`・`pre_merge_commit_selftest.py`・`docs_only.py --selftest`。**前 2 つの正典は 1 つ**なので、両方がそれを読めているかもここで確かめる。**`sql.ps1` は DDL を拒む判定**——[ADR-0064](../docs/decisions/0064-稼働DBのスキーマはmigrateだけで動かし適用の記録はコミットごとに突き合わせる.md)。**最後の 1 つはマージの「木が同じなら流さない」判定**——緩めると **`main` に入る瞬間の検査が黙って消える**（[ADR-0067](../docs/decisions/0067-マージコミットは木がブランチ先端と同じなら木から決まる段を流さない.md)）） |
| **改行の検査** | `normalize_eol.py --selftest` → `normalize_eol.py --check`（**作業コピーの改行が LF か**。`git ls-files --eol` が判定の正典で、**何を LF にすべきかは `.gitattributes` が決める**——この道具は拡張子の一覧を持たない。1 秒で終わるので前に置く） |
| **秘密の検査** | `lint_secrets.py`（秘密・絶対パスの混入） |
| **ドキュメント規約の検査** | `lint_docs.py --selftest` → `lint_docs.py`（ドキュメント規約） |
| **CLB デザインの検査** | `lint_design.py --selftest` → `lint_design.py`（CLB デザインの静的検査）＋ `sql_mutate.py --selftest` ＋ `which_gates.py --selftest`（**変異点の数え方と計器の選び方。`-Mode Tests` の掃引は載せない**——理由の現在形は [ADR-0058 決定 9](../docs/decisions/0058-行セットの差分で殺す掃引は入力コーパスを持たず行動テストが流した入力をその場で当てる.md)） |
| **テスト・カバレッジ・スキーマ** | `dotnet test`（テスト・カバレッジ・スキーマ） |
| **行セットの差分で殺す掃引** | `sql_sweep.ps1 -Mode Rows`（**クエリの SQL を 1 点ずつ壊し、行セットが変わらない点を報告する**。[ADR-0058](../docs/decisions/0058-行セットの差分で殺す掃引は入力コーパスを持たず行動テストが流した入力をその場で当てる.md) の決定 9。**開発者の決定。2026-09-20**。**直前の `dotnet test` の後に置くのは、ビルドを二度しないで済むからである**（掃引は自分でビルドするので、順を入れ替えても壊れない）。`-Mode Tests` は分かかるので載せない——流す契機は [31 §6](../docs/31_検証のルール.md)） |
| **稼働 DB とスキーマ正典の同値検査** | `migrate.ps1 -Verify`（稼働 DB とスキーマ正典の同値。[ADR-0020](../docs/decisions/0020-スキーマは現在形の正典で持ち変更は差分で配る.md)） |
| **ミューテーションテスト** | `dotnet stryker`（ミューテーション。**`docs` の文書だけを変えた回は流さない**——判定は [`docs_only.py`](git-hooks/docs_only.py)、決定は [ADR-0068](../docs/decisions/0068-docsの文書だけを変えた回はミューテーションの段を流さない.md)。**飛ばすのはこの段だけで、ほかの段はすべて流れる**。**5 プロジェクト**——会計コアの純粋層とサーバ層、取引先部品の純粋層とサーバ層、共有インフラ。[ADR-0012 §8](../docs/decisions/0012-テスト方針とカバレッジのゲート.md)・[ADR-0025 §6](../docs/decisions/0025-取引先を部品として分ける.md)） |

**マージが自動でコミットするときは `pre-merge-commit` が呼ばれる**——git はマージで `pre-commit` を呼ばないので、置かないと **`main` に入る瞬間だけ誰も見ていない**。
**これからコミットする木が、取り込むブランチ先端の木と同一なら、上の表の段のうち `migrate.ps1 -Verify` だけを流す**（稼働 DB は木の中に無いから）。
**この「`migrate.ps1 -Verify` だけを流す側」を軽い経路、上の表を全部流す側を全段と呼ぶ。**
**違うなら `pre-commit` へ委譲して全段を流す**——`main` が先へ進んでいた合流と、
**判定できないとき**（取り込む先端を 1 つに決められない・これからコミットする木を書けない・先端の木を引けない）がそれに当たる
（[ADR-0067](../docs/decisions/0067-マージコミットは木がブランチ先端と同じなら木から決まる段を流さない.md)。**ブランチの最終コミットが同じ木を全段で見ているから**。開発者の決定。2026-09-20）。
**取り込む先端は `GITHEAD_<sha>` 環境変数から取る**——**`MERGE_HEAD` はこのフックからは見えない。**
**git が書くのはマージが止まったあと**（衝突・`--no-commit`・フックが拒んだとき）で、
**止まったマージを締めくくる `git commit` は `pre-commit` が受ける**（同 ADR の帰結）。
判定だけ見たいときは次を打つ。**印字してマージを止める**ので、`git merge --abort` で戻す。

```powershell
$env:PRE_MERGE_COMMIT_EXPLAIN = 1; git merge --no-ff <ブランチ>; Remove-Item Env:\PRE_MERGE_COMMIT_EXPLAIN
```

**軽い経路でも 0 を返さない**のは、返すと `migrate.ps1 -Verify` を飛ばしたままマージが成立するからである。
**スクリプト自身の終了コードは 全段=10／軽い経路=11 だが、`git merge` 経由で見えるのは git の 1** である。

有効にするのは clone 後の 1 回だけ。

```powershell
git config core.hooksPath tools/git-hooks
```

`*.mod.cs`（CLB スクリプト）を変更した場合と DB スキーマを変更した場合は、
deploy だけでは反映されない。**サーバの再起動が必要**。
