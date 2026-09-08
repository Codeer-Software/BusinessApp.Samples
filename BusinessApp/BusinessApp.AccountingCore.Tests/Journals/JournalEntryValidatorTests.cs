namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>計上の関門（docs/10 §1 の不変条件）。</summary>
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

    // --- 摘要（docs/10 §4-2-1。法税規則 55 ① の記載事項「内容」）---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("	")]
    [InlineData("　")]          // 全角空白だけ
    [InlineData(" 　	 ")]      // 混ぜても同じ
    public void 摘要が空の仕訳は計上できない(string? description)
    {
        var entry = AccountingFixture.CashSale(Ordinary) with { Description = description };

        var violation = AssertViolation(JournalViolationCodes.DescriptionMissing, Validate(entry));

        // **利用者に出る文**そのものを固定する。事実と次の一手を持ち、
        // **「計上できません」を繰り返さない**こと——見出しが既にそう言っている（docs/21 §2-6）。
        // **「内容」の語を使わない**——同じ画面に明細の「内容」欄がある（2026-09-08 の自己レビュー）。
        Assert.Equal("「摘要」が入っていません。何の取引かを書いてください。", violation.Message);
    }

    // 「摘要が入っていれば通る」は `貸借が一致した仕訳は計上できる` が既に表明している
    // （検体は既定で摘要を持つ）ので、ここには置かない（ADR-0012 §2 の「カバレッジのためのテスト」）。

    [Fact]
    public void 摘要の前後に空白があっても中身があれば計上できる()
    {
        var entry = AccountingFixture.CashSale(Ordinary) with { Description = "  8 月分の通信費  " };

        Assert.DoesNotContain(
            Validate(entry), v => v.Code == JournalViolationCodes.DescriptionMissing);
    }

    [Fact]
    public void 明細が_1_行も無くても摘要の違反は出る()
    {
        // **早く返る検査の中に入れると、直すところが 2 つあるのに 1 つしか見えない。**
        // 明細を入れ忘れた伝票で摘要の違反が消えないことを、ここで固定する。
        var violations = Validate(AccountingFixture.Entry(Ordinary) with { Description = null });

        AssertViolation(JournalViolationCodes.NoLines, violations);
        AssertViolation(JournalViolationCodes.DescriptionMissing, violations);
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
            new FiscalCalendar([], [orphan]),
            HasSelectablePartner: true);

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
        // フィクスチャは計上日を取引日の 2 日後にする。取引日をその翌日にすれば逆転する。
        var entry = AccountingFixture.CashSale(Ordinary) with { TransactionDate = Ordinary.AddDays(3) };

        AssertViolation(JournalViolationCodes.PostingDateBeforeTransaction, Validate(entry));
    }

    [Fact]
    public void 取引日と計上日が同じ日でも計上できる()
    {
        // **その日のうちに起票して計上する**のがいちばん普通の運用である。
        // ここを見ていないと `<` を `<=` に取り違えても気づけない（ミューテーションテストで発見）。
        var entry = AccountingFixture.Entry(
            Ordinary,
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

        Assert.False(Validate(entry).HasError());
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

    /// <summary>
    /// <b>0 と -1 を別々の伝票で試す。</b>
    /// </summary>
    /// <remarks>
    /// <para>1 つの伝票に両方を入れて件数を数えると、<b>境界（0）の検査が死んでいても
    /// -1 が 1 件出して緑になる</b>（ミューテーションテストで発覚）。件数ではなく
    /// <b>それぞれの値で鳴ること</b>を見る。</para>
    /// <para><b>違反に行番号を添えない</b>ので、「0 行目」と存在しない行を名指しすることはない
    /// （2026-08-31 の自己レビュー）。行を特定する手段がその行番号そのものである。</para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 行番号は正の整数でなければならない(int lineNo)
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(lineNo, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(9, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

        var violation = Assert.Single(
            Validate(entry).Where(v => v.Code == JournalViolationCodes.LineNoInvalid));

        Assert.Null(violation.LineNo);
        Assert.Equal(JournalLineRules.LineNoNotStorable, violation.Message);
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

        // 通常の仕訳では**計上を止める重さ**であること（取消では警告に落ちる）。
        // 重さを見ないと、全部を警告に変えても緑のまま計上が通ってしまう。
        var violations = Validate(entry);
        Assert.Equal(ViolationSeverity.Error, AssertViolation(JournalViolationCodes.DepartmentInactive, violations).Severity);
        Assert.True(violations.HasError());
    }

    [Fact]
    public void 補助科目を使う科目は補助科目なしで計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

        var violation = AssertViolation(JournalViolationCodes.SubAccountRequired, Validate(entry));

        // **選べる補助科目があるので「選んでください」でよい。**
        Assert.Equal("勘定科目「普通預金」は「補助科目を使う」がオンです。「補助科目」を選んでください。", violation.Message);
    }

    [Fact]
    public void 選べる補助科目が無い科目では登録してからと言う()
    {
        // **踏めない案内をしない**（docs/21 §1・§2-3。qa/02 R45-17 と同じ型）。
        // 当座預金は「補助科目を使う」がオンだが、補助科目は無効なものしか無いので、
        // 候補ダイアログは 0 件で開く——「選んでください」では次の一手にならない。
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.CurrentAccount, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

        var violation = AssertViolation(JournalViolationCodes.SubAccountRequired, Validate(entry));

        Assert.Equal(
            "勘定科目「当座預金」は「補助科目を使う」がオンですが、選べる補助科目がありません。"
            + "補助科目マスタに登録してから選んでください。",
            violation.Message);
    }


    [Fact]
    public void 親の勘定科目に属さない補助科目は使えない()
    {
        // 「現金の補助科目を普通預金の明細に付ける」を塞ぐ。補助元帳が壊れる。
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.SubAccountOfCash),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

        AssertViolation(JournalViolationCodes.SubAccountMismatch, Validate(entry));
    }

    [Fact]
    public void マスタにない補助科目は使えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.UnknownSubAccount),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

        AssertViolation(JournalViolationCodes.SubAccountUnknown, Validate(entry));
    }

    [Fact]
    public void 無効な補助科目は新たに使えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

        AssertViolation(JournalViolationCodes.SubAccountInactive, Validate(entry));
    }

    [Fact]
    public void 補助科目を使わない科目の明細に補助科目は付けられない()
    {
        // **補助科目は 2 値**（ADR-0038 §3。docs/04 §1 の A-3）。
        // 現金は「補助科目を使う」がオフなので、補助科目を付けたまま計上できない。
        // **この形は開発機に実在する**（規則より前に計上された明細 1 行。伝票 36）。
        var entry = SubAccountOnUnusedAccount();

        var violation = AssertViolation(JournalViolationCodes.SubAccountNotAllowed, Validate(entry));

        // **利用者に出る文**を固定する（docs/21 §2-6。事実と次の一手だけを持つ）。
        Assert.Equal("勘定科目「現金」は「補助科目を使う」がオフです。「補助科目」を空にしてください。", violation.Message);
        Assert.Equal(ViolationSeverity.Error, violation.Severity);
        Assert.Equal(1, violation.LineNo);

        // **「その補助科目は親が違う」を重ねて出さない。** レジ（現金の補助科目）は親としては正しく、
        // 直すべきは補助科目の選び方ではなく、補助科目を付けたこと自体である。
        Assert.DoesNotContain(Validate(entry), v => v.Code == JournalViolationCodes.SubAccountMismatch);
    }

    [Fact]
    public void 取消では補助科目の付いた明細でも止めない()
    {
        // **規則より前に計上された伝票を打ち消せなくなってはいけない**（docs/10 §5・ADR-0004）。
        // 取消の明細はサーバが原仕訳から作るので、利用者に直す手立てが無い。
        // **DDL のトリガはここより狭い**（計上済みの原仕訳を写した明細だけを外す）。
        // 関門を通らない経路のために狭くしてあり、アプリからは差が出ない。
        var entry = SubAccountOnUnusedAccount() with
        {
            EntryType = EntryType.Reversal,
            OriginalEntryId = new JournalEntryId(9),
        };

        var violations = Validate(entry);

        Assert.Equal(
            ViolationSeverity.Warning,
            AssertViolation(JournalViolationCodes.SubAccountNotAllowed, violations).Severity);
        Assert.False(violations.HasError());
    }

    [Fact]
    public void 取消では補助科目が無くても止めない()
    {
        // 「要る」側も同じ線である（使う科目に変えられた後の過去の明細は補助科目を持たない）。
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000)) with
        {
            EntryType = EntryType.Reversal,
            OriginalEntryId = new JournalEntryId(9),
        };

        var violations = Validate(entry);

        Assert.Equal(
            ViolationSeverity.Warning,
            AssertViolation(JournalViolationCodes.SubAccountRequired, violations).Severity);
        Assert.False(violations.HasError());
    }

    [Fact]
    public void 訂正では補助科目の付いた明細を止める()
    {
        // **再計上の中身は利用者が決める**（JournalCorrectionPosting は明細を書き換えない）ので、
        // 補助科目を空にすれば通る——止めても行き止まりにならない。
        // **外すと、訂正を経由して規則より後の違反を新しく帳簿へ入れられる**（自己レビューで見つけた）。
        var entry = SubAccountOnUnusedAccount() with
        {
            EntryType = EntryType.Correction,
            OriginalEntryId = new JournalEntryId(9),
        };

        var violations = Validate(entry);

        Assert.Equal(
            ViolationSeverity.Error,
            AssertViolation(JournalViolationCodes.SubAccountNotAllowed, violations).Severity);
        Assert.True(violations.HasError());
    }

    /// <summary>補助科目を使わない科目（現金）の明細に補助科目を付けた伝票。</summary>
    private static JournalEntry SubAccountOnUnusedAccount()
        => AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                subAccountId: AccountingFixture.SubAccountOfCash),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

    [Fact]
    public void 取引先を要する科目に取引先がなければ計上できない()
    {
        // **相手方を欠いた行は「相手方別」のどの帳簿にも載らない**（docs/40 §4-1。docs/04 §1 の A-4）。
        var entry = ReceivableWithoutPartner();

        var violation = AssertViolation(JournalViolationCodes.PartnerRequired, Validate(entry));

        Assert.Equal(1, violation.LineNo);
        Assert.Equal(
            "勘定科目「売掛金」は「取引先を要する」がオンです。伝票の「取引先」を選んでください。",
            violation.Message);
    }

    [Fact]
    public void 選べる取引先が無ければ登録してからと言う()
    {
        // **踏めない案内をしない**（docs/21 §2-3。qa/02 R53-06 と同じ型）。
        // 取引先マスタが空の DB では、「選んでください」は 0 件のダイアログにしかならない。
        var violation = AssertViolation(
            JournalViolationCodes.PartnerRequired,
            JournalEntryValidator.ValidateForPosting(
                ReceivableWithoutPartner(), AccountingFixture.Context(hasSelectablePartner: false)));

        Assert.Equal(
            "勘定科目「売掛金」は「取引先を要する」がオンですが、選べる取引先がありません。"
            + "取引先マスタに登録するか、無効にした取引先を有効に戻してから選んでください。",
            violation.Message);
    }

    [Fact]
    public void 明細の取引先で足りる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.AccountsReceivable, 1_000,
                partner: AccountingFixture.Partner),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void 伝票の取引先で足りる()
    {
        // **見るのは実効値である**（JournalEntry.PartnerOf）——明細が空なら伝票のものが帳簿に載るので、
        // ここで止めると**帳簿には取引先が載る行を関門が拒む**ことになる。
        var entry = ReceivableWithoutPartner() with { PartnerId = AccountingFixture.Partner };

        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void 取引先を要しない科目に取引先が付いていても止めない()
    {
        // **片側だけの規則である**（補助科目の 2 値と違う）。取引先は科目に属さないので、
        // どの科目の行にも意味のある相手方がありうる（現金の行の支払先など）。
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.Partner),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void 取消では取引先が無くても止めない()
    {
        // **規則より前に計上された伝票を打ち消せなくなってはいけない**（docs/10 §5・ADR-0004）。
        // 開発機に取引先の無い計上済み明細が 10 行ある（2026-09-08 実測）。
        var entry = ReceivableWithoutPartner() with
        {
            EntryType = EntryType.Reversal,
            OriginalEntryId = new JournalEntryId(9),
        };

        var violations = Validate(entry);

        Assert.Equal(
            ViolationSeverity.Warning,
            AssertViolation(JournalViolationCodes.PartnerRequired, violations).Severity);
        Assert.False(violations.HasError());
    }

    [Fact]
    public void 訂正では取引先が無いと止める()
    {
        // **再計上の中身は利用者が決める**ので、取引先を選べば通る——行き止まりにならない。
        // 外すと、訂正を経由して規則より後の違反を新しく帳簿へ入れられる（qa/02 R53-01）。
        var entry = ReceivableWithoutPartner() with
        {
            EntryType = EntryType.Correction,
            OriginalEntryId = new JournalEntryId(9),
        };

        var violations = Validate(entry);

        Assert.Equal(
            ViolationSeverity.Error,
            AssertViolation(JournalViolationCodes.PartnerRequired, violations).Severity);
        Assert.True(violations.HasError());
    }

    /// <summary>取引先を要する科目（売掛金）の明細に、取引先を付けていない伝票。</summary>
    private static JournalEntry ReceivableWithoutPartner()
        => AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.AccountsReceivable, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

    [Fact]
    public void 親の勘定科目に属する補助科目は使える()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.MainBank),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

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
            AccountingFixture.Line(2, side, AccountingFixture.OtherPayable, 2_000));

        AssertViolation(JournalViolationCodes.Unbalanced, Validate(entry));
    }

    [Fact]
    public void 貸借科目の明細に部門はなくてよい()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 50_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 50_000));

        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void 法定記載事項を備えた明細は計上できる()
    {
        // 帳簿の法定記載事項（消税法 30 ⑧）を明細が満たす形（docs/11 §8）。
        // 取引先は識別子と名前の写しを両方持つ（docs/10 §4-2）。
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
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.BankAccount, 10_000,
                subAccountId: AccountingFixture.MainBank));

        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void 金額が零以下の明細は計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 0),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 0));

        AssertViolation(JournalViolationCodes.AmountNotPositive, Validate(entry));
    }

    [Fact]
    public void 税区分のない明細は計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000, taxCategoryId: default(TaxCategoryId)),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

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

        var violations = Validate(entry);
        Assert.Equal(ViolationSeverity.Error, AssertViolation(JournalViolationCodes.AccountInactive, violations).Severity);
        Assert.True(violations.HasError());
    }

    [Theory]
    [InlineData(EntryType.Reversal)]
    [InlineData(EntryType.Correction)]
    public void 取消と訂正では無効なマスタでも止めない(EntryType entryType)
    {
        // **後からマスタを無効にしたせいで、訂正も取消もできない仕訳が帳簿に残ってはいけない**
        // （docs/10 §6・ADR-0004）。新たな計上には使えないが、どちらも過去を打ち消す・直す操作である。
        //
        // **訂正を含めるのは 2026-08-25 の自己レビューで直した。** 訂正は取消を先に計上してから
        // 再計上の下書きを開くので（ADR-0015）、ここが Error だと
        // **取消だけが確定して再計上は永久にできない**——利用者から見れば詰む。
        var ordinary = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.RetiredDepartment),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank));
        var entry = ordinary with { EntryType = entryType, OriginalEntryId = new JournalEntryId(9) };

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
            AccountingFixture.Line(1, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

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
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000) with
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
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000) with
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
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_100) with
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
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_100));

        Assert.Empty(Validate(entry));
    }

    /// <summary>
    /// 消費税行が本体行から引き継がないと、税区分別集計・部門別税集計が本体行と突き合わない（docs/11 §2）。
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
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_100));

        AssertViolation(JournalViolationCodes.TaxLineNotInherited, Validate(entry));
    }

    [Fact]
    public void 本体行に親行は指定できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000) with { ParentLineNo = 2 },
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

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
