namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 補助科目は 2 値である（ADR-0038 §3。docs/04 §1 の A-3）。
/// </summary>
/// <remarks>
/// <para><b>使う科目では補助科目が要り、使わない科目は持てない。</b>
/// 関門（<c>JournalEntryValidator</c>）が本体で、ここは<b>関門が走らない経路</b>
/// （CSV 取込・<c>sql</c> CLI・手作業の SQL）への最後の守りである。</para>
/// <para><b>広さは関門に揃えてある</b>（docs/10 §4-2-1 の二層の広さ）——
/// <b>外すのは取消だけ</b>で、両方向とも同じ線である。取消の明細はサーバが原仕訳から作り、
/// 利用者に直す手立てが無いので、止めると規則より前の伝票を打ち消せなくなる（docs/10 §5・ADR-0004）。
/// <b>訂正（再計上）は外さない</b>——中身は利用者が決めるので、補助科目を空にすれば通る。</para>
/// <para><b>下書きには求めない。</b> 摘要（<see cref="JournalDescriptionGuardTests"/>）と同じく、
/// 見るのは<b>下書き → 計上の UPDATE</b> だけである。</para>
/// </remarks>
public class JournalSubAccountGuardTests
{
    /// <summary>
    /// 科目 2（売上高）に補助科目を 2 つ作り、科目 1 を「補助科目を使う」にして補助科目を 1 つ作る。
    /// </summary>
    /// <remarks>
    /// <b>識別子をずらすためだけの 2 行ではない。</b> 科目 1 の補助科目が id 3 になるので、
    /// 「明細の勘定科目」「補助科目」「その親」が全部 1 という縮退（qa/03 L-02）を避けられる。
    /// </remarks>
    private const string UsesSubAccount = """
        INSERT INTO sub_accounts (account_id, code, name) VALUES (2, 'T001', '店頭');
        INSERT INTO sub_accounts (account_id, code, name) VALUES (2, 'T002', '通販');
        UPDATE accounts SET uses_sub_account = 1 WHERE id = 1;
        INSERT INTO sub_accounts (account_id, code, name) VALUES (1, 'S001', '本店');
        """;

    /// <summary>科目 1 の補助科目だけ作る（科目 1 は「使わない」のまま）。</summary>
    private const string SubAccountOfUnusedAccount = """
        INSERT INTO sub_accounts (account_id, code, name) VALUES (2, 'T001', '店頭');
        INSERT INTO sub_accounts (account_id, code, name) VALUES (2, 'T002', '通販');
        INSERT INTO sub_accounts (account_id, code, name) VALUES (1, 'S001', '本店');
        """;

    /// <summary>科目 1 の補助科目の識別子（上の 2 つの後に採番される）。</summary>
    private const string SubAccountOfAccount1 = "3";

    private const string Post =
        "UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-20 10:00:00' WHERE id = 1";

