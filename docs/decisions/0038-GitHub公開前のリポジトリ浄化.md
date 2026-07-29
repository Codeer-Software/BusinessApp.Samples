# ADR-0038: GitHub 公開前のリポジトリ浄化（マシン固有設定の追跡外化・履歴書き換え・ベンダーサンプル除去）

- 日付: 2026-07-29
- 状態: 採用
- 関連: ADR-0024（User Secrets）・ADR-0025（環境設定の Git 除外）・ADR-0037（BusinessApp 移行）

## 背景（問題）

リポジトリを GitHub で公開することになり、追跡ファイル全 444 件と Git 履歴全コミットを対象に
機密・個人情報スキャンを実施した。API キー・秘密鍵・実メールアドレスの混入は現行・履歴とも無かったが、
次の 3 点が見つかった:

1. **ルート `.claude/settings.json` にローカル絶対パス**（`C:\Users\<user>\DEV\...` のデザイナ exe
   フルパス 16 行と Write/Edit の絶対パス 2 行）がコミットされていた。OS ユーザー名と
   ディレクトリ構造の露出であり、そもそもマシン固有設定は追跡すべきでない。
2. **Git 履歴に同種のローカル絶対パスが残存**。ADR-0025 以前の `appsettings.Development.json` /
   `designer.settings.Development.json`（履歴 167 コミット分）と `.claude/settings.json` の過去版。
   キー欄はすべて空で実害はユーザー名露出のみだが、公開に伴い完全に消すことにした。
3. **`references/SampleProject_AuthPatterns/` はベンダー（Codeer）提供サンプルのコピー**であり、
   公開リポジトリでの再配布権が未確認だった。

付随して、再配布する `LocalData/font/NotoSansJP.ttf`（SIL OFL 1.1）にライセンス全文が
同梱されていなかった。

## 決定

1. **マシン固有の許可リストは Git 追跡外の `.claude/settings.local.json` に置く**（Designer/ と同じ方式）。
   - ルート `.claude/settings.json` はポータブルなルールのみ（手動管理の恒久リスト、という役割は変えない）。
   - 雛形として `.claude/settings.local.json.sample` を追跡する。プレースホルダは
     `<リポジトリ親フォルダ>`（Write/Edit 用。Git Bash 形式 `//c/...`）と `<デザイナexeのパス>`
     （Bash 用。JSON 文字列なのでバックスラッシュは 2 個ずつ）。
   - 注意: settings.local.json / settings.json のトップレベルに `_README` キーを書くと
     Claude Code のスキーマ検証に弾かれるため、説明は本 ADR と CLAUDE.md §5 に置く。
2. **`git filter-repo` で全履歴を書き換え、ユーザー名を含む絶対パスを履歴からも除去する**
   （blob 内の OS ユーザー名文字列を `REDACTED` に置換）。全コミットハッシュが変わることは
   ユーザーが了承済み。リモートは未設定（公開はこの書き換え後が初 push）。
   書き換え前のバックアップを `../AccountingApp-backup-<日付>.git`（mirror clone）に保持する。
3. **`references/SampleProject_AuthPatterns/` は追跡外化し、履歴からも完全に削除する**
   （filter-repo の path 除去）。ローカルには参照用に残し `.gitignore` で除外。
   Designer/Project.md の該当参照には「Git 追跡外・clone 先には無い」旨を注記した。
4. **フォントライセンス同梱**: `LocalData/font/LICENSE`（Noto CJK の SIL OFL 1.1 全文）を追加。

## 影響・運用

- デザイナ exe を再ビルドしてパスが変わったら、`Designer/LocalEnvironment.md` とルート／Designer の
  両 `settings.local.json` を更新する（CLAUDE.md §5 に反映済み）。
- 新しい環境でのセットアップは `.claude/settings.local.json.sample` をコピーして実パスに置換する。
- 以後、**絶対パスや環境固有値を含む許可・設定を追跡ファイルに書かない**。追跡してよいのは
  ポータブルなルールと `*.sample` 雛形のみ（ADR-0025 の原則を Claude Code 設定にも拡張）。
- 履歴書き換え後、旧ハッシュを参照するドキュメント記述（例: 「70ef403 で誤コミット」等のコミット言及)
  は新ハッシュと一致しなくなるが、経緯説明としてそのまま残す（実害なし）。
