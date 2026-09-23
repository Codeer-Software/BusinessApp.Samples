-- 016 ベンダーが配る行を REPLACE の暗黙の DELETE から守る（制度ルールの 2 表）
--
-- **1 つの規則を 1 ファイルにまとめ、いちばん後ろの番号に置く**（008・011 と同じ作法。ddl/README の各行）。
-- **014 は既に稼働 DB へ配ってある**ので、その表のトリガを 014 の末尾へ足すと、
-- **正典と配達でトリガの作られる順が食い違う**（SchemaSnapshot.TriggerOrder が表ごとに見る）。
-- **015 のぶんも同じファイルに置く**——規則が 1 つなら置き場も 1 つである。
--
--------------------------------------------------------------------------------
-- なぜ要るか
--
-- **SQLite の挙動と、接続の PRAGMA に頼らない理由は 005 の「REPLACE の暗黙の DELETE を塞ぐ」の節が持つ。**
-- **ここに写さない**——同じ説明が 2 か所にあると、片方が古くなる。
-- **制度ルールの 2 表には、その穴を塞ぐものが 1 つも無かった**——
-- 2026-09-23 に実測した 3 つの経路は、どれもエラー無しで通った。
--
--   ① INSERT OR REPLACE INTO tax_rates (id, …) VALUES (1, 'reduced', …)
--      → **`id = 1` の standard の行が音もなく消え、reduced の行が 2 本になる**。
--        重なりのトリガは `o.rate_kind = NEW.rate_kind` で同じ区分しか見ないので 1 本も鳴らない
--   ② UPDATE OR REPLACE tax_rates SET id = 1 WHERE id = 2
--      → **`id` はどのトリガの `OF` にも無いので、トリガが 1 本も発火せずに 1 行消える**
--   ③ INSERT OR REPLACE INTO transition_purchase_rates (id, …) VALUES (1, '2015-01-01', …, 99, …)
--      → **控除割合 80% の行が音もなく消え、別の期間の 99% の行に置き換わる**（014 も同じ穴だった）。
--        **同じ期間のまま差し替える文は重なりのトリガが断る**ので、通るのは期間ごと別物にしたときである
--
-- **どれも帳簿の数字を静かに変える。** 制度値が 1 つ違えば、そこから作った仕訳の税額が全部違う。
-- **この 2 表には画面が無く、書き込む経路は `sql` CLI・取込・直打ちだけ**なので、
-- **書き込みを止められる最後の場所が DDL である**（qa/03 の L-26。
-- ADR-0004 の「規約ではなく機械に守らせる」）。
--
-- **塞ぐのは「識別子の衝突」だけである。**
--   - **素の UPDATE は通す。** **配った値そのものの誤りを直すときは UPDATE する**（014 の注記・ADR-0069 の決定 5）
--   - **DELETE も通す。** **誤って配った行を引っ込める経路が要る**（ベンダーがマイグレーションで行う）
--   - **どちらも「正典と違う行」を作れるが、そこを見るのは行の同値検査である**
--     （`VendorRows`——`migrate.ps1 -Verify` と `MigrationEquivalenceTests` が毎回突き合わせる）。
--     **ここで塞ぐのは、同値検査が「行が 1 本減った」としか言えない形**——
--     **REPLACE は別の行を消すので、消えた行が何だったかを誰も言えない。**
--------------------------------------------------------------------------------

CREATE TRIGGER trg_transition_purchase_rates_no_replace_insert
BEFORE INSERT ON transition_purchase_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '経過措置の控除割合の行を、既にある行の識別子へ被せられない。')
     WHERE EXISTS (SELECT 1 FROM transition_purchase_rates o WHERE o.id = NEW.id);
END;

CREATE TRIGGER trg_transition_purchase_rates_no_replace_update
BEFORE UPDATE ON transition_purchase_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '経過措置の控除割合の行を、既にある行の識別子へ被せられない。')
     WHERE EXISTS (SELECT 1 FROM transition_purchase_rates o
                    WHERE o.id <> OLD.id AND o.id = NEW.id);
END;

CREATE TRIGGER trg_tax_rates_no_replace_insert
BEFORE INSERT ON tax_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '税率の行を、既にある行の識別子へ被せられない。')
     WHERE EXISTS (SELECT 1 FROM tax_rates o WHERE o.id = NEW.id);
END;

CREATE TRIGGER trg_tax_rates_no_replace_update
BEFORE UPDATE ON tax_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '税率の行を、既にある行の識別子へ被せられない。')
     WHERE EXISTS (SELECT 1 FROM tax_rates o WHERE o.id <> OLD.id AND o.id = NEW.id);
END;
