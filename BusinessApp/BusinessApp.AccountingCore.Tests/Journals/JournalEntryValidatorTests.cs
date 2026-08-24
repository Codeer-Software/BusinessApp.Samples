namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>計上の関門（docs/04 §1 の不変条件）。</summary>
public class JournalEntryValidatorTests
{
    private static readonly DateOnly Ordinary = new(2026, 5, 20);

    [Fact]
    public void 貸借が一致した仕訳は計上できる()
    {
        Assert.Empty(Validate(AccountingFixture.CashSale(Ordinary)));
    }

    [Fact]
    public void 貸借が一致しない仕訳は計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 100_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 99_999,
                department: AccountingFixture.SalesDepartment));

        AssertViolation(JournalViolationCodes.Unbalanced, Validate(entry));
    }

    [Fact]
    public void 明細のない仕訳は計上できない()
    {
        var violations = Validate(AccountingFixture.Entry(Ordinary));

        AssertViolation(JournalViolationCodes.NoLines, violations);
        // 明細が無い時点で以降の明細検査は無意味なので、貸借一致の違反は重ねて出さない。
        Assert.DoesNotContain(violations, v => v.Code == JournalViolationCodes.Unbalanced);
    }

    [Fact]
    public void 会計期間のない日付には計上できない()
    {
        AssertViolation(JournalViolationCodes.PeriodNotFound, Validate(AccountingFixture.CashSale(new DateOnly(2027, 4, 1))));
    }

    [Fact]
    public void 締め済みの期間には計上できない()
    {
        var entry = AccountingFixture.CashSale(new DateOnly(2026, 9, 15));
        var context = AccountingFixture.Context(septemberStatus: PeriodStatus.Closed);

        AssertViolation(JournalViolationCodes.PeriodClosed, JournalEntryValidator.ValidateForPosting(entry, context));
    }

    [Fact]
    public void 締め済みの年度には計上できない()
    {
        var entry = AccountingFixture.CashSale(Ordinary);
        var context = AccountingFixture.Context(fiscalYearStatus: PeriodStatus.Closed);

        AssertViolation(JournalViolationCodes.PeriodClosed, JournalEntryValidator.ValidateForPosting(entry, context));
    }

    [Fact]
    public void 期間が属する会計年度がなければ計上できない()
    {
        // 期間はあるのに年度が無いのはマスタが壊れた状態。
        // FiscalCalendar.IsPostable と判断が食い違わないことを固定する。
        var orphan = new AccountingPeriod(
            new AccountingPeriodId(99),
            new FiscalYearId(99),
            new DateRange(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31)),
            PeriodStatus.Open);
        var context = new PostingContext(
            new AccountCatalog(AccountingFixture.Accounts),
            new SubAccountCatalog(AccountingFixture.SubAccounts),
            new DepartmentCatalog(AccountingFixture.Departments),
            new FiscalCalendar([], [orphan]));

        var violations = JournalEntryValidator.ValidateForPosting(AccountingFixture.CashSale(Ordinary), context);

        // 「期間がない」（I-03）と混ぜない。マスタ破損は運用者への通知が要る。
        Assert.Contains(violations, v => v.Code == JournalViolationCodes.PeriodOrphaned);
        Assert.DoesNotContain(violations, v => v.Code == JournalViolationCodes.PeriodNotFound);
        Assert.False(context.Calendar.IsPostable(Ordinary));
    }

    [Fact]
    public void 損益科目の明細に部門がなければ計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 100_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 100_000));

        Assert.Equal(2, AssertViolation(JournalViolationCodes.DepartmentMissing, Validate(entry)).LineNo);
    }

    [Fact]
    public void 計上済みの仕訳はもう一度計上できない()
    {
        // 「検証が通った＝保存してよい」と解釈されると、二重計上の経路になる（I-05）。
        var entry = AccountingFixture.CashSale(Ordinary) with
        {
            Status = EntryStatus.Posted,
            EntryNo = 1,
            PostedAt = new DateTimeOffset(2026, 5, 20, 10, 0, 0, TimeSpan.FromHours(9)),
        };

        var codes = Validate(entry).Select(v => v.Code).ToList();

        Assert.Contains(JournalViolationCodes.AlreadyPosted, codes);
        Assert.Contains(JournalViolationCodes.EntryNoNotAllowed, codes);
    }

    [Fact]
    public void 計上前の伝票が伝票番号を持ってはいけない()
    {
        // 伝票番号は計上時にしか採らない。先に持たせると欠番の穴が空く（I-17）。
        AssertViolation(JournalViolationCodes.EntryNoNotAllowed,
            Validate(AccountingFixture.CashSale(Ordinary) with { EntryNo = 5 }));
    }

    [Fact]
    public void 計上日が取引日より前なら計上できない()
    {
        var entry = AccountingFixture.CashSale(Ordinary) with { TransactionDate = Ordinary.AddDays(1) };

        AssertViolation(JournalViolationCodes.PostingDateBeforeTransaction, Validate(entry));
    }

    [Fact]
    public void 取引日が過年度でも計上できる()
    {
        // 遅れて起票した過年度の取引を当期に計上するのは正常な運用である。
        var entry = AccountingFixture.CashSale(Ordinary) with { TransactionDate = new DateOnly(2025, 12, 31) };

        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void 伝票の会計年度が計上日の年度と食い違えば計上できない()
    {
        // 食い違ったまま通すと、別の年度の番号列から伝票番号が出る（I-17 の一連番号が壊れる）。
        var entry = AccountingFixture.CashSale(Ordinary) with { FiscalYearId = AccountingFixture.OtherFiscalYear };

        AssertViolation(JournalViolationCodes.FiscalYearMismatch, Validate(entry));
    }

    [Fact]
    public void 行番号は正の整数でなければならない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(0, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(-1, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        AssertViolation(JournalViolationCodes.LineNoInvalid, Validate(entry));
    }

    [Fact]
    public void マスタにない部門は使えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 1_000,
                department: AccountingFixture.UnknownDepartment));

        AssertViolation(JournalViolationCodes.DepartmentUnknown, Validate(entry));
    }

    [Fact]
    public void 無効な部門は新たに使えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 1_000,
                department: AccountingFixture.RetiredDepartment));

        AssertViolation(JournalViolationCodes.DepartmentInactive, Validate(entry));
    }

    [Fact]
    public void 補助科目を使う科目は補助科目なしで計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        AssertViolation(JournalViolationCodes.SubAccountRequired, Validate(entry));
    }

    [Fact]
    public void 親の勘定科目に属さない補助科目は使えない()
    {
        // 「現金の補助科目を普通預金の明細に付ける」を塞ぐ。補助元帳が壊れる。
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.SubAccountOfCash),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        AssertViolation(JournalViolationCodes.SubAccountMismatch, Validate(entry));
    }

    [Fact]
    public void マスタにない補助科目は使えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.UnknownSubAccount),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        AssertViolation(JournalViolationCodes.SubAccountUnknown, Validate(entry));
    }

    [Fact]
    public void 無効な補助科目は新たに使えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        AssertViolation(JournalViolationCodes.SubAccountInactive, Validate(entry));
    }

    [Fact]
    public void 親の勘定科目に属する補助科目は使える()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.MainBank),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        Assert.Empty(Validate(entry));
    }

    /// <summary>
    /// 借方だけ・貸方だけの伝票は、金額が正であることと貸借一致の組み合わせで構造的に塞がっている。
    /// 「金額 0 を許す」変更が入った瞬間に片側だけの伝票が通るので、意図をここで固定する。
    /// </summary>
    [Theory]
    [InlineData(DebitCredit.Debit)]
    [InlineData(DebitCredit.Credit)]
    public void 片側だけの伝票は計上できない(DebitCredit side)
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, side, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, side, AccountingFixture.AccountsPayable, 2_000));

        AssertViolation(JournalViolationCodes.Unbalanced, Validate(entry));
    }

    [Fact]
    public void 貸借科目の明細に部門はなくてよい()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 50_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 50_000));

        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void 法定記載事項を備えた明細は計上できる()
    {
        // 帳簿の法定記載事項（消法 30 ⑧）を明細が満たす形（docs/06 §8）。
        // 取引先は識別子と名前の写しを両方持つ（docs/04 §4-2）。
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 10_000,
                department: AccountingFixture.SalesDepartment) with
            {
                PartnerId = AccountingFixture.Partner,
                PartnerNameSnapshot = "株式会社取引先",
                ItemDescription = "事務用品",
                TaxTreatment = TaxTreatment.ForTaxableSales,
                TaxPoint = Ordinary,
            },
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Cash, 10_000,
                subAccountId: AccountingFixture.SubAccountOfCash));

        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void 金額が零以下の明細は計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 0),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 0));

        AssertViolation(JournalViolationCodes.AmountNotPositive, Validate(entry));
    }

    [Fact]
    public void 税区分のない明細は計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000, taxCategoryId: default(TaxCategoryId)),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        AssertViolation(JournalViolationCodes.TaxCategoryMissing, Validate(entry));
    }

    [Fact]
    public void 無効な勘定科目は新たに使えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Cash, 1_000));

        AssertViolation(JournalViolationCodes.AccountInactive, Validate(entry));
    }

    [Fact]
    public void 取消では無効なマスタでも止めない()
    {
        // **後からマスタを無効にしたせいで、訂正も取消もできない仕訳が帳簿に残ってはいけない**
        // （docs/04 §6・ADR-0004）。新たな計上には使えないが、取消は過去の反転である。
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.RetiredDepartment),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank))
            with { EntryType = EntryType.Reversal, OriginalEntryId = new JournalEntryId(9) };

        var violations = Validate(entry);

        foreach (var code in new[]
                 {
                     JournalViolationCodes.AccountInactive,
                     JournalViolationCodes.DepartmentInactive,
                     JournalViolationCodes.SubAccountInactive,
                 })
        {
            Assert.Equal(ViolationSeverity.Warning, AssertViolation(code, violations).Severity);
        }

        // 警告は返るが、計上はできる。
        Assert.False(violations.HasError());
    }

    [Fact]
    public void マスタにない勘定科目は使えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.UnknownAccount, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Cash, 1_000));

        var violations = Validate(entry);

        AssertViolation(JournalViolationCodes.AccountUnknown, violations);
        // 科目が引けない行に「部門がない」まで重ねて出さない。
        Assert.DoesNotContain(violations, v => v.Code == JournalViolationCodes.DepartmentMissing);
    }

    [Fact]
    public void 行番号は重複できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(1, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        Assert.Equal(1, AssertViolation(JournalViolationCodes.LineNoInvalid, Validate(entry)).LineNo);
    }

    [Theory]
    [InlineData(EntryType.Correction)]
    [InlineData(EntryType.Reversal)]
    public void 訂正と取消は原仕訳の指定を要求する(EntryType entryType)
    {
        var entry = AccountingFixture.CashSale(Ordinary) with { EntryType = entryType };

        AssertViolation(JournalViolationCodes.OriginalEntryMissing, Validate(entry));
    }

    [Fact]
    public void 原仕訳を指定した取消は計上できる()
    {
        var entry = AccountingFixture.CashSale(Ordinary) with
        {
            EntryType = EntryType.Reversal,
            OriginalEntryId = new JournalEntryId(1000),
        };

        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void 消費税行は伝票内の行を指していなければならない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000) with
            {
                IsTaxLine = true,
                ParentLineNo = 99,
            });

        AssertViolation(JournalViolationCodes.TaxLineParentInvalid, Validate(entry));
    }

    [Fact]
    public void 消費税行は親行の指定を省略できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000) with
            {
                IsTaxLine = true,
            });

        AssertViolation(JournalViolationCodes.TaxLineParentInvalid, Validate(entry));
    }

    [Fact]
    public void 消費税行が別の消費税行を親に指すことはできない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 100) with
            {
                IsTaxLine = true,
                ParentLineNo = 1,
            },
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_100) with
            {
                IsTaxLine = true,
                ParentLineNo = 2,
            });

        Assert.Equal(3, AssertViolation(JournalViolationCodes.TaxLineParentInvalid, Validate(entry)).LineNo);
    }

    [Fact]
    public void 本体行から引き継いだ消費税行は計上できる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.SalesDepartment,
                taxCategoryId: AccountingFixture.TaxablePurchase),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 100,
                department: AccountingFixture.SalesDepartment,
                taxCategoryId: AccountingFixture.TaxablePurchase) with
            {
                IsTaxLine = true,
                ParentLineNo = 1,
            },
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_100));

        Assert.Empty(Validate(entry));
    }

    /// <summary>
    /// 消費税行が本体行から引き継がないと、税区分別集計・部門別税集計が本体行と突き合わない（docs/06 §2）。
    /// </summary>
    [Theory]
    [InlineData("debitCredit")]
    [InlineData("department")]
    [InlineData("taxCategory")]
    [InlineData("taxTreatment")]
    public void 消費税行が本体行から引き継いでいなければ計上できない(string diff)
    {
        var taxLine = AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 100,
            department: AccountingFixture.SalesDepartment,
            taxCategoryId: AccountingFixture.TaxablePurchase) with
        {
            IsTaxLine = true,
            ParentLineNo = 1,
        };

        taxLine = diff switch
        {
            "debitCredit" => taxLine with { DebitCredit = DebitCredit.Credit },
            "department" => taxLine with { DepartmentId = null },
            "taxCategory" => taxLine with { TaxCategoryId = AccountingFixture.OutOfScope },
            _ => taxLine with { TaxTreatment = TaxTreatment.Common },
        };

        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.SalesDepartment,
                taxCategoryId: AccountingFixture.TaxablePurchase),
            taxLine,
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_100));

        AssertViolation(JournalViolationCodes.TaxLineNotInherited, Validate(entry));
    }

    [Fact]
    public void 本体行に親行は指定できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000) with { ParentLineNo = 2 },
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        AssertViolation(JournalViolationCodes.TaxLineParentInvalid, Validate(entry));
    }

    [Fact]
    public void 違反は一件で打ち切らずすべて返す()
    {
        var entry = AccountingFixture.Entry(
            new DateOnly(2027, 4, 1),
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 999));

        var codes = Validate(entry).Select(v => v.Code).ToList();

        Assert.Contains(JournalViolationCodes.Unbalanced, codes);
        Assert.Contains(JournalViolationCodes.PeriodNotFound, codes);
        Assert.Contains(JournalViolationCodes.DepartmentMissing, codes);
    }

    [Fact]
    public void nullでは検証できない()
    {
        Assert.Throws<ArgumentNullException>(
            () => JournalEntryValidator.ValidateForPosting(null!, AccountingFixture.Context()));
        Assert.Throws<ArgumentNullException>(
            () => JournalEntryValidator.ValidateForPosting(AccountingFixture.CashSale(Ordinary), null!));
    }

    private static IReadOnlyList<Violation> Validate(JournalEntry entry)
        => JournalEntryValidator.ValidateForPosting(entry, AccountingFixture.Context());

    private static Violation AssertViolation(string code, IReadOnlyList<Violation> violations)
    {
        var violation = violations.FirstOrDefault(v => v.Code == code);
        Assert.True(violation is not null, $"{code} が検出されていない。実際: {string.Join(" / ", violations)}");
        return violation!;
    }
}
