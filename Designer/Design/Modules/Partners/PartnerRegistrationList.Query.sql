-- 適格請求書発行事業者の登録の**独立した一覧**（docs/07 §3-4）。
--
-- **見るための一覧である。** 入力（追加・修正）の導線は取引先の詳細だけにしてあり
-- （同 §3-4）、ここは「取込の結果を一括で確かめる」ために残す（フェーズ 6）。
-- だから**出所（source）と公表システム側の更新年月日を列に出す**——
-- 手入力の画面には出さないもので、区別が要るのはこの一覧だけである。
--
-- **取引先名を出すためにクエリモジュールにした。** 親 FK を IdFieldDesign にすると
-- （ヘッダ＋明細の正典。qa/01 D-17）、参照先の名前を出せなくなる。
-- ADR-0022 / ADR-0027 と同じ判断である——一覧の中身が素の姿と違うならクエリモジュール。
--
-- パラメータは「未指定なら効かない」形で書く。CLB は空の検索欄を NULL または空文字で
-- 束縛するので、**両方を見る**（クエリモジュールの定石。_specs/QueryAndSql.md）。
--
-- 日付は date() を通して比較する。DATE 列の正規形は 'YYYY-MM-DD 00:00:00' で（qa/01 A-04）、
-- 検索欄から来る値が時刻付きとは限らないため、辞書順比較のままでは境界の 1 日が落ちうる。
SELECT
    -- 取引先の詳細へのリンクに使う（表には出さない）。
    r.partner_id                AS partner_id,
    p.code                      AS partner_code,
    p.name                      AS partner_name,
    r.registration_no           AS registration_no,
    r.valid_from                AS valid_from,
    r.ended_on                  AS ended_on,
    -- **区分値は生のまま返す。** 日本語の見出しは CLB のデザイン enum が持っており
    -- （C# の列挙型・DDL の CHECK と 3 者一致を機械で検査している）、
    -- ここで CASE を書くと 4 つ目の写しになる。
    r.end_reason                AS end_reason,
    r.published_name            AS published_name,
    r.confirmed_on              AS confirmed_on,
    r.source                    AS source,
    r.nta_updated_on            AS nta_updated_on
FROM partner_invoice_registrations r
JOIN partners p ON p.id = r.partner_id
WHERE (@p_partner_id IS NULL OR @p_partner_id = '' OR r.partner_id = @p_partner_id)
  -- 登録番号の部分一致。**利用者が打った文字をワイルドカードにしない**（仕訳帳と同じ理由）。
  -- 逃がす順序は「まず \ を、次に % と _ を」。逆にすると付けたばかりの \ をもう一度逃がす。
  AND (@p_registration_no IS NULL OR @p_registration_no = ''
       OR r.registration_no LIKE
          '%' || replace(replace(replace(@p_registration_no, '\', '\'), '%', '\%'), '_', '\_') || '%' ESCAPE '\')
  AND (@p_valid_from_from IS NULL OR @p_valid_from_from = ''
       OR date(r.valid_from) >= date(@p_valid_from_from))
  AND (@p_valid_from_to IS NULL OR @p_valid_from_to = ''
       OR date(r.valid_from) <= date(@p_valid_from_to))
-- 取引先ごとに、登録の**古い順**に並べる（登録 → 取消 → 再登録の履歴がその順に読める）。
-- **取引先コードで並べる。識別子で並べない**（代理キーは順序を持たない。qa/03 L-19）。
-- 同じ取引先に同じ日から始まる登録は 1 件だけなので（ux_partner_invoice_registrations_valid_from）、
-- この 2 つで並びは一意に決まる。
ORDER BY p.code, date(r.valid_from)
