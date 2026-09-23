-- 0042 版の断りは、日付が ISO の形をしているときだけ鳴らす（0041 の作り直し）
--
-- **0041 の版の条件が、日付の書式の断りより先に鳴っていた。**
-- **SQLite のトリガの発火順は仕様上 undefined** で、実測では**後に作ったものから鳴る**——
-- 0041 のトリガは最後に作られるので、**日付が読めない値のときに「版の形が違う」と言い、
-- 直すべき「日付の形が違う」が出なかった**（2026-09-23 に検体で出した）。
--
-- **2 本を作り直す。** 末尾に作り直すので、表ごとのトリガの作られた順は正典（ddl/014）と同じままである。
-- 正典の説明は ddl/014 が持つ。

DROP TRIGGER trg_transition_purchase_rates_value_shape_insert;
DROP TRIGGER trg_transition_purchase_rates_value_shape_update;

CREATE TRIGGER trg_transition_purchase_rates_value_shape_insert
BEFORE INSERT ON transition_purchase_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「控除割合」は 1 以上 100 以下の整数で入れる。')
     WHERE typeof(NEW.rate_percent) <> 'integer';
    SELECT RAISE(ABORT, '「版」「法源」「出典 URL」は文字で入れる。')
     WHERE typeof(NEW.version) <> 'text'
        OR typeof(NEW.legal_basis) <> 'text'
        OR typeof(NEW.source_url) <> 'text';
    SELECT RAISE(ABORT, '「版」は「transition_purchase_rate:<有効期間の開始日>」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS substr(NEW.valid_from, 1, 10)
       AND NEW.version <> 'transition_purchase_rate:' || substr(NEW.valid_from, 1, 10);
END;

CREATE TRIGGER trg_transition_purchase_rates_value_shape_update
BEFORE UPDATE OF valid_from, rate_percent, version, legal_basis, source_url ON transition_purchase_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「控除割合」は 1 以上 100 以下の整数で入れる。')
     WHERE typeof(NEW.rate_percent) <> 'integer';
    SELECT RAISE(ABORT, '「版」「法源」「出典 URL」は文字で入れる。')
     WHERE typeof(NEW.version) <> 'text'
        OR typeof(NEW.legal_basis) <> 'text'
        OR typeof(NEW.source_url) <> 'text';
    SELECT RAISE(ABORT, '「版」は「transition_purchase_rate:<有効期間の開始日>」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS substr(NEW.valid_from, 1, 10)
       AND NEW.version <> 'transition_purchase_rate:' || substr(NEW.valid_from, 1, 10);
END;
