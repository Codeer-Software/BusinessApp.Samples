namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// マスタと台帳の <c>CHECK</c>・<c>UNIQUE</c>・外部キーが、実際に書き込みを拒むこと。
/// </summary>
/// <remarks>
/// <para><b>全部、2026-09-13 の制約ノックアウトの初回掃引で「誰も見張っていない」と出た制約である</b>
/// （ADR-0053・qa/02 のラウンド 88）。区分値は <c>EnumConsistencyTests</c> が
/// <b>CHECK の字面</b>で見ていたが、<b>DB が本当に弾くか</b>は誰も確かめていなかった。</para>
/// <para><b>テストを足したら、その点だけ掃引し直して「殺せた」に変わることを確かめる</b>——
/// <c>pwsh -NoProfile -File tools/clb/knockout.ps1 -Only &lt;点&gt;</c>。
/// <b>別の関門が先に鳴っていて、名前の主張を検査できていない</b>という形を、この回だけで 5 件踏んだ。</para>
/// </remarks>
public class MasterConstraintTests
{
    // ---- company_profile ----

    /// <summary>法人番号は 13 桁の数字だけ。<b>形式が崩れた番号は照会にも申告にも使えない。</b></summary>
    [Theory]
    [InlineData("'123456789012'")]
    [InlineData("'12345678901234'")]
    [InlineData("'123456789012A'")]
    [InlineData("'１２３４５６７８９０１２３'")]
    public void 事業所の法人番号は13桁の数字でなければならない(string corporateNumber)
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(
            db,
            $"UPDATE company_profile SET corporate_number = {corporateNumber} WHERE id = 1;",
            "corporate_number GLOB");
    }

    /// <summary>決算月は 1〜12。<b>範囲の外だと会計年度の期間計算が破綻する。</b></summary>
    [Theory]
    [InlineData("0")]
    [InlineData("13")]
    public void 決算月は1から12までしか入らない(string month)
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(
            db,
            $"UPDATE company_profile SET fiscal_year_end_month = {month} WHERE id = 1;",
            "fiscal_year_end_month BETWEEN 1 AND 12");
    }

    // ---- accounting_periods ----

    [Fact]
    public void 会計期間は実在しない会計年度にぶら下がれない()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByForeignKey(
            db,
            "INSERT INTO accounting_periods (fiscal_year_id, start_date, end_date) VALUES (999, '2026-06-01', '2026-06-30');");
    }

    [Fact]
    public void 会計期間の締め状態は決めた値しか入らない()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(
            db,
            "INSERT INTO accounting_periods (fiscal_year_id, start_date, end_date, status) VALUES (1, '2026-06-01', '2026-06-30', 'unknown');",
            "status IN ('open', 'closed')");
    }

    /// <summary><b>終わりが始まりより前の期間は作れない。</b> 作れると、どの期間にも属さない日が生まれる。</summary>
    [Fact]
    public void 会計期間の終わりは始まりより前にできない()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(
            db,
            "INSERT INTO accounting_periods (fiscal_year_id, start_date, end_date) VALUES (1, '2026-06-30', '2026-06-01');",
            "start_date <= end_date");
    }

    /// <summary><b>同じ会計年度で同じ開始日の期間は 2 つ作れない。</b> 作れると、日から期間が 1 つに決まらない。</summary>
    [Fact]
    public void 会計期間は同じ会計年度で開始日を重複できない()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByUnique(
            db,
            "INSERT INTO accounting_periods (fiscal_year_id, start_date, end_date) VALUES (1, '2026-05-01', '2026-05-31');",
            "accounting_periods.fiscal_year_id, accounting_periods.start_date");
    }

    // ---- tax_categories ----

    /// <summary>税区分の区分値は決めた値しか入らない。</summary>
    [Theory]
    [InlineData("default_tax_treatment", "'unknown'", "default_tax_treatment IN")]
    [InlineData("is_active", "2", "is_active IN (0, 1)")]
    public void 税区分の区分値は決めた値しか入らない(string column, string value, string expression)
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(db, $"UPDATE tax_categories SET {column} = {value} WHERE id = 1;", expression);
    }

    /// <summary>税率の種類は決めた 3 つしか入らない。</summary>
    /// <remarks>
    /// <b>課税の区分も一緒に課税売上へ動かす。</b> 対象外のまま税率の種類を入れると、
    /// 「課税でないなら税率の種類は持てない」の CHECK が先に鳴り、
    /// <b>この CHECK を外しても赤いまま</b>になる（2026-09-13 に掃引で確かめた）。
    /// </remarks>
    [Fact]
    public void 税率の種類は決めた値しか入らない()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(
            db,
            "UPDATE tax_categories SET taxation_type = 'taxable_sales', rate_kind = 'unknown' WHERE id = 1;",
            "rate_kind IN");
    }

    /// <summary>
    /// <b>税率の種類を持てるのは課税売上・課税仕入だけで、その 2 つは必ず持つ。</b>
    /// </summary>
    /// <remarks>
    /// 対になった 2 本の CHECK である。<b>片方ずつ撃たないと、両方を見張ったことにならない</b>
    /// （qa/03 の L-44 と同じ「対で持つ」）。
    /// </remarks>
    [Theory]
    // 課税でないのに税率の種類がある
    [InlineData("'out_of_scope'", "'standard'", "OR rate_kind IS NULL")]
    // 課税なのに税率の種類が無い
    [InlineData("'taxable_sales'", "NULL", "OR rate_kind IS NOT NULL")]
    public void 税率の種類は課税の区分とだけ組み合わせられる(string taxationType, string rateKind, string expression)
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(
            db,
            $"UPDATE tax_categories SET taxation_type = {taxationType}, rate_kind = {rateKind} WHERE id = 1;",
            expression);
    }

    // ---- accounts ----

    [Fact]
    public void 勘定科目は実在しない既定税区分を指せない()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByForeignKey(db, "UPDATE accounts SET default_tax_category_id = 999 WHERE id = 1;");
    }

    /// <summary>
    /// 勘定科目の旗は 0 か 1 だけ。<b>2 が入ると「立っている」の判定が書き手ごとにぶれる。</b>
    /// </summary>
    [Theory]
    [InlineData("is_contra")]
    [InlineData("uses_sub_account")]
    [InlineData("is_cash_equivalent")]
    [InlineData("is_fixed_asset")]
    [InlineData("is_active")]
    [InlineData("requires_partner")]
    public void 勘定科目の旗は0か1しか入らない(string column)
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(db, $"UPDATE accounts SET {column} = 2 WHERE id = 1;", $"{column} IN (0, 1)");
    }

    // ---- sub_accounts ----

    [Fact]
    public void 補助科目は実在しない勘定科目にぶら下がれない()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByForeignKey(db, "INSERT INTO sub_accounts (account_id, code, name) VALUES (999, 'S01', '普通預金A');");
    }

    [Fact]
    public void 補助科目の有効の旗は0か1しか入らない()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, "INSERT INTO sub_accounts (account_id, code, name) VALUES (1, 'S01', '小口現金');");

        Rejected.ByCheck(db, "UPDATE sub_accounts SET is_active = 2 WHERE code = 'S01';", "is_active IN (0, 1)");
    }

    // ---- departments ----

    [Theory]
    [InlineData("is_company_wide")]
    [InlineData("is_active")]
    public void 部門の旗は0か1しか入らない(string column)
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(db, $"UPDATE departments SET {column} = 2 WHERE id = 2;", $"{column} IN (0, 1)");
    }

    // ---- partners ----

    [Fact]
    public void 取引先の有効の旗は0か1しか入らない()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(db, "UPDATE partners SET is_active = 2 WHERE id = 1;", "is_active IN (0, 1)");
    }

    /// <summary>
    /// <b>名寄せの親には、さらに親を持つ取引先を選べない</b>（深さ 1 の森。ADR-0028 §2）。
    /// </summary>
    /// <remarks>
    /// <b>トリガは INSERT と UPDATE で 2 本あるので検体も 2 つ要る</b>（qa/03 の L-44 の型）。
    /// 深い木を許すと、名寄せの先をたどる処理に終わりが無くなる。
    /// </remarks>
    [Fact]
    public void 名寄せの親に子を持つ取引先を選べない()
    {
        using var db = Nested();

        Rejected.ByTrigger(
            db,
            """
            INSERT INTO partners (code, name, parent_partner_id)
                SELECT 'P004', '孫', id FROM partners WHERE code = 'P003';
            """,
            "名寄せの親には、さらに親を持つ取引先を選べません");
    }

    /// <summary>上と対。<b>後から親を付け替えても止まる。</b></summary>
    [Fact]
    public void 名寄せの親を後から子に付け替えられない()
    {
        using var db = Nested();
        TestDatabase.Execute(db, "INSERT INTO partners (code, name) VALUES ('P004', '孫');");

        Rejected.ByTrigger(
            db,
            """
            UPDATE partners SET parent_partner_id = (SELECT id FROM partners WHERE code = 'P003')
             WHERE code = 'P004';
            """,
            "名寄せの親には、さらに親を持つ取引先を選べません");
    }

    /// <summary>
    /// <b>他の取引先の親になっている取引先には、親を付けられない</b>（深さ 1 の森のもう半分）。
    /// </summary>
    /// <remarks>
    /// <b>同じトリガの中に <c>RAISE</c> が 2 本ある。</b> 制約ノックアウトはトリガ 1 本を単位にするので、
    /// <b>片方しか検査していなくても「見張られている」と報告する</b>——
    /// 1 制約 1 規則でないものは、テストの側で対を持つしかない（qa/03 の L-44 の型）。
    /// </remarks>
    [Fact]
    public void 子を持つ取引先に親を付けられない()
    {
        using var db = Nested();
        TestDatabase.Execute(db, "INSERT INTO partners (code, name) VALUES ('P005', 'よその親');");

        Rejected.ByTrigger(
            db,
            """
            UPDATE partners SET parent_partner_id = (SELECT id FROM partners WHERE code = 'P005')
             WHERE code = 'P002';
            """,
            "他の取引先の名寄せの親になっている取引先には、親を付けられません");
    }

    /// <summary>
    /// <b>境界の有効値は通る。</b> 制約ノックアウトは「外す」方向しか測らないので、
    /// <b>締めすぎた変更はテストにも計器にも見えない</b>——肯定側を対で持つ。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void 決算月は1と12を受け入れる(int month)
    {
        using var db = SchemaSeed.Create();

        TestDatabase.Execute(db, $"UPDATE company_profile SET fiscal_year_end_month = {month} WHERE id = 1;");

        Assert.Equal(
            (long)month,
            TestDatabase.ScalarOf<long>(db, "SELECT fiscal_year_end_month FROM company_profile WHERE id = 1"));
    }

    /// <summary>13 桁ちょうどの法人番号は書いて読み戻せる。</summary>
    [Fact]
    public void 事業所の法人番号は13桁なら書いて読み戻せる()
    {
        using var db = SchemaSeed.Create();

        TestDatabase.Execute(db, "UPDATE company_profile SET corporate_number = '1234567890123' WHERE id = 1;");

        Assert.Equal(
            "1234567890123",
            TestDatabase.ScalarOf<string>(db, "SELECT corporate_number FROM company_profile WHERE id = 1"));
    }

    // ---- partner_invoice_registrations ----

    // **表の `UNIQUE (partner_id, registration_no, valid_from)` を撃つ検体は書けない。**
    // 2026-09-14 に日付の守り（011_date_format）が入り、**開始日が ISO の年月日に揃った**ので、
    // **表の UNIQUE が拒む組はすべて ux_partner_invoice_registrations_valid_from も拒む**
    // （同じ valid_from なら同じ date(valid_from)）。**それまでは `'20260401'` のような
    // 日付として読めない値だけが表の UNIQUE に届いていた**が、その値はもう入らない。
    // 表の UNIQUE は**論理的に冗長**になり、制約ノックアウトでは殺せない（qa/02 のラウンド 89）。

    /// <summary>
    /// <b>同じ取引先で同じ開始日の登録は、番号が違っても 2 つ持てない</b>
    /// （<c>ux_partner_invoice_registrations_valid_from</c>）。
    /// </summary>
    /// <remarks>
    /// <b>番号を変えて撃つ。</b> 表の <c>UNIQUE</c> は登録番号まで含むので、
    /// 番号が違えば同じ日の 2 件が入ってしまう——そこを塞ぐのがこのインデックスである。
    /// <b>逆に、表の <c>UNIQUE</c> だけを撃つ検体は書けない</b>——このインデックスは
    /// <c>(partner_id, date(valid_from))</c> で、<b>表の UNIQUE が拒む組をすべて拒む</b>ので、
    /// 表の側は論理的に冗長である（2026-09-13 の掃引で確かめた。qa/02 のラウンド 88）。
    /// </remarks>
    [Fact]
    public void 登録は同じ取引先で開始日を重複できない()
    {
        using var db = Registered();

        Rejected.ByUnique(
            db,
            "INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from) VALUES (1, 'T9999999999999', '2026-04-01');",
            "index 'ux_partner_invoice_registrations_valid_from'");
    }

    // ---- app_users ----

    /// <summary><b>利用者名は重複できない。</b> 重複すると、記帳者の記録がどちらの人か決まらない。</summary>
    [Fact]
    public void 利用者名は重複できない()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, "INSERT INTO app_users (user_name, hash, salt) VALUES ('keiri', 'h', 's');");

        Rejected.ByUnique(
            db,
            "INSERT INTO app_users (user_name, hash, salt) VALUES ('keiri', 'h2', 's2');",
            "app_users.user_name");
    }

    /// <summary>
    /// 親（P002。自分は親を持たない）と、その下の子（P003）を作る。
    /// <b>P003 は「さらに親を持つ取引先」なので、これを親に選ぶ操作が拒まれる。</b>
    /// </summary>
    private static SqliteConnection Nested()
    {
        var db = SchemaSeed.Create();
        TestDatabase.Execute(db, """
            INSERT INTO partners (code, name) VALUES ('P002', '名寄せの親');
            INSERT INTO partners (code, name, parent_partner_id)
                SELECT 'P003', '名寄せの子', id FROM partners WHERE code = 'P002';
            """);
        return db;
    }

    /// <summary>マスタに登録を 1 本。</summary>
    private static SqliteConnection Registered()
    {
        var db = SchemaSeed.Create();
        TestDatabase.Execute(db,
            "INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from) VALUES (1, 'T1234567890123', '2026-04-01');");
        return db;
    }
}
