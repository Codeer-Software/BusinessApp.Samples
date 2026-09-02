namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using BusinessApp.AccountingCore.Shared;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;
using Microsoft.Data.Sqlite;

/// <summary>
/// 伝票と明細が <c>journal_entries</c> / <c>journal_lines</c> の制約に収まっているか（保存の手前の網）。
/// </summary>
/// <remarks>
/// <b>ここが空いていると、利用者に届くのは枠組みの言葉である</b>——実機では
/// <c>SQLite Error 19: 'NOT NULL constraint failed: journal_lines.account_id'</c> が
/// そのままトーストに出た（qa/03 L-16）。
/// </remarks>
public class JournalSubmitRequirementsTests
{
    private static IReadOnlyList<ModuleSubmitData> Adding(params ModuleData[] rows)
        => [new ModuleSubmitData { ModuleName = JournalSubmitRequirements.EntryModuleName, Add = [.. rows] }];

    private static IReadOnlyList<ModuleSubmitData> Updating(params ModuleData[] rows)
        => [new ModuleSubmitData { ModuleName = JournalSubmitRequirements.EntryModuleName, Update = [.. rows] }];

    private static List<string> CodesOf(IReadOnlyList<Violation> violations)
        => violations.Select(v => v.Code).ToList();

    private static List<string> MessagesOf(IReadOnlyList<Violation> violations)
        => violations.Select(v => v.Message).ToList();

    // --- 明細 ---

    [Fact]
    public void 必須項目が揃った明細は通る()
    {
        Assert.Empty(JournalSubmitRequirements.Check(Adding(SubmitData.Line())));
        Assert.Empty(JournalSubmitRequirements.Check(Updating(SubmitData.Line())));
    }

    /// <summary>
    /// <b>新規では、差分に無い＝入っていない。</b> 5 つとも DDL の <c>NOT NULL</c> に当たる。
    /// </summary>
    [Theory]
    [InlineData("DebitCredit", JournalLineRules.DebitCreditMissing)]
    [InlineData("Account", JournalLineRules.AccountMissing)]
    [InlineData("Amount", JournalLineRules.AmountMissing)]
    [InlineData("TaxCategory", JournalLineRules.TaxCategoryMissing)]
    public void 新規で明細の必須項目が差分に無ければ止める(string fieldName, string message)
    {
        var violations = JournalSubmitRequirements.Check(Adding(SubmitData.LineWithout(3, fieldName)));

        Assert.Equal([message], MessagesOf(violations));
        Assert.Equal(3, violations[0].LineNo);
    }

    /// <summary>
    /// 行番号そのものが無い行は、<b>差し戻しに行を添えられない</b>（嘘の行を指さない）。
    /// だから文言のほうが「明細の行を入れ直してください」と、どうすればよいかを持つ。
    /// </summary>
    [Fact]
    public void 行番号が無い明細は行を添えずに止める()
    {
        var violations = JournalSubmitRequirements.Check(Adding(SubmitData.LineWithout(3, "LineNo")));

        Assert.Equal([JournalLineRules.LineNoMissing], MessagesOf(violations));
        Assert.Null(violations[0].LineNo);
    }

    /// <summary>
    /// <b>税区分だけは、計上の検証と同じコードで返す。</b> 同じ原因に 2 つのコードを作らない
    /// （<see cref="JournalViolationCodes"/> 冒頭の規約の裏返し）。
    /// </summary>
    [Fact]
    public void 税区分の欠落は計上の検証と同じコードで返す()
    {
        var violations = JournalSubmitRequirements.Check(Adding(SubmitData.LineWithout(3, "TaxCategory")));

        Assert.Equal([JournalViolationCodes.TaxCategoryMissing], CodesOf(violations));
    }

