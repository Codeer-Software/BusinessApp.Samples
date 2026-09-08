namespace BusinessApp.AccountingCore.Tests.Journals;

using System.Reflection;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;
using BusinessApp.Partners;

/// <summary>
/// 伝票の複製（ADR-0048）。
/// </summary>
/// <remarks>
/// <b>ここで守っているのは「複製した伝票が、していない出来事を語らない」ことである。</b>
/// 計上時点の写し・適用した制度の版・投入元を写すと、
/// <b>帳簿に嘘の記録が残る</b>——しかも利用者からは見えない欄なので、気づけない。
/// </remarks>
public class JournalDuplicationTests
{
    private static readonly DateOnly TransactionDate = new(2026, 5, 20);
    private static readonly DateOnly DuplicatedOn = new(2026, 6, 10);
    private static readonly DateTimeOffset EnteredAt = new(2026, 6, 10, 9, 0, 0, TimeSpan.FromHours(9));

    /// <summary>複製して、できた下書きを取り出す（作れないはずの検体は下の Theory が見る）。</summary>
    private static JournalEntry Duplicate(JournalEntry original)
    {
        var result = JournalDuplication.Duplicate(
            original, DuplicatedOn, EnteredAt, AccountingFixture.FiscalYear);

        Assert.True(result.Created);
        Assert.Empty(result.Violations);
        return result.Draft!;
    }

    /// <summary>計上済みの伝票（写してはいけない欄を全部埋めてある）。</summary>
    /// <remarks>
    /// <b>写さない欄には、既定値と違う値を入れる。</b> 既定値のままだと、
    /// 写していても写していなくても同じに見える（qa/03 L-02 の縮退）。
    /// </remarks>
    private static JournalEntry Posted()
        => AccountingFixture.CashSale(TransactionDate) with
        {
            Id = new JournalEntryId(42),
            Status = EntryStatus.Posted,
            EntryType = EntryType.Correction,
            EntryNo = 7,
            OriginalEntryId = new JournalEntryId(41),
            PostedAt = new DateTimeOffset(2026, 5, 20, 18, 0, 0, TimeSpan.FromHours(9)),
            PostedBy = 91,
            SourceComponent = "expense",
            SourceDocumentId = "EX-001",
            IdempotencyKey = "expense/EX-001",
            PartnerId = AccountingFixture.Partner,
        };

    // --- 写すもの（取引の内容） -------------------------------------------------

    [Fact]
    public void 取引日と摘要と取引先を写す()
    {
        // **通常の伝票で見る。** 取消・訂正は摘要の接頭辞を落とすので、
        // その検体で「摘要を写す」を表明すると、剥がす規則ごと固定してしまう。
        var original = Posted() with { EntryType = EntryType.Normal };

        var copy = Duplicate(original);

        Assert.Equal(original.TransactionDate, copy.TransactionDate);
        Assert.Equal(original.Description, copy.Description);
        Assert.Equal(original.PartnerId, copy.PartnerId);
        Assert.Equal(AccountingFixture.DefaultDescription, copy.Description);
    }

    /// <summary>明細の内容を写す（貸借・科目・部門・金額・税区分ほか）。</summary>
    [Fact]
    public void 明細の内容を写す()
    {
        var original = AccountingFixture.Entry(
            TransactionDate,
            AccountingFixture.Line(
                1, DebitCredit.Debit, AccountingFixture.BankAccount, 100_000,
                subAccountId: AccountingFixture.MainBank,
                department: AccountingFixture.SalesDepartment,
                taxCategoryId: AccountingFixture.TaxablePurchase,
                partner: AccountingFixture.Partner) with
            {
                TaxTreatment = TaxTreatment.ForTaxableSales,
                ItemDescription = "文房具",
                BookOnlyDeduction = "public_transport",
            },
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 100_000,
                department: AccountingFixture.SalesDepartment));

        var line = Duplicate(original).Lines[0];

