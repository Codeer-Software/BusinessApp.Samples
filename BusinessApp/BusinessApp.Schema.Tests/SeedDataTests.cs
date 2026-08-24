namespace BusinessApp.Schema.Tests;

/// <summary>
/// 初期データ（<c>Designer/seed/</c>）の検査。
/// </summary>
/// <remarks>
/// 初期データは「新しく会計コアを立ち上げたとき、最初に入っていてほしいもの」であり、
/// <b>間違っていても designcheck もコンパイラも教えてくれない</b>。
/// 制約に反していないことと、設計上の約束（税率を名前に書かない・用途区分を既定値で埋めない等）が
/// 守られていることを検査する。
/// </remarks>
public class SeedDataTests
{
    [Fact]
    public void 初期データ一式が適用できる()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM company_profile"));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM fiscal_years"));
        Assert.Equal(12L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM accounting_periods"));
        Assert.Equal(6L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM departments"));
        Assert.Equal(10L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM tax_categories"));
        Assert.Equal(105L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM accounts"));
    }

    [Fact]
    public void 初期データは二度流せない()
    {
        // 冪等ではない。運用で二重適用しないことを前提にしている（UNIQUE 制約が守る）。
        using var db = TestDatabase.CreateWithSeed();

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(db,
            File.ReadAllText(TestDatabase.SeedFiles()[1])));
    }

    /// <summary>会計期間は年度を隙間なく覆い、月初日と月末日でつながっている。</summary>
    [Fact]
    public void 会計期間は年度を隙間なく覆う()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal("2026-04-01", TestDatabase.ScalarOf<string>(db, "SELECT MIN(start_date) FROM accounting_periods"));
        Assert.Equal("2027-03-31", TestDatabase.ScalarOf<string>(db, "SELECT MAX(end_date) FROM accounting_periods"));

        var gaps = TestDatabase.ScalarOf<long>(db, """
            SELECT COUNT(*) FROM accounting_periods p
            WHERE p.start_date <> (SELECT DATE(MAX(q.end_date), '+1 day') FROM accounting_periods q WHERE q.end_date < p.start_date)
              AND p.start_date <> (SELECT MIN(start_date) FROM accounting_periods)
            """);

        Assert.Equal(0L, gaps);
    }

    [Fact]
    public void 会計期間は会計年度の範囲に収まっている()
    {
        using var db = TestDatabase.CreateWithSeed();

        var outside = TestDatabase.ScalarOf<long>(db, """
            SELECT COUNT(*) FROM accounting_periods p
            JOIN fiscal_years y ON y.id = p.fiscal_year_id
            WHERE p.start_date < y.start_date OR p.end_date > y.end_date
            """);

        Assert.Equal(0L, outside);
    }

    /// <summary>優良な電子帳簿は課税期間の初日から要件を満たす必要がある（法 8 ④・令 2）。</summary>
    [Fact]
    public void 優良な電子帳簿の適用開始日が年度の開始日と一致している()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM fiscal_years WHERE premium_ledger_from IS NOT NULL AND premium_ledger_from <> start_date"));
    }

    [Fact]
    public void 全社共通の部門がちょうど一件ある()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM departments WHERE is_company_wide = 1"));
        Assert.Equal("全社共通", TestDatabase.ScalarOf<string>(db, "SELECT name FROM departments WHERE is_company_wide = 1"));
    }

    /// <summary>
    /// 税区分の名前に税率の数値を書かない。その日に何 % かは制度ルールが決めるので、
    /// 名前に埋めると改正の日にマスタ名が嘘になる（CLAUDE.md §2-3）。
    /// </summary>
    [Fact]
    public void 税区分の名前に税率の数値を書いていない()
    {
        using var db = TestDatabase.CreateWithSeed();

        var withRate = TestDatabase.Query(db, """
            SELECT name FROM tax_categories
            WHERE rate_kind IN ('standard', 'reduced') AND (name LIKE '%10%' OR name LIKE '%8%' OR name LIKE '%％%' OR name LIKE '%!%%' ESCAPE '!')
            """);

        Assert.Empty(withRate);
    }

    /// <summary>
    /// 非課税にも売上／仕入の軸が通っていること。課税だけ分けて非課税を 1 つに潰すと、
    /// 課税売上割合の分母（課税＋免税＋非課税の売上高）を税区分だけでは作れない（docs/06 §7）。
    /// </summary>
    [Fact]
    public void 非課税にも売上と仕入の区別がある()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM tax_categories WHERE taxation_type = 'non_taxable_sales'"));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM tax_categories WHERE taxation_type = 'non_taxable_purchase'"));
    }

    /// <summary>
    /// 評価勘定（通常残高が科目区分と逆の科目）に印が付いていること。
    /// 付いていないと、科目区分だけから借方残／貸方残を決める処理が必ず誤る。
    /// </summary>
    [Theory]
    [InlineData("1350")]  // 貸倒引当金
    [InlineData("1940")]  // 減価償却累計額
    [InlineData("4090")]  // 売上値引・戻り高
    [InlineData("5110")]  // 期末仕掛品棚卸高
    public void 評価勘定に印が付いている(string code)
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, $"SELECT is_contra FROM accounts WHERE code = '{code}'"));
    }

    [Fact]
    public void 評価勘定以外に印は付いていない()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(4L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM accounts WHERE is_contra = 1"));
    }

    /// <summary>引当金は負債と費用が対で無いと計上仕訳が組めない。</summary>
    [Theory]
    [InlineData("2400", "6035")]  // 賞与引当金 ↔ 賞与引当金繰入額
    [InlineData("2600", "6045")]  // 退職給付引当金 ↔ 退職給付費用
    [InlineData("1350", "6285")]  // 貸倒引当金 ↔ 貸倒引当金繰入額
    public void 引当金には相手勘定がある(string provision, string expense)
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db,
            $"SELECT COUNT(*) FROM accounts WHERE code IN ('{provision}', '{expense}')"));
    }

    /// <summary>法人税等調整額を使うには繰延税金資産・負債が要る（相手勘定が無いと仮勘定に逃げる）。</summary>
    [Fact]
    public void 税効果会計の科目が揃っている()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(3L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM accounts WHERE code IN ('1700', '2700', '7920')"));
    }

    /// <summary>同じ性質の科目で既定税区分の扱いが割れていないこと。</summary>
    [Fact]
    public void 固定資産の取得はすべて課税仕入を既定にしている()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db, """
            SELECT COUNT(*) FROM accounts a
            WHERE a.is_fixed_asset = 1
              AND (a.default_tax_category_id IS NULL
                   OR a.default_tax_category_id <> (SELECT id FROM tax_categories WHERE code = 'TP'))
            """));
    }

    [Fact]
    public void 課税区分と税率区分の対応が取れている()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(6L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM tax_categories WHERE taxation_type IN ('taxable_sales', 'taxable_purchase')"));
        Assert.Equal(6L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM tax_categories WHERE rate_kind IS NOT NULL"));
    }

    /// <summary>
    /// 個別対応方式では用途区分を取引ごとに選ぶ。既定値で埋めると
    /// 「値は入っているが意味がない」状態を作る（docs/04 §9-1）。
    /// </summary>
    [Fact]
    public void 用途区分の既定値は入れていない()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM tax_categories WHERE default_tax_treatment IS NOT NULL"));
    }

    [Fact]
    public void 勘定科目は科目区分をすべて備えている()
    {
        using var db = TestDatabase.CreateWithSeed();

        foreach (var category in new[] { "asset", "liability", "equity", "revenue", "expense" })
        {
            Assert.True(
                TestDatabase.ScalarOf<long>(db, $"SELECT COUNT(*) FROM accounts WHERE category = '{category}'") > 0,
                $"{category} の科目が 1 つも無い");
        }
    }

    /// <summary>コードの先頭桁と科目区分が体系どおりに対応している（docs/04 §6）。</summary>
    [Theory]
    [InlineData("1", "asset")]
    [InlineData("2", "liability")]
    [InlineData("3", "equity")]
    [InlineData("4", "revenue")]
    public void 科目コードの先頭桁が科目区分と一致している(string prefix, string category)
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db,
            $"SELECT COUNT(*) FROM accounts WHERE SUBSTR(code, 1, 1) = '{prefix}' AND category <> '{category}'"));
    }

    [Fact]
    public void 費用の科目コードは五以上で始まる()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM accounts WHERE category = 'expense' AND CAST(SUBSTR(code, 1, 1) AS INTEGER) < 5"));
    }

    [Fact]
    public void 科目コードはすべて四桁である()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM accounts WHERE LENGTH(code) <> 4"));
    }

    /// <summary>税抜経理なので、消費税行の計上先が要る（docs/06 §2）。</summary>
    [Fact]
    public void 仮払消費税等と仮受消費税等の科目がある()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal("仮払消費税等", TestDatabase.ScalarOf<string>(db, "SELECT name FROM accounts WHERE code = '1540'"));
        Assert.Equal("仮受消費税等", TestDatabase.ScalarOf<string>(db, "SELECT name FROM accounts WHERE code = '2280'"));
    }

    [Fact]
    public void 既定税区分は実在する税区分を指している()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db, """
            SELECT COUNT(*) FROM accounts a
            WHERE a.default_tax_category_id IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM tax_categories t WHERE t.id = a.default_tax_category_id)
            """));
    }

    /// <summary>
    /// 判断が分かれる科目には既定税区分を入れない。既定値があると間違ったまま通ってしまう
    /// （docs/04 §6「値が入っていない行の穴埋めに使わない」）。
    /// </summary>
    [Theory]
    [InlineData("4290")]  // 雑収入
    [InlineData("6280")]  // 貸倒損失
    [InlineData("4410")]  // 固定資産売却益
    [InlineData("7510")]  // 固定資産売却損
    public void 判断が分かれる科目に既定税区分を入れていない(string code)
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db,
            $"SELECT COUNT(*) FROM accounts WHERE code = '{code}' AND default_tax_category_id IS NOT NULL"));
    }

    [Fact]
    public void 伝票番号の採番は会計年度ごとに一件で一から始まる()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_entry_sequences"));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT next_entry_no FROM journal_entry_sequences"));
    }
}