    /// <summary>
    /// <b>更新では、差分に無い＝変えていない。</b> 保存されている値は DDL を通っているので見ない。
    /// ここで止めると、1 項目だけ直す普通の保存ができなくなる（qa/01 F-12）。
    /// </summary>
    [Fact]
    public void 更新は触った項目しか載らないので他の項目を見ない()
    {
        // 実機で届く形——識別子と、直した 1 項目だけ。
        var changed = SubmitData.LineChanging("12", "Amount", new NumberFieldData { Value = 500 });

        Assert.Empty(JournalSubmitRequirements.Check(Updating(changed)));
    }

    /// <summary>
    /// <b>更新でも「空にする」は止める。</b> 入っていた値を消す操作は正規の直し方で、
    /// 止めなければ DB の <c>NOT NULL</c> に当たる（qa/03 L-14 が取引先で踏んだ形と同じ）。
    /// </summary>
    [Theory]
    [InlineData("DebitCredit")]
    [InlineData("Account")]
    [InlineData("Amount")]
    [InlineData("TaxCategory")]
    public void 更新で明細の必須項目を空にしたら止める(string fieldName)
    {
        var emptied = SubmitData.LineChanging("12", fieldName, Emptied(SubmitData.Line().Fields[fieldName]));

        Assert.Single(JournalSubmitRequirements.Check(Updating(emptied)));
    }

    /// <summary>型はそのままに、値だけを「空」にする（空の表し方は型ごとに違う）。</summary>
    private static FieldDataBase Emptied(FieldDataBase field) => field switch
    {
        NumberFieldData => new NumberFieldData { Value = null },
        LinkFieldData => new LinkFieldData { Value = string.Empty },
        DateFieldData => new DateFieldData { Value = null },
        _ => new SelectFieldData { Value = string.Empty },
    };

    /// <summary>
    /// 想定していない型は<b>「入っていない」に倒す</b>。読めない値を通すと枠組みの言葉で失敗する。
    /// </summary>
    [Fact]
    public void 想定していない型のフィールドは入っていないものとして止める()
    {
        var line = SubmitData.LineChanging("12", "Account", new TextFieldData { Value = "7" });

        Assert.Equal([JournalViolationCodes.RequiredValueMissing], CodesOf(JournalSubmitRequirements.Check(Updating(line))));
    }

    /// <summary>
    /// <b>選択肢も同じ扱い</b>——読めない型は「入っていない」1 件だけにする。
    /// </summary>
    /// <remarks>
    /// ここで「候補外」も鳴らすと、<b>1 つの誤りに 2 つの指摘</b>が出る。
    /// どちらが本当かを利用者に考えさせない。
    /// </remarks>
    [Fact]
    public void 想定していない型の選択肢は候補外として重ねて鳴らさない()
    {
        // **値そのものは候補外にする。** 正しい値を渡すと、型のガードを外しても
        // 「値がたまたま通った」と区別が付かない（2026-09-02 の自己レビュー）。
        var line = SubmitData.LineChanging("12", "DebitCredit", new TextFieldData { Value = "both" });

        Assert.Equal([JournalViolationCodes.RequiredValueMissing], CodesOf(JournalSubmitRequirements.Check(Updating(line))));
    }

    // --- 金額（DDL: amount > 0 AND typeof(amount) = 'integer'。SQLite の INTEGER は 64 ビット）---

    /// <remarks>
    /// <c>decimal</c> は属性の引数にできないので <c>double</c> で受けて変換する
    /// （<c>NumberFieldData.Value</c> は <c>decimal?</c>）。
    /// </remarks>
    /// <remarks>
    /// 境界は DDL の <c>amount &gt; 0</c> に合わせる。<b>0.5 はこちらではなく「端数がある」側</b>——
    /// 1 で切ると、1 円未満の端数に「1 円以上にしてください」と案内することになる。
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 金額が正でなければ止める(double amount)
    {
        var line = SubmitData.LineWith(3, "Amount", new NumberFieldData { Value = (decimal)amount });
        var violations = JournalSubmitRequirements.Check(Updating(line));

        // 直し方が「借方貸方の入れ替え」なので、桁の話とはコードも文言も分ける。
        Assert.Equal([JournalViolationCodes.AmountNotPositive], CodesOf(violations));
        Assert.Equal([JournalLineRules.AmountNotPositive], MessagesOf(violations));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(100.5)]
    public void 金額に一円未満の端数があれば止める(double amount)
    {
        var line = SubmitData.LineWith(3, "Amount", new NumberFieldData { Value = (decimal)amount });
        var violations = JournalSubmitRequirements.Check(Updating(line));

        Assert.Equal([JournalViolationCodes.AmountNotStorable], CodesOf(violations));
        Assert.Equal([JournalLineRules.AmountHasFraction], MessagesOf(violations));
    }

