-- 開発・デモ専用。**実運用に投入しない**（ADR-0031）。
--
-- 役割の分離を実機で見せるために、経理担当と経理責任者の利用者へ役割を付ける。
-- 対象ペルソナの役割（docs/02_ペルソナ.md）が画面に効いていることを確かめるためのもので、
-- ミッションの目的①（CLB の技術デモ）に効く。
--
-- **このファイルはアカウントを作らない。役割を付けるだけである。**
-- `hash` / `salt` は CLB の PasswordHashHelper が作るもので、SQL では作れない
-- （`_specs/Authentication.md`）。**先に管理画面で 2 人を作ってから**これを流す。
-- 手順は seed/README の「開発機でデモ用の利用者を用意する」。
--
-- 何度流してもよい（UPDATE だけなので冪等）。居ない利用者の行は空振りする。
--
-- **利用者名は開発者が決めた**（2026-08-31）。役職の名前（総務部長・総務部の一般）から採る。
-- `keiri_sekininsha` のような役割そのままの名前は**長くて打つのが面倒**、というのが理由である。

-- 経理担当（総務部の一般。docs/02 §の役割の表）。取引先は編集できる
-- （新規取引のたびに要る。docs/08 の台帳）。
UPDATE app_users
   SET can_access_app = 1, is_sysadmin = 0, accounting_role = 'staff', partner_role = 'editor'
 WHERE user_name = 'soumu_ippan';

-- 経理責任者（総務部長。docs/02）。**担当の画面にも入れる**——
-- 階層は条件の OR で表す（ADR-0026 §1 の追記②）。
UPDATE app_users
   SET can_access_app = 1, is_sysadmin = 0, accounting_role = 'manager', partner_role = 'editor'
 WHERE user_name = 'soumu_bucho';

-- 情報システム管理者。**会計は触れない**（開発者の決定。2026-08-28。ADR-0026 の帰結）。
-- admin は CLB がユーザーテーブルの空のときに作るので、ここでは役割だけを付ける。
UPDATE app_users
   SET can_access_app = 1, is_sysadmin = 1, accounting_role = NULL, partner_role = NULL
 WHERE user_name = 'admin';
