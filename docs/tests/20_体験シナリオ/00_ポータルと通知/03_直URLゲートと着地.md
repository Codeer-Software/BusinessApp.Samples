---
title: 体験シナリオ ポータル-03: 直 URL ゲートと着地（フレーム素 URL・権限外ブロック・職務分掌）
status: current
scope: ポータル・通知
audience: [テスト, 開発]
updated: 2026-08-11
verified: 2026-08-11
modules: []
verifies: [Shell/PortalHome, Shell/NotificationCenter, Expense/MyApplication, Expense/ApprovalInbox, Expense/ExpenseSettlementQueue, Timesheet/TimeEntry, Timesheet/TimeEntryAdmin, Sales/Quote, Sales/Invoice, Accounting/JournalEntryBoard, Purchasing/VendorInvoice, Management/BudgetVsActual, MasterBusiness/Partner, MasterSystem/AppUser]
screens: []
supersedes: []
related: []
---
# 体験シナリオ ポータル-03: 直 URL ゲートと着地（フレーム素 URL・権限外ブロック・職務分掌）

> 作成: 2026-08-09 ／ 所要時間: 約15分 ／ 影響: なし（閲覧のみ）
> 主役: **soumu_bucho**（経理）／**kaihatsu2_shinjin**（一般）／**admin**（システム管理者）。パスワードはユーザー名と同じ。
> 「/ExpenseStaff」のような**フレーム素の URL がそのフレームの既定業務画面に着地する**こと（旧 /XXX/Top 規約は 2026-08-05 に全廃）、権限外 URL がブロックされることを、ブラウザのアドレスバーから直接確かめる。

## 物語

サイドバーのリンクは信用できる。では、リンクを介さずに URL を直接叩いたら？
ブックマーク・リロード・URL 共有はどれも「直 URL」なので、ここが守られていないと権限設計は絵に描いた餅になる。アドレスバーを武器に、全フレームの門番を試して回る。

## 前提

| 項目 | 内容 |
|---|---|
| 操作方法 | `http://localhost:5085` + 下表のパスをアドレスバーに直接入力（例: `http://localhost:5085/Accounting`） |
| 着地とゲートの正 | docs/13 §5（着地表）と docs/10 §2（フレームゲート表） |

### フレーム素 URL の一覧（着地＝そのフレームで一番使う業務画面）

| # | URL | 必要な権限 | 着地する画面 |
|---|---|---|---|
| 1 | `/` （= /Main/PortalHome） | なし（全ログインユーザー） | 業務ポータル |
| 2 | `/ExpenseStaff` | 経費精算（既定ON） | 申請 |
| 3 | `/ExpenseApprover` | 承認者 | 承認待ち |
| 4 | `/ExpenseAccounting` | 経理機能 | 精算処理待ち |
| 5 | `/Timesheet` | 工数入力（既定ON） | 工数入力 |
| 6 | `/TimesheetAccounting` | 経理機能 | 工数管理（経理） |
| 7 | `/SalesStaff` | 営業機能 | 見積 |
| 8 | `/SalesBilling` | 経理機能 | 請求書 |
| 9 | `/Accounting` | 経理機能 | 振替伝票 |
| 10 | `/Purchasing` | 経理機能 | 仕入先請求書 |
| 11 | `/ManagementApprover` | 承認者 | 予実対比 |
| 12 | `/ManagementFull` | 経理機能 | 予実対比 |
| 13 | `/MasterBusiness` | 経理機能 | 取引先 |
| 14 | `/MasterAdmin` | システム管理者 | ユーザー管理 |

---

## ステップ 1: 経理で「入れる URL」を全部叩く（soumu_bucho）

**soumu_bucho** でログインし、上表の 2〜13 のうち**システム管理以外の全 URL**を順に直接入力する（soumu_bucho は経理機能＋承認者＋経費・工数フラグ保有なので 2〜13 全部に入れる）。

- **期待結果**: それぞれ上表の「着地する画面」がいきなり開く。「トップ」「ホーム画面」のような中間ページは存在しない。
- **確認ポイント**: 着地した画面のサイドバーで、該当リンクがアクティブ表示（現在地ハイライト）になっている。

## ステップ 2: 権限外 URL のブロック（soumu_bucho → /MasterAdmin）

そのまま `/MasterAdmin` を直接入力する。

- **期待結果**: **ブロックされる**（不正なアクセスの旨のエラー表示になり、ユーザー管理画面は表示されない）。経理といえどもシステム管理者フラグが無ければ組織・承認ガバナンスには触れない。

## ステップ 3: 一般社員の門前払い（kaihatsu2_shinjin）

ログアウト → **kaihatsu2_shinjin** でログインし、次を順に直接入力する。

| URL | 期待結果 |
|---|---|
| `/ExpenseStaff` | ○ 申請一覧に着地（経費精算フラグは既定 ON） |
| `/Timesheet` | ○ 工数入力に着地 |
| `/ExpenseApprover` | × ブロック（承認者ではない） |
| `/SalesStaff` | × ブロック（営業機能なし） |
| `/Accounting` | × ブロック（経理機能なし） |
| `/MasterAdmin` | × ブロック |

- **確認ポイント**: ブロックは**画面が開かない**ことが本質。サイドバーにリンクが出ないだけの「見せない」ではなく、URL を知っていても入れない「入れない」になっている（UserReadCondition による多層防御）。

## ステップ 4: admin の職務分掌（admin）

ログアウト → **admin** でログインし、次を直接入力する。

| URL | 期待結果 |
|---|---|
| `/MasterAdmin` | ○ ユーザー管理に着地（admin の唯一の職場） |
| `/Accounting` | × ブロック |
| `/ExpenseStaff` | × ブロック（admin は経費精算フラグも OFF） |
| `/Timesheet` | × ブロック（工数入力フラグも OFF） |

- **確認ポイント**: admin はスーパーユーザーではない。「システムを管理する人」と「業務をする人」がデータ（権限フラグ）で分離されている——isAdmin の特別扱いはコード上も全廃済み（ADR-0043）。

## ステップ 5: 特殊な URL を 2 つ確かめる

1. **通知だけセグメント名が違う**: `/Main/Notification` を直接入力 → 通知センターが開く。画面を実現しているモジュール名は `NotificationCenter` だが、URL セグメントは `Notification`。**モジュール名 = URL とは限らない**唯一の例外。
2. **旧 /Top 規約の廃止**: `/ExpenseStaff/Top` のような旧形式 URL を入力 → 業務画面には着地しない（該当セグメントはもう存在しない）。古いブックマークやドキュメントの /Top 表記は、すべて素の `/ExpenseStaff` に読み替える。

## つまずいたときは

- **ブロック時の見た目**: エラーメッセージ表示（不正なアクセス等）。真っ白なページになった場合はリロードするか URL を素のフレーム名に直す。
- **kaihatsu2_shinjin で /ExpenseApprover に入れてしまう**: そのユーザーが承認者化されている（部門メンバーの課長/部長行 or テンプレ個人指名で is_approver が立つ）。admin のユーザー管理で「承認者」欄を確認。
- **着地画面が本書と違う**: docs/13 §5 が正。フレームの TopPageModuleDesign が変更された可能性があるので、変更したなら本書も追随させること。