    /// <summary>
    /// <b>上限を書かないと、関門自身が DB の受理集合の外の値を通す</b>（qa/03 L-14 の型）。
    /// <c>amount</c> は 64 ビット整数なので、<see cref="long"/> を 1 超えた値は整数として格納できない。
    /// </summary>
    [Fact]
    public void 金額が扱える大きさを超えたら止める()
    {
        var line = SubmitData.LineWith(3, "Amount", new NumberFieldData { Value = (decimal)long.MaxValue + 1 });
        var violations = JournalSubmitRequirements.Check(Updating(line));

        Assert.Equal([JournalViolationCodes.AmountNotStorable], CodesOf(violations));
        Assert.Equal([JournalLineRules.AmountTooLarge], MessagesOf(violations));
    }

    /// <summary>
    /// <b>1 円と上限ちょうどは通す。</b> 上限を 1 つ内側に取ると、
    /// 大きな取引を扱う利用者だけが計上できなくなる。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(long.MaxValue)]
    public void 金額は一円から上限ちょうどまで通る(long amount)
    {
        var line = SubmitData.LineWith(3, "Amount", new NumberFieldData { Value = amount });

        Assert.Empty(JournalSubmitRequirements.Check(Updating(line)));
    }

    // --- 行番号 ---

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void 行番号が1以上の整数でなければ止める(double lineNo)
    {
        var line = SubmitData.LineWith(3, "LineNo", new NumberFieldData { Value = (decimal)lineNo });
        var violations = JournalSubmitRequirements.Check(Updating(line));

        Assert.Equal([JournalViolationCodes.LineNoInvalid], CodesOf(violations));
        Assert.Equal([JournalLineRules.LineNoNotStorable], MessagesOf(violations));
    }

    /// <summary>
    /// 行番号は差し戻しの文言に「N 行目」と添えるので <see cref="int"/> に収める。
    /// <b>上限ちょうどは通し、そこを 1 つ超えたら行番号として扱わない。</b>
    /// </summary>
    [Fact]
    public void 行番号は_int_の上限ちょうどまで通る()
    {
        var line = SubmitData.LineWith(3, "LineNo", new NumberFieldData { Value = int.MaxValue });

        Assert.Empty(JournalSubmitRequirements.Check(Updating(line)));
    }

    [Fact]
    public void 行番号が_int_に収まらなければ止める()
    {
        var line = SubmitData.LineWith(3, "LineNo", new NumberFieldData { Value = (decimal)int.MaxValue + 1 });
        var violations = JournalSubmitRequirements.Check(Updating(line));

        Assert.Equal([JournalViolationCodes.LineNoInvalid], CodesOf(violations));

        // 行番号として読めないので、差し戻しに行を添えない（嘘の行を指さない）。
        Assert.Null(violations[0].LineNo);
    }

    // --- 伝票 ---

    [Fact]
    public void 必須項目が揃った新規の伝票は通る()
    {
        Assert.Empty(JournalSubmitRequirements.Check(Adding(SubmitData.NewEntry("@temporary:1", "draft"))));
    }

