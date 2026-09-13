namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

/// <summary>
/// 仕訳と仕訳明細の <c>CHECK</c>・<c>UNIQUE</c>・外部キーが、実際に書き込みを拒むこと。
/// </summary>
/// <remarks>
/// <para><b>全部、2026-09-13 の制約ノックアウトの初回掃引で「誰も見張っていない」と出た制約である</b>
/// （ADR-0053・qa/02 のラウンド 88）。区分値の一致は <c>EnumConsistencyTests</c> が
/// <b>CHECK の字面</b>で見ていたが、<b>DB が本当に弾くか</b>は誰も確かめていなかった。</para>
/// <para><b>テストを足したら、その点だけ掃引し直して「殺せた」に変わることを確かめる</b>——
/// <c>pwsh -NoProfile -File tools/clb/knockout.ps1 -Only &lt;点&gt;</c>。
/// <b>別の関門が先に鳴っていて、名前の主張を検査できていないテストが実在した</b>
/// （<c>SchemaConstraintTests</c> の伝票番号の重複。qa/03 の L-45）。</para>
/// </remarks>
public class JournalConstraintTests
{
    // ---- journal_entries の外部キー ----

    /// <summary>存在しない会計年度・原仕訳・取引先を指す伝票は作れない。</summary>
    /// <remarks>
    /// <b>列名ではなく値を差し替える。</b> 列を差し込む形にすると、既にある列と重なった瞬間に
    /// SQLite が「duplicate column name」で落ち、<b>外部キーに届かないまま Assert.Throws が通る</b>。
    /// </remarks>
    [Theory]
    [InlineData("999", "NULL")]
    [InlineData("1", "999")]
    public void 伝票のマスタ参照は実在しない値を拒む(string fiscalYearId, string partnerId)
    {
        using var db = SchemaSeed.Create();

        Rejected.ByForeignKey(db, $"""
            INSERT INTO journal_entries (description, fiscal_year_id, partner_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', {fiscalYearId}, {partnerId}, '2026-05-21', '2026-05-21', 'draft', 'normal', '2026-05-21 10:00:00');
            """);
    }

