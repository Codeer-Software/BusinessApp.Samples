---
title: ddl — スキーマ定義
status: current
scope: 会計コア
audience: [開発]
updated: 2026-09-07
supersedes: []
related: [../Project.md, ../../docs/10_会計ドメイン設計.md, ../../docs/12_マスタ台帳.md, ../../docs/decisions/0020-スキーマは現在形の正典で持ち変更は差分で配る.md]
---
# ddl — スキーマ定義

会計コアのテーブル定義。**新規 DB を作るときは**番号順に `sql` CLI で流す（自前で DB 接続しない）。
既にある DB へは直接流さず、マイグレーション（下記）で配る。

```powershell
pwsh -NoProfile -File tools/clb/sql.ps1 -File Designer/ddl/001_organization.sql
```

`tools/clb/sql.ps1` がデザイナ exe のパスを `../LocalEnvironment.md`（Git 追跡外）から解決し、
結果 JSON を標準出力に返す。**一時ファイルを作らない**（[30 §8](../../docs/30_作業のルール.md)）。

**スキーマを変えたらサーバとデザイナの再起動が要る**（列定義が static にキャッシュされるため）。

**本フォルダは現在形の正典**であり、その場で書き換える
（[ADR-0020](../../docs/decisions/0020-スキーマは現在形の正典で持ち変更は差分で配る.md)）。
**既存 DB への配布は [`../migrations/`](../migrations/README.md) が担う**。書き換えたら
同じ変更をマイグレーションとしても書き、`tools/clb/migrate.ps1 -Apply` → `-Verify` で稼働 DB に当てる。
マイグレーションの書き方の規約は migrations の README が持つ。
網は 2 つ: **マイグレーションの書き忘れ・書き間違い**は同値テスト（`MigrationEquivalenceTests`）が、
**稼働 DB そのもののスキーマのずれ**はコミット前フックの `-Verify` が捕まえる。

## テスト

`BusinessApp.Schema.Tests` が**この DDL ファイルそのものを**インメモリ SQLite に適用して検査する。
テスト用に書き写したスキーマを使わないので、写し間違いも「直したつもり」も起きない。

```powershell
dotnet test BusinessApp.slnx
```

検査するのは 4 種類。

1. **スキーマの形** — 主キー・日付列の宣言型・金額の型・論理削除列の不在・他部品のテーブルを参照しないこと
2. **制約の実効** — 改ざん・重複・FK 違反などの書き込みが実際に拒まれること
3. **初期データ** — `Designer/seed/` が制約に反しないこと、設計上の約束が守られていること
4. **区分値の一致** — 同じ区分値が **DDL の `CHECK` ・ CLB のデザイン enum ・ C# の列挙型**の
   3 か所で一致していること。どれか 1 つを直し忘れると、コンパイルも designcheck も通ったまま
   「画面で選べるのに保存できない」という壊れ方をする

## ファイル

| # | ファイル | 内容 |
|---|---|---|
| 001 | [`001_organization.sql`](001_organization.sql) | 事業所情報 |
| 002 | [`002_periods.sql`](002_periods.sql) | 会計年度・会計期間 |
| 003 | [`003_consumption_tax.sql`](003_consumption_tax.sql) | 税区分 |
| 004 | [`004_masters.sql`](004_masters.sql) | 勘定科目・補助科目・部門・**取引先（取引先部品のもの** — [ADR-0025](../../docs/decisions/0025-取引先を部品として分ける.md)**）** |
| 005 | [`005_journals.sql`](005_journals.sql) | 仕訳・仕訳明細・伝票番号の採番（マスタではなくデータ） |
| 006 | [`006_partner_registrations.sql`](006_partner_registrations.sql) | 適格請求書発行事業者の登録（有効期間つき。**公表情報の写し**。取引先部品のもの） |
| 007 | [`007_auth.sql`](007_auth.sql) | 利用者アカウント（**認証部品のテーブル**。本体は CLB のもので、役割の列だけを間借りする——[ADR-0032](../../docs/decisions/0032-認証部品のapp_usersを正典に迎え入れる.md)） |

各テーブルの扱い（所有・誰が編集するか・版と削除）は [docs/12_マスタ台帳](../../docs/12_マスタ台帳.md) が持つ。

適用順は**外部キーの向き**で決まっている。税区分（003）を勘定科目（004）より先に作るのは、
勘定科目が既定税区分を参照するためである（逆向きの参照は無い）。

## 規約

`ClaudeCodeForDesigner/Docs/DatabaseGuidelines.md` に従う。とくに次を外さない。

- 主キーは `id INTEGER PRIMARY KEY AUTOINCREMENT`、外部キーは `{単数形}_id INTEGER`
- テーブル・列は snake_case 英語
- **日付列を `TEXT` で宣言しない。** `DATE` / `DATETIME` / `TIME` で宣言する
  （`TEXT` だと CLB が `DateOnly` を `MM/dd/yyyy` 文字列にして比較し、年跨ぎの範囲検索が壊れる）
- **金額は `INTEGER`**（整数円）。`REAL` を使わない

## この DDL に固有の方針

### 検証を DB にも二重に置く

`AccountingCore` の検証（[ADR-0008](../../docs/decisions/0008-CLBとCSharpライブラリの責務分担.md)）が
第一の関門だが、**CSV 取込・API・スクリプト・手作業の SQL のどれからでも通る最後の関門**として
`CHECK` 制約・`UNIQUE` 制約・トリガを置く。
[ADR-0004](../../docs/decisions/0004-優良な電子帳簿への準拠と仕訳の不変性.md) の
「規則を迂回する経路を作らない」を、規約ではなく DB に守らせる。