    /// <summary>
    /// <b>伝票にも同じ網を掛ける。</b> 明細だけを塞ぐと、
    /// 「画面は経路の 1 本でしかない」という理由が伝票には当たっていないことになる（docs/09 §1）。
    /// </summary>
    [Theory]
    [InlineData("TransactionDate", JournalLineRules.TransactionDateMissing)]
    [InlineData("PostingDate", JournalLineRules.PostingDateMissing)]
    [InlineData("FiscalYear", JournalLineRules.FiscalYearMissing)]
    public void 新規で伝票の必須項目が差分に無ければ止める(string fieldName, string message)
    {
        var violations = JournalSubmitRequirements.Check(Adding(SubmitData.NewEntryWithout("@temporary:1", fieldName)));

        Assert.Equal([JournalViolationCodes.RequiredValueMissing], CodesOf(violations));
        Assert.Equal([message], MessagesOf(violations));

        // 伝票の違反に行番号は付かない。
        Assert.Null(violations[0].LineNo);
    }

    /// <summary>
    /// <b>計上するだけの保存（識別子と状態だけ）を止めない。</b> これがいちばん多い保存の形で、
    /// ここで止めると開いて計上ボタンを押す操作が通らなくなる。
    /// </summary>
    [Fact]
    public void 更新の伝票は触った項目しか載らないので他の項目を見ない()
    {
        Assert.Empty(JournalSubmitRequirements.Check(Updating(SubmitData.Entry("1", "posted"))));
    }

    /// <summary>
    /// 会計年度は画面が計上日から引いて入れるので、<b>空なのは「その計上日を含む年度が無い」とき</b>。
    /// 「選んでください」と書くと、選べる年度があるように読める。
    /// </summary>
    [Fact]
    public void 会計年度を空にした保存は年度が無いことを伝える()
    {
        var entry = SubmitData.Entry("1", "posted");
        entry.Fields["FiscalYear"] = new LinkFieldData { Value = string.Empty };

        Assert.Equal(
            [JournalLineRules.FiscalYearMissing],
            MessagesOf(JournalSubmitRequirements.Check(Updating(entry))));
    }

    // --- まとめ方 ---

    /// <summary>
    /// <b>1 件で止めない。</b> 直しては弾かれを繰り返させない（例外が全件を並べる理由と同じ）。
    /// </summary>
    [Fact]
    public void 違反は伝票と明細をまたいで全件返す()
    {
        var violations = JournalSubmitRequirements.Check(
        [
            new ModuleSubmitData
            {
                ModuleName = JournalSubmitRequirements.EntryModuleName,
                Add =
                [
                    SubmitData.NewEntryWithout("@temporary:1", "TransactionDate"),
                    SubmitData.LineWithout(1, "Account"),
                    SubmitData.LineWithout(2, "TaxCategory"),
                ],
                Update = [SubmitData.LineChanging("12", "Amount", new NumberFieldData { Value = 0 })],
            },
        ]);

        Assert.Equal(
            [$":{JournalViolationCodes.RequiredValueMissing}",
             $"1:{JournalViolationCodes.RequiredValueMissing}",
             $"2:{JournalViolationCodes.TaxCategoryMissing}",
             $":{JournalViolationCodes.AmountNotPositive}"],
            violations.Select(v => $"{v.LineNo}:{v.Code}"));
    }

    /// <summary>
    /// 伝票でも明細でもないモジュールは見ない。<b>同じ束に混ざって届く</b>ので、
    /// 名前で見分けられなければ取引先の保存まで差し戻してしまう（qa/01 F-11）。
    /// </summary>
    [Fact]
    public void 仕訳以外のモジュールは見ない()
    {
        var partner = new ModuleData { Name = "Partner" };
        partner.Fields["Id"] = new IdFieldData { Value = "1" };

        Assert.Empty(JournalSubmitRequirements.Check(Adding(partner)));
    }

    // --- 往復（qa/03 L-14 の処方）---

