---
title: ADR-0025: 環境固有設定ファイルの Git 除外と .sample 雛形方式
status: current
scope: 全体
audience: [開発]
updated: 2026-07-29
supersedes: []
related: []
---
# ADR-0025: 環境固有設定ファイルの Git 除外と .sample 雛形方式

- 日付: 2026-07-18
- 状態: 採用・実装済み（deploy.ps1・designcheck で検証済み）
- 関連: ADR-0024（User Secrets 移行——本 ADR はその「Git 除外はしない」判断を同日中に変更）、ADR-0017（LocalData 移設）

## 背景（ユーザー指摘 2026-07-18）

ADR-0024 で秘密は User Secrets へ出したが、`appsettings.Development.json` には**ローカル絶対パス（`C:\Users\<user>\...`）が残ったまま**コミットされていた。ユーザーの指摘: 環境固有情報を Git に入れる理由はもう無く、「環境構築に必要なら、その方法をドキュメントなりサンプルファイルなりに残せばいい」。

ADR-0024 で「除外しない」とした根拠は「設定の雛形として履歴で共有する価値」だったが、それは .sample ファイルで完全に代替できる。秘密が抜けた今、残る中身は環境固有パスだけであり、コミットし続けると①別環境で必ず差分が出る②その差分のコミット/無視の判断を毎回迫られる、という運用負荷だけが残る。

## 決定

**環境固有の設定ファイル 2 つを Git 追跡から外し、`.sample` 雛形（`<REPO_ROOT>` プレースホルダ入り）をコミットする。**

| ファイル | 対応 |
|---|---|
| `AccountingApp/AccountingApp.Server/appsettings.Development.json` | `git rm --cached` ＋ .gitignore 追加。雛形 `appsettings.Development.json.sample` を同じ場所にコミット |
| `Designer/Designer/designer.settings.Development.json` | 同上（designcheck / sql CLI の接続先。同種の問題があるため同時に対応） |
| `Designer/tools/deploy.ps1` | 除外ではなく**修正**: `$Workspace` / `$Destination` の既定値を `$PSScriptRoot` 相対に変更し、環境非依存化 |

- 環境固有でないもの（`appsettings.json`・`designer.settings.json`＝AllowCliSqlAccess 等）は従来どおりコミット
- `.claude/settings.json`（許可リスト内の絶対パス）は「手動管理の恒久リスト」というユーザー方針（ルート CLAUDE.md §5）があるためスコープ外
- セットアップ手順の正典は `LocalData/README.md`（.sample コピー→ `<REPO_ROOT>` 置換。秘密は User Secrets＝ADR-0024）

## 検証（2026-07-18）

- `git rm --cached` 後も作業ツリーのファイルは無傷、`git grep` で追跡ファイルにローカルパス残存なし（`.claude/settings.json` のみ=スコープ外）
- `deploy.ps1` を引数なしで実行 → 相対既定値で 156 エントリのデプロイ成功
- `designcheck` → findingCount 0（ワークスペース直下の .sample はデザイナに無視される）
- サーバ実行系は無変更（ディスク上のファイル内容は一切変えていないため再検証不要）

## 検討済み・見送り: 全 Git 履歴からの削除（2026-07-18 ユーザーと合意）

filter-repo による履歴からの完全削除も検討したが**見送り**。理由: ①全履歴にキー不存在は検証済みで、履歴に残る中身はローカルパスのみ＝除去の実益が小さい ②本ファイルは初回コミットから存在するため全 169 コミットのハッシュが変わり、docs（進捗台帳・デモ台本・E2E・ADR 等）の実在ハッシュ引用 42 件が宙に浮く ③リモートが無くバックアップも外部に無い。**履歴ごと公開する方針が決まった時点で改めて判断する**（その際は commit-map で docs のハッシュ張替、または公開用に履歴リセットも選択肢）。

## 注意（既知の副作用）

**ignore されたファイルは、それを追跡していた過去コミットへの checkout で黙って上書きされる**（Git は未追跡ファイルの上書きは拒否するが、ignore 済みファイルは保護しない）。つまり 0025 以前のコミットを checkout して戻ると、ローカルの `appsettings.Development.json` / `designer.settings.Development.json` が旧コミット内容（サニタイズ版。d6333a6 以前はさらに旧 `C:\Codeer.LowCode.Blazor.Local` パス）で上書きされたり、main へ戻る際に削除されたりしうる（キーはどの履歴にも無いので漏洩は起きない。壊れるのはローカル設定の方）。過去コミットを行き来した後は両ファイルの内容を確認し、必要なら .sample から再作成すること。