**二重防御は「片方を緩めても、もう片方が残る」ことに意味がある。** 逆に**片方だけ厳しくすると気づけない**
（C# だけ厳しくすると CSV 取込が抜け、DB だけ厳しくすると画面で通って保存で落ちる）ので、
両側を 1 つの表で並べる。**この表そのものを機械で突き合わせることは採らない**——表は文書であって定義ではなく、読む形を作っても壊れたときに気づけない。
**行ごとの列集合の一致は、定義（トリガの定義文・デザイン JSON・C# の定数）どうしをテストで突き合わせられるものだけ守る**
（[20 §4](../../docs/20_実装の原則.md) の表。読めなかったら落ちる表明を必ず持つ）。

| 不変条件 | DB 側の担保 | C# 側の担保 |
|---|---|---|
| I-01 伝票単位で貸借一致 | — （行をまたぐので `CHECK` では書けない） | `I-01` |
| I-03 有効な会計期間に属する | — | `I-03` ＋ `E-PERIOD-ORPHAN` |
| I-04 締め済み期間に計上できない | — | `I-04` |
| I-05 計上済み仕訳は変更も削除もされない | `journal_entries` / `journal_lines` の `BEFORE UPDATE` / `BEFORE DELETE` トリガ（**明細を計上済みの伝票へ付け替える UPDATE も止める**。`NEW` 側を見るトリガが要る。qa/03 L-25） | `I-05` |
| I-06 訂正・取消は原仕訳を持つ | `CHECK`（自己参照の禁止も） | `I-06` |
| I-13 損益科目の明細には部門がある | — （科目区分が要る） | `I-13` |
| I-14 外部伝票の二重計上を防ぐ | `idempotency_key` の `UNIQUE` | — （フェーズ 6 の投入 API） |
| I-17 伝票番号を再利用しない | `UNIQUE (fiscal_year_id, entry_no)` ＋ 採番表 ＋ 下書きに番号を持たせない `CHECK` | `I-17` ＋ `E-FISCAL-YEAR` |
| 入力年月日は変えられない | `BEFORE UPDATE` トリガ | — （サーバが値を決める） |
| 取消は原仕訳 1 本につき 1 本 | 部分 UNIQUE インデックス | `E-REVERSAL-DUPLICATE` |
| 再計上は原仕訳 1 本につき 1 本 | 部分 UNIQUE インデックス | `E-CORRECTION-DUPLICATE` |
| 再計上より先に取消がある | — （順序は行をまたぐ） | `E-CORRECTION-ORIGINAL-LIVE` ＋ `E-CORRECTION-BEFORE-REVERSAL` |
| 金額は正の整数円 | `CHECK (amount > 0)` | `E-AMOUNT` |
| 行番号は正の整数で伝票内に一意 | `CHECK (line_no > 0)` ＋ `UNIQUE` | `E-LINE-NO` |
| 消費税行だけが親行を持つ | `CHECK` | `E-TAX-PARENT` ＋ `E-TAX-INHERIT` |
| 部門「全社共通」は 1 件だけ | 部分 UNIQUE インデックス | — |
| 使用中のマスタは意味を変えられない（[ADR-0038](../../docs/decisions/0038-使用中のマスタは意味を変えられない.md)） | 4 マスタの `BEFORE UPDATE OF <意味を決める列>` トリガ ＋ REPLACE で id を乗っ取る経路を止める `BEFORE INSERT` / `BEFORE UPDATE OF id` トリガ | `MasterMeaningGate` |
| 単一法人（[ADR-0005](../../docs/decisions/0005-単一法人に徹する.md)） | `CHECK (id = 1)` | — |

行をまたぐ判定・マスタを引く判定は DB の `CHECK` では書けないので、`JournalEntryValidator` だけが担保する。

### 伝票番号は会計年度ごとの連番にし、振り直さない

弥生会計も会計期間（年度）単位で採番しており、番号の付け方を強制する法令は無い
（[市販ソフト比較](../../docs/research/2026-08-24_市販会計ソフト比較_伝票番号の採番.md)）。

**弥生会計は再付番できるが、本プロジェクトは作らない。** 伝票番号は帳簿間の相互関連性
（電帳規則 5 ⑤一ロ）を担保する一連番号なので、後から振り直すと出力済みの帳簿との対応が切れる。
欠番があること自体は問題にならない。

### 認証部品のテーブルに外部キーを張らない

`creator` / `updater` は CLB の予約名で、値は認証部品の利用者識別子だが、
**`app_users` への外部キーは張らない。**

利用者アカウントは会計コアの責務ではなく別部品のものであり（[ADR-0029 §6](../../docs/decisions/0029-所有は相手抜きで存在するかで導き粒度は決断で決める.md)）、
DB 制約で結ぶと**会計コアが認証部品なしでは立ち上がらなくなる**。
C# のモジュール依存（[ADR-0013](../../docs/decisions/0013-機能単位のモジュール分割と依存方向.md)）と
同じ規律を DB にも当て、スキーマテストが「他部品のテーブルを参照していないこと」を検査する。

### 論理削除を使わない

CLB の予約名 `LogicalDelete` は**どのテーブルにも置かない**。

- 仕訳は計上したら消せない（I-05）。論理削除の列があること自体が誤った経路になる
- マスタは削除ではなく**無効化**する（`is_active` の意味も含めて [docs/12_マスタ台帳](../../docs/12_マスタ台帳.md) が持つ）

## 保留リスト

（なし。取引先の登録テーブルまわりの未確認は [13_取引先設計](../../docs/13_取引先設計.md) の保留リストが持つ）
