---
title: ドキュメント規約（総則）
status: current
scope: 全体
audience: [開発]
updated: 2026-08-28
supersedes: []
related: [../README.md, フロントマター.md, 本文.md]
---
# ドキュメント規約 — 総則

> 本リポジトリの `.md` 文書すべてに適用する。**唯一の入口は [docs/README.md](../README.md)**（何をするときに何を読むか）。
> 本書はその索引が成立し続けるための約束事を定める。

**規約は 3 冊に分かれている。節番号は 3 冊を通して一意なので、`00 §4-6` のように節だけで指してよい。**

| 冊 | 持っている節 |
|---|---|
| 本書（総則） | §1 なぜこの規約があるか・§2 適用範囲・§5 1 文書 1 責務と行数・§6 検証・§7 運用 |
| [フロントマター](フロントマター.md) | §3（必須フィールド・`status` の判定・ADR の特則・任意フィールド） |
| [本文の書き方](本文.md) | §4（現在形・追記の禁止・保留・呼称・公開・出典・`superseded` へのリンク） |

## 1. なぜこの規約があるか

長期開発では文書が増え続け、**どれが正か分からなくなり**、読む側（開発者・Claude の両方）の
コンテキストを圧迫する。量そのものは問題ではない。読まなくていい文書を読まずに済むなら、
`historical` はいくら増えてもコストにならない。

したがって本規約の目的は 2 つだけである。

1. **読まなくていい文書を、開いて数行で判別できること**
2. **`current` の本文が、常に「今どうなっているか」だけを語っていること**

フロントマターはその手段であって目的ではない。

## 2. 適用範囲

- 対象: **Git 追跡下の `.md` すべて**（`docs/` 配下＋`CLAUDE.md`・`README.md`・
  `Designer/Project.md`・`Designer/ddl/README.md`・`Designer/seed/README.md`・
  `Designer/migrations/README.md`・`tools/README.md`）
- 対象外:
  - `Designer/CLAUDE.md` と `Designer/ClaudeCodeForDesigner/` … **デザイナが再生成する生成物**。
    手で直しても次のワークスペース更新で失われる（前者は追跡下にあるが対象外）
  - Git 追跡外のファイル全般（`Designer/LocalEnvironment.md`・`LocalData/` 配下など。
    **`LocalData/README.md` も追跡外**なので対象にならない）
  - ベンダー同梱ファイル（`BusinessApp/` 配下）
  - `.claude/skills/` … フロントマターの項目がスキルの仕様で決まっており、本規約とは別物

## 5. 1 文書 1 責務・行数

- 目安は **250 行**。超えたら分割を検討する（機械的な上限ではなく、警告として出す）
- **台帳・ログ型（`growth: append`）は行数警告の対象外**。代わりに**最初から分割方針を持たせる**
  （月別・番号帯別）
- **「引くもの」は行数の対象外。** `decisions/`（ADR）と `research/`（制度リサーチ）は
  必要なときに 1 本だけ開く文書であり、通読しない。行数警告からも管理指標からも除く
- 管理指標は「**毎回・頻繁に読む `current` の総行数**」とする（上記を除いた合計）

### 5-1. 250 行はモデルの制約ではない。編集上の規律である

**この数字を「文脈が溢れるから」と読まないこと。** 溢れない。実測と一次情報は次のとおり
（2026-08-28 調査）。

- 本リポジトリの密度は **1 行 ≒ 29 トークン**（`CLAUDE.md` 253 行 = 7.3k トークン）。
  250 行 ≒ 7k トークン、350 行 ≒ 10k トークン
- [Anthropic のプロンプト設計ガイド](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/claude-prompting-best-practices)が
  「大きな文書」として構造化を勧め始めるのは **20k トークン以上**——この密度なら **約 700 行**にあたる
- [Claude Opus 5 は 1M トークンが既定かつ最大](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-opus-5)で、
  指示追従・ツール呼び出し・推論は窓全体で一貫すると公表されている
