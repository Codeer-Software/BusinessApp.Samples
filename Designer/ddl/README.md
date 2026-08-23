---
title: ddl — スキーマ定義
status: current
scope: 会計コア
audience: [開発]
updated: 2026-08-24
supersedes: []
related: [../Project.md, ../../docs/04_会計ドメイン設計.md, ../../docs/decisions/0006-マスタの三層分類.md]
---
# ddl — スキーマ定義

会計コアのテーブル定義。**適用は番号順**で、`sql` CLI から流す（自前で DB 接続しない）。

```powershell
pwsh -NoProfile -File tools/clb/sql.ps1 -File Designer/ddl/001_organization.sql
```

`tools/clb/sql.ps1` がデザイナ exe のパスを `../LocalEnvironment.md`（Git 追跡外）から解決し、
結果 JSON を標準出力に返す。**一時ファイルを作らない**（[CLAUDE.md](../../CLAUDE.md) §3-2-1）。

**スキーマを変えたらサーバとデザイナの再起動が要る**（列定義が static にキャッシュされるため）。

## テスト

`BusinessApp.Schema.Tests` が**この DDL ファイルそのものを**インメモリ SQLite に適用して検査する。
テスト用に書き写したスキーマを使わないので、写し間違いも「直したつもり」も起きない。

```powershell
dotnet test BusinessApp.slnx
```

検査するのは「スキーマの形」（主キー・日付列の宣言型・金額の型・論理削除列の不在・
他部品のテーブルを参照しないこと）と「制約が実際に書き込みを拒むこと」の 2 種類である。

## ファイル

| # | ファイル | 内容 | ADR-0006 の層 |
|---|---|---|---|
| 001 | [`001_organization.sql`](001_organization.sql) | 事業所情報 | ② 会計設定 |
| 002 | [`002_periods.sql`](002_periods.sql) | 会計年度・会計期間 | ② 会計設定 |
| 003 | [`003_consumption_tax.sql`](003_consumption_tax.sql) | 税区分 | ③ 会計マスタ |
| 004 | [`004_masters.sql`](004_masters.sql) | 勘定科目・補助科目・部門・取引先 | ③ 会計マスタ |
| 005 | [`005_journals.sql`](005_journals.sql) | 仕訳・仕訳明細・伝票番号の採番 | データ |

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

| 不変条件 | DB 側の担保 |
|---|---|
| I-05 計上済み仕訳は変更も削除もされない | `journal_entries` / `journal_lines` の `BEFORE UPDATE` / `BEFORE DELETE` トリガ |
| I-06 訂正・取消は原仕訳を持つ | `journal_entries` の `CHECK` |
| I-14 外部伝票の二重計上を防ぐ | `idempotency_key` の `UNIQUE` |
| I-17 伝票番号を再利用しない | `UNIQUE (fiscal_year_id, entry_no)` ＋ 単調増加の採番表 |
| 金額は正の整数円 | `journal_lines.amount` の `CHECK (amount > 0)` |
| 部門「全社共通」は 1 件だけ | 部分 UNIQUE インデックス |
| 単一法人（[ADR-0005](../../docs/decisions/0005-単一法人に徹する.md)） | `company_profile` の `CHECK (id = 1)` |

I-01（貸借一致）・I-03（有効な会計期間）・I-13（損益科目の部門）は
**行をまたぐ判定**なので DB の `CHECK` では書けない。`JournalEntryValidator` が担保する。

### 認証部品のテーブルに外部キーを張らない

`creator` / `updater` は CLB の予約名で、値は認証部品のユーザー識別子だが、
**`app_users` への外部キーは張らない。**

ユーザーは会計コアの責務ではなく別部品のものであり（[ADR-0006](../../docs/decisions/0006-マスタの三層分類.md)）、
DB 制約で結ぶと**会計コアが認証部品なしでは立ち上がらなくなる**。
C# のモジュール依存（[ADR-0013](../../docs/decisions/0013-機能単位のモジュール分割と依存方向.md)）と
同じ規律を DB にも当て、スキーマテストが「他部品のテーブルを参照していないこと」を検査する。

### 論理削除を使わない

CLB の予約名 `LogicalDelete` は**どのテーブルにも置かない**。

- 仕訳は計上したら消せない（I-05）。論理削除の列があること自体が誤った経路になる
- マスタは削除ではなく**無効化**する（[ADR-0006](../../docs/decisions/0006-マスタの三層分類.md)）。
  `is_active` は「入力時の候補に出すか」の意味であり、過去データの表示・検索を妨げない

## 保留リスト

- 2026-08-23 伝票番号を**会計年度ごとの連番**にしたが、市販ソフトの実際の挙動を未調査
  （CLAUDE.md §2-6）。年度ごと → 通し番号への変更は採番表の初期値を引き継ぐだけで済むが、
  逆向きは既存伝票の振り直しになるため、安全な側に倒してある（未了）
- 2026-08-23 取引先の**適格請求書発行事業者としての登録状況**は有効期間つきの別テーブルが要る
  （[docs/06 §1](../../docs/06_消費税設計.md)）。1 列では表せないので、フェーズ 3 で追加する（未了）
