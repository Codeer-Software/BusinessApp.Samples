-- 0044 ベンダーが配る行を REPLACE の暗黙の DELETE から守る（Designer/ddl/016_vendor_row_no_replace.sql）
--
-- **014 の表にも当てる。** あちらは 0039 で配って以来この穴を抱えていた
-- （2026-09-23 に実測——`INSERT OR REPLACE` で控除割合 80% の行が 99% に化けた）。
-- **CREATE TRIGGER は後から足せる**ので、表の作り直しは要らない。
--
-- 正典の説明は ddl/016 が持つ。ここは配達物なので、同じ説明を写さない。

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
