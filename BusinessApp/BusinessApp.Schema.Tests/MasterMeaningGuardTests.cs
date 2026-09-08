namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 使用中のマスタは意味を変えられない（ADR-0038）。<b>DDL のトリガの側。</b>
/// </summary>
/// <remarks>
/// <para>関門（<c>MasterMeaningGate</c>）が本体で、こちらは<b>取込・CLI・SQL の直打ちのどれからでも通る最後の守り</b>である（ADR-0038 §4）。</para>
/// <para><b>どの制約で落ちたかまで見て、DB が変わっていないことを読み戻す。</b> 例外の型だけだと、
/// 別の制約に当たっても緑のままになる（<c>JournalImmutabilityTests</c> と同じ作法）。</para>
/// </remarks>
public class MasterMeaningGuardTests
{
    /// <summary>
    /// 計上済みの明細が使う行（現金 1・売上高 2・営業部 2・対象外 1）の意味を決める列は変えられない。
    /// 3 表の**守る列を 1 列ずつ**踏む（トリガの列リストから 1 列落としても気づけるように）。
    /// 補助科目は計上済みの明細を持つ検体が別なので、下の別テストで踏む。
    /// </summary>
    [Theory]
    [InlineData("UPDATE accounts SET category = 'expense' WHERE id = 1", "勘定科目の意味", "SELECT category FROM accounts WHERE id = 1", "asset")]
    [InlineData("UPDATE accounts SET code = '1101' WHERE id = 1", "勘定科目の意味", "SELECT code FROM accounts WHERE id = 1", "1100")]
    [InlineData("UPDATE accounts SET is_contra = 1 WHERE id = 1", "勘定科目の意味", "SELECT is_contra FROM accounts WHERE id = 1", "0")]
    [InlineData("UPDATE accounts SET uses_sub_account = 1 WHERE id = 1", "勘定科目の意味", "SELECT uses_sub_account FROM accounts WHERE id = 1", "0")]
    [InlineData("UPDATE departments SET code = 'D09' WHERE id = 2", "部門の意味", "SELECT code FROM departments WHERE id = 2", "D01")]
    [InlineData("UPDATE departments SET is_company_wide = 1 WHERE id = 2", "部門の意味", "SELECT is_company_wide FROM departments WHERE id = 2", "0")]
    [InlineData("UPDATE tax_categories SET taxation_type = 'non_taxable_sales' WHERE id = 1", "税区分の意味", "SELECT taxation_type FROM tax_categories WHERE id = 1", "out_of_scope")]
    [InlineData("UPDATE tax_categories SET rate_kind = 'standard' WHERE id = 1", "税区分の意味", "SELECT rate_kind FROM tax_categories WHERE id = 1", "")]
    [InlineData("UPDATE tax_categories SET code = 'OUT2' WHERE id = 1", "税区分の意味", "SELECT code FROM tax_categories WHERE id = 1", "OUT")]
    public void 使用中のマスタの意味を決める列は変えられない(string sql, string reason, string readBack, string unchanged)
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, sql));

        Assert.Contains(reason, thrown.Message, StringComparison.Ordinal);
        Assert.Equal(unchanged, TestDatabase.ScalarOf<object>(db, readBack) is DBNull or null ? "" : TestDatabase.ScalarOf<object>(db, readBack)?.ToString());
    }

    /// <summary>
    /// <b>「取引先を要する」は、使用中の科目ではオフにできない</b>（docs/10 §6-2。<b>一方通行</b>）。
    /// </summary>
    /// <remarks>
    /// <b>これを止めないと、二層の守りをまとめて外せる</b>——オフにして計上し、また戻せば、
    /// 計上の関門も <c>trg_journal_entries_partner_presence_when_posted</c> も素通りする
    /// （自己レビューで見つけた。2026-09-08。qa/03 L-08 の型）。
    /// </remarks>
    [Fact]
    public void 使用中の科目では取引先の必須を外せない()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();
        TestDatabase.Execute(db, "UPDATE accounts SET requires_partner = 1 WHERE id = 1");

        var thrown = Assert.Throws<SqliteException>(
            () => TestDatabase.Execute(db, "UPDATE accounts SET requires_partner = 0 WHERE id = 1"));

        Assert.Contains("取引先を必須から外すことはできない", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT requires_partner FROM accounts WHERE id = 1"));
    }

    /// <summary>
    /// <b>厳しくする向き（オフ → オン）は、使用中でも通る。</b>
    /// </summary>
    /// <remarks>
    /// <b>ここが赤くなったら、規則を後から採り入れられなくしている</b>——
    /// 立てたいのは<b>計上済みの明細がある科目</b>（売掛金・買掛金）である（docs/10 §6-2）。
    /// </remarks>
    [Fact]
    public void 使用中の科目でも取引先を必須にはできる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, "UPDATE accounts SET requires_partner = 1 WHERE id = 1");

        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT requires_partner FROM accounts WHERE id = 1"));
    }

    /// <summary>
    /// <b>計上済みの明細が無い科目なら、オフにできる。</b>（「使用中」の線は ADR-0038 §1 と同じ）
    /// </summary>
    [Fact]
    public void 使っていない科目なら取引先の必須を外せる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();
        // 科目 1・2 は計上済みの明細が使っている。**別の科目**を足して踏む。
        TestDatabase.Execute(db, """
            INSERT INTO accounts (code, name, category, requires_partner) VALUES ('1300', '売掛金', 'asset', 1);
            UPDATE accounts SET requires_partner = 0 WHERE code = '1300';
            """);

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(
            db, "SELECT requires_partner FROM accounts WHERE code = '1300'"));
    }

    /// <summary>
    /// <b>守れるのは「使用中になってから」だけである</b>（この規則の残余。docs/10 §6-2）。
    /// </summary>
    /// <remarks>
    /// <b>計上済みの明細が 1 行も無い科目ではオフにできる</b>ので、
    /// <b>使い始める前なら「オフ → 計上 → オン」で規則より前の行を作れる</b>。
    /// これは「使用中」の線（ADR-0038 §1）をそのまま使った結果であり、
    /// <b>塞いだつもりで塞げていない、と読まれないように検体で書き留めておく</b>
    /// （自己レビューで指摘された。2026-09-08。qa/03 L-22 の型）。
    /// </remarks>
    [Fact]
    public void 使い始める前なら規則より前の行を作れる()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, "UPDATE accounts SET requires_partner = 1 WHERE id = 1");

        // ① まだ計上済みの明細が無いので、オフに戻せる
        TestDatabase.Execute(db, "UPDATE accounts SET requires_partner = 0 WHERE id = 1");

        // ② 取引先の無い明細を計上できる
        TestDatabase.Execute(db, SchemaSeed.PostedEntry);

        // ③ オンに戻せる（ここまで関門もトリガも 1 度も鳴らない）
        TestDatabase.Execute(db, "UPDATE accounts SET requires_partner = 1 WHERE id = 1");

        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, """
            SELECT COUNT(*) FROM journal_lines l
              JOIN journal_entries e ON e.id = l.journal_entry_id
              JOIN accounts a ON a.id = l.account_id
             WHERE e.status = 'posted' AND a.requires_partner = 1
               AND COALESCE(l.partner_id, e.partner_id) IS NULL
            """));
    }

    /// <summary>
    /// 意味を決めない列（<c>Guarded</c> に無い列。一覧は docs/12 §2）は、使用中でも変えられる。
    /// </summary>
    [Theory]
    [InlineData("UPDATE accounts SET name = '現金及び預金' WHERE id = 1", "SELECT name FROM accounts WHERE id = 1", "現金及び預金")]
    [InlineData("UPDATE accounts SET name_kana = 'げんきん' WHERE id = 1", "SELECT name_kana FROM accounts WHERE id = 1", "げんきん")]
    [InlineData("UPDATE accounts SET is_active = 0 WHERE id = 1", "SELECT is_active FROM accounts WHERE id = 1", "0")]
    [InlineData("UPDATE accounts SET display_order = 9 WHERE id = 1", "SELECT display_order FROM accounts WHERE id = 1", "9")]
    [InlineData("UPDATE accounts SET default_tax_category_id = 1 WHERE id = 1", "SELECT default_tax_category_id FROM accounts WHERE id = 1", "1")]
    [InlineData("UPDATE departments SET name = '営業本部' WHERE id = 2", "SELECT name FROM departments WHERE id = 2", "営業本部")]
    [InlineData("UPDATE tax_categories SET name = '不課税' WHERE id = 1", "SELECT name FROM tax_categories WHERE id = 1", "不課税")]
    public void 使用中でも名前や有効は変えられる(string sql, string readBack, string expected)
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, sql);

        Assert.Equal(expected, TestDatabase.ScalarOf<object>(db, readBack)?.ToString());
    }

    /// <summary>
    /// <b>同じ値を書き直す UPDATE は通る。</b> 画面は触っていない列も書き戻すことがある
    /// （<c>WHEN NEW.x IS NOT OLD.x</c> で守る理由）。
    /// </summary>
    [Fact]
    public void 同じ値を書き直すだけなら通る()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, "UPDATE accounts SET category = 'asset', code = '1100', is_contra = 0 WHERE id = 1");
    }

    /// <summary>使われていない行は自由に変えられる。</summary>
    [Fact]
    public void 使われていないマスタは意味を変えられる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();
        TestDatabase.Execute(db, "INSERT INTO accounts (code, name, category) VALUES ('6070', '通信費', 'expense')");

        TestDatabase.Execute(db, "UPDATE accounts SET category = 'asset', code = '1700' WHERE code = '6070'");
        TestDatabase.Execute(db, "UPDATE departments SET code = 'D90' WHERE id = 1");

        Assert.Equal("asset", TestDatabase.ScalarOf<string>(db, "SELECT category FROM accounts WHERE code = '1700'"));
        Assert.Equal("D90", TestDatabase.ScalarOf<string>(db, "SELECT code FROM departments WHERE id = 1"));
    }

    /// <summary>
    /// <b>下書きだけが参照している行は変えてよい</b>（ADR-0038 §1）。下書きは直せる。
    /// </summary>
    [Fact]
    public void 下書きだけが使うマスタは意味を変えられる()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', 1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', 1, 2, 100, 1);
            """);

        TestDatabase.Execute(db, "UPDATE accounts SET category = 'expense' WHERE id = 1");
        TestDatabase.Execute(db, "UPDATE departments SET code = 'D09' WHERE id = 2");
        // 課税区分は非課税へ（課税に変えると別の CHECK——税率区分が要る——に当たり、トリガの検査にならない）
        TestDatabase.Execute(db, "UPDATE tax_categories SET taxation_type = 'non_taxable_sales' WHERE id = 1");

        Assert.Equal("expense", TestDatabase.ScalarOf<string>(db, "SELECT category FROM accounts WHERE id = 1"));
    }

    /// <summary>補助科目は、計上済みの明細が使っていればコードも親の勘定科目も変えられない。名前は変えられる。</summary>
    [Theory]
    [InlineData("UPDATE sub_accounts SET account_id = 2 WHERE id = 1", "SELECT account_id FROM sub_accounts WHERE id = 1", "1")]
    [InlineData("UPDATE sub_accounts SET code = 'S002' WHERE id = 1", "SELECT code FROM sub_accounts WHERE id = 1", "S001")]
    public void 使用中の補助科目はコードも勘定科目も変えられない(string sql, string readBack, string unchanged)
    {
        using var db = WithPostedSubAccountLine();

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, sql));

        Assert.Contains("補助科目の意味", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(unchanged, TestDatabase.ScalarOf<object>(db, readBack)?.ToString());

        TestDatabase.Execute(db, "UPDATE sub_accounts SET name = '本店（改名）' WHERE id = 1");
        Assert.Equal("本店（改名）", TestDatabase.ScalarOf<string>(db, "SELECT name FROM sub_accounts WHERE id = 1"));
    }

    /// <summary>
    /// <b>REPLACE で使用中の行を乗っ取れない</b>（qa/03 L-26 と同じ型。2026-09-07 の自己レビューで実測して塞いだ）。
    /// </summary>
    /// <remarks>
    /// <c>INSERT OR REPLACE</c> は同じ id の行を消して入れ直すが、消す側の DELETE トリガは既定では発火せず、
    /// 外部キーの違反も相殺されて通る。<c>UPDATE OR REPLACE ... SET id</c> も同じ。
    /// 4 表の <c>BEFORE UPDATE OF ...</c> のトリガだけでは、この経路が開いたままだった。
    /// </remarks>
    [Theory]
    [InlineData("INSERT OR REPLACE INTO accounts (id, code, name, category) VALUES (1, '1100', '現金', 'expense')", "勘定科目を上書きできない", "SELECT category FROM accounts WHERE id = 1", "asset")]
    [InlineData("INSERT OR REPLACE INTO departments (id, code, name, is_company_wide) VALUES (2, 'D01', '営業部', 1)", "部門を上書きできない", "SELECT is_company_wide FROM departments WHERE id = 2", "0")]
    [InlineData("INSERT OR REPLACE INTO tax_categories (id, code, name, taxation_type) VALUES (1, 'OUT', '対象外', 'non_taxable_sales')", "税区分を上書きできない", "SELECT taxation_type FROM tax_categories WHERE id = 1", "out_of_scope")]
    public void 使用中の行をREPLACEで置き換えられない(string sql, string reason, string readBack, string unchanged)
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, sql));

        Assert.Contains(reason, thrown.Message, StringComparison.Ordinal);
        Assert.Equal(unchanged, TestDatabase.ScalarOf<object>(db, readBack)?.ToString());
    }

    /// <summary>
    /// <b>未使用の行の id を、使用中の id に付け替えて乗っ取れない</b>（<c>UPDATE OR REPLACE ... SET id</c>）。
    /// 使用中の行の id を動かすことも同じく拒む。
    /// </summary>
    [Fact]
    public void 使用中のidをUPDATE_OR_REPLACEで乗っ取れない()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();
        TestDatabase.Execute(db, "INSERT INTO accounts (code, name, category) VALUES ('6070', '通信費', 'expense')");

        var takeover = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            db, "UPDATE OR REPLACE accounts SET id = 1 WHERE code = '6070'"));
        Assert.Contains("勘定科目を上書きできない", takeover.Message, StringComparison.Ordinal);

        var move = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            db, "UPDATE accounts SET id = 99 WHERE id = 1"));
        Assert.Contains("勘定科目を上書きできない", move.Message, StringComparison.Ordinal);

        Assert.Equal("現金", TestDatabase.ScalarOf<string>(db, "SELECT name FROM accounts WHERE id = 1"));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT account_id FROM journal_lines WHERE id = 1"));
    }

    /// <summary>補助科目の REPLACE も同じ（計上済みの明細を持つ検体が別なので分けてある）。</summary>
    [Fact]
    public void 使用中の補助科目をREPLACEで置き換えられない()
    {
        using var db = WithPostedSubAccountLine();

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            db, "INSERT OR REPLACE INTO sub_accounts (id, account_id, code, name) VALUES (1, 2, 'S001', '本店')"));

        Assert.Contains("補助科目を上書きできない", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT account_id FROM sub_accounts WHERE id = 1"));
    }

    /// <summary><b>id を指定しない INSERT（seed・画面）はそのまま通る</b>（ADR-0038 の帰結。標本の投入を止めない）。</summary>
    [Fact]
    public void idを指定しないINSERTは通る()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, """
            INSERT INTO accounts (code, name, category) VALUES ('6070', '通信費', 'expense');
            INSERT INTO sub_accounts (account_id, code, name) VALUES (1, 'S001', '本店');
            INSERT INTO departments (code, name) VALUES ('D02', '開発部');
            INSERT INTO tax_categories (code, name, taxation_type) VALUES ('NS', '非課税売上', 'non_taxable_sales');
            """);

        Assert.Equal(3L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM accounts"));
    }

    // --- 取引先（009。ADR-0047 の決定 9 で 4 マスタと揃えた） ----------------------

    /// <summary>
    /// 計上済みの伝票が使っている取引先は、コードを変えられない。
    /// </summary>
    /// <remarks>
    /// <b>取引先だけは「使用中」の数え方が違う</b>——明細（<c>journal_lines.partner_id</c>）と
    /// <b>伝票（<c>journal_entries.partner_id</c>）の両方</b>を見る。明細が空なら伝票の値が
    /// 実効値になるからで、明細だけを見ると<b>伝票にだけ取引先を入れた計上済みの伝票を取りこぼす</b>。
    /// </remarks>
    [Theory]
    [InlineData("UPDATE journal_lines SET partner_id = 1 WHERE id = 1")]
    [InlineData("UPDATE journal_entries SET partner_id = 1 WHERE id = 1")]
    public void 使用中の取引先のコードは変えられない(string use)
    {
        using var db = WithPostedPartner(use);

        var thrown = Assert.Throws<SqliteException>(
            () => TestDatabase.Execute(db, "UPDATE partners SET code = 'P999' WHERE id = 1"));

        Assert.Contains("取引先のコードは変更できない", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("P001", TestDatabase.ScalarOf<string>(db, "SELECT code FROM partners WHERE id = 1"));
    }

    /// <summary>下書きだけが使う取引先のコードは変えられる（使用中は計上済みだけ。ADR-0038 §1）。</summary>
    [Fact]
    public void 下書きだけが使う取引先のコードは変えられる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, entered_at, partner_id)
                VALUES (1, '2026-05-21', '2026-05-21', 'draft', 'normal', '下書き', '2026-05-21 10:00:00', 1);
            UPDATE partners SET code = 'P999' WHERE id = 1;
            """);

        Assert.Equal("P999", TestDatabase.ScalarOf<string>(db, "SELECT code FROM partners WHERE id = 1"));
    }

    /// <summary>
    /// 使用中の取引先の id は、<c>REPLACE</c> でも <c>UPDATE</c> でも乗っ取れない（qa/03 L-26 の型）。
    /// </summary>
    /// <remarks>
    /// <b>UPDATE のトリガを通らずに意味が変わる経路</b>を塞ぐ。
    /// <c>INSERT OR REPLACE</c> は行ごと差し替えるので、コードの凍結だけでは足りない。
    /// </remarks>
    [Theory]
    [InlineData("INSERT OR REPLACE INTO partners (id, code, name) VALUES (1, 'P999', '別の会社')")]
    [InlineData("UPDATE partners SET id = 9 WHERE id = 1")]
    public void 使用中の取引先は置き換えられない(string sql)
    {
        using var db = WithPostedPartner("UPDATE journal_lines SET partner_id = 1 WHERE id = 1");

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, sql));

        Assert.Contains("取引先は置き換えられない", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("株式会社取引先", TestDatabase.ScalarOf<string>(db, "SELECT name FROM partners WHERE id = 1"));
    }

    /// <summary>
    /// <b>id が動かない更新は、SET 句に id が並んでいても通る。</b>
    /// </summary>
    /// <remarks>
    /// <c>UPDATE OF id</c> は<b>値が同じでも SET 句に列があれば発火する</b>ので、
    /// <c>WHEN NEW.id IS NOT OLD.id</c> が無いと<b>使用中の取引先は名称も住所も一切直せない</b>
    /// ——しかも断りは「置き換えられない」なので、利用者には理由が分からない。
    /// 会計コアの 4 マスタは持っていた条件が、取引先へ写したときに落ちていた（2026-09-09 の自己レビュー）。
    /// </remarks>
    [Fact]
    public void 使用中の取引先でもidが動かない更新は通る()
    {
        using var db = WithPostedPartner("UPDATE journal_lines SET partner_id = 1 WHERE id = 1");

        TestDatabase.Execute(db, "UPDATE partners SET id = id, name = '株式会社取引先（新）' WHERE id = 1");

        Assert.Equal("株式会社取引先（新）", TestDatabase.ScalarOf<string>(db, "SELECT name FROM partners WHERE id = 1"));
    }

    /// <summary>計上済みの伝票が取引先 1 を使っている DB。<paramref name="use"/> が明細か伝票かを決める。</summary>
    /// <remarks>
    /// <b>計上する前に付ける。</b> 計上済みの明細も伝票も、後からは書き換えられない（I-05）ので、
    /// 下書きのうちに取引先を入れてから計上する（画面と同じ順である）。
    /// </remarks>
    private static SqliteConnection WithPostedPartner(string use)
    {
        var db = TestDatabase.Create();
        TestDatabase.Execute(db, SchemaSeed.Masters);
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, entered_at)
                VALUES (1, '2026-05-20', '2026-05-20', 'draft', 'normal', '5 月分の現金売上', '2026-05-20 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', 1, 100000, 1);
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                VALUES (1, 2, 'credit', 2, 2, 100000, 1);
            """);
        TestDatabase.Execute(db, use);
        TestDatabase.Execute(db, """
            UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-20 10:00:00' WHERE id = 1;
            UPDATE journal_entry_sequences SET next_entry_no = next_entry_no + 1 WHERE fiscal_year_id = 1;
            """);
        return db;
    }

    private static SqliteConnection WithPostedSubAccountLine()
    {
        var db = SchemaSeed.Create();
        TestDatabase.Execute(db, """
            -- **補助科目を使う科目にしてから付ける**（ADR-0038 §3。使わない科目の明細に
            -- 補助科目を付けたままでは計上できない——trg_journal_entries_sub_account_presence_when_posted）。
            -- 計上済みの明細がまだ無いので、意味の凍結のトリガには当たらない。
            UPDATE accounts SET uses_sub_account = 1 WHERE id = 1;
            INSERT INTO sub_accounts (account_id, code, name) VALUES (1, 'S001', '本店');
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', 1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, sub_account_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', 1, 1, 100, 1);
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                VALUES (1, 2, 'credit', 2, 2, 100, 1);
            UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-20 10:00:00' WHERE id = 1;
            """);
        return db;
    }
}
