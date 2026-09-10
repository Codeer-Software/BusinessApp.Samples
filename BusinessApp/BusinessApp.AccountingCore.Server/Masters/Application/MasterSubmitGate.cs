namespace BusinessApp.AccountingCore.Server.Masters.Application;

using System.Globalization;

using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.Repository.Data;
using BusinessApp.AccountingCore.Server.Masters.Infrastructure;

/// <summary>
/// マスタを保存するときの関門（docs/12 §2-1・ADR-0047。qa/03 L-28 の型）。
/// </summary>
/// <remarks>
/// <para><b>DDL の制約に当たると、利用者には定型文しか出ない</b>——
/// 「保存できませんでした。入力内容を確かめ、画面を開き直してもう一度お試しください。」
/// 会計コアのマスタ 4 画面には関門が 1 つも無く、コードの重複も「全社共通」の 2 件目も
/// 税率区分の欠けも、全部この 1 文になっていた（qa/03 L-28。2026-09-04 の探索的テストで実測）。
/// <b>ここは、その手前に置く網である。DB の制約を外すのではない。</b></para>
/// <para><b><see cref="MasterMeaningGate"/> とは別の関門である。</b>
/// あちらは「使用中の行の意味を変えられない」（ADR-0038）を見る。こちらは
/// <b>値そのものが正しいか・重複していないか</b>を見る。<b>順は意味の凍結が先</b>——
/// あちらは直す手立てが無い（新しい行を作るしかない）が、こちらは値を直せば通る。</para>
/// <para><b>追加も更新も見る。</b> 追加だけを守る関門は、正しい値で作ってから壊す経路を残す
/// （<c>PartnerSubmitGate</c> と同じ理由）。<b>更新では触った欄しか届かない</b>ので
/// （qa/01 F-12）、2 つの欄をまたぐ規則は保存されている側と組んで判定する。</para>
/// <para><b>理由は 1 つだけ返す</b>（docs/21 §2-6）。</para>
/// </remarks>
public sealed class MasterSubmitGate(MasterCodeStore store)
{
    /// <summary>課税の区分（税率区分が要るもの）。<b>値は DDL の <c>CHECK</c> の写しである</b>（docs/20 §4）。</summary>
    private static readonly string[] TaxableTypes = ["taxable_sales", "taxable_purchase"];

    /// <summary>
    /// コードを持つマスタ（docs/12 §2-1）。
    /// </summary>
    /// <remarks>
    /// <b>会計年度も入れてある。</b> 作成の画面はまだ無い（フェーズ 4）が、
    /// <b>ここに無いと、画面ができた日に関門だけが漏れる</b>。DDL のトリガは既に 6 表とも守っている。
    /// <b>取引先はここに無い</b>——取引先部品のものなので <c>PartnerSubmitGate</c> が持つ（ADR-0025）。
    /// </remarks>
    public static readonly IReadOnlyList<CodedMaster> Coded =
    [
        new("Account", "accounts", "科目コード"),
        new("SubAccount", "sub_accounts", "補助科目コード", new("account_id", "Account")),
        new("Department", "departments", "部門コード"),
        new("TaxCategory", "tax_categories", "税区分コード"),
        new("FiscalYear", "fiscal_years", "年度コード"),
    ];

    /// <summary>部品の組み立て。</summary>
    public static MasterSubmitGate Create(IDbAccessor dbAccessor, string dataSourceName)
        => new(new MasterCodeStore(dbAccessor, dataSourceName));

    /// <summary>保存を包む。<paramref name="save"/> は CLB 本来の保存処理。</summary>
    public async Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        ArgumentNullException.ThrowIfNull(transactionData);
        ArgumentNullException.ThrowIfNull(save);

        // **入れ物の名前ではなく、中身の名前で担当を決める**（qa/02 R16-16 の型）。
        foreach (var (data, adding) in transactionData.SelectMany(
                     d => d.Add.Select(r => (Row: r, Adding: true))
                           .Concat(d.Update.Select(r => (Row: r, Adding: false)))))
        {
            if (Coded.FirstOrDefault(m => m.ModuleName == data.Name) is CodedMaster master)
            {
                await RejectAsync(master, data, adding, transactionData);
            }
        }