    /// <summary>
    /// <b>関門が通した明細は、DB も受け取れる。</b>
    /// </summary>
    /// <remarks>
    /// <para>片側だけを見ると必ず穴が残る——qa/03 L-14 は「関門が差し戻すか」しか見ておらず、
    /// <b>関門自身が DDL の受理集合の外の値を作っていた</b>ことに気づけなかった。
    /// 通すことと書けることを 1 本のテストで結ぶ。</para>
    /// <para><b>書いた値を読み戻して record ごと比べる</b>（qa/03 L-04）。件数だけ数えると、
    /// 勘定科目と税区分を取り違えて書いても緑のままになる。</para>
    /// </remarks>
    [Theory]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void 関門が通した明細は_DB_も受け取れる(long amount)
    {
        using var server = new AccountingServer();
        var line = StorableLine(server, amount);

        Assert.Empty(JournalSubmitRequirements.Check(Adding(line)));

        var entry = server.InsertDraft();
        server.InsertLine(entry, line);

        Assert.Equal(AccountingServer.ValuesOf(line), server.StoredLine(entry, 3));
    }

    /// <summary>
    /// <b>DDL の <c>CHECK</c> が並べていない選択肢を、保存へ渡さない。</b>
    /// </summary>
    /// <remarks>
    /// <para>「入っているか」しか見ていなかった（2026-09-02 のフェーズ 2.5 の D で見つけた）。
    /// <c>debit_credit</c> / <c>status</c> / <c>entry_type</c> にはどれも
    /// <c>CHECK (… IN (…))</c> が付いていて、候補外の値は <b>DB が拒む</b>——
    /// そこまで届くと、利用者には <c>SQLite Error 19</c> がそのまま出る（qa/01 F-16・qa/03 L-14）。</para>
    /// <para><b>DB が実際に拒むことも同じテストで確かめる。</b> 関門だけを見ていると、
    /// 「関門は止めるが DB は受け取れた」（＝過剰な関門）と
    /// 「関門は通すが DB が拒む」（＝穴）の区別が付かない。</para>
    /// </remarks>
    /// <remarks>
    /// <b>検体に「大文字小文字だけが違う値」を入れる</b>（2026-09-02 の自己レビュー）。
    /// 綴りの違う値（<c>"both"</c>）だけだと、
    /// <b>「候補外を弾く実装」と「PascalCase を通す寛容な読み手」を区別できない</b>——
    /// <c>DbValue.ToDefinedEnum</c> は <c>"Debit"</c> を通してしまい、DDL の
    /// <c>CHECK (debit_credit IN ('debit','credit'))</c> が拒む（qa/03 L-21）。
    /// </remarks>
    [Theory]
    [InlineData("both")]
    [InlineData("Debit")]
    [InlineData("_debit")]
    public void 候補外の借方貸方は保存へ渡さず_DB_も拒む(string value)
    {
        using var server = new AccountingServer();
        var line = StorableLine(server, 1000);
        line.Fields["DebitCredit"] = new SelectFieldData { Value = value };

        var violations = JournalSubmitRequirements.Check(Adding(line));

        Assert.Equal([JournalViolationCodes.ChoiceNotStorable], CodesOf(violations));
        Assert.Equal([JournalLineRules.DebitCreditNotStorable], MessagesOf(violations));
        Assert.Equal(3, violations[0].LineNo);

        var entry = server.InsertDraft();
        Assert.Throws<SqliteException>(() => server.InsertLine(entry, line));
    }

    /// <summary>用途区分も同じ形で守る（列も CHECK も既にある。使い始めるのはフェーズ 3）。</summary>
    [Fact]
    public void 候補外の用途区分は保存へ渡さない()
    {
        var line = SubmitData.Line();
        line.Fields["TaxTreatment"] = new SelectFieldData { Value = "ForTaxableSales" };

        var violations = JournalSubmitRequirements.Check(Adding(line));

        Assert.Equal([JournalViolationCodes.ChoiceNotStorable], CodesOf(violations));
        Assert.Equal([JournalLineRules.TaxTreatmentNotStorable], MessagesOf(violations));
    }

    /// <summary>正しい書き方（snake_case）は通る（鳴りっぱなしの関門にしない）。</summary>
    [Theory]
    [InlineData("for_taxable_sales")]
    [InlineData("common")]
    [InlineData("for_exempt_sales")]
    public void 正しい用途区分は通る(string value)
    {
        var line = SubmitData.Line();
        line.Fields["TaxTreatment"] = new SelectFieldData { Value = value };

        Assert.Empty(JournalSubmitRequirements.Check(Adding(line)));
    }

