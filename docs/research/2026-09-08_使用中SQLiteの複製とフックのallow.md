---
title: 使用中 SQLite の複製と、フックの allow の効き方（2026-09-08 時点）
status: current
scope: 全体
audience: [開発]
updated: 2026-09-08
supersedes: []
related: [../decisions/0046-稼働DBの退避と復元を戻せる道具に閉じる.md, ../decisions/0044-削除はごみ箱送りに一本化しrmを機械で止める.md, ../30_作業のルール.md, ../../tools/clb/db_snapshot.ps1, ../../tools/claude/guard_delete.py]
---
# 使用中 SQLite の複製と、フックの `allow` の効き方

## 調査の目的

**稼働 DB の退避と復元の道具を作るにあたり、2 つを確かめる**
（[ADR-0046](../decisions/0046-稼働DBの退避と復元を戻せる道具に閉じる.md)）。

1. **使用中の SQLite をファイル複製すると何が起きるか。** 素の `cp` を拒み続ける根拠になる
2. **PreToolUse フックが `allow` を返すと、`.claude/settings.json` の `deny`・`ask` はどうなるか。**
   **「戻せる道具だけに `allow` を出す」という設計の、危険の大きさがこれで決まる**

**同じ事実が ADR・スクリプト・作業のルールに散りはじめたので、ここへ集める**
（[00 §4-6](../00_ドキュメント規約/本文.md)。指摘は 2026-09-08 の自己レビュー）。

## 1. 使用中の SQLite をファイル複製すると壊れる（一次情報）

**出典**: SQLite 公式「How To Corrupt An SQLite Database File」
<https://www.sqlite.org/howtocorrupt.html>（**ページを開いて確認。確認日 2026-09-08**）。

**§1.2 Backup or restore while a transaction is active** の逐語と訳。

> Systems that run automatic backups in the background might try to make a backup copy of an
> SQLite database file while it is in the middle of a transaction. The backup copy then might
> contain some old and some new content, and thus be corrupt.

（トランザクションの最中に複製すると、写しには**古い内容と新しい内容が混ざり**、壊れうる。）

> It is also safe to make a copy of an SQLite database file as long as there are no transactions
> in progress while the copy is taking place. If the previous write transaction failed, then it is
> important that any rollback journal (the *-journal file) or write-ahead log (the *-wal file)
> be copied together with the database file itself.

（進行中のトランザクションが無ければ複製してよい。**ただし前の書き込みが失敗していたなら、
ジャーナル（`-journal`）または WAL（`-wal`）も本体と一緒に複製しなければならない。**
——**「〜ならば安全」という十分条件**であって、必要条件として読まないこと。）

**同じページが安全な方法を 3 つ挙げている**——`sqlite3_rsync`、**`VACUUM INTO`**、バックアップ API。

**§1.4 は「本体を別のファイルで上書きするとき、元の DB に付いていた hot journal を消さないこと」を
壊れ方として挙げている**（同ページ。確認日 2026-09-08）。
**復元で連れ（`-wal` / `-shm` / `-journal`）も一緒に退ける根拠はここである。**

## 2. `VACUUM INTO` は一貫した写しを作る（一次情報）

**出典**: SQLite 公式「VACUUM」<https://www.sqlite.org/lang_vacuum.html>
（**ページを開いて確認。確認日 2026-09-08**）。

> The VACUUM INTO command is transactional in the sense that the generated output database is a
> consistent snapshot of the original database.

（出力は元のデータベースの**一貫した写し**である。）

> However, if the VACUUM INTO command is interrupted by an unplanned shutdown or power loss,
> then the generated output database might be incomplete and corrupt.

（ただし**予期しないシャットダウンや電源断で中断された**場合、出力は不完全で壊れうる。）

**後者が、退避を取った直後に `PRAGMA integrity_check` を通す根拠である。**

## 3. この開発機の実測

- **`PRAGMA journal_mode` は `delete`**（2026-09-08。`sql` CLI で実行）。**`-wal` は存在しない**
- **`BusinessApp.Server` が動いている最中でも、稼働 DB を排他で開けた**
  （`[System.IO.File]::Open(path, 'Open', 'ReadWrite', 'None')` が成功）。
  **したがってファイルロックの有無では「サーバが動いている」を判定できない**
- **`VACUUM INTO` は、稼働中のサーバがある状態でも成功した**（デザイナの `sql` CLI 経由）

## 4. フックの `allow` は、`settings.json` の `deny` を飛ばさない（実測）

**これは文献では決着しなかった。**
公式「Hooks reference」<https://code.claude.com/docs/en/hooks>（**開いて確認。確認日 2026-09-08**）は、
`permissionDecision: "deny"` と「無言は承認ではない」を書いているが、
**`"allow"` を返したときに権限規則がどうなるかは書いていない。**

**そこで実験した**（2026-09-08）。

| 手順 | 中身 |
|---|---|
| 前提 | `.claude/settings.json` の `deny` に `Bash(truncate:*)` がある |
| 仕込み | `guard_delete.py` に一時的な分岐を入れ、**ファイルに触れない引数**の呼び出し 1 つだけに `allow` を返させた |
| 確認 | フック本体が `{"permissionDecision": "allow"}` を標準出力へ出すことを、直に起こして確かめた |
| 実行 | その呼び出しを Bash ツールから打った |
| 結果 | **拒まれた**（`Permission to use Bash with command ... has been denied.`） |

**結論: `deny` はフックの `allow` より強い。** 一時的な分岐はそのあと外した。

**確認できなかったこと**

- **`ask` は同じ形で試していない。** `ask` に載っているのは `Read(...)` と `Edit(...)` の形で、
  このフックは `Write`・`Bash`・`PowerShell` しか見ないため、同じ実験を組めなかった
- **`allow` が「確認のプロンプトを飛ばす」こと自体**は、公式の文面でも実験でも確かめていない
  （そう振る舞っているように見える、までである）
- **`deny` に載っていない当たり先**（たとえば `tools/claude/` 配下）については、
  `deny` が無いので当然この実験は何も言わない。**そこは `allow` を返せばそのまま通る**

## 5. これをどう使ったか

- **素の `cp` は拒み続ける**（§1）。退避は `VACUUM INTO` で取り、取った直後と戻す前に
  `integrity_check` を通す（§2）
- **復元では連れも一緒に退ける**（§1 の §1.4）
- **サーバの停止判定は、プロセスの有無を主に、ファイルロックを保険にする**（§3）
- **`allow` を出す形は狭く絞る**（§4）。`deny` が最後の砦として残ることは分かったが、
  **`deny` に載っていないものは守られない**ので、絞る理由は消えていない

**判断そのものは [ADR-0046](../decisions/0046-稼働DBの退避と復元を戻せる道具に閉じる.md) が持つ。**
ここにあるのは事実だけである。
