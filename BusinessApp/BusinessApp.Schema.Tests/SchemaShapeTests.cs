namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

/// <summary>
/// スキーマの形。CLB の規約から外れると<b>静かに壊れる</b>ものを検査する。
/// </summary>
public class SchemaShapeTests
{
    private static readonly string[] BusinessTables =
    [
        "company_profile", "fiscal_years", "accounting_periods", "tax_categories",
        "accounts", "sub_accounts", "departments", "partners", "partner_invoice_registrations",
        "journal_entries", "journal_lines", "journal_entry_sequences",
    ];

    [Fact]
    public void DDL一式が適用できる()
    {
        using var db = TestDatabase.Create();

        var tables = TestDatabase.Query(db, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'");

        Assert.All(BusinessTables, table => Assert.Contains(table, tables));
    }

    [Fact]
    public void 主キーはすべてINTEGERのidで自動採番される()
    {
        using var db = TestDatabase.Create();

        foreach (var table in BusinessTables)
        {
            var pk = TestDatabase.Query(db, $"SELECT name || ' ' || type FROM pragma_table_info('{table}') WHERE pk = 1");

            Assert.Equal(["id INTEGER"], pk);
        }
    }

    /// <summary>
    /// 日付・日時の列を TEXT で宣言してはいけない。CLB は宣言型を .NET 型にマップするので、
    /// TEXT だと <c>DateOnly</c> が <c>MM/dd/yyyy</c> 文字列になり、年跨ぎの範囲検索が壊れる
    /// （DatabaseGuidelines）。designcheck では検出できない。
    /// </summary>
    [Theory]
    [InlineData("fiscal_years", "start_date", "DATE")]
    [InlineData("fiscal_years", "end_date", "DATE")]
    [InlineData("fiscal_years", "premium_ledger_from", "DATE")]
    [InlineData("accounting_periods", "start_date", "DATE")]
    [InlineData("accounting_periods", "end_date", "DATE")]
    [InlineData("journal_entries", "transaction_date", "DATE")]
    [InlineData("journal_entries", "posting_date", "DATE")]
    [InlineData("journal_entries", "entered_at", "DATETIME")]
    [InlineData("journal_entries", "posted_at", "DATETIME")]
    [InlineData("journal_lines", "tax_point", "DATE")]
    [InlineData("partner_invoice_registrations", "valid_from", "DATE")]
    [InlineData("partner_invoice_registrations", "ended_on", "DATE")]
    [InlineData("partner_invoice_registrations", "confirmed_on", "DATE")]
    [InlineData("partner_invoice_registrations", "nta_updated_on", "DATE")]
    public void 日付列はDATEまたはDATETIMEで宣言されている(string table, string column, string expected)
    {
        using var db = TestDatabase.Create();

        var declared = TestDatabase.Query(db, $"SELECT type FROM pragma_table_info('{table}') WHERE name = '{column}'");

        Assert.Equal([expected], declared);
    }

    /// <summary>金額は整数円。REAL を使うと丸め誤差が帳簿に入る（docs/10 §3）。</summary>
    [Fact]
    public void 金額列はINTEGERである()
    {
        using var db = TestDatabase.Create();

        var declared = TestDatabase.Query(db, "SELECT type FROM pragma_table_info('journal_lines') WHERE name = 'amount'");

        Assert.Equal(["INTEGER"], declared);
    }

    /// <summary>
    /// 論理削除の列を置かない（docs/12 マスタ台帳・Designer/ddl/README）。
    /// 仕訳は消せず、マスタは無効化する。列があること自体が誤った経路になる。
    /// </summary>
    [Fact]
    public void 論理削除の列はどのテーブルにも無い()
    {
        using var db = TestDatabase.Create();

        foreach (var table in BusinessTables)
        {
            var columns = TestDatabase.Query(db, $"SELECT name FROM pragma_table_info('{table}')");

            Assert.DoesNotContain("logical_delete", columns);
            Assert.DoesNotContain("is_deleted", columns);
            Assert.DoesNotContain("deleted_at", columns);
        }
    }

    /// <summary>マスタは削除ではなく無効化する。有効フラグが無いと「削除するしかない」設計になる。</summary>
    [Theory]
    [InlineData("accounts")]
    [InlineData("sub_accounts")]
    [InlineData("departments")]
    [InlineData("partners")]
    [InlineData("tax_categories")]
    public void 会計マスタには有効フラグがある(string table)
    {
        using var db = TestDatabase.Create();

        var columns = TestDatabase.Query(db, $"SELECT name FROM pragma_table_info('{table}')");

        Assert.Contains("is_active", columns);
    }

    [Fact]
    public void 外部キーが有効になっている()
    {
        using var db = TestDatabase.Create();

        Assert.Equal(1, TestDatabase.ScalarOf<long>(db, "PRAGMA foreign_keys"));
    }

    [Fact]
    public void 計上済み仕訳を守るトリガが揃っている()
    {
        using var db = TestDatabase.Create();

        var triggers = TestDatabase.Query(db, "SELECT name FROM sqlite_master WHERE type = 'trigger'");

        Assert.Contains("trg_journal_entries_posted_no_update", triggers);
        Assert.Contains("trg_journal_entries_posted_no_delete", triggers);
        Assert.Contains("trg_journal_lines_posted_no_update", triggers);
        Assert.Contains("trg_journal_lines_posted_no_delete", triggers);
        Assert.Contains("trg_journal_lines_posted_no_insert", triggers);
        Assert.Contains("trg_journal_lines_no_move_into_posted", triggers);
        Assert.Contains("trg_journal_entries_no_replace_posted_insert", triggers);
        Assert.Contains("trg_journal_entries_no_replace_posted_update", triggers);
        Assert.Contains("trg_journal_lines_no_replace_posted_insert", triggers);
        Assert.Contains("trg_journal_lines_no_replace_posted_update", triggers);
        // 摘要のない仕訳を計上させない（docs/10 §4-2-1）
        Assert.Contains("trg_journal_entries_description_required_when_posted", triggers);
        // 使用中のマスタの意味を守る 4 本（ADR-0038）
        Assert.Contains("trg_accounts_meaning_frozen_when_posted", triggers);
        Assert.Contains("trg_sub_accounts_meaning_frozen_when_posted", triggers);
        Assert.Contains("trg_departments_meaning_frozen_when_posted", triggers);
        Assert.Contains("trg_tax_categories_meaning_frozen_when_posted", triggers);
        foreach (var table in new[] { "accounts", "sub_accounts", "departments", "tax_categories" })
        {
            Assert.Contains($"trg_{table}_no_replace_used_insert", triggers);
            Assert.Contains($"trg_{table}_no_replace_used_update", triggers);
        }
    }

    /// <summary>
    /// 会計コアのスキーマは、会計コアのテーブルだけを参照する。
    /// </summary>
    /// <remarks>
    /// <para>とくに<b>認証部品の <c>app_users</c> に外部キーを張らない</b>。
    /// ユーザーは会計コアの責務ではなく別部品のものであり（ADR-0029）、DB 制約で結ぶと
    /// 会計コアが認証部品なしでは立ち上がらなくなる。</para>
    /// <para>C# のモジュール依存（ADR-0013）と同じ規律を、DB のスキーマにも当てる。</para>
    /// </remarks>
    [Fact]
    public void 会計コアのスキーマは他部品のテーブルを参照しない()
    {
        using var db = TestDatabase.Create();

        var crossing = new List<string>();
        foreach (var table in BusinessTables)
        {
            foreach (var target in TestDatabase.Query(db, $"SELECT \"table\" FROM pragma_foreign_key_list('{table}')"))
            {
                if (!BusinessTables.Contains(target))
                {
                    crossing.Add($"{table} -> {target}");
                }
            }
        }

        Assert.Empty(crossing);
    }

    [Fact]
    public void DDLは番号順に並んでいて欠番がない()
    {
        var numbers = TestDatabase.DdlFiles()
            .Select(path => int.Parse(Path.GetFileName(path)[..3]))
            .ToList();

        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
    }
}