        Assert.Equal(DebitCredit.Debit, line.DebitCredit);
        Assert.Equal(AccountingFixture.BankAccount, line.AccountId);
        Assert.Equal(AccountingFixture.MainBank, line.SubAccountId);
        Assert.Equal(AccountingFixture.SalesDepartment, line.DepartmentId);
        Assert.Equal(AccountingFixture.Partner, line.PartnerId);
        Assert.Equal(Yen.From(100_000), line.Amount);
        Assert.Equal(AccountingFixture.TaxablePurchase, line.TaxCategoryId);
        Assert.Equal(TaxTreatment.ForTaxableSales, line.TaxTreatment);
        Assert.Equal("文房具", line.ItemDescription);
        Assert.Equal("public_transport", line.BookOnlyDeduction);
    }

    /// <summary>
    /// <b>取消・訂正を複製すると、摘要の接頭辞は落ちる。</b>
    /// </summary>
    /// <remarks>
    /// <b>複製でできるのは通常の伝票である。</b> 「伝票番号 44 の取消: …」を写すと、
    /// <b>していない取消を名乗る通常の伝票</b>ができる——摘要は帳簿の記載事項
    /// （法税規則 55 ① の「内容」）なので、<b>帳簿に嘘が残る</b>
    /// （2026-09-09 の実機確認で見つけた）。
    /// </remarks>
    [Theory]
    [InlineData(EntryType.Reversal, "伝票番号 44 の取消: 5 月分の現金売上", "5 月分の現金売上")]
    [InlineData(EntryType.Correction, "伝票番号 44 の訂正: 5 月分の現金売上", "5 月分の現金売上")]
    [InlineData(EntryType.Correction, "伝票番号 5 の訂正: 伝票番号 3 の取消: 家賃", "家賃")]
    public void 取消と訂正の摘要は接頭辞を落として写す(EntryType entryType, string description, string expected)
    {
        var copy = Duplicate(Posted() with { EntryType = entryType, Description = description });

        Assert.Equal(expected, copy.Description);
    }

    /// <summary>
    /// 本文の無い取消の摘要は、引き継ぐものが無いので<b>空</b>になる。
    /// </summary>
    /// <remarks>
    /// <b>空文字ではなく NULL</b>（docs/04 §1 の A-5）。<b>計上には摘要が要る</b>ので、
    /// 利用者はここで何の取引かを書くことになる——それが正しい。
    /// </remarks>
    [Fact]
    public void 本文の無い取消の摘要は空になる()
    {
        var copy = Duplicate(Posted() with { EntryType = EntryType.Reversal, Description = "伝票番号 44 の取消" });

        Assert.Null(copy.Description);
    }

    /// <summary>摘要の無い伝票を複製しても落ちない（空は空のまま）。</summary>
    /// <remarks>
    /// <b>摘要の無い計上済みが稼働 DB に 2 件ある</b>（規則より前の伝票。docs/10 §4-2-1）。
    /// </remarks>
    [Theory]
    [InlineData(EntryType.Normal)]
    [InlineData(EntryType.Reversal)]
    public void 摘要の無い伝票を複製しても落ちない(EntryType entryType)
    {
        var copy = Duplicate(Posted() with { EntryType = entryType, Description = null });

        Assert.Null(copy.Description);
    }

    /// <summary>空白だけの摘要は、通常の伝票でも空にする。</summary>
    /// <remarks>
    /// <b>素通しにすると「見た目は入っているのに計上のときだけ断られる」</b>
    /// （10 §4-2-1 の二層は空白だけを空とみなす。2026-09-09 の自己レビュー）。
    /// </remarks>
    [Fact]
    public void 空白だけの摘要は通常の伝票でも空にする()
    {
        var copy = Duplicate(Posted() with { EntryType = EntryType.Normal, Description = "   " });

        Assert.Null(copy.Description);
    }

    /// <summary>
    /// <b>通常の伝票の摘要は、同じ形をしていても剥がさない。</b>
    /// </summary>
    /// <remarks>
    /// 利用者が「伝票番号 12 の取消について」と書くことはある。無条件に剥がすと本文を落とす
    /// （<c>AmendmentRules.Describe</c> が同じ理由で種別を見ている）。
    /// </remarks>
    [Fact]
    public void 通常の伝票の摘要は同じ形でも剥がさない()
    {
        var copy = Duplicate(
            Posted() with { EntryType = EntryType.Normal, Description = "伝票番号 12 の取消: 家賃" });

        Assert.Equal("伝票番号 12 の取消: 家賃", copy.Description);
    }

    // --- 写さないもの（出来事の記録） -------------------------------------------

    /// <summary>
    /// <b>計上の事実は 1 つも写さない。</b>
    /// </summary>
    /// <remarks>
    /// 写すと<b>していない計上を語る伝票</b>ができる。伝票番号は計上時に採る（I-17）。
    /// </remarks>
    [Fact]
    public void 計上の事実を写さない()
    {
        var copy = Duplicate(Posted());

        Assert.Null(copy.Id);
        Assert.Null(copy.EntryNo);
        Assert.Equal(EntryStatus.Draft, copy.Status);
        Assert.Null(copy.PostedAt);
        Assert.Null(copy.PostedBy);
    }

    /// <summary>
    /// <b>訂正・取消を複製しても、できるのは通常の記帳である。</b>
    /// </summary>
    /// <remarks>
    /// 種別を写すと<b>原仕訳を持たない訂正伝票</b>ができて I-06 を破る。
    /// </remarks>
    [Theory]
    [InlineData(EntryType.Normal)]
    [InlineData(EntryType.Correction)]
    [InlineData(EntryType.Reversal)]
    public void 種別は通常になり原仕訳との関係は切れる(EntryType entryType)
    {
        var copy = Duplicate(Posted() with { EntryType = entryType });

        Assert.Equal(EntryType.Normal, copy.EntryType);
        Assert.Null(copy.OriginalEntryId);
    }

    /// <summary>
    /// <b>投入元は写さない</b>（docs/10 §10）。
    /// </summary>
    /// <remarks>
    /// 投入元は<b>「その部品が投げた」という事実</b>である。手で複製したものは手入力なので、
    /// 写すと投入元の部品が<b>自分が投げていない伝票を自分のものとして辿る</b>。
    /// 冪等キーは一意（I-14）なので、そもそも写せない。
    /// </remarks>
    [Fact]
    public void 投入元と冪等キーを写さない()
    {
        var copy = Duplicate(Posted());

        Assert.Null(copy.SourceComponent);
        Assert.Null(copy.SourceDocumentId);
        Assert.Null(copy.IdempotencyKey);
    }

    /// <summary>
    /// <b>明細の「計上時点の写し」と「適用した制度の版」を写さない。</b>
    /// </summary>
    /// <remarks>
    /// <b>ここがいちばん重い。</b> 適用した版（I-16）を写すと、
    /// <b>新しい日付の伝票に古い税率の版が付いたまま計上できる</b>。
    /// 取引先名と登録番号の写しは計上時に取り直す（ADR-0018）。
    /// </remarks>
    [Fact]
    public void 明細の計上時点の写しと制度の版を写さない()
    {
        var original = AccountingFixture.Entry(
            TransactionDate,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 100_000) with
            {
                PartnerNameSnapshot = "株式会社取引先",
                RegistrationNoSnapshot = "T1234567890123",
                AppliedRuleVersion = new RuleVersion("2023-10-01"),
                TaxPoint = new DateOnly(2026, 5, 20),
                EvidenceRef = "EV-001",
            },
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 100_000,
                department: AccountingFixture.SalesDepartment));

        var line = Duplicate(original).Lines[0];

        Assert.Null(line.PartnerNameSnapshot);
        Assert.Null(line.RegistrationNoSnapshot);
        Assert.Null(line.AppliedRuleVersion);
        Assert.Null(line.EvidenceRef);

        // **課税仕入れの時点は写す**（画面に無い欄なので、落とすと入れ直せない）。
        Assert.Equal(new DateOnly(2026, 5, 20), line.TaxPoint);
    }

    /// <summary>
    /// <b>消費税行は落とし、行番号を 1 から振り直す。</b>
    /// </summary>
    /// <remarks>
    /// 税行はシステムが計上時に作る（docs/11 §2）ので、写すと
    /// <b>利用者が直せない行だけが古い金額のまま残る</b>。
    /// </remarks>
    [Fact]
    public void 消費税行を落として行番号を振り直す()
    {
        var original = AccountingFixture.Entry(
            TransactionDate,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 100_000),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 10_000) with
            {
                IsTaxLine = true,
                ParentLineNo = 1,
            },
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Sales, 110_000,
                department: AccountingFixture.SalesDepartment));

        var copy = Duplicate(original);

        Assert.Equal(2, copy.Lines.Count);
        Assert.Equal([1, 2], copy.Lines.Select(l => l.LineNo));
        Assert.All(copy.Lines, l => Assert.False(l.IsTaxLine));
        Assert.All(copy.Lines, l => Assert.Null(l.ParentLineNo));

        // **本体行はそのまま残る**（落としたのは税行だけ）。
        Assert.Equal([AccountingFixture.Cash, AccountingFixture.Sales], copy.Lines.Select(l => l.AccountId));
    }

    // --- 日付と年度 -------------------------------------------------------------

    /// <summary>
    /// 計上日は複製した日、会計年度はその計上日の年度、入力年月日はいま。
    /// </summary>
    /// <remarks>
    /// <b>会計期間への帰属は「いま起こす記帳」のもの</b>である（I-03）。
    /// 原仕訳の計上日を写すと、閉じた期間に新しい伝票を落とせてしまう。
    /// </remarks>
    [Fact]
    public void 計上日と年度と入力年月日は複製した時点のもの()
    {
        var original = Posted() with { FiscalYearId = AccountingFixture.OtherFiscalYear };

        var copy = Duplicate(original);

        Assert.Equal(DuplicatedOn, copy.PostingDate);
        Assert.Equal(AccountingFixture.FiscalYear, copy.FiscalYearId);
        Assert.Equal(EnteredAt, copy.EnteredAt);

        // **取引日だけは原仕訳のものを引き継ぐ**（ADR-0048 の決定 5）。
        Assert.Equal(original.TransactionDate, copy.TransactionDate);
        Assert.NotEqual(copy.PostingDate, copy.TransactionDate);
    }

    /// <summary>下書きでも計上済みでも同じように複製できる（原仕訳の状態を見ない）。</summary>
    [Theory]
    [InlineData(EntryStatus.Draft)]
    [InlineData(EntryStatus.Posted)]
    public void 原仕訳の状態を問わない(EntryStatus status)
    {
        var copy = Duplicate(Posted() with { Status = status });

        Assert.Equal(EntryStatus.Draft, copy.Status);
        Assert.Equal(2, copy.Lines.Count);
    }

    [Fact]
    public void 原仕訳を渡さなければ止まる()
        => Assert.Equal(
            "original",
            Assert.Throws<ArgumentNullException>(
                () => JournalDuplication.Duplicate(null!, DuplicatedOn, EnteredAt, AccountingFixture.FiscalYear))
                .ParamName);

    // --- 複製できる種別（ADR-0048 の決定 6） -----------------------------------

    /// <summary>
    /// <b>期首残高・決算振替・繰越は複製できない。</b>
    /// </summary>
    /// <remarks>
    /// <b>複製でできるのは通常の伝票</b>なので、決算振替を複製すると
    /// 「損益 → 繰越利益剰余金」を<b>期中に通常の伝票として計上できる</b>（I-10）し、
    /// 繰越の複製は翌期首の残高を二重に載せる（I-12）。
    /// <b>いまはどれも未実装だが、実装した日に黙って開かないようにここで止める</b>
    /// （2026-09-09 の自己レビュー。qa/03 L-13 の型）。
    /// </remarks>
    [Theory]
    [InlineData(EntryType.Opening, "期首残高")]
    [InlineData(EntryType.Closing, "決算振替")]
    [InlineData(EntryType.Carryover, "繰越")]
    public void 期首残高と決算振替と繰越は複製できない(EntryType entryType, string label)
    {
        var result = JournalDuplication.Duplicate(
            Posted() with { EntryType = entryType }, DuplicatedOn, EnteredAt, AccountingFixture.FiscalYear);

        Assert.False(result.Created);
        Assert.Equal(
            [JournalViolationCodes.DuplicationTargetNotDuplicable],
            result.Violations.Select(v => v.Code));
        Assert.Equal(
            $"種別が「{label}」の伝票は対象にできません。対象にできるのは通常の伝票と訂正・取消です。",
            result.Violations[0].Message);

        // **見出しの語を文の側で繰り返さない**（docs/21 §2-6。`ViolationMessageTests` が全文に当てる）。
        Assert.DoesNotContain("複製できません", result.Violations[0].Message, StringComparison.Ordinal);
    }

    /// <summary>通常・訂正・取消は複製できる（種別ごとの線を両側から見る）。</summary>
    [Theory]
    [InlineData(EntryType.Normal)]
    [InlineData(EntryType.Correction)]
    [InlineData(EntryType.Reversal)]
    public void 通常と訂正と取消は複製できる(EntryType entryType)
        => Assert.True(
            JournalDuplication.Duplicate(
                Posted() with { EntryType = entryType },
                DuplicatedOn, EnteredAt, AccountingFixture.FiscalYear).Created);

    // --- 欄が増えた日に気づく ---------------------------------------------------

    /// <summary>
    /// <b>伝票と明細の欄を数え、どちらの側にあるかを 1 つずつ決めてある。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>欄が増えた日に、黙って「写さない」側へ落ちるのを止める。</b>
    /// 複製は写す欄を書き出して組み立てるので、<b>新しい欄は何も言わずに落ちる</b>
    /// ——それが既定として正しい（ADR-0048 の決定 2）が、
    /// <b>決めた覚えのないまま落ちるのは別の話</b>である。
    /// ここが赤くなったら、その欄を写すかどうかを決めて表に足す。</para>
    /// <para><b>一覧を実装から読まない</b>（qa/03 の「検体は期待する値を自分で書き下す」）。</para>
    /// </remarks>
    [Fact]
    public void 伝票の欄はすべて写す側か写さない側に決めてある()
        => AssertCovered(
            typeof(JournalEntry),
            copied: ["TransactionDate", "Description", "PartnerId"],
            notCopied:
            [
                "Id", "EntryNo", "Status", "EntryType", "OriginalEntryId",
                "SourceComponent", "SourceDocumentId", "IdempotencyKey",
                "PostedAt", "PostedBy",
            ],
            decidedOutside: ["FiscalYearId", "PostingDate", "EnteredAt", "Lines"],
            computed: ["DebitTotal", "CreditTotal", "IsBalanced"]);

    [Fact]
    public void 明細の欄はすべて写す側か写さない側に決めてある()
        => AssertCovered(
            typeof(JournalLine),
            copied:
            [
                "DebitCredit", "AccountId", "SubAccountId", "DepartmentId", "PartnerId",
                "Amount", "TaxCategoryId", "TaxTreatment", "TaxPoint",
                "ItemDescription", "BookOnlyDeduction",
            ],
            notCopied:
            [
                "PartnerNameSnapshot", "RegistrationNoSnapshot", "AppliedRuleVersion",
                "IsTaxLine", "ParentLineNo", "EvidenceRef",
            ],
            decidedOutside: ["LineNo"],
            computed: []);

    /// <summary>
    /// <b>「写す」に並べた欄は本当に写り、「写さない」に並べた欄は本当に落ちる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>一覧と実装を結ぶのはここである。</b> 上の網羅テストは
    /// 「4 つの一覧の和が型の欄と一致する」ことしか見ないので、
    /// <b>一覧に足して実装を直し忘れても緑</b>だった（2026-09-09 の自己レビューで、
    /// 3 通りの壊し方が全部すり抜けることを実測された）。</para>
    /// <para><b>一覧を回して、欄ごとに読む。</b> 手で書き写した別の一覧を並べると、
    /// 欄が増えた日にそちらを直し忘れる。</para>
    /// <para><b>検体が縮退していないことも同時に見る</b>——
    /// 原仕訳の側が既定値なら、写していなくても一致してしまう（qa/03 L-02）。</para>
    /// </remarks>
    [Fact]
    public void 明細は写す欄が写り写さない欄が落ちる()
    {
        var original = AccountingFixture.Entry(TransactionDate, SentinelLine());

        AssertCopiedAndDropped(
            original.Lines[0],
            Duplicate(original).Lines[0],
            copied:
            [
                "DebitCredit", "AccountId", "SubAccountId", "DepartmentId", "PartnerId",
                "Amount", "TaxCategoryId", "TaxTreatment", "TaxPoint",
                "ItemDescription", "BookOnlyDeduction",
            ],
            notCopied: ["PartnerNameSnapshot", "RegistrationNoSnapshot", "AppliedRuleVersion", "EvidenceRef", "ParentLineNo"]);
    }

    /// <summary>
    /// 伝票の側も同じように読む。
    /// </summary>
    /// <remarks>
    /// <b>摘要はここで見ない。</b> 写し方が種別で変わる（取消・訂正は接頭辞を落とす。決定 4）ので、
    /// <b>「非既定の種別」と「摘要をそのまま写す」を同時に満たす検体が作れない</b>
    /// ——複製できて接頭辞を落とさない種別は「通常」だけで、それは種別の既定値である。
    /// 摘要は上の 4 本が種別ごとに見る。
    /// </remarks>
    [Fact]
    public void 伝票は写す欄が写り写さない欄が落ちる()
    {
        var original = Posted();

        AssertCopiedAndDropped(
            original,
            Duplicate(original),
            copied: ["TransactionDate", "PartnerId"],
            notCopied:
            [
                "Id", "EntryNo", "Status", "EntryType", "OriginalEntryId",
                "SourceComponent", "SourceDocumentId", "IdempotencyKey", "PostedAt", "PostedBy",
            ]);
    }

    /// <summary>
    /// 写す欄と写さない欄を、名前の一覧から読み比べる。
    /// </summary>
    /// <remarks>
    /// <b>「写さない」の期待値は型の既定値</b>（<c>null</c>・<c>false</c>・列挙の 0）である。
    /// 複製は写す欄だけを書き出して組み立てるので、写さない欄は既定値のまま残る。
    /// </remarks>
    private static void AssertCopiedAndDropped(
        object source, object copy, string[] copied, string[] notCopied)
    {
        foreach (var name in copied)
        {
            var property = source.GetType().GetProperty(name)!;
            var value = property.GetValue(source);

            Assert.NotEqual(Default(property.PropertyType), value);
            Assert.Equal(value, property.GetValue(copy));
        }

        foreach (var name in notCopied)
        {
            var property = source.GetType().GetProperty(name)!;

            Assert.NotEqual(Default(property.PropertyType), property.GetValue(source));
            Assert.Equal(Default(property.PropertyType), property.GetValue(copy));
        }
    }

    /// <summary>型の既定値。</summary>
    private static object? Default(Type type)
        => type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? Activator.CreateInstance(type)
            : null;

    /// <summary>
    /// 欄という欄に<b>既定値でない値</b>を入れた明細。
    /// </summary>
    /// <remarks>
    /// <b><c>IsTaxLine</c> だけは立てない</b>——立てると行ごと落ちる（決定 3）ので、
    /// 「写らない」ことをこの検体では見られない。税行は
    /// <see cref="消費税行を落として行番号を振り直す"/> が専門に見る。
    /// </remarks>
    private static JournalLine SentinelLine()
        => AccountingFixture.Line(
            7, DebitCredit.Credit, AccountingFixture.BankAccount, 12_345,
            subAccountId: AccountingFixture.MainBank,
            department: AccountingFixture.SalesDepartment,
            taxCategoryId: AccountingFixture.TaxablePurchase,
            partner: AccountingFixture.Partner) with
        {
            TaxTreatment = TaxTreatment.Common,
            TaxPoint = new DateOnly(2026, 4, 30),
            ItemDescription = "文房具",
            BookOnlyDeduction = "public_transport",
            PartnerNameSnapshot = "株式会社取引先",
            RegistrationNoSnapshot = "T1234567890123",
            AppliedRuleVersion = new RuleVersion("2023-10-01"),
            EvidenceRef = "EV-001",
            ParentLineNo = 3,
        };

    /// <summary>型の欄が、4 つの区分のどれかに 1 度だけ現れることを確かめる。</summary>
    private static void AssertCovered(
        Type type,
        string[] copied,
        string[] notCopied,
        string[] decidedOutside,
        string[] computed)
    {
        string[] all = [.. copied, .. notCopied, .. decidedOutside, .. computed];

        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal),
            all.OrderBy(n => n, StringComparer.Ordinal));
    }
}
