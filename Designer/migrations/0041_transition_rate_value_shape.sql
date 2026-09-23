-- 0041 制度ルールの値の型と版の字を守り、法源に施行版を書き足す
--
-- **CHECK ではなくトリガで書く。** 表は 0039 で作って当ててあるので、SQLite では CHECK を後から足せない
-- （008・010・011 が同じ理由でトリガにしてある）。正典の説明は ddl/014 が持つ。
--
-- **法源の訂正。** 附則 53 ① 一〜三は**令和8年10月1日施行版にしか無い**——
-- 現行版の附則 53 ① は号を持たない 1 文である。**施行版を書かないと、出典 URL を開いても指した号が見つからない**。
-- **版の字と期間は変えていない**ので、上のトリガより先に流してよい（トリガは version と valid_from しか見ない）。

UPDATE transition_purchase_rates
   SET legal_basis = '所税法等一部改正法（平成28年法律第15号）附則 53 ① 一（令和8年10月1日施行版）'
 WHERE version = 'transition_purchase_rate:2026-10-01'
   AND legal_basis = '所税法等一部改正法（平成28年法律第15号）附則 53 ① 一';

UPDATE transition_purchase_rates
   SET legal_basis = '所税法等一部改正法（平成28年法律第15号）附則 53 ① 二（令和8年10月1日施行版）'
 WHERE version = 'transition_purchase_rate:2028-10-01'
   AND legal_basis = '所税法等一部改正法（平成28年法律第15号）附則 53 ① 二';

UPDATE transition_purchase_rates
   SET legal_basis = '所税法等一部改正法（平成28年法律第15号）附則 53 ① 三（令和8年10月1日施行版）'
 WHERE version = 'transition_purchase_rate:2030-10-01'
   AND legal_basis = '所税法等一部改正法（平成28年法律第15号）附則 53 ① 三';

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
     WHERE NEW.version <> 'transition_purchase_rate:' || date(NEW.valid_from);
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
     WHERE NEW.version <> 'transition_purchase_rate:' || date(NEW.valid_from);
END;