        return await save();
    }

    private async Task RejectAsync(
        CodedMaster master, ModuleData data, bool adding,
        IReadOnlyList<ModuleSubmitData> transactionData)
    {
        var id = Id(data);

        // **追加はコードを必ず伴う。** 画面は必ず送ってくるが、**取込は列ごと落とせる**
        // （`code` の無い CSV）——**取込こそこの関門が守る経路である**（2026-09-09 の自己レビュー）。
        // 更新は差分しか届かない（qa/01 F-12）ので、載っていないことが正常である。
        if (adding && !data.Fields.ContainsKey("Code"))
        {
            throw new MasterRejectedException($"「{master.CodeLabel}」を入れてください。");
        }

        await RejectBadCodeAsync(master, data, id);
        await RejectSecondCompanyWideDepartmentAsync(master, data, id);
        await RejectInconsistentTaxCategoryAsync(master, data, id);
        await RejectSubAccountUnderPlainAccountAsync(master, data, transactionData);
    }

    /// <summary>コードの書式と、大小を無視した重複（ADR-0047）。</summary>
    /// <remarks>
    /// <b>コードを触っていなくても、親が動いたら数え直す。</b> 補助科目を別の勘定科目へ移すと、
    /// コードは 1 字も変わらないのに<b>一意の範囲（勘定科目, コード）が変わる</b>ので、
    /// 移した先に同じコードがあれば重複になる。差分にはコードが載らない（qa/01 F-12）から、
    /// <b>保存されている字を読み直して数える</b>（2026-09-09 の自己レビュー。
    /// 親を読み直す穴と同じ家系で、こちらだけ残っていた）。
    /// </remarks>
    private async Task RejectBadCodeAsync(CodedMaster master, ModuleData data, long? id)
    {
        var touched = Text(data, "Code") is string code ? MasterCode.Normalize(code) : null;
        var normalized = touched ?? await StoredCodeForParentMoveAsync(master, data, id);
        if (normalized is null)
        {
            return;
        }

        if (touched is not null)
        {
            if (normalized.Length == 0)
            {
                throw new MasterRejectedException($"「{master.CodeLabel}」を入れてください。");
            }

            if (MasterCode.DescribeProblem(master.CodeLabel, normalized) is string problem)
            {
                throw new MasterRejectedException(problem);
            }

            // **正規化した姿を差分に書き戻す。** 比べるときだけ落とすと、関門が「同じ」と通した値を
            // DB のトリガが「違う」と拒む（関門の受理集合が DB より広い。qa/03 L-14 の型）。
            if (data.Fields["Code"] is TextFieldData text)
            {
                text.Value = normalized;
            }
        }

        var conflict = await store.FindConflictingCodeAsync(
            master, normalized, id, (await ParentAsync(master, data, id)).Id);
        if (conflict is null)
        {
            return;
        }

        // **ぶつかった相手の字を見せる。** 大小だけが違うとき、字を見比べないと理由が分からない。
        // **補助科目だけは範囲が違う**（一意なのは勘定科目とコードの組）。範囲を言わないと、
        // 利用者は「全社で一意」と読んで要らない採番規則を作る（2026-09-09 の自己レビュー）。
        var scope = master.Parent is null ? string.Empty : "この勘定科目の中では";
        var reason = string.Equals(conflict, normalized, StringComparison.Ordinal)
            ? $"{scope}既に使われています。"
            : $"大文字と小文字を区別しないので、{scope}既にある「{conflict}」と同じコードになります。";

        // **直し方は、利用者が動かした欄の側で言う。** コードを触っていないのに
        // 「別のコードを入れてください」だけ出すと、いま選んだ勘定科目が原因だと伝わらない。
        var howToFix = touched is null
            ? "別の勘定科目を選ぶか、コードを変えてください。"
            : "別のコードを入れてください。";

        throw new MasterRejectedException(
            $"「{master.CodeLabel}」の「{normalized}」は{reason}{howToFix}");
    }

    /// <summary>
    /// コードを触らずに親だけを動かした更新で、<b>保存されているコード</b>。数え直す必要が無ければ <c>null</c>。
    /// </summary>
    private async Task<string?> StoredCodeForParentMoveAsync(CodedMaster master, ModuleData data, long? id)
    {
        if (master.Parent is not CodedParent parent
            || !data.Fields.ContainsKey(parent.FieldName)
            || id is not long existing)
        {
            return null;
        }

        var stored = await store.FindStoredAsync(master, existing, ["code"]);
        return stored is null
            ? null
            : Convert.ToString(stored["code"], CultureInfo.InvariantCulture);
    }

    /// <summary>「全社共通」の部門は 1 つだけ（docs/10 §9-1）。</summary>
    /// <remarks>
    /// 見るのは DB に保存済みの行だけである。<b>同じ保存に「全社共通」の行を 2 つ載せる経路（取込・API）は関門では数えず、
    /// DDL の部分 UNIQUE インデックス（<c>ux_departments_company_wide</c>）が定型文で拒む</b>——画面は 1 行ずつしか保存しない。
    /// </remarks>
    private async Task RejectSecondCompanyWideDepartmentAsync(CodedMaster master, ModuleData data, long? id)
    {
        if (master.ModuleName != "Department" || Boolean(data, "IsCompanyWide") is not true)
        {
            return;
        }

        var current = await store.CompanyWideDepartmentAsync(id);
        if (current is null)
        {
            return;
        }

        // **ぶつかった相手を教える**（qa/02 R57-06）。コードの重複の断りは相手の字を見せているのに、
        // こちらだけ「いまなっている部門」と言うのは非対称で、利用者は一覧を探しに行くことになる。
        throw new MasterRejectedException(
            $"「全社共通」の部門は 1 つだけです。いま「全社共通」になっているのは部門名「{current.Value.Name}」（部門コード {current.Value.Code}）です。"
            + "そちらをオフにしてから、こちらをオンにしてください。");
    }

    /// <summary>
    /// 課税の区分には税率区分が要り、課税でない区分には付かない（docs/11 §1。DDL の 2 本の <c>CHECK</c>）。
    /// </summary>
    /// <remarks>
    /// <b>2 つの欄をまたぐので、触っていない側は保存されている値を引く</b>（qa/01 F-12）。
    /// <b>新規は届いた値だけで判定できる</b>——両方が必ず載っているからである。
    /// </remarks>
    private async Task RejectInconsistentTaxCategoryAsync(CodedMaster master, ModuleData data, long? id)
    {
        if (master.ModuleName != "TaxCategory")
        {
            return;
        }

        var taxationType = Text(data, "TaxationType");
        var rateKind = Text(data, "RateKind");
        if (taxationType is null && rateKind is null)
        {
            return;
        }

        if ((taxationType is null || rateKind is null) && id is long stored)
        {
            var row = await store.FindStoredAsync(master, stored, ["taxation_type", "rate_kind"]);
            taxationType ??= Stored(row, "taxation_type");
            rateKind ??= Stored(row, "rate_kind");
        }

        var taxable = TaxableTypes.Contains(taxationType, StringComparer.Ordinal);
        var hasRate = !string.IsNullOrEmpty(rateKind);

        if (taxable && !hasRate)
        {
            throw new MasterRejectedException(
                "「課税区分」が「課税売上」「課税仕入」のときは「税率区分」が要ります。「税率区分」を選んでください。");
        }

        if (!taxable && hasRate)
        {
            throw new MasterRejectedException(
                "「課税区分」が「課税売上」「課税仕入」でないので、「税率区分」は空にしてください。");
        }
    }

    /// <summary>
    /// 補助科目を使わない勘定科目の下に、補助科目は作れない（ADR-0038 §3 の 2 値）。
    /// </summary>
    /// <remarks>
    /// <b>2026-09-08 の回では明細の側しか塞いでいなかった</b>——
    /// マスタの画面からは、使わない設定の科目にも補助科目を足せた（2026-09-09 に塞いだ。qa/03 L-27）。
    /// </remarks>
    private async Task RejectSubAccountUnderPlainAccountAsync(
        CodedMaster master, ModuleData data, IReadOnlyList<ModuleSubmitData> transactionData)
    {
        if (master.ModuleName != "SubAccount")
        {
            return;
        }

        // **科目が実在しないときは、ここで止めない**——外部キーが拒む。
        // 実在の断りは 1 か所（DB）に置き、ここは 2 値の規則だけを見る。
        var parent = await ParentAsync(master, data, Id(data));
        var uses = parent.Id is long accountId
            ? await store.UsesSubAccountAsync(accountId)
            : UsesSubAccountInSubmit(parent.Key, transactionData);

        if (uses is false)
        {
            throw new MasterRejectedException(
                "この勘定科目は「補助科目を使う」がオフなので、補助科目を作れません。"
                + "先に勘定科目の「補助科目を使う」をオンにしてください。");
        }
    }

    /// <summary>
    /// 同じ保存の中で作られている勘定科目が「補助科目を使う」か。見つからなければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>科目と補助科目を同じ保存で作る形（取込・API）では、親がまだ DB に無い</b>
    /// （仮の識別子。qa/01 C-08）。DB を引くだけだと <b>ADR-0038 §3 の 2 値が丸ごと消える</b>——
    /// この規則には DB 側の受け皿が無い（<c>uses_sub_account</c> を見るトリガは明細の側だけ）ので、
    /// <b>ここが唯一の守りである</b>（2026-09-09 の自己レビュー）。
    /// </remarks>
    private static bool? UsesSubAccountInSubmit(
        string? key, IReadOnlyList<ModuleSubmitData> transactionData)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        // **入れ物の名前ではなく、中身の名前で探す**（qa/02 R16-16 の型）。
        var parent = transactionData
            .SelectMany(d => d.Add.Concat(d.Update))
            .FirstOrDefault(d => d.Name == "Account" && IdText(d) == key);

        return parent is null ? null : Boolean(parent, "UsesSubAccount");
    }

    /// <summary>
    /// 差分に載っている識別子の字面（仮の識別子もそのまま）。
    /// </summary>
    /// <remarks>
    /// <b>読めない型なら止める</b>（<see cref="Id"/> と同じ理由）。ここは
    /// <b>自分の行より後ろの行も見る</b>ので、その行の <c>Id</c> をまだ検査していないことがある。
    /// </remarks>
    private static string? IdText(ModuleData data)
    {
        if (!data.Fields.TryGetValue("Id", out var field))
        {
            return null;
        }

        return field is IdFieldData id
            ? id.Value
            : throw UnreadableFieldException.For(data.Name, "Id", field);
    }

    /// <summary>
    /// 親を指している値の字面。<b>数値として読めるとは限らない</b>（仮の識別子）。
    /// </summary>
    /// <remarks>
    /// <b>型を 1 つに決め打ちしない</b>（<c>PartnerSubmitGate.Reference</c> と同じ戒め）。
    /// <b>読めない型は止める</b>——黙って保存されている親へ落ちると、
    /// 「移した先」ではなく「移す前」の範囲で重複を数えることになる。
    /// </remarks>
    private static string? ParentKey(CodedParent parent, ModuleData data)
    {
        if (!data.Fields.TryGetValue(parent.FieldName, out var value))
        {
            return null;
        }

        return value switch
        {
            LinkFieldData link => link.Value,
            IdFieldData reference => reference.Value,
            _ => throw UnreadableFieldException.For(data.Name, parent.FieldName, value),
        };
    }

    /// <summary>
    /// 触られた文字列の欄。<b>触られていなければ <c>null</c></b>（更新は差分しか届かない）。
    /// </summary>
    /// <remarks>
    /// <b>届いているのに読めない型なら止める</b>（<see cref="UnreadableFieldException"/>）。
    /// <c>null</c> を返すと「触られていない」と見分けがつかず、
    /// <b>欄の型が変わった日に検査が黙って素通しへ落ちる</b>
    /// （<c>PartnerSubmitGate.Reference</c> が名指しする形。2026-09-09 の自己レビュー）。
    /// </remarks>
    private static string? Text(ModuleData data, string field)
        => data.Fields.TryGetValue(field, out var value)
            ? value switch
            {
                TextFieldData text => text.Value ?? string.Empty,
                SelectFieldData select => select.Value ?? string.Empty,
                _ => throw UnreadableFieldException.For(data.Name, field, value),
            }
            : null;

    /// <summary>
    /// 触られた真偽の欄。<b>読めない型なら止める</b>（<see cref="Text"/> と同じ理由）。
    /// </summary>
    /// <remarks>
    /// 黙って <c>null</c> を返すと、<b>「全社共通」の 2 件目の検査だけが丸ごと素通し</b>になる。
    /// </remarks>
    private static bool? Boolean(ModuleData data, string field)
        => data.Fields.TryGetValue(field, out var value)
            ? value is BooleanFieldData boolean
                ? boolean.Value
                : throw UnreadableFieldException.For(data.Name, field, value)
            : null;

    /// <summary>
    /// 親（補助科目の勘定科目）の識別子。
    /// </summary>
    /// <remarks>
    /// <para><b>差分に無ければ、保存されている値を読み直す。</b> CLB は触った欄しか送らない（qa/01 F-12）ので、
    /// 「補助科目のコードだけを直す」という<b>いちばん普通の更新</b>で親が届かない。
    /// そこで <c>null</c> のまま重複を照会すると、<c>account_id = NULL</c> が
    /// <b>1 行も返さない</b>——関門は素通り、DB の一意索引が定型文で拒む、という
    /// qa/03 L-28 に戻る形になる（2026-09-09 の自己レビューで見つけた）。</para>
    /// <para><b>参照の型を 1 つに決め打ちしない</b>（<c>PartnerSubmitGate.Reference</c> と同じ理由）——
    /// 決め打ちにすると、フィールドの型が変わった日に検査が黙って素通しに落ち、
    /// フィクスチャが自分で同じ型を組むのでテストは緑のままになる。</para>
    /// </remarks>
    private async Task<(long? Id, string? Key)> ParentAsync(CodedMaster master, ModuleData data, long? id)
    {
        if (master.Parent is not CodedParent parent)
        {
            return (null, null);
        }

        var key = ParentKey(parent, data);
        if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return (parsed, key);
        }

        // 差分に無い（か仮の識別子で数値として読めない）ので、保存されている親を読む。
        if (id is not long stored)
        {
            return (null, key);
        }

        var row = await store.FindStoredAsync(master, stored, [parent.Column]);
        return long.TryParse(
                   Stored(row, parent.Column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var found)
            ? (found, key)
            : (null, key);
    }

    /// <summary>保存されている値の字面。NULL は空文字。</summary>
    private static string? Stored(IReadOnlyDictionary<string, object?>? row, string column)
        => row is null || !row.TryGetValue(column, out var value)
            ? null
            : string.Format(CultureInfo.InvariantCulture, "{0}", value);

    /// <summary>
    /// 保存しようとしている行の識別子。新規（仮の識別子）なら <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>「読めない型」と「まだ識別子が無い」を分ける。</b> 型で黙って <c>null</c> に落とすと、
    /// <b>更新が新規として扱われ、自分自身を重複と誤って断る</b>向きに倒れる
    /// （<c>@temporary:</c> の値が数値として読めないのは<b>正常</b>なので、そちらは <c>null</c> のまま）。
    /// </remarks>
    private static long? Id(ModuleData data)
    {
        if (!data.Fields.TryGetValue("Id", out var field))
        {
            return null;
        }

        if (field is not IdFieldData id)
        {
            throw UnreadableFieldException.For(data.Name, "Id", field);
        }

        return long.TryParse(id.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
