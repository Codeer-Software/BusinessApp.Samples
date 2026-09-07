---
title: migrations — 既存 DB への配達物
status: current
scope: 会計コア
audience: [開発]
updated: 2026-09-08
supersedes: []
related: [../ddl/README.md, ../../docs/decisions/0020-スキーマは現在形の正典で持ち変更は差分で配る.md]
---
# migrations — 既存 DB への配達物

スキーマの**正典は [`../ddl/`](../ddl/README.md)（現在形）**であり、本フォルダは
**既存 DB へ変更を届けるための差分**である（[ADR-0020](../../docs/decisions/0020-スキーマは現在形の正典で持ち変更は差分で配る.md)）。
読み手はここを読まなくてよい。今のスキーマを知りたければ `ddl/` だけ読む。

```powershell
pwsh -NoProfile -File tools/clb/migrate.ps1 -Status   # 適用状況
pwsh -NoProfile -File tools/clb/migrate.ps1 -Apply    # 未適用を番号順に適用
pwsh -NoProfile -File tools/clb/migrate.ps1 -Verify   # 稼働 DB と ddl/ の同値検査
```

適用後は**サーバとデザイナの再起動が要る**（列定義が static にキャッシュされるため）。

## スキーマを変えるときの手順

1. **`ddl/` を直接書き換える**（正典は現在形。履歴を再生しない）
2. **同じ変更を `NNNN_名前.sql` としてここに書く**（単一連番・小文字スネークケース。
   例: `0001_add_posted_by.sql`）。DDL と DML（制度ルールの行）を混在させてよい。
   ただし**データ行の同値の網はフェーズ 3 で入る**（[ADR-0020](../../docs/decisions/0020-スキーマは現在形の正典で持ち変更は差分で配る.md) 帰結）。
   それまで DML マイグレーションは書かない
3. `dotnet test` — 同値テスト（`MigrationEquivalenceTests`）が
   「baseline ＋ migrations の再生 ≡ ddl/」を検査する
4. `migrate.ps1 -Apply` で稼働 DB に当て、`-Verify` で確かめてからコミットする

## 書き方の規則（機械が守る）

- **BEGIN / COMMIT / ROLLBACK / SAVEPOINT を書かない。** ランナーが 1 本 1 トランザクションで包み、
  `schema_migrations` への記録も同じトランザクションに入れる（`sql` CLI は `--file` も `--query` も
  文ごとの autocommit でトランザクションを張らない。2026-08-25 実測・CLB 1.3.20）。
  同値テストも同じ包み方で再生するので、規則をすり抜けたトランザクション文は
  ランナーの BEGIN/COMMIT と衝突してテストが落ちる
- **`PRAGMA foreign_keys` を書かない。** SQLite はトランザクション内ではこの PRAGMA を
  **黙って無視する**ので、書いても効かない（テストと本番の両方で。接続の外部キーは ON。
  2026-08-25 実測・CLB 1.3.20 同梱の SQLite）。テーブルの作り直しで一時的に外部キー検査を遅らせたいときは、
  トランザクション内でも効く **`PRAGMA defer_foreign_keys = ON;`** を使う（COMMIT 時に検査され、
  COMMIT で自動的に戻る）
- **末尾は `;` で終える**（最後の文の後ろにコメントを置かない）。ランナーが記録の INSERT を
  後ろに連結するため
> **この配下の `docs/NN` は、2026-09-06 の改番より前の番号である**
> （[ADR-0040](../../docs/decisions/0040-docs直下の番号は十の位でカテゴリを表す.md)）。
> 適用済みファイルは書き換えないので、**旧番号が残るのが正しい**
> （コメントだけの差分の例外は**記録の訂正のためのもので、改番のような整形には使わない**）。
> **とくに `docs/04` は、いまは別の生きた文書（実装計画と現在地）に着地する**——
> ここだけは「古い」と気づけないので、読むときは改番前の対応表で引き直すこと。
> `lint_docs.py` も同じ理由でこの配下を検査から外している。

- **適用済みのファイルは編集しない・改名しない。** チェックサム（SHA-256・改行 LF 正規化）で
  ランナーが拒む。直したければ次の番号で新しいマイグレーションを書く。
  開発中にまだどの DB にも当てていないなら、DB を捨てて作り直してよい（Down は無い）。
  **コメントだけの編集でも拒まれる**（実例は qa/02 の R45-11）。その形なら、差分がコメントだけであることを
  `git diff` で確かめたうえで、`schema_migrations` のその行の `checksum` を現在のファイルの値に書き直す
  （値は `-Status` の表示で分かる）——スキーマは変わっていないので「記録の訂正」であり移行ではない、と整理した
  （この整理は Claude が起こし、**開発者が手順として承認した。2026-09-08**。
  ADR-0020 の機械強制を人手で外す運用なので、**差分がコメントだけであることの確認を省かない**
  ——この歯止めは Claude が置いたものである）