    [Theory]
    [InlineData("Status", "archived", JournalLineRules.StatusNotStorable)]
    [InlineData("Status", "Draft", JournalLineRules.StatusNotStorable)]
    [InlineData("EntryType", "adjustment", JournalLineRules.EntryTypeNotStorable)]
    [InlineData("EntryType", "Normal", JournalLineRules.EntryTypeNotStorable)]
    public void 候補外の伝票の選択肢は保存へ渡さない(string fieldName, string value, string message)
    {
        var entry = SubmitData.NewEntry("@temporary:1", "draft");
        entry.Fields[fieldName] = new SelectFieldData { Value = value };

        var violations = JournalSubmitRequirements.Check(Adding(entry));

        Assert.Equal([JournalViolationCodes.ChoiceNotStorable], CodesOf(violations));
        Assert.Equal([message], MessagesOf(violations));

        // 伝票の違反に行番号は付かない。
        Assert.Null(violations[0].LineNo);
    }

    /// <summary>
    /// <b>伝票の選択肢も、DB が実際に拒むところまで見る。</b>
    /// </summary>
    /// <remarks>
    /// 関門が鳴ることだけを見ていると、<b>DDL から <c>CHECK</c> が消えた日に
    /// 関門だけが過剰なまま残る</b>——「関門は止めるが DB は受け取れる」と
    /// 「関門は通すが DB が拒む」の区別が付かない（2026-09-02 の自己レビュー）。
    /// </remarks>
    [Theory]
    [InlineData("status", "archived")]
    [InlineData("entry_type", "adjustment")]
    public void 候補外の伝票の選択肢は_DB_も拒む(string column, string value)
    {
        using var server = new AccountingServer();

        Assert.Throws<SqliteException>(() => server.Execute(
            $"""
            insert into journal_entries (fiscal_year_id, transaction_date, posting_date, {column})
            values (1, '2026-08-24 00:00:00', '2026-08-24 00:00:00', '{value}')
            """));
    }

    /// <summary>
    /// <b>空欄は「候補外」ではない。</b> 同じ 1 つの誤りを 2 件にしない。
    /// </summary>
    /// <remarks>
    /// 必須の検査（<c>RequiredLineValues</c>）が「選んでください」と言う場所である。
    /// ここも鳴ると、利用者は 1 つの空欄に 2 つの指摘を読むことになる。
    /// <b>欄を落とすのではなく空文字を渡す</b>——落とす形は上の Theory が既に見ており、
    /// 空文字の経路（画面が値を消したとき）はこちらでしか通らない。
    /// </remarks>
    [Fact]
    public void 空文字の選択肢は必須の検査だけが鳴らす()
    {
        var line = SubmitData.LineWith(3, "DebitCredit", new SelectFieldData { Value = string.Empty });

        var violations = JournalSubmitRequirements.Check(Adding(line));

        Assert.Equal([JournalViolationCodes.RequiredValueMissing], CodesOf(violations));
        Assert.Equal([JournalLineRules.DebitCreditMissing], MessagesOf(violations));
    }

    /// <summary>
    /// 実在する勘定科目と税区分を<b>コードから引いた</b>明細（初期データの id を書き写さない）。
    /// 借方貸方・金額・行番号は既定のまま——値はすべて違う（qa/03 L-02）。
    /// </summary>
    private static ModuleData StorableLine(AccountingServer server, long amount)
    {
        var line = SubmitData.Line();
        line.Fields["Account"] = new LinkFieldData { Value = server.Text(server.AccountOf("1100").Value) };
        line.Fields["TaxCategory"] = new LinkFieldData { Value = server.Text(server.TaxCategoryOf("OUT").Value) };
        line.Fields["Amount"] = new NumberFieldData { Value = amount };
        return line;
    }
}