    /// <summary>
    /// 原仕訳の参照先が実在しないと、<b>取り消したはずの伝票がどこにも無い</b>状態になる（I-06）。
    /// </summary>
    [Fact]
    public void 訂正伝票は実在しない原仕訳を指せない()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByForeignKey(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, original_entry_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上の訂正', 1, 999, '2026-05-21', '2026-05-21', 'draft', 'correction', '2026-05-21 10:00:00');
            """);
    }

    // ---- journal_entries の CHECK ----

    /// <summary>
    /// 伝票の区分値は決めた値しか入らない。
    /// </summary>
    /// <remarks>
    /// <para><b>列名を差し替える形にしない。</b> <c>status</c> を差し込むと列が重複し、
    /// SQLite は「duplicate column name」で落ちる——<b>CHECK に届かないまま Assert.Throws が通る</b>。
    /// 値だけを差し替える。</para>
    /// <para><b>これは「状態が拒まれる」ことのテストであって、<c>CHECK (status IN …)</c> のテストではない。</b>
    /// 2 値以外の状態は、<c>status = 'draft' OR (entry_no IS NOT NULL AND posted_at IS NOT NULL)</c> と
    /// <c>status = 'posted' OR entry_no IS NULL</c> の対が<b>必ず先に弾く</b>——
    /// 伝票番号が NULL なら前者、NULL でなければ後者が鳴るので、逃げ場が無い。
    /// つまり <c>CHECK (status IN …)</c> は<b>論理的に冗長</b>で、
    /// <b>どんなテストを書いても制約ノックアウトでは殺せない</b>（2026-09-13 に掃引で確かめた）。</para>
    /// </remarks>
    [Theory]
    [InlineData("'unknown'", "'normal'", "status IN")]
    [InlineData("'draft'", "'unknown'", "entry_type IN")]
    public void 伝票の区分値は決めた値しか入らない(string status, string entryType, string expression)
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(
            db,
            $"""
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', 1, '2026-05-21', '2026-05-21', {status}, {entryType}, '2026-05-21 10:00:00');
            """,
            expression);
    }

    /// <summary>
    /// <b>自分自身を原仕訳にした伝票は作れない。</b> 作れると、取消の連鎖をたどる処理が止まらなくなる。
    /// </summary>
    [Fact]
    public void 伝票は自分自身を原仕訳にできない()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(
            db,
            """
            INSERT INTO journal_entries (id, description, fiscal_year_id, original_entry_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (7, '5 月分の現金売上の訂正', 1, 7, '2026-05-21', '2026-05-21', 'draft', 'correction', '2026-05-21 10:00:00');
            """,
            "original_entry_id <> id");
    }

    /// <summary>
    /// <b>計上したのに伝票番号か計上日時が無い行は作れない</b>（I-17 の前提）。
    /// 帳簿の並び順と検索がこの 2 つに依っている。
    /// </summary>
    [Theory]
    [InlineData("entry_no = NULL, posted_at = '2026-05-21 10:00:00'")]
    [InlineData("entry_no = 1, posted_at = NULL")]
    public void 計上済みの伝票は伝票番号と計上日時を欠けない(string assignments)
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, SchemaSeed.Draft);

        Rejected.ByCheck(
            db,
            $"""
            UPDATE journal_entries SET status = 'posted', {assignments}
             WHERE id = (SELECT MAX(id) FROM journal_entries);
            """,
            "entry_no IS NOT NULL AND posted_at IS NOT NULL");
    }

    /// <summary>
    /// <b>伝票番号は整数でなければならない。</b> SQLite は型が緩く、整数でない値が入ると
    /// <b>並べ替えも比較もその型の規則で行われる</b>（帳簿の並びが崩れる）。
    /// </summary>
    /// <remarks>
    /// <b>検体に <c>'1'</c> を使わない。</b> INTEGER の列に入れた <c>'1'</c> は
    /// 型親和性で整数に変換されるので <c>typeof</c> は <c>integer</c> になり、
    /// <b>この CHECK を通ってしまう</b>（2026-09-13 実測）。
    /// 変換されない値——**桁落ちする実数**と**数でない文字列**——で撃つ。
    /// </remarks>
    [Theory]
    [InlineData("1.5")]
    [InlineData("'abc'")]
    public void 伝票番号は整数でなければならない(string entryNo)
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, SchemaSeed.Draft);

        Rejected.ByCheck(
            db,
            $"""
            UPDATE journal_entries SET status = 'posted', entry_no = {entryNo}, posted_at = '2026-05-21 10:00:00'
             WHERE id = (SELECT MAX(id) FROM journal_entries);
            """,
            "typeof(entry_no) = 'integer'");
    }

    /// <summary>
    /// I-17 伝票番号を再利用しない。
    /// </summary>
    /// <remarks>
    /// <b>正規の経路（下書き → 計上）で 2 本目を計上する。</b> 直に <c>status = 'posted'</c> の行を
    /// INSERT すると <c>trg_journal_entries_no_posted_insert</c> が先に鳴り、
    /// <b>UNIQUE に届かないまま緑になる</b>——それが qa/03 の L-45 である。
    /// </remarks>
    [Fact]
    public void 同じ会計年度で伝票番号は重複できない()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();
        TestDatabase.Execute(db, SchemaSeed.Draft);

        Rejected.ByUnique(
            db,
            """
            UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-21 10:00:00'
             WHERE id = (SELECT MAX(id) FROM journal_entries);
            """,
            "journal_entries.fiscal_year_id, journal_entries.entry_no");
    }

    // ---- journal_lines ----

    /// <summary>明細が指すマスタは実在しなければならない。</summary>
    [Theory]
    [InlineData("sub_account_id")]
    [InlineData("department_id")]
    [InlineData("partner_id")]
    public void 明細のマスタ参照は実在しない値を拒む(string column)
    {
        using var db = Drafted();

        Rejected.ByForeignKey(db, $"""
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, {column}, amount, tax_category_id)
                VALUES (1, 3, 'debit', 1, 999, 100, 1);
            """);
    }

    [Fact]
    public void 明細は実在しない伝票にぶら下がれない()
    {
        using var db = Drafted();

        Rejected.ByForeignKey(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (999, 1, 'debit', 1, 100, 1);
            """);
    }

    [Fact]
    public void 明細は実在しない税区分を指せない()
    {
        using var db = Drafted();

        Rejected.ByForeignKey(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 3, 'debit', 1, 100, 999);
            """);
    }

    /// <summary>
    /// 行番号は 1 以上の整数。<b>整数でない値を許すと、行の並びがその型の規則になる。</b>
    /// </summary>
    /// <remarks>
    /// <c>'3'</c> は型親和性で整数に変換されるので検体にならない（上の伝票番号と同じ）。
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("'abc'")]
    public void 明細の行番号は1以上の整数でなければならない(string lineNo)
    {
        using var db = Drafted();

        Rejected.ByCheck(
            db,
            $"""
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, {lineNo}, 'debit', 1, 100, 1);
            """,
            "line_no > 0 AND typeof(line_no) = 'integer'");
    }

    /// <summary>明細の区分値は決めた値しか入らない。</summary>
    /// <remarks>
    /// <para><b>ここも値だけを差し替える</b>（列を重ねない理由は上と同じ）。</para>
    /// <para><b><c>is_tax_line</c> の行は「0/1 以外が拒まれる」ことのテストであって、
    /// <c>CHECK (is_tax_line IN (0, 1))</c> のテストではない。</b>
    /// <c>is_tax_line = 1 OR parent_line_no IS NULL</c> と
    /// <c>is_tax_line = 0 OR parent_line_no IS NOT NULL</c> の対が、
    /// 0 でも 1 でもない値に対して<b>親の行番号の有無を同時に要求して矛盾する</b>ので、
    /// この CHECK も<b>論理的に冗長</b>である（伝票の状態と同じ型。2026-09-13 の掃引で確かめた）。</para>
    /// </remarks>
    [Theory]
    [InlineData("'unknown'", "NULL", "0", "debit_credit IN")]
    [InlineData("'debit'", "'unknown'", "0", "tax_treatment IN")]
    [InlineData("'debit'", "NULL", "2", "is_tax_line IN (0, 1)")]
    public void 明細の区分値は決めた値しか入らない(
        string debitCredit, string taxTreatment, string isTaxLine, string expression)
    {
        using var db = Drafted();

        Rejected.ByCheck(
            db,
            $"""
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id, tax_treatment, is_tax_line)
                VALUES (1, 3, {debitCredit}, 1, 100, 1, {taxTreatment}, {isTaxLine});
            """,
            expression);
    }

    /// <summary>
    /// <b>金額は 1 円以上の整数。</b> DDL が「<c>INTEGER</c> と書くだけでは整数にならない」と注記する要である
    /// ——実数のまま入ると、<b>貸借一致の判定と保存値がずれる</b>（I-01）。
    /// </summary>
    /// <remarks>
    /// <b>`0` と `-1` だけでは <c>typeof</c> の側を検査できない</b>——どちらも <c>amount &gt; 0</c> で落ちる。
    /// 制約ノックアウトも CHECK 1 本を単位にするので、この差は原理的に見えない（qa/03 の L-44 の型）。
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("'abc'")]
    public void 明細の金額は1以上の整数でなければならない(string amount)
    {
        using var db = Drafted();

        Rejected.ByCheck(
            db,
            $"""
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 3, 'debit', 1, {amount}, 1);
            """,
            "amount > 0 AND typeof(amount) = 'integer'");
    }

    /// <summary>
    /// <b>境界の有効値は通る。</b> 制約ノックアウトは「外す」方向しか測らないので、
    /// <b>締めすぎた変更はテストにも計器にも見えない</b>——肯定側を対で持つ。
    /// </summary>
    [Fact]
    public void 金額1円と行番号1の明細は作れる()
    {
        using var db = Drafted();

        TestDatabase.Execute(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 3, 'debit', 1, 1, 1);
            """);

        Assert.Equal(
            1L,
            TestDatabase.ScalarOf<long>(db, "SELECT amount FROM journal_lines WHERE journal_entry_id = 1 AND line_no = 3"));
    }

    // ---- journal_entry_sequences ----

    /// <summary>
    /// <b>採番の行が会計年度ごとに 2 本あると、同じ番号が 2 度出る</b>（I-17）。
    /// </summary>
    [Fact]
    public void 採番の行は会計年度ごとに一本しか持てない()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByUnique(
            db,
            "INSERT INTO journal_entry_sequences (fiscal_year_id) VALUES (1);",
            "journal_entry_sequences.fiscal_year_id");
    }

    [Fact]
    public void 採番の行は実在しない会計年度を指せない()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByForeignKey(db, "INSERT INTO journal_entry_sequences (fiscal_year_id) VALUES (999);");
    }

    /// <summary>マスタと下書き 1 本（明細 2 行）。明細の検体はこの伝票にぶら下げる。</summary>
    private static Microsoft.Data.Sqlite.SqliteConnection Drafted()
    {
        var db = SchemaSeed.Create();
        TestDatabase.Execute(db, SchemaSeed.Draft);
        return db;
    }
}
