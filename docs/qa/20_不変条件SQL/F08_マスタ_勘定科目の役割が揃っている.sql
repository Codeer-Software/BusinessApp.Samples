-- 何を保証するか: アプリが `accounts.account_role` で引く役割がすべて存在し、有効で、期待する科目区分に付いていること。
-- 違反時の意味: **SQL が無言で 0 を返す**。
--               BUG-0434（未払金）・BUG-0439（現預金・売掛・買掛）・BUG-0446（仮払消費税）は
--               いずれも「科目コードの直書きをやめて役割で引く」形に直した。
--               だから役割が 1 つ欠けた瞬間に
--                 ・資金繰り予測の当月出金から未払金が消える
--                 ・ポータル KPI の売掛金・買掛金が 0 円になる
--                 ・税行が生成できず、「ほかの人が同時に伝票を確定した可能性があります」という
--                   **まったく無関係な文言**で確定が失敗する
--               `ux_accounts_account_role` は**重複だけ**を防ぐ。欠落は誰も見ていない。
--               役割を無効科目に付けた場合も同じく静かに壊れる。
-- 出典: Designer/ddl/630・700・720・818・819・820 ／ BUG-0054 / 0434 / 0439 / 0446
-- 備考: ここが**役割リストの正典**。新しい役割を使い始めたら、この VALUES に足すこと。

WITH required(役割, 期待科目型) AS (
  VALUES ('accounts_receivable',        'asset'),
         ('accounts_payable',           'liability'),
         ('trade_payable',              'liability'),
         ('consumption_tax_receivable', 'asset'),
         ('consumption_tax_payable',    'liability'),
         ('salary_expense',             'expense'),
         ('depreciation_expense',       'expense'),
         ('disposal_loss',              'expense'),
         ('sale_loss',                  'expense'),
         ('sale_gain',                  'revenue'),
         ('sale_cost',                  'expense'),
         ('disposal_receivable',        'asset'),
         ('wip_asset',                  'asset'),
         ('wip_transfer',               'expense')
)
SELECT r.役割,
       CASE WHEN a.id IS NULL                    THEN '役割の科目が無い'
            WHEN a.is_active = 0                 THEN '役割の科目が無効になっている'
            WHEN a.account_type <> r.期待科目型  THEN '科目区分が期待と違う' END AS 違反,
       a.code AS 科目コード, a.name AS 科目名, a.account_type AS 科目区分,
       r.期待科目型, a.is_active AS 有効
FROM required r
LEFT JOIN accounts a ON a.account_role = r.役割
WHERE a.id IS NULL OR a.is_active = 0 OR a.account_type <> r.期待科目型
