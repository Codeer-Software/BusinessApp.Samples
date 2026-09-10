namespace BusinessApp.AccountingCore.Server.Journals.Application;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.ServerSupport;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 保存へ渡そうとしている伝票と明細が、<b>DDL がそのまま受け取れる形か</b>を見る。
/// </summary>
/// <remarks>
/// <para><b>関門の役目は「DB が拒む値を保存へ渡さない」ところまでである</b>
/// （qa/03 L-14 の処方を仕訳へ当てたもの。qa/03 L-16）。渡してしまうと、保存の失敗は
/// <c>ModuleSubmitResult.ExceptionMessage</c> で返り、CLB がそれをそのままトーストに出す
/// （qa/01 F-16）。</para>
/// <para><b>規則と文言は <see cref="JournalLineRules"/> が持つ</b>——計上の検証と共有していて、
/// どちらが先に捕まえたかで利用者に出る言葉が変わらないようにするため。</para>
/// <para><b>計上のときだけでなく、下書き保存でも見る。</b> DB が拒むかどうかに伝票の状態は関係ない。</para>
/// <para><b>見るのは「1 行だけで判定できること」に限る。</b> ここが見ていないものが 2 つある。
/// <list type="bullet">
///   <item><b>外部キーの実在</b>（勘定科目・税区分・部門・取引先）。マスタを引く必要があり、
///     それは計上の検証（<c>AccountUnknown</c> ほか）が持つ。画面の候補は実在するものしか出さない。</item>
///   <item><b>行番号の重複</b>（<c>UNIQUE (journal_entry_id, line_no)</c>）。
///     <b>差分に載っていない行と衝突しうるので、保存済みの行を読まないと判定できない。</b>
///     <b>計上でも通り抜ける</b>——計上の検証は保存の<b>後</b>に読み直した伝票を見るので
///     （<see cref="JournalSubmitGate"/>）、<c>UNIQUE</c> のほうが先に当たる。
///     枠組みの言葉で失敗する形を直すのは docs/04 §1 の B-1 である。</item>
/// </list>
/// どちらも通り抜けると枠組みの言葉で失敗する（<c>SaveFailureMessage</c> が利用者の語に差し替える）。</para>
/// </remarks>
internal static class JournalSubmitRequirements
{
    /// <summary>伝票のモジュール名。</summary>
    public const string EntryModuleName = JournalSubmitGate.EntryModuleName;

    /// <summary>明細のモジュール名。<b>伝票と同じ束に混ざって届く</b>ので名前で見分ける（qa/01 F-11）。</summary>
    public const string LineModuleName = "JournalLine";

    /// <summary>入っていないと DDL の <c>NOT NULL</c> に当たる、伝票の項目。</summary>
    private static readonly (string FieldName, string Message)[] RequiredEntryValues =
    [
        ("TransactionDate", JournalLineRules.TransactionDateMissing),
        ("PostingDate", JournalLineRules.PostingDateMissing),
        ("FiscalYear", JournalLineRules.FiscalYearMissing),
    ];

    /// <summary>入っていないと DDL の <c>NOT NULL</c> に当たる、明細の項目。</summary>
    private static readonly (string FieldName, string Message)[] RequiredLineValues =
    [
        ("LineNo", JournalLineRules.LineNoMissing),
        ("DebitCredit", JournalLineRules.DebitCreditMissing),
        ("Account", JournalLineRules.AccountMissing),
        ("Amount", JournalLineRules.AmountMissing),
        ("TaxCategory", JournalLineRules.TaxCategoryMissing),
    ];

    /// <summary>差分を見て、保存へ渡せない理由を全部返す。</summary>
    /// <remarks>
    /// <b>1 件で止めない。</b> 直しては弾かれを繰り返させないためで、
    /// <see cref="JournalPostingRejectedException"/> が全件を並べて見せる。
    /// </remarks>
    public static IReadOnlyList<Violation> Check(IReadOnlyList<ModuleSubmitData> transactionData)
    {
        var violations = new List<Violation>();

        // **新規と更新で「差分に無い」の意味が違う**（qa/01 F-12）。
        // 新規に載っていない項目は「入っていない」、更新に載っていない項目は「変えていない」——
        // 後者は保存されている値がそのまま残り、その値は DDL を通っている。
        foreach (var (data, isNew) in RowsIn(transactionData))
        {
            if (data.Name == EntryModuleName)
            {
                CheckEntry(data, isNew, violations);
            }
            else if (data.Name == LineModuleName)
            {
                CheckLine(data, isNew, violations);
            }
        }

        return violations;
    }

