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

    /// <summary>
    /// <b>長すぎる摘要は計上できない。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>保存の関門だけでは足りない。</b> あちらは<b>差分に載った欄しか見ない</b>（qa/01 の F-12）ので、
    /// <b>上限を置く前に書かれた長い下書きは、保存せずに「計上する」を押すだけで計上でき、以後不変になる</b>
    /// （I-05）。<b>投入 API も同じ検証を通る</b>（docs/10 §10）。</para>
    /// <para><b>符号点で数える</b>——<c>string.Length</c> だと 🙂 が 2 になり、
    /// SQLite の <c>LENGTH()</c> と食い違う。</para>
    /// </remarks>
    [Fact]
    public void 長すぎる摘要は計上できない()
    {
        var entry = AccountingFixture.CashSale(Ordinary) with
        {
            Description = new string('あ', JournalLineRules.TextMaxLength + 31),
        };

        var violation = AssertViolation(JournalViolationCodes.DescriptionTooLong, Validate(entry));

        Assert.Equal(JournalLineRules.DescriptionTooLong(231), violation.Message);
    }

    /// <summary>上限ちょうどの摘要は計上できる。</summary>
    [Fact]
    public void 上限ちょうどの摘要は計上できる()
    {
        var entry = AccountingFixture.CashSale(Ordinary) with
        {
            Description = new string('あ', JournalLineRules.TextMaxLength),
        };

        Assert.DoesNotContain(
            Validate(entry), v => v.Code == JournalViolationCodes.DescriptionTooLong);
    }

    /// <summary>
    /// <b>長すぎる明細の「内容」も計上できない</b>（何行目かを添える）。
    /// </summary>
    /// <remarks>
    /// <b>「内容」は必須ではない</b>ので、見るのは長さだけである。
    /// </remarks>
    [Fact]
    public void 長すぎる内容は行を添えて計上できない()
    {
        var entry = AccountingFixture.CashSale(Ordinary);
        var broken = entry with
        {
            Lines =
            [
                entry.Lines[0] with
                {
                    ItemDescription = new string('い', JournalLineRules.TextMaxLength + 1),
                },
                entry.Lines[1],
            ],
        };

        var violation = AssertViolation(JournalViolationCodes.ItemDescriptionTooLong, Validate(broken));

        Assert.Equal(JournalLineRules.ItemDescriptionTooLong(201), violation.Message);
        Assert.Equal(broken.Lines[0].LineNo, violation.LineNo);
    }

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
            AccountingFixture.Partners());

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
        // **補助科目は 2 値**（ADR-0038 §3・docs/15 §1）。
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
        // **相手方を欠いた行は「相手方別」のどの帳簿にも載らない**（docs/40 §4-1・docs/15 §1-2）。
        var entry = ReceivableWithoutPartner();

        var violation = AssertViolation(JournalViolationCodes.PartnerRequired, Validate(entry));

        Assert.Equal(1, violation.LineNo);
        Assert.Equal(
            "勘定科目「売掛金」は「取引先を要する」がオンです。伝票の「取引先」か、この行の「取引先」を選んでください。",
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
        // 開発機に取引先の無い計上済み明細が実在する（件数と数え方は qa/04）。
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
        // （docs/15 §1・ADR-0004）。新たな計上には使えないが、どちらも過去を打ち消す・直す操作である。
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

        var violation = AssertViolation(JournalViolationCodes.LineNoInvalid, Validate(entry));

        // 番号は文に埋める。「1 行目: 重なっています」では、1 行目が 2 つあることが読めない。
        Assert.Null(violation.LineNo);
        Assert.Equal("行番号 1 が 2 つの明細に付いています。行番号は画面が自動で振るので、明細を入力し直してください。", violation.Message);
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

    // --- 取引先の実在と有効（docs/15 §1-2。科目・補助科目・部門と同じ形） --------------------------

    /// <summary>マスタに無い取引先は、明細の行番号つきで断る。</summary>
    [Fact]
    public void 明細の取引先がマスタに無ければ計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.UnknownPartner),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 1_000,
                department: AccountingFixture.SalesDepartment));

        var violations = Validate(entry);

        var violation = AssertViolation(JournalViolationCodes.PartnerUnknown, violations);
        Assert.Equal(1, violation.LineNo);
        Assert.Equal("取引先が取引先マスタにありません。別の取引先を選ぶか、取引先マスタに登録してください。", violation.Message);
        Assert.True(violations.HasError());
    }

    /// <summary>伝票の取引先は伝票として 1 回だけ見る（行番号なし）。明細が空でも行ごとには言わない。</summary>
    [Fact]
    public void 伝票の取引先が無効なら計上できない()
    {
        var entry = AccountingFixture.CashSale(Ordinary) with { PartnerId = AccountingFixture.RetiredPartner };

        var violations = Validate(entry);

        var violation = AssertViolation(JournalViolationCodes.PartnerInactive, violations);
        Assert.Null(violation.LineNo);
        Assert.Equal(
            "取引先「取引をやめた先」は無効なので、新しい計上には使えません。別の取引先を選ぶか、取引先マスタで有効に戻してください。",
            violation.Message);
        Assert.Equal(ViolationSeverity.Error, violation.Severity);
        Assert.Single(violations, v => v.Code == JournalViolationCodes.PartnerInactive);
    }

    /// <summary>明細が自分の取引先を持つときは行ごとに見る。</summary>
    [Fact]
    public void 明細の取引先が無効なら行番号つきで断る()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.RetiredPartner),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 1_000,
                department: AccountingFixture.SalesDepartment));

        var violation = AssertViolation(JournalViolationCodes.PartnerInactive, Validate(entry));

        Assert.Equal(1, violation.LineNo);
    }

    /// <summary>
    /// <b>重さは、利用者が直せるかで決まる。</b> 取消は明細も伝票も写しなので止めない（警告）。
    /// <b>訂正は、伝票も明細も止める（Error）</b>——どちらも再計上の下書きで選び直せる。
    /// <b>明細は 2026-09-16 まで警告だった</b>（画面に列が無かったため）。列を足したので理由が消えた。
    /// 「マスタに無い」も同じ重さ（DDL の取引先のトリガが、取消の写しではマスタに無い取引先を通すのと同じ広さ）。
    /// </summary>
    [Theory]
    [InlineData(EntryType.Reversal, "entry", "inactive", ViolationSeverity.Warning)]
    [InlineData(EntryType.Reversal, "line", "inactive", ViolationSeverity.Warning)]
    [InlineData(EntryType.Reversal, "entry", "unknown", ViolationSeverity.Warning)]
    [InlineData(EntryType.Reversal, "line", "unknown", ViolationSeverity.Warning)]
    [InlineData(EntryType.Correction, "entry", "inactive", ViolationSeverity.Error)]
    [InlineData(EntryType.Correction, "line", "inactive", ViolationSeverity.Error)]
    [InlineData(EntryType.Correction, "entry", "unknown", ViolationSeverity.Error)]
    [InlineData(EntryType.Correction, "line", "unknown", ViolationSeverity.Error)]
    public void 取消と訂正で取引先の断りの重さは直せるかで決まる(
        EntryType entryType, string where, string problem, ViolationSeverity expected)
    {
        var partner = problem == "inactive" ? AccountingFixture.RetiredPartner : AccountingFixture.UnknownPartner;
        var code = problem == "inactive" ? JournalViolationCodes.PartnerInactive : JournalViolationCodes.PartnerUnknown;
        var ordinary = where == "entry"
            ? AccountingFixture.CashSale(Ordinary) with { PartnerId = partner }
            : AccountingFixture.Entry(
                Ordinary,
                AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000, partner: partner),
                AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 1_000,
                    department: AccountingFixture.SalesDepartment));
        var entry = ordinary with { EntryType = entryType, OriginalEntryId = new JournalEntryId(41) };

        var violation = AssertViolation(code, Validate(entry));

        Assert.Equal(expected, violation.Severity);
        Assert.Equal(where == "line" ? 1 : null, violation.LineNo);
    }

    /// <summary>
    /// <b>無効な勘定科目の断りも、直し先ごとに 1 件。</b>
    /// 取引先と同じ形である（docs/21 §2-6）——<b>こちらのほうが起きやすい</b>（1 科目を何行でも使う）。
    /// </summary>
    [Fact]
    public void 同じ無効な勘定科目を何行で使っても断りは_1_件で場所を並べる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var violations = Validate(entry);

        var violation = AssertViolation(JournalViolationCodes.AccountInactive, violations);
        Assert.Null(violation.LineNo);
        Assert.Equal(
            "勘定科目「廃止した費用科目」は無効なので、新しい計上には使えません。行 1・行 2 で使っています。"
            + "別の勘定科目を選ぶか、勘定科目マスタで有効に戻してください。",
            violation.Message);
        Assert.Equal(ViolationSeverity.Error, violation.Severity);
        Assert.True(violations.HasError());
        Assert.Single(violations, v => v.Code == JournalViolationCodes.AccountInactive);
    }

    /// <summary>
    /// <b>1 行だけで使ったら、並べ書きは出さない。</b>「行 N:」で足りるところに同じことを 2 回書かない
    /// （docs/21 §2-6・§3）。<b>減らなすぎの側</b>を撃つ検体である。
    /// </summary>
    [Fact]
    public void 無効な勘定科目が_1_行だけなら行番号で指して並べ書きは出さない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Cash, 1_000));

        var violation = AssertViolation(JournalViolationCodes.AccountInactive, Validate(entry));

        Assert.Equal(1, violation.LineNo);
        Assert.Equal(
            "勘定科目「廃止した費用科目」は無効なので、新しい計上には使えません。"
            + "別の勘定科目を選ぶか、勘定科目マスタで有効に戻してください。",
            violation.Message);
    }

    /// <summary>マスタに無い勘定科目も同じ。<b>その行は以降の検査を飛ばす</b>ので、2 行目も静かである。</summary>
    [Fact]
    public void マスタに無い同じ勘定科目を何行で使っても断りは_1_件で場所を並べる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.UnknownAccount, 1_000),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.UnknownAccount, 1_000),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var violations = Validate(entry);

        var violation = AssertViolation(JournalViolationCodes.AccountUnknown, violations);
        Assert.Null(violation.LineNo);
        Assert.Equal(
            "勘定科目が勘定科目マスタにありません。行 1・行 2 で使っています。"
            + "別の勘定科目を選ぶか、勘定科目マスタに登録してください。",
            violation.Message);
        Assert.Equal(ViolationSeverity.Error, violation.Severity);
        Assert.True(violations.HasError());
        Assert.Single(violations, v => v.Code == JournalViolationCodes.AccountUnknown);
    }

    /// <summary>
    /// <b>まとめる単位は勘定科目であって、違反の種類ではない。</b>
    /// <b>無効な 2 科目</b>——同じコード・同じ重さ——なら <b>2 件出る</b>。
    /// 1 件にまとめると、片方の直し忘れに気づけない。
    /// </summary>
    [Fact]
    public void 無効な勘定科目が_2_つなら_2_件出る()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.OtherRetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var inactive = Validate(entry)
            .Where(v => v.Code == JournalViolationCodes.AccountInactive)
            .ToArray();

        Assert.Equal(2, inactive.Length);
        Assert.Contains(inactive, v => v.LineNo == 1 && v.Message.Contains("廃止した費用科目」", StringComparison.Ordinal));
        Assert.Contains(inactive, v => v.LineNo == 2 && v.Message.Contains("もう 1 つ廃止した費用科目", StringComparison.Ordinal));
        // **1 か所ずつなので、場所の並べ書きは付かない。**
        Assert.DoesNotContain(inactive, v => v.Message.Contains("で使っています", StringComparison.Ordinal));
    }

    /// <summary><b>マスタに無い側も勘定科目ごとに数える。</b> 文が同じでも、行番号が別の直し先を指している。</summary>
    [Fact]
    public void マスタに無い勘定科目も勘定科目ごとに数える()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.UnknownAccount, 1_000),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.OtherUnknownAccount, 1_000),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var unknown = Validate(entry)
            .Where(v => v.Code == JournalViolationCodes.AccountUnknown)
            .ToArray();

        Assert.Equal(2, unknown.Length);
        Assert.Equal([1, 2], unknown.Select(v => v.LineNo).OrderBy(no => no).ToArray());
        Assert.DoesNotContain(unknown, v => v.Message.Contains("で使っています", StringComparison.Ordinal));
    }

    /// <summary><b>場所は 3 つでも全部並べる。</b> 先頭 2 つで打ち切らない。</summary>
    [Fact]
    public void 同じ無効な勘定科目が_3_行なら場所を_3_つ並べる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(3, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(4, DebitCredit.Credit, AccountingFixture.Cash, 3_000));

        var violation = AssertViolation(JournalViolationCodes.AccountInactive, Validate(entry));

        Assert.Contains("行 1・行 2・行 3 で使っています。", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>同じ行番号を 2 度並べない。</b> 行番号が重なった伝票（<c>E-LINE-NO</c>）でも
    /// 「行 1・行 1 で使っています。」とは言わない——場所が 1 つに畳まれるので<b>行番号で指す</b>。
    /// </summary>
    [Fact]
    public void 行番号が重なっても同じ場所を_2_度並べない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var violation = AssertViolation(JournalViolationCodes.AccountInactive, Validate(entry));

        Assert.Equal(1, violation.LineNo);
        Assert.DoesNotContain("で使っています", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>指せない行番号は場所に出さない。</b> 0 や負の行番号を並べると、存在しない行を名指しする
    /// （<c>ValidateStructure</c> が同じ線を引いている）。<b>全部落ちたら場所は言わない。</b>
    /// </summary>
    [Fact]
    public void 行番号が_0_以下の行は場所に出さない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(0, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(1, DebitCredit.Credit, AccountingFixture.Cash, 1_000));

        var violation = AssertViolation(JournalViolationCodes.AccountInactive, Validate(entry));

        Assert.Null(violation.LineNo);
        Assert.DoesNotContain("で使っています", violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("行 0", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>訂正では重さが警告に落ちるが、まとめ方は変わらない</b>（<c>InactiveSeverity</c>）。
    /// 断りは 1 件で、場所も並ぶ。<b>計上は止まらない</b>（ADR-0015）。
    /// </summary>
    [Fact]
    public void 訂正でも同じ無効な勘定科目の断りは_1_件で場所を並べる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000)) with
        {
            EntryType = EntryType.Correction,
            OriginalEntryId = new JournalEntryId(2),
        };

        var violations = Validate(entry);

        var violation = AssertViolation(JournalViolationCodes.AccountInactive, violations);
        Assert.Equal(ViolationSeverity.Warning, violation.Severity);
        Assert.Contains("行 1・行 2 で使っています。", violation.Message, StringComparison.Ordinal);
        Assert.Single(violations, v => v.Code == JournalViolationCodes.AccountInactive);
        Assert.False(violations.HasError());
    }

    /// <summary>
    /// <b>場所の並びも行番号の順である。</b> ドメインの型は並びを持たないので、
    /// <b>渡す順を逆にしても「行 1・行 2」になる</b>（控えを集める側の <c>OrderBy</c> が消えたら赤くなる）。
    /// </summary>
    [Fact]
    public void 場所の並びは渡した順ではなく行番号の順で集める()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment));

        var violation = AssertViolation(JournalViolationCodes.AccountInactive, Validate(entry));

        Assert.Contains("行 1・行 2 で使っています。", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>断りの並びも行番号の順である。</b> ドメインの型は並びを持たないので、
    /// <b>渡す順を逆にしても「①行 1・②行 2」になる</b>（束ねた差し戻しの番号は並び順で振る）。
    /// </summary>
    [Fact]
    public void 断りの並びは渡した順ではなく行番号の順()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.OtherRetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment));

        var inactive = Validate(entry)
            .Where(v => v.Code == JournalViolationCodes.AccountInactive)
            .ToArray();

        Assert.Equal([1, 2], inactive.Select(v => v.LineNo).ToArray());
    }

    /// <summary>無効な部門の断りも、直し先ごとに 1 件。</summary>
    [Fact]
    public void 同じ無効な部門を何行で使っても断りは_1_件で場所を並べる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.RetiredDepartment),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.RetiredDepartment),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var violations = Validate(entry);

        var violation = AssertViolation(JournalViolationCodes.DepartmentInactive, violations);
        Assert.Null(violation.LineNo);
        Assert.Equal(
            "部門「廃止した部門」は無効なので、新しい計上には使えません。行 1・行 2 で使っています。"
            + "別の部門を選ぶか、部門マスタで有効に戻してください。",
            violation.Message);
        Assert.Equal(ViolationSeverity.Error, violation.Severity);
        Assert.True(violations.HasError());
        Assert.Single(violations, v => v.Code == JournalViolationCodes.DepartmentInactive);
    }

    /// <summary>1 行だけなら行番号で指す（部門の側）。</summary>
    [Fact]
    public void 無効な部門が_1_行だけなら行番号で指して並べ書きは出さない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.RetiredDepartment),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Cash, 1_000));

        var violation = AssertViolation(JournalViolationCodes.DepartmentInactive, Validate(entry));

        Assert.Equal(1, violation.LineNo);
        Assert.Equal(
            "部門「廃止した部門」は無効なので、新しい計上には使えません。"
            + "別の部門を選ぶか、部門マスタで有効に戻してください。",
            violation.Message);
    }

    /// <summary>マスタに無い部門も同じ。</summary>
    [Fact]
    public void マスタに無い同じ部門を何行で使っても断りは_1_件で場所を並べる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.UnknownDepartment),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.UnknownDepartment),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var violations = Validate(entry);

        var violation = AssertViolation(JournalViolationCodes.DepartmentUnknown, violations);
        Assert.Null(violation.LineNo);
        Assert.Equal(
            "部門が部門マスタにありません。行 1・行 2 で使っています。"
            + "別の部門を選ぶか、部門マスタに登録してください。",
            violation.Message);
        Assert.True(violations.HasError());
        Assert.Single(violations, v => v.Code == JournalViolationCodes.DepartmentUnknown);
    }

    /// <summary><b>無効な部門が 2 つなら 2 件</b>——同じコード・同じ重さで分かれることを撃つ。</summary>
    [Fact]
    public void 無効な部門が_2_つなら_2_件出る()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.RetiredDepartment),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.OtherRetiredDepartment),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var inactive = Validate(entry)
            .Where(v => v.Code == JournalViolationCodes.DepartmentInactive)
            .ToArray();

        Assert.Equal(2, inactive.Length);
        Assert.Contains(inactive, v => v.LineNo == 1 && v.Message.Contains("廃止した部門」", StringComparison.Ordinal));
        Assert.Contains(inactive, v => v.LineNo == 2 && v.Message.Contains("もう 1 つ廃止した部門", StringComparison.Ordinal));
        Assert.DoesNotContain(inactive, v => v.Message.Contains("で使っています", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>消費税行は本体行で代表させる。</b> 税行はシステムが作り、利用者は直接編集できない
    /// （docs/11 §2）ので、<b>税行の行番号を並べても踏めない</b>——しかも税行の部門は
    /// 本体行と同じであることを強制しているので、そのままだと<b>必ず 2 行に見える</b>。
    /// </summary>
    [Fact]
    public void 消費税行は場所を本体行で代表させる()
    {
        var taxLine = AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 100,
            department: AccountingFixture.RetiredDepartment) with
        {
            IsTaxLine = true,
            ParentLineNo = 1,
        };
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.RetiredDepartment),
            taxLine,
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 1_100));

        var violation = AssertViolation(JournalViolationCodes.DepartmentInactive, Validate(entry));

        Assert.Equal(1, violation.LineNo);
        Assert.DoesNotContain("で使っています", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>本体行を指していない消費税行は、自分の行番号で数える。</b>
    /// 親が無いのに親で代表させると、場所が「伝票」になって<b>行の断りが伝票の断りに見える</b>。
    /// その税行そのものは <c>ValidateTaxLine</c> が別に断るので、ここで隠さない。
    /// </summary>
    [Fact]
    public void 本体行を指していない消費税行は自分の行番号で数える()
    {
        var orphan = AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 100,
            department: AccountingFixture.RetiredDepartment) with
        {
            IsTaxLine = true,
        };
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.RetiredDepartment),
            orphan,
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 1_100));

        var violation = AssertViolation(JournalViolationCodes.DepartmentInactive, Validate(entry));

        Assert.Contains("行 1・行 2 で使っています。", violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("伝票", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>無効な補助科目の断りも、直し先ごとに 1 件。</summary>
    [Fact]
    public void 同じ無効な補助科目を何行で使っても断りは_1_件で場所を並べる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var violations = Validate(entry);

        var violation = AssertViolation(JournalViolationCodes.SubAccountInactive, violations);
        Assert.Null(violation.LineNo);
        Assert.Equal(
            "補助科目「解約した口座」は無効なので、新しい計上には使えません。行 1・行 2 で使っています。"
            + "別の補助科目を選ぶか、補助科目マスタで有効に戻してください。",
            violation.Message);
        Assert.Equal(ViolationSeverity.Error, violation.Severity);
        Assert.True(violations.HasError());
        Assert.Single(violations, v => v.Code == JournalViolationCodes.SubAccountInactive);
    }

    /// <summary>1 行だけなら行番号で指す（補助科目の側）。</summary>
    [Fact]
    public void 無効な補助科目が_1_行だけなら行番号で指して並べ書きは出さない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Cash, 1_000));

        var violation = AssertViolation(JournalViolationCodes.SubAccountInactive, Validate(entry));

        Assert.Equal(1, violation.LineNo);
        Assert.Equal(
            "補助科目「解約した口座」は無効なので、新しい計上には使えません。"
            + "別の補助科目を選ぶか、補助科目マスタで有効に戻してください。",
            violation.Message);
    }

    /// <summary>マスタに無い補助科目も同じ。</summary>
    [Fact]
    public void マスタに無い同じ補助科目を何行で使っても断りは_1_件で場所を並べる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.UnknownSubAccount),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.UnknownSubAccount),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var violations = Validate(entry);

        var violation = AssertViolation(JournalViolationCodes.SubAccountUnknown, violations);
        Assert.Null(violation.LineNo);
        Assert.Equal(
            "補助科目が補助科目マスタにありません。行 1・行 2 で使っています。"
            + "別の補助科目を選ぶか、補助科目マスタに登録してください。",
            violation.Message);
        Assert.True(violations.HasError());
        Assert.Single(violations, v => v.Code == JournalViolationCodes.SubAccountUnknown);
    }

    /// <summary>
    /// <b>無効な補助科目が 2 つなら 2 件</b>——親の科目が違っても、まとめる単位は補助科目である。
    /// </summary>
    [Fact]
    public void 無効な補助科目が_2_つなら_2_件出る()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.CurrentAccount, 1_000,
                subAccountId: AccountingFixture.RetiredCurrent),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var inactive = Validate(entry)
            .Where(v => v.Code == JournalViolationCodes.SubAccountInactive)
            .ToArray();

        Assert.Equal(2, inactive.Length);
        Assert.Contains(inactive, v => v.LineNo == 1 && v.Message.Contains("解約した口座", StringComparison.Ordinal));
        Assert.Contains(inactive, v => v.LineNo == 2 && v.Message.Contains("解約した当座", StringComparison.Ordinal));
        Assert.DoesNotContain(inactive, v => v.Message.Contains("で使っています", StringComparison.Ordinal));
    }

    /// <summary><b>マスタに無い補助科目も補助科目ごとに数える。</b></summary>
    [Fact]
    public void マスタに無い補助科目も補助科目ごとに数える()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.UnknownSubAccount),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.OtherUnknownSubAccount),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var unknown = Validate(entry)
            .Where(v => v.Code == JournalViolationCodes.SubAccountUnknown)
            .ToArray();

        Assert.Equal(2, unknown.Length);
        Assert.Equal([1, 2], unknown.Select(v => v.LineNo).OrderBy(no => no).ToArray());
    }

    /// <summary>
    /// <b>「補助科目を使う」がオフの行は、場所に数えない。</b>
    /// その行の直し方は「有効に戻す」ではなく「補助科目を空にする」なので、
    /// <b>有効に戻せば直る場所として並べると嘘になる</b>（2026-09-16 の自己レビュー）。
    /// </summary>
    [Fact]
    public void 補助科目を使わない科目の行は無効の場所に数えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.SalesDepartment, subAccountId: AccountingFixture.RetiredBank),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var violations = Validate(entry);

        Assert.Equal(1, AssertViolation(JournalViolationCodes.SubAccountNotAllowed, violations).LineNo);
        var inactive = AssertViolation(JournalViolationCodes.SubAccountInactive, violations);
        Assert.Equal(2, inactive.LineNo);
        Assert.DoesNotContain("で使っています", inactive.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>親の科目が違う行も、場所に数えない。</b> その行は「別の補助科目にする」が直し方で、
    /// <b>有効に戻しても直らない</b>。
    /// </summary>
    [Fact]
    public void 親の科目が違う行は無効の場所に数えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.CurrentAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var violations = Validate(entry);

        Assert.Equal(1, AssertViolation(JournalViolationCodes.SubAccountMismatch, violations).LineNo);
        var inactive = AssertViolation(JournalViolationCodes.SubAccountInactive, violations);
        Assert.Equal(2, inactive.LineNo);
        Assert.DoesNotContain("で使っています", inactive.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>勘定科目がマスタに無い行も、補助科目の場所に数えない。</b>
    /// その行は勘定科目の断りで打ち切られ、補助科目の検査に届かない。
    /// </summary>
    [Fact]
    public void 勘定科目がマスタに無い行は補助科目の場所に数えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.UnknownAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.BankAccount, 1_000,
                subAccountId: AccountingFixture.RetiredBank),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Cash, 2_000));

        var inactive = AssertViolation(JournalViolationCodes.SubAccountInactive, Validate(entry));

        Assert.Equal(2, inactive.LineNo);
        Assert.DoesNotContain("で使っています", inactive.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>断りは取引先ごとに 1 件。ただし場所は捨てない。</b>
    /// 同じ取引先を伝票と明細で使っても、同じ文を並べない（2026-09-16 の自己レビュー）。
    /// <b>行番号は付けず、使っている場所を文に並べる</b>——
    /// 直し方の 1 つ（別の取引先を選ぶ）は<b>場所ごと</b>なので、1 か所だけ示すと残りを直さなくてよく読める。
    /// </summary>
    [Fact]
    public void 同じ取引先を伝票と明細で使っても断りは_1_件で場所を並べる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.RetiredPartner),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.RetiredPartner),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Sales, 2_000,
                department: AccountingFixture.SalesDepartment)) with
        {
            PartnerId = AccountingFixture.RetiredPartner,
        };

        var violations = Validate(entry);

        var violation = AssertViolation(JournalViolationCodes.PartnerInactive, violations);
        Assert.Null(violation.LineNo);
        Assert.Equal(
            "取引先「取引をやめた先」は無効なので、新しい計上には使えません。伝票・行 1・行 2 で使っています。"
            + "別の取引先を選ぶか、取引先マスタで有効に戻してください。",
            violation.Message);
        Assert.Equal(ViolationSeverity.Error, violation.Severity);
        Assert.True(violations.HasError());
        Assert.Single(violations, v => v.Code == JournalViolationCodes.PartnerInactive);
    }

    /// <summary>伝票が取引先を持たなくても、明細の繰り返しは 1 件にまとめ、行番号を並べる。</summary>
    [Fact]
    public void 明細だけで同じ取引先を繰り返しても断りは_1_件で場所を並べる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.RetiredPartner),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.RetiredPartner),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Sales, 2_000,
                department: AccountingFixture.SalesDepartment));

        var violation = AssertViolation(JournalViolationCodes.PartnerInactive, Validate(entry));

        Assert.Null(violation.LineNo);
        Assert.Contains("行 1・行 2 で使っています。", violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("伝票", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>場所の並びは行番号の順である。</b> ドメインの型は並びを持たないので、
    /// <b>渡す順を逆にしても同じ文になる</b>ことを固定する（実装の <c>OrderBy</c> が消えたら赤くなる）。
    /// </summary>
    [Fact]
    public void 場所の並びは渡した順ではなく行番号の順()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Sales, 2_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.RetiredPartner),
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.RetiredPartner));

        var violation = AssertViolation(JournalViolationCodes.PartnerInactive, Validate(entry));

        Assert.Contains("行 1・行 2 で使っています。", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>まとめる単位は取引先であって、違反の種類ではない。</b>
    /// <b>無効な 2 社</b>——同じコード・同じ重さ——を別の行に置いても <b>2 件出る</b>。
    /// 1 件にまとめると、片方の直し忘れに気づけない。
    /// </summary>
    [Fact]
    public void 無効な取引先が_2_社なら_2_件出る()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.RetiredPartner),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.OtherRetiredPartner),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Sales, 2_000,
                department: AccountingFixture.SalesDepartment));

        var violations = Validate(entry);

        var inactive = violations.Where(v => v.Code == JournalViolationCodes.PartnerInactive).ToArray();
        Assert.Equal(2, inactive.Length);
        Assert.Contains(inactive, v => v.LineNo == 1 && v.Message.Contains("取引をやめた先", StringComparison.Ordinal));
        Assert.Contains(inactive, v => v.LineNo == 2 && v.Message.Contains("もう 1 社やめた先", StringComparison.Ordinal));
        // **1 か所ずつなので、場所の並べ書きは付かない。**
        Assert.DoesNotContain(inactive, v => v.Message.Contains("で使っています", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>マスタに無い側も、取引先ごとに 1 件。</b> 文は取引先の名前を持てない（マスタに無いので）が、
    /// <b>2 社なら 2 件出る</b>——文が同じでも、行番号が別の直し先を指している。
    /// </summary>
    [Fact]
    public void マスタに無い取引先も取引先ごとに数える()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.UnknownPartner),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.OtherUnknownPartner),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Sales, 2_000,
                department: AccountingFixture.SalesDepartment));

        var unknown = Validate(entry)
            .Where(v => v.Code == JournalViolationCodes.PartnerUnknown)
            .ToArray();

        Assert.Equal(2, unknown.Length);
        Assert.Equal([1, 2], unknown.Select(v => v.LineNo).ToArray());
    }

    /// <summary>同じ「マスタに無い」取引先を繰り返したときは、1 件にまとめて場所を並べる。</summary>
    [Fact]
    public void マスタに無い同じ取引先を繰り返しても断りは_1_件で場所を並べる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.UnknownPartner),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.UnknownPartner),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Sales, 2_000,
                department: AccountingFixture.SalesDepartment));

        var violations = Validate(entry);

        var violation = AssertViolation(JournalViolationCodes.PartnerUnknown, violations);
        Assert.Null(violation.LineNo);
        Assert.Equal(
            "取引先が取引先マスタにありません。行 1・行 2 で使っています。"
            + "別の取引先を選ぶか、取引先マスタに登録してください。",
            violation.Message);
        Assert.Single(violations, v => v.Code == JournalViolationCodes.PartnerUnknown);
    }

    /// <summary>
    /// <b>まとめる仕掛けが、有効な相手を巻き込まない。</b>
    /// 伝票に有効な相手、行 2 に無効な相手を置くと、<b>無効な側だけが 1 件</b>出る。
    /// </summary>
    [Fact]
    public void 有効な相手と無効な相手が混ざっても無効な側だけを断る()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.OtherPartner),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.RetiredPartner),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.Sales, 2_000,
                department: AccountingFixture.SalesDepartment)) with
        {
            PartnerId = AccountingFixture.Partner,
        };

        var violations = Validate(entry);

        var violation = AssertViolation(JournalViolationCodes.PartnerInactive, violations);
        Assert.Equal(2, violation.LineNo);
        Assert.DoesNotContain(
            violations, v => v.Code == JournalViolationCodes.PartnerUnknown);
    }

    /// <summary>有効な取引先なら、伝票にも明細にも何も言わない（検体が縮退していないことも見る）。</summary>
    [Fact]
    public void 有効な取引先には何も言わない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000,
                partner: AccountingFixture.OtherPartner),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 1_000,
                department: AccountingFixture.SalesDepartment)) with
        {
            PartnerId = AccountingFixture.Partner,
        };

        var violations = Validate(entry);

        Assert.DoesNotContain(
            violations,
            v => v.Code is JournalViolationCodes.PartnerUnknown or JournalViolationCodes.PartnerInactive);
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