    /// <summary>科目 1 の明細に補助科目を付ける／付けない下書きを 1 件書く。</summary>
    private static SqliteConnection Draft(
        string setup, string? subAccountId, string entryType = "normal", bool mirrorPosted = true)
    {
        // **識別子を明示する**——原仕訳を先に入れる種別があるので、採番に任せると id が 1 でなくなる。
        var db = SchemaSeed.Create();
        TestDatabase.Execute(db, setup);
        if (entryType != "normal")
        {
            // **原仕訳は計上済みで、同じ組み合わせの明細を持つ**（本番の形）。
            // トリガが外すのは「原仕訳を写しただけの明細」だけなので、この明細が要る。
            // 計上済みにするのはこのトリガが見ない INSERT ではなく UPDATE なので、
            // **原仕訳の側でも規則を満たしている必要がある**——だから補助科目は付けない。
            TestDatabase.Execute(db, $"""
                INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type,
                                             description, entered_at)
                    VALUES (99, 1, '2026-05-19', '2026-05-19', 'draft', 'normal', '原仕訳', '2026-05-19 10:00:00');
                INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, sub_account_id, amount, tax_category_id)
                    VALUES (99, 1, 'debit', 1, {subAccountId ?? "NULL"}, 100000, 1);
                INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                    VALUES (99, 2, 'credit', 2, 2, 100000, 1);
                """);

            if (mirrorPosted)
            {
                // 原仕訳を計上済みにする。**規則より前に計上された伝票**を作るので、
                // このトリガだけ外す（TestDatabase の許可表に理由を書いてある）。
                TestDatabase.WithoutTrigger(
                    db,
                    "trg_journal_entries_sub_account_presence_when_posted",
                    "UPDATE journal_entries SET status = 'posted', entry_no = 9, posted_at = '2026-05-19 11:00:00' WHERE id = 99");
            }
        }

        TestDatabase.Execute(db, $"""
            INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type,
                                         description, entered_at, original_entry_id)
                VALUES (1, 1, '2026-05-20', '2026-05-20', 'draft', '{entryType}', '5 月分の現金売上',
                        '2026-05-20 10:00:00', {(entryType == "normal" ? "NULL" : "99")});
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, sub_account_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', 1, {subAccountId ?? "NULL"}, 100000, 1);
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                VALUES (1, 2, 'credit', 2, 2, 100000, 1);
            """);
        return db;
    }

    private static string StatusOf(SqliteConnection db)
        => TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 1");

