-- 560_password_policy.sql — パスワードポリシーのマスタ（ADR-0059・BusinessAppSQLite）
--
-- これまでパスワードの検証は一切無く、1 文字でも「a」でも通った（CLB の PasswordField は
-- 入力をハッシュ化するだけで、長さ・文字種の検証を持たない。サーバ側にも検証は無かった）。
--
-- 閾値をコードに埋めない（CLAUDE.md §3）。社内規程は会社ごとに違い、NIST SP 800-63B のように
-- 推奨自体が改訂される領域なので、「御社のポリシーに合わせられます」と言える形にする。
--
-- 行は 1 件のみ（id = 1 固定。CHECK でそれ以上作れないようにする）。
-- モジュール側は CanCreate:false / CanDelete:false。
CREATE TABLE IF NOT EXISTS password_policies (
  id                      INTEGER PRIMARY KEY CHECK (id = 1),
  -- 最小文字数
  min_length              INTEGER NOT NULL DEFAULT 12,
  -- 英大文字 / 英小文字 / 数字 / 記号 のうち、最低いくつの種類を含めるか（0〜4。0 = 制限なし）
  required_kinds          INTEGER NOT NULL DEFAULT 3,
  -- ユーザー識別名と同じ文字列をパスワードにできるか（開発 DB では 1 にして admin/admin を使う）
  allow_same_as_user_name INTEGER NOT NULL DEFAULT 0,
  -- 現在のパスワードと同じものへの変更を禁止するか（自己変更画面でのみ判定できる）
  forbid_reuse_current    INTEGER NOT NULL DEFAULT 1,
  -- 画面に添える補足（社内規程へのリンク等。空なら出さない）
  note                    TEXT,
  created_at              DATETIME,
  creator                 TEXT,
  updated_at              DATETIME,
  updater                 TEXT
);

-- 開発 DB の初期値（2026-08-16 ユーザー指示）:
--   開発中は ID とパスワードが同じ状態のほうが扱いやすいので、緩く始める。
--   「4 文字以上なら何でも可・ID 同一可・現在と同じでも可」。
--   本番想定の推奨値は min_length=12 / required_kinds=3 / allow_same_as_user_name=0 /
--   forbid_reuse_current=1（この画面で切り替えられることがデモの訴求点でもある）。
INSERT OR IGNORE INTO password_policies
  (id, min_length, required_kinds, allow_same_as_user_name, forbid_reuse_current, note)
VALUES
  (1, 4, 0, 1, 0, '開発用の緩い設定です。本番運用では 12 文字以上・3 種類以上・ID 同一禁止を推奨します。');