    private static IEnumerable<(ModuleData Data, bool IsNew)> RowsIn(
        IReadOnlyList<ModuleSubmitData> transactionData)
        => transactionData.SelectMany(d => d.Add.Select(a => (a, true)))
            .Concat(transactionData.SelectMany(d => d.Update.Select(u => (u, false))));

    private static void CheckEntry(ModuleData data, bool isNew, List<Violation> violations)
    {
        foreach (var (fieldName, message) in RequiredEntryValues)
        {
            if (IsMissing(data, fieldName, isNew))
            {
                violations.Add(new Violation(JournalViolationCodes.RequiredValueMissing, message));
            }
        }

        AddIfUndefinedChoice<EntryStatus>(data, "Status", JournalLineRules.StatusNotStorable, null, violations);
        AddIfUndefinedChoice<EntryType>(data, "EntryType", JournalLineRules.EntryTypeNotStorable, null, violations);

        // **元の伝票は、利用者が触ってよい場面が 1 つも無い**（qa/03 L-30）。取消・訂正の伝票はサーバが作るときに入れる
        // （<c>JournalEntryStore.InsertDraftAsync</c>——この関門を通らない）ので、**この経路で来た値は新規でも更新でも採らない**。
        // 更新の差分に載っているのは「変えた」ときだけ（qa/01 F-12）。画面は閲覧専用にしてあり、来るのは画面を通らない経路である。
        if (isNew ? !IsMissing(data, "OriginalEntry", true) : data.Fields.ContainsKey("OriginalEntry"))
        {
            violations.Add(new Violation(
                JournalViolationCodes.OriginalEntrySystemAssigned, JournalLineRules.OriginalEntryNotEditable));
        }
    }

    private static void CheckLine(ModuleData data, bool isNew, List<Violation> violations)
    {
        var lineNo = LineNoOf(data);

        foreach (var (fieldName, message) in RequiredLineValues)
        {
            if (IsMissing(data, fieldName, isNew))
            {
                // 税区分だけは、既に固有のコードを持っている（計上の検証と同じ原因・同じ文言）。
                var code = fieldName == "TaxCategory"
                    ? JournalViolationCodes.TaxCategoryMissing
                    : JournalViolationCodes.RequiredValueMissing;
                violations.Add(new Violation(code, message, lineNo));
            }
        }

        // 金額と行番号は「入っているか」だけでなく「その値を DDL が受け取れるか」も見る。
        // 画面は Min: 1・MaxFractionDigits: 0 で防いでいるが、**画面は経路の 1 本でしかない**。
        if (NumberOf(data, "Amount") is decimal amount && !JournalLineRules.IsStorableAmount(amount))
        {
            violations.Add(new Violation(AmountCodeOf(amount), AmountMessageOf(amount), lineNo));
        }

        if (NumberOf(data, "LineNo") is decimal no && !JournalLineRules.IsStorableLineNo(no))
        {
            violations.Add(new Violation(
                JournalViolationCodes.LineNoInvalid, JournalLineRules.LineNoNotStorable, lineNo));
        }

        AddIfUndefinedChoice<DebitCredit>(
            data, "DebitCredit", JournalLineRules.DebitCreditNotStorable, lineNo, violations);

        // **用途区分もこの形である**（2026-09-02 の自己レビュー）。使い始めるのはフェーズ 3 だが、
        // 列は既にあり、DDL の CHECK も既にある——**空けておく理由が無い**。
        AddIfUndefinedChoice<TaxTreatment>(
            data, "TaxTreatment", JournalLineRules.TaxTreatmentNotStorable, lineNo, violations);
    }

