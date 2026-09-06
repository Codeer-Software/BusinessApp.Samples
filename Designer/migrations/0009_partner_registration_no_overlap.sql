-- 登録の期間の重なりを DB でも拒む（docs/07 §3-5 R-I4・R-I5。2026-09-02）。
-- 本線の関門は PartnerRegistrationSubmitGate。ここは API を迂回した経路への最後の守り。
-- 隣接（前の行の終わりの日＝次の行の登録年月日）は許す——計上時の引き当て
-- （InvoiceRegistrationHistory.InEffectOn）が「終わりの日を含み、同日は新しいほうを採る」と
-- 決めており、どちらの制度解釈でも決定的に引ける形だから（正常形かは未確認。docs/07 §3-5）。
--
-- トリガの追加だけなので、テーブルの作り直しは要らない。
--
-- **適用の前後どちらかで、必ず既存の違反を洗い出して直すこと。** トリガは新しい書き込みしか
-- 見ないので適用自体は成功するが、既に重なっている行は InEffectOn が黙って新しいほうを採り、
-- **誤った番号が計上のたびに焼かれ続ける**（docs/07 §3-5。2026-09-02 のレビュー指摘で格上げ）。
-- 稼働 DB は 2026-09-02 に洗い出して直した（qa/02 ラウンド 32）。洗い出しの SQL:
--   SELECT COUNT(*) FROM partner_invoice_registrations a
--     JOIN partner_invoice_registrations b
--       ON a.partner_id = b.partner_id AND a.id <> b.id
--      AND date(a.valid_from) < date(b.valid_from)
--      AND (a.ended_on IS NULL OR date(b.valid_from) < date(a.ended_on));

CREATE TRIGGER trg_partner_invoice_registrations_no_overlap_insert
BEFORE INSERT ON partner_invoice_registrations
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '登録の期間が重なっている。取消・失効年月日と登録年月日を見直す。')
     WHERE EXISTS (
        SELECT 1 FROM partner_invoice_registrations o
         WHERE o.partner_id = NEW.partner_id
           AND (
                (date(o.valid_from) < date(NEW.valid_from)
                     AND (o.ended_on IS NULL OR date(NEW.valid_from) < date(o.ended_on)))
             OR (date(NEW.valid_from) < date(o.valid_from)
                     AND (NEW.ended_on IS NULL OR date(o.valid_from) < date(NEW.ended_on)))
           )
     );
END;

CREATE TRIGGER trg_partner_invoice_registrations_no_overlap_update
BEFORE UPDATE OF partner_id, valid_from, ended_on ON partner_invoice_registrations
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '登録の期間が重なっている。取消・失効年月日と登録年月日を見直す。')
     WHERE EXISTS (
        SELECT 1 FROM partner_invoice_registrations o
         WHERE o.partner_id = NEW.partner_id
           AND o.id <> NEW.id
           AND (
                (date(o.valid_from) < date(NEW.valid_from)
                     AND (o.ended_on IS NULL OR date(NEW.valid_from) < date(o.ended_on)))
             OR (date(NEW.valid_from) < date(o.valid_from)
                     AND (NEW.ended_on IS NULL OR date(o.valid_from) < date(NEW.ended_on)))
           )
     );
END;
