-- 085_org_users.sql — 組織・ユーザーの標準 seed（2026-07-09 全面再編 → docs/decisions/0018）
-- 命名規約: ユーザー識別名 = {部プレフィックス}_{役職}、表示名 = 「{部名} {役職}{名前}」、パスワード = 識別名
-- 役職: bucho(部長)/buchodairi(部長代理)/kacho1(第一課長)/kacho2(第二課長)/ippan(一般社員)
-- 5部 x 5名 + admin。role 列は 100_roles.sql の後（105_org_roles.sql）で設定する
-- ハッシュ: PBKDF2-HMAC-SHA256 / 100,000 反復 / 32B / base64（CLB PasswordHashHelper と同方式・実測検証済み）

INSERT OR IGNORE INTO app_users (user_name, name, hash, salt, department_id) VALUES
    ('admin', 'システム 管理者', 'sWO/THJ+jvWVuQfQYrgDCAM6eZUh75j2I98T/9i+MBQ=', 'p1+u9MumsyFlvQnFVr+Gf9+mn/CcZ95BuUCOzCgFw+E=', NULL),
    ('eigyo_bucho', '営業部 部長一郎', 'ZjgMAoK9RZpnz/+An3Az+FvhKkfkXwzoaWM8fejlzHo=', 'FmDXKpqJeFlaWhUeUWqLzLFVxPM+VDN/L7fN4+ua6ts=', (SELECT id FROM departments WHERE code = '20')),
    ('eigyo_buchodairi', '営業部 部長代理次郎', 'F8kwd6O07sTxHBqhHhof6DEIbc6oMPLArxGg2FasK0Y=', 'tFr/uI70+klQimNW7/QygczjZV63m943xi45ZFSOAOY=', (SELECT id FROM departments WHERE code = '20')),
    ('eigyo_kacho1', '営業部 第一課長花子', 'B6c21pp1ihBnYDkBDQiMscdKYQ7wrsQtPGmaULAHrr0=', 'jFLrtpqsN/NupaiqhPV+O74TIJpms1HqNODfRaq9wBs=', (SELECT id FROM departments WHERE code = '20')),
    ('eigyo_kacho2', '営業部 第二課長健太', '+uvgTxVl+N/dpecwRJjp0iCsQ3fT4km8wf/QsTFgSEY=', 'b/D4H0Fgxl0Zp5NBENBEm0XZgHibqkT52n7OHvb82hE=', (SELECT id FROM departments WHERE code = '20')),
    ('eigyo_ippan', '営業部 一般みさき', 'A2kfEDcGyipvNOBkAqDDhT6fxczCsyH8nMOAj403W+c=', 'vQKuMHarO9FKwd0N1tKW/gBPzcRL6qmFo4GFanomCio=', (SELECT id FROM departments WHERE code = '20')),
    ('kaihatsu1_bucho', '開発1部 部長一郎', 'Awkg59RoSXILtxvhsj1uUl5cjXRulOe4KXY0rozPS/o=', '09a8nospQZKS/pSjjm+OvgTnPBEAF5apyarirRjS/jQ=', (SELECT id FROM departments WHERE code = '31')),
    ('kaihatsu1_buchodairi', '開発1部 部長代理次郎', 'nfalIaafWS0p6nEiDNooTBa3Qez/vp4mWxNrYURxIhk=', 'GXP6runqR2dKxBIHuJP63BbOB91Z6LOagHsf4/d05fM=', (SELECT id FROM departments WHERE code = '31')),
    ('kaihatsu1_kacho1', '開発1部 第一課長花子', 'KfX2GVho1WIADAaDdO6yyQ+0ibyup/h0ti4ggDK5dPU=', 'H2ZXCCwkDHoy6MV3jx2tvIMWxKyYvU4PdG6Rb5/RBDg=', (SELECT id FROM departments WHERE code = '31')),
    ('kaihatsu1_kacho2', '開発1部 第二課長健太', '/C4/MuAWM2RtZ1lVUtZ3QfxWvwxHd/WKGWvu9ou2qRo=', 'PqOdbwaKt+iIB85h/pD21ht9EjTuKscWAiv1GYDQP4k=', (SELECT id FROM departments WHERE code = '31')),
    ('kaihatsu1_ippan', '開発1部 一般みさき', 'PpbvZhXyxUNWzVCSVtQT3iukPP78/RYn71ZvVk52N2g=', '+9YVWSM47qchGUCOLmCXFi9SZ6ExLecFGcWlgBoEqAE=', (SELECT id FROM departments WHERE code = '31')),
    ('kaihatsu2_bucho', '開発2部 部長一郎', '5i1Es71T12wbNqRsgoP5BGw777fWqnwk+do1RQa4GX8=', '1vCrfdSSgLTPRL0hqQtZ3BtxGpqDgFJkAeIHW9Wh9fE=', (SELECT id FROM departments WHERE code = '32')),
    ('kaihatsu2_buchodairi', '開発2部 部長代理次郎', 'PQ/k1trkfoJPsWfkSAOACjpDfz690fBmnWpZFWTQSpQ=', 'PVo+ZWUMdMpoQbzQXS8KFuhIHzpmS1FuWvTtmPdI1Ck=', (SELECT id FROM departments WHERE code = '32')),
    ('kaihatsu2_kacho1', '開発2部 第一課長花子', '9HsBMjZ0UgMfRyBetL9Rb1iOT7Tfb8irWaqZv+0lhz8=', 'TwPzS9ZdiKCiIzEY3SsQAgHOfr8CbOpWKu3WSwOcmuI=', (SELECT id FROM departments WHERE code = '32')),
    ('kaihatsu2_kacho2', '開発2部 第二課長健太', '3GYT4oaLtvhVnVE8NFqENLZNRpEVz1zn68+10GyUylk=', 'mnaSgkbNkk0YqNn3U1zAwDBRqd4v+qYzN4MxO998VlM=', (SELECT id FROM departments WHERE code = '32')),
    ('kaihatsu2_ippan', '開発2部 一般みさき', 'yXKH524Vl/q2u4rswmPvwt8Pe7/7gn32C9Wg3OdB640=', 'r6zJ1GdXhzLij7h4SD9QWHD4hLWRuiVKacKBplXaWYg=', (SELECT id FROM departments WHERE code = '32')),
    ('saas_bucho', 'SaaS事業部 部長一郎', 'hYGbl2LoukPxc6hlU8ETZ/ozmW7rTzAEQUCuT4XpQGE=', '+w1QXIQ2/cYeRk7Tmf78N2MdYCDuLT2ea7SeQrkrbR8=', (SELECT id FROM departments WHERE code = '40')),
    ('saas_buchodairi', 'SaaS事業部 部長代理次郎', 'oHau5Ss/pQ2H20JpkcM34QEa/4bXKxaasEjk2QUNkJY=', 'WvKo13G6MANzn6TXM7NgxpvtDjMRvehKu22IssC0iaM=', (SELECT id FROM departments WHERE code = '40')),
    ('saas_kacho1', 'SaaS事業部 第一課長花子', 'KKDd3D+J/xGq/1Qpjl+lU8TVEJYB2xpie2LfcDAT88E=', '3N33lPBQWLrr42ir11dC6tR2I0H57LtHj4gcuffXsaY=', (SELECT id FROM departments WHERE code = '40')),
    ('saas_kacho2', 'SaaS事業部 第二課長健太', 'EK6H6InpHVMs5S92MWY1oRYbNr5ZGeT/hb9ias704tE=', 'ITA5EOEWbaBjVRGd8x+ZyL9w809gujG96gHp4UyWdpM=', (SELECT id FROM departments WHERE code = '40')),
    ('saas_ippan', 'SaaS事業部 一般みさき', 'l89caKAKlxlS1h50KsehRr0NGBzIi42WvTshXb26DuY=', 'ihhY7j9I7qTXFB0sCSGovbeukL0fBKA1SjxL012diNA=', (SELECT id FROM departments WHERE code = '40')),
    ('soumu_bucho', '総務部 部長一郎', 'BRk6x1AvvMrTMbYIb0gYVCH6MthhXWmlaqcekFtzkSw=', 'J0zHDDFRht0eDckhko7Wr081KI8phOFhvC/U/1j3xEc=', (SELECT id FROM departments WHERE code = '10')),
    ('soumu_buchodairi', '総務部 部長代理次郎', 'QQHSrQWoM3hcWvtbXSnG09ro7cifqFkXsmu7MLRGnj0=', '/70pQVqkCcQnhIK/QDJppt0s4kYkIdEzlQ+u8o5ax/g=', (SELECT id FROM departments WHERE code = '10')),
    ('soumu_kacho1', '総務部 第一課長花子', 'k5sfLODmfLREFeLyLro9XuSsdt83//WCofEn3hE2+44=', 'o/+vFKYkAC4RwdDVB4M18RPclVp/o7Aa4QmlMjUDjL0=', (SELECT id FROM departments WHERE code = '10')),
    ('soumu_kacho2', '総務部 第二課長健太', 'nnwwxLfO1AkclCXYl5yoHjFQfAOkfAx4oaKIhk55J/o=', 'JTXP6vrCtGppKTXWHokTN/jZdgHQJYlxb46RW0bpyiM=', (SELECT id FROM departments WHERE code = '10')),
    ('soumu_ippan', '総務部 一般みさき', 'T91rhhoG1YTTQcg9xNnLi2CqM8hy8kue4GLsZ5MiLfw=', 'TrEOF8rneKx7UVRK2iPjJrh5dioxr+JyfRGW+fI6Pps=', (SELECT id FROM departments WHERE code = '10'));