- **正規化テキストで正典と同値になるように書く**（同値テストの比較は
  「コメント除去＋空白正規化したテキストの完全一致」。`TestSupport/SchemaSnapshot.cs`）。
  - **列の追加** — `ALTER TABLE ... ADD COLUMN`。正典側でも新しい列を
    **列リストの末尾（テーブル制約の前）**に置く。SQLite は追加列をちょうどその位置に
    挿入するので、テキストが一致する（2026-08-25 実測）
  - **それ以外の形の変更**（列の削除・変更・制約の追加・トリガやインデックスの変更）—
    **正典の定義文をそのまま写した作り直し**で行う（下のレシピ）。
    写し忘れ・写し間違いは同値テストが落とす
- **`ALTER TABLE ... RENAME TO` を使わない。** SQLite の RENAME は、改名した表の格納テキストを
  引用付き（`CREATE TABLE "x" ...`）に変え、さらに**他のテーブルの FK 句・トリガ本体・ビュー定義の
  中の参照テキストまで黙って書き換える**（2026-08-25 実測）。どちらも正典のテキストと一致しなくなり、
  マイグレーションが触っていないはずのオブジェクトまで同値が壊れる

## テーブルの作り直しのレシピ（2026-08-25 実測）

RENAME を使わずに、同一トランザクション内で完結させる（トランザクションはランナーが張る）。

```sql
-- ① 外部キー検査を COMMIT まで遅らせる（トランザクション内でも効く唯一の方法。
--    PRAGMA foreign_keys はトランザクション内では黙って無視される）
PRAGMA defer_foreign_keys = ON;
-- ② データを退避する。AUTOINCREMENT の表は採番の現在値も控える（⑧ で使う）
CREATE TABLE x_rebuild AS SELECT * FROM x;
CREATE TABLE x_rebuild_seq AS SELECT seq FROM sqlite_sequence WHERE name = 'x';
-- ③ 旧表を消す（自表のインデックス・トリガも一緒に消える。他表から参照されていても ① で通る）
DROP TABLE x;
-- ④ 正典の CREATE TABLE を逐語で写す
-- ⑤ 退避から書き戻す（列を明示する）
INSERT INTO x (...) SELECT ... FROM x_rebuild;
-- ⑥ 退避を消す
DROP TABLE x_rebuild;
-- ⑦ インデックス・トリガを正典から逐語で写す
-- ⑧ AUTOINCREMENT の表は採番の現在値を復元する（DROP で sqlite_sequence の行が消え、
--    書き戻しでは「現存する最大 id」までしか戻らない。末尾の行が過去に消されていた場合、
--    復元しないと消えた id が再利用され、帳簿間のリンクが別の伝票を指す）。
--    ⑤ で 1 行も書き戻していないと sqlite_sequence に行が無く UPDATE が空振りするので、
--    **必ずこの 2 文の形で書く**（片方だけだと採番が黙って戻る）
UPDATE sqlite_sequence SET seq = max(seq, (SELECT seq FROM x_rebuild_seq))
 WHERE name = 'x' AND EXISTS (SELECT 1 FROM x_rebuild_seq);
INSERT INTO sqlite_sequence (name, seq)
    SELECT 'x', seq FROM x_rebuild_seq
    WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'x');
DROP TABLE x_rebuild_seq;
```

計上済みの仕訳を持つ表（`journal_entries` / `journal_lines`）の作り直しは、
③〜⑤ が I-05 のトリガを一時的に外した状態でデータを動かすことに注意する。
レシピの中でデータを**変えない**こと（変えるならそれは別のデータマイグレーションであり、
I-05 の例外を作る決定として ADR が要る）。

**制約を後付けする作り直しでは、既存の行が新しい制約に反していると ⑤ の書き戻しで失敗して
丸ごと巻き戻る**（データは失われないが、適用できない）。マイグレーションのヘッダに
「適用前に矛盾行を数える SELECT」を書いておき、当たった人が手で直してから適用できるようにする
（DML マイグレーションはフェーズ 3 まで無いので、直す正規の経路が他に無い）。

## baseline/ — 同値テストの起点

`ddl/` の凍結コピー ＋ `VERSION`（どこまで畳んだか）。**手で編集することは決してない。**
掃除（ADR-0020 §4）のときだけ、現在の `ddl/` のコピーで置き換えて前進する。

掃除の手順: 存在するすべての DB が適用済みの番号まで畳む。
① `baseline/*.sql` を現在の `ddl/*.sql` のコピーで置き換える
② `VERSION` に畳んだ番号を書く ③ その番号以前のマイグレーションを消す。
`schema_migrations` の行は消さない（記録として残す）。

**④ コミットする前に、使い捨ての印を置く。** ①〜③ は凍結ファイルの変更・削除なので、
コミット前フックの 1 段目（`tools/clb/check_frozen.py`）が**必ず拒む**。

```
git rev-parse --git-dir       # 印を置く場所（ふつうは .git）
<git-dir>/allow-frozen-edit   # に、畳んだ理由を 1 行書いて置く
```

**印は読まれた時点で消える。1 コミットしか効かない。**
フックの迂回フラグは使わない（`tools/claude/block_no_verify.py` が塞いでいる）。
**掃除以外でこの印を使ったなら、それは事故なので差し戻す。**
