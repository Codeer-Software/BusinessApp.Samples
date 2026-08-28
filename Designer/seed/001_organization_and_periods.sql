-- 001 事業所情報・会計年度・会計期間（docs/08_マスタ台帳）
--
-- ペルソナ（docs/02）の株式会社アルタイルシステムズ。3 月決算・第 18 期。
-- **これは「初期データ」であって「デモデータ」ではない。** 仕訳のデモデータはフェーズ 7。

INSERT INTO company_profile (name, name_kana, corporate_number, representative_name,
                             postal_code, address, phone_number, fiscal_year_end_month)
VALUES ('株式会社アルタイルシステムズ', 'カブシキガイシャアルタイルシステムズ',
        '1234567890123', '織田 智也',
        '150-0002', '東京都渋谷区渋谷1-2-3 アルタイルビル 5F', '03-1234-5678', 3);

-- 第 18 期（2026-04-01 〜 2027-03-31）。
-- 優良な電子帳簿は課税期間の初日から要件を満たす必要があるので、開始日と一致させる。
INSERT INTO fiscal_years (code, label, start_date, end_date, status, premium_ledger_from)
VALUES ('FY18', '第 18 期（2026 年度）', '2026-04-01', '2027-03-31', 'open', '2026-04-01');

-- 月次の会計期間 12 本。締めは月次 → 年度の順に行う。
INSERT INTO accounting_periods (fiscal_year_id, start_date, end_date, status) VALUES
    (1, '2026-04-01', '2026-04-30', 'open'),
    (1, '2026-05-01', '2026-05-31', 'open'),
    (1, '2026-06-01', '2026-06-30', 'open'),
    (1, '2026-07-01', '2026-07-31', 'open'),
    (1, '2026-08-01', '2026-08-31', 'open'),
    (1, '2026-09-01', '2026-09-30', 'open'),
    (1, '2026-10-01', '2026-10-31', 'open'),
    (1, '2026-11-01', '2026-11-30', 'open'),
    (1, '2026-12-01', '2026-12-31', 'open'),
    (1, '2027-01-01', '2027-01-31', 'open'),
    (1, '2027-02-01', '2027-02-28', 'open'),
    (1, '2027-03-01', '2027-03-31', 'open');

-- 伝票番号の採番。会計年度ごとに 1 行（I-17）。
INSERT INTO journal_entry_sequences (fiscal_year_id, next_entry_no) VALUES (1, 1);