    /// <summary>
    /// 選択肢の値が、DDL の <c>CHECK</c> が並べている値のどれかであること。
    /// </summary>
    /// <remarks>
    /// <para><b>「入っているか」だけでは足りない</b>（qa/03 L-14 の型）。
    /// <c>debit_credit</c> / <c>status</c> / <c>entry_type</c> にはどれも
    /// <c>CHECK (… IN (…))</c> が付いていて、<b>候補外の値は DB が拒む</b>。
    /// 画面は選択欄なので起こらないが、<b>画面は経路の 1 本でしかない</b>——
    /// 金額と行番号に同じ理由で上限を置いたのと揃える。</para>
    /// <para>受理集合は<b>列挙子から引く</b>。値を書き並べると、
    /// 列挙子と DDL と 3 か所目が生まれる（<c>EnumConsistencyTests</c> が
    /// 列挙子と DDL の一致を守っているので、列挙子を見れば DDL を見たことになる）。</para>
    /// <para><b>照合は <see cref="DbValue.ToSnakeCase{T}"/> で行う。</b>
    /// <c>ToDefinedEnum</c> は使わない——あれは<b>DB から読んだ値を C# に直す寛容な読み手</b>で、
    /// <c>ToPascalCase</c> を通してから名前を照合するので <c>"Debit"</c> も <c>"_debit"</c> も通す。
    /// DDL の <c>CHECK</c> が受け取るのは <c>'debit'</c> だけなので、
    /// <b>関門が通して DB が拒む</b>——qa/03 L-21 が閉じようとした穴そのものが残る
    /// （2026-09-02 の自己レビューで指摘され、実際に <c>"Debit"</c> が通ることを確かめた）。</para>
    /// <para><b>空欄は見ない。</b> それは必須の検査（<see cref="RequiredLineValues"/>）の仕事で、
    /// ここで重ねて鳴らすと同じ 1 つの誤りが 2 件になる。</para>
    /// </remarks>
    private static void AddIfUndefinedChoice<T>(
        ModuleData data, string fieldName, string message, int? lineNo, List<Violation> violations)
        where T : struct, Enum
    {
        var value = data.Fields.TryGetValue(fieldName, out var field) ? (field as SelectFieldData)?.Value : null;

        if (!string.IsNullOrEmpty(value) && !IsStorableChoice<T>(value))
        {
            violations.Add(new Violation(JournalViolationCodes.ChoiceNotStorable, message, lineNo));
        }
    }

    /// <summary>DDL の <c>CHECK</c> が受け取る値か（列挙子を DB の書き方に直して照合する）。</summary>
    private static bool IsStorableChoice<T>(string value) where T : struct, Enum
        => Enum.GetValues<T>().Any(member => string.Equals(
            DbValue.ToSnakeCase(member), value, StringComparison.Ordinal));

    /// <summary>
    /// 0 以下と「持てない形」を分ける。<b>直し方が違う</b>（借方貸方の入れ替えか、桁と単位か）。
    /// </summary>
    /// <remarks>
    /// 境界は DDL に合わせて <c>amount &gt; 0</c> で切る。<b>0.5 は「正でない」ではなく「端数がある」</b>——
    /// 1 で切ると、1 円未満の端数が「金額は 1 円以上にしてください」と案内されて意味が通らない。
    /// </remarks>
    private static string AmountCodeOf(decimal amount)
        => amount <= 0m ? JournalViolationCodes.AmountNotPositive : JournalViolationCodes.AmountNotStorable;

    private static string AmountMessageOf(decimal amount)
    {
        if (amount <= 0m)
        {
            return JournalLineRules.AmountNotPositive;
        }

        return amount > long.MaxValue ? JournalLineRules.AmountTooLarge : JournalLineRules.AmountHasFraction;
    }

    /// <summary>
    /// 差分に載っていないか、載っていても空か。<b>新規のときだけ「載っていない」を欠落とみなす。</b>
    /// </summary>
    private static bool IsMissing(ModuleData data, string fieldName, bool isNew)
        => data.Fields.TryGetValue(fieldName, out var field) ? IsEmpty(field) : isNew;

    /// <summary>
    /// 空の表し方は型ごとに違う。<b>参照と選択は空文字、数値と日付は null。</b>
    /// </summary>
    /// <remarks>
    /// 想定していない型が来たら<b>「入っていない」に倒す</b>。読めない値を通すより、
    /// 止めて理由を見せるほうが安全側である（通せば DB の言葉で失敗する）。
    /// </remarks>
    private static bool IsEmpty(FieldDataBase field) => field switch
    {
        NumberFieldData number => number.Value is null,
        LinkFieldData link => string.IsNullOrEmpty(link.Value),
        SelectFieldData select => string.IsNullOrEmpty(select.Value),
        DateFieldData date => date.Value is null,
        _ => true,
    };

    private static decimal? NumberOf(ModuleData data, string fieldName)
        => (data.Fields.TryGetValue(fieldName, out var field) ? field as NumberFieldData : null)?.Value;

    /// <summary>差し戻しに添える行番号。<b>読めないときは付けない</b>（嘘の行を指さない）。</summary>
    private static int? LineNoOf(ModuleData data)
        => NumberOf(data, "LineNo") is decimal no && JournalLineRules.IsStorableLineNo(no) ? (int)no : null;
}