    [Fact]
    public void 補助科目を使わない科目の明細に補助科目を付けたままでは計上できない()
    {
        using var db = Draft(SubAccountOfUnusedAccount, SubAccountOfAccount1);

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));

        Assert.Contains("補助科目を使わない勘定科目の明細に補助科目は付けられない", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 補助科目を使う科目の明細に補助科目が無ければ計上できない()
    {
        using var db = Draft(UsesSubAccount, subAccountId: null);

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));

        Assert.Contains("補助科目を使う勘定科目の明細には補助科目が要る", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 補助科目を使う科目に補助科目が付いていれば計上できる()
    {
        using var db = Draft(UsesSubAccount, SubAccountOfAccount1);

        TestDatabase.Execute(db, Post);

        Assert.Equal("posted", StatusOf(db));
        // **書いた値が残っていることまで見る**（qa/03 L-04）。
        Assert.Equal(3L, TestDatabase.ScalarOf<long>(
            db, "SELECT sub_account_id FROM journal_lines WHERE journal_entry_id = 1 AND line_no = 1"));
    }

    [Fact]
    public void 使わない科目でも補助科目が空なら計上できる()
    {
        using var db = Draft(SubAccountOfUnusedAccount, subAccountId: null);

        TestDatabase.Execute(db, Post);

        Assert.Equal("posted", StatusOf(db));
    }

    [Fact]
    public void 取消は補助科目が付いていても計上できる()
    {
        // **規則より前に計上された伝票を打ち消せなくなってはいけない**（docs/10 §5・ADR-0004）。
        // 取消の明細はサーバが原仕訳から作るので、利用者に直す手立てが無い。
        using var db = Draft(SubAccountOfUnusedAccount, SubAccountOfAccount1, "reversal");

        TestDatabase.Execute(db, Post);

        Assert.Equal("posted", StatusOf(db));
    }

    [Fact]
    public void 取消は補助科目が無くても計上できる()
    {
        // 「要る」側も同じ線である。使う科目に変えられた後の過去の明細は補助科目を持たない。
        using var db = Draft(UsesSubAccount, subAccountId: null, "reversal");

        TestDatabase.Execute(db, Post);

        Assert.Equal("posted", StatusOf(db));
    }

    [Fact]
    public void 訂正は補助科目が付いていると計上できない()
    {
        // **訂正の再計上は利用者が直せる**ので止める（JournalCorrectionPosting は明細を書き換えない）。
        // ここを外すと、訂正を経由して規則より後の違反を新しく帳簿へ入れられる。
        using var db = Draft(SubAccountOfUnusedAccount, SubAccountOfAccount1, "correction");

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));

        Assert.Contains("補助科目を使わない勘定科目の明細に補助科目は付けられない", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 訂正は補助科目が無いと計上できない()
    {
        using var db = Draft(UsesSubAccount, subAccountId: null, "correction");

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));

        Assert.Contains("補助科目を使う勘定科目の明細には補助科目が要る", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 別の伝票の違反明細は関係ない()
    {
        // **トリガは「この伝票の明細」だけを見る。** `l.journal_entry_id = NEW.id` を落とすと、
        // **違反明細を持つ伝票が 1 件でもある DB では、以後どの伝票も計上できなくなる**——
        // 稼働 DB には規則より前に計上された伝票が実在する（伝票 36）。
        using var db = Draft(SubAccountOfUnusedAccount, SubAccountOfAccount1, "reversal");
        TestDatabase.Execute(db, Post);

        // 違反明細を持つ取消（伝票 1）が計上済みのまま、正しい伝票を新しく計上する。
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type,
                                         description, entered_at)
                VALUES (2, 1, '2026-05-21', '2026-05-21', 'draft', 'normal', '別の伝票', '2026-05-21 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (2, 1, 'debit', 1, 500, 1);
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                VALUES (2, 2, 'credit', 2, 2, 500, 1);
            UPDATE journal_entries SET status = 'posted', entry_no = 2, posted_at = '2026-05-21 11:00:00' WHERE id = 2;
            """);

        Assert.Equal("posted", TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 2"));
    }

    [Fact]
    public void 取消と名乗るだけでは外れない()
    {
        // **entry_type は取込・CLI・手打ちの SQL が自由に書ける列である。**
        // 原仕訳に同じ組み合わせの明細が無ければ、取消でも止める。
        using var db = Draft(SubAccountOfUnusedAccount, subAccountId: null, "reversal");
        TestDatabase.Execute(
            db, $"UPDATE journal_lines SET sub_account_id = {SubAccountOfAccount1} WHERE journal_entry_id = 1 AND line_no = 1");

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));

        Assert.Contains("補助科目を使わない勘定科目の明細に補助科目は付けられない", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 計上済みの違反伝票を触ると計上済みの断りが出る()
    {
        // **OLD.status を見ていないと、規則より前に計上された伝票を触ったときにこのトリガが鳴り、**
        // **本来出るべき「計上済みの仕訳は変更できない」を隠す**（0015 が摘要で直したのと同じ型）。
        using var db = Draft(SubAccountOfUnusedAccount, SubAccountOfAccount1, "reversal");
        TestDatabase.Execute(db, Post);

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            db, "UPDATE journal_entries SET description = '触った' WHERE id = 1"));

        Assert.Contains("計上済みの仕訳は変更できない", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 下書きの伝票は触れる()
    {
        // **NEW.status を落とすと、下書きの保存まで止まる。**
        using var db = Draft(SubAccountOfUnusedAccount, SubAccountOfAccount1);

        TestDatabase.Execute(db, "UPDATE journal_entries SET description = '書き直した' WHERE id = 1");

        Assert.Equal("書き直した", TestDatabase.ScalarOf<string>(db, "SELECT description FROM journal_entries WHERE id = 1"));
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 下書きのままなら補助科目を付けたままにできる()
    {
        // **打ちかけの伝票を止めない。** ここが赤くなったら、下書きにまで規則を広げてしまっている。
        using var db = Draft(SubAccountOfUnusedAccount, subAccountId: null);

        TestDatabase.Execute(db, $"UPDATE journal_lines SET sub_account_id = {SubAccountOfAccount1} WHERE id = 1");

        Assert.Equal(3L, TestDatabase.ScalarOf<long>(db, "SELECT sub_account_id FROM journal_lines WHERE id = 1"));
        Assert.Equal("draft", StatusOf(db));
    }
}