- 長文脈の劣化として報告されてきたものの多くは、**長さそのものではなく
  「答えを含む部分が失われた」「意味の近い無関係な情報が混ざった」ことによる**
  （[Distractor-Aware Truncation, arXiv 2608.03297, 2026-08](https://arxiv.org/html/2608.03297)。
  素朴に中間を切り落とすと Opus は −0.433、無関係な情報だけを除く切り方だと +0.008）

**つまり効くのは行数ではなく、1 回の読み込みに混ざる「いま関係のない行」の割合である。**
350 行でも全部が同じ主題なら無害で、200 行でも 3 つの主題が混ざれば 2/3 が邪魔になる。
だから本節の本体は行数ではなく**「1 文書 1 責務」**であり、250 行はその代理指標にすぎない。

それでも 250 を保つ理由は 3 つ（開発者の決定。2026-08-28）。

1. **本プロジェクトが実際に踏んだ失敗は文脈溢れではなく、腐りと矛盾である**
   （[qa/02](../qa/02_自己レビュー記録.md) のラウンド 17・18 で延べ 128 件）。
   短さは「同じ事実の置き場が減る」ことで効く。**単一責務の文書になれば、
   長い文書の一部だけが腐るという面倒が減る**
2. [Claude Fable 5 のガイド](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-fable-5)は
   「前世代向けに書いた指示はしばしば **prescriptive すぎ、出力品質を下げうる**」と述べている。
   文書を長くしてよい理由にはならず、むしろ逆である
3. **開発者が一息で読める量である**

## 6. 検証（`tools/docs/lint_docs.py`）

```
python tools/docs/lint_docs.py            # 規約違反の検査（error / warn）
python tools/docs/lint_docs.py --stats    # current の行数など指標
python tools/docs/lint_docs.py --selftest # 検査そのものが空回りしていないか
```

検査項目:

| 種別 | 内容 |
|---|---|
| error | フロントマターが無い／`status` が 3 値以外／必須フィールド欠落／`supersedes`・`related` のリンク先が実在しない |
| error | `superseded` なのに `related` が空／ADR の実ファイルと `decisions/README.md` の行が食い違う |
| error | **`current` でない文書をコードのコメントが参照している**（ファイル名・ADR 番号・`docs/NN` の短縮形。後継へ張り替える。歴史として残す行は `lint-docs:ignore`。凍結される `Designer/migrations/` は対象外。開発者の提案 2026-08-25） |
| error | **`current` の文書の本文が `superseded` の文書へリンクしている**（**規則と免除の一覧は [§4-10](本文.md) が持つ**。2026-08-28） |
| error | **本文（フロントマター以外の行）を変えたのに `updated:` が今日でない**（作業ツリーと HEAD を比べる。フロントマターだけの変更なら動かさなくてよい。**マージ・cherry-pick・rebase の途中は飛ばす**——飛ばしたことは印字する。開発者の指示 2026-08-27） |
| error | **履歴と `updated:` の突合**——本文を最後に変えたコミットの author 日より `updated:` が古い（作業ツリーの検査を迂回して入った分を拾う。1 日の猶予つき） |
| error | **文書を読めなかった**（黙って捨てると「検査文書数」だけが減り、何も見ないまま緑になる） |
| warn | `current` で 250 行超（`growth: append` は除く）／`docs/*.md` が `README.md` の索引に載っていない |
| warn | `current` の冒頭 20 行に `更新:` `追補:` が積まれている／`current` の本文に未処理マーカーが残っている |

**検査を流したら、検査そのものも振り返る**（[CLAUDE.md §4-2](../../CLAUDE.md)）。
結果を使って終わりにせず、有用だったか・時間に見合ったか・どう良くできるかを毎回考える。

## 7. 運用（毎回の作法）

1. 文書を追加・改名したら **[docs/README.md](../README.md) の索引**を更新する（lint が突合する）
2. 判断をしたら **ADR** を起こし、[`decisions/README.md`](../decisions/README.md) に 1 行足す（lint が突合する）
3. 後回しにするものは**保留リスト**へ（[§4-4](本文.md)）
4. コミット前に `python tools/docs/lint_docs.py` と `python tools/docs/lint_secrets.py` を流す
5. **マージ前に別の目を通す**（[CLAUDE.md](../../CLAUDE.md) §3-2-1）。
   **lint が見るのは、フロントマター・リンク・日付という「形」だけである。**
   [§4-6](本文.md) の重複も、**書いてある内容の矛盾も、機械では捕まらない**
