namespace BusinessApp.AccountingCore.Server.Masters;

using System.Globalization;

using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// マスタを保存するときの関門（docs/04 §1 の B-1・B-2）。
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
        new("SubAccount", "sub_accounts", "補助科目コード", "account_id", "Account"),
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
        foreach (var data in transactionData.SelectMany(d => d.Add.Concat(d.Update)))
        {
            if (Coded.FirstOrDefault(m => m.ModuleName == data.Name) is CodedMaster master)
            {
                await RejectAsync(master, data);
            }
        }

        return await save();
    }

    private async Task RejectAsync(CodedMaster master, ModuleData data)
    {
        var id = Id(data);

        await RejectBadCodeAsync(master, data, id);
        await RejectSecondCompanyWideDepartmentAsync(master, data, id);
        await RejectInconsistentTaxCategoryAsync(master, data, id);
        await RejectSubAccountUnderPlainAccountAsync(master, data);
    }

    /// <summary>コードの書式と、大小を無視した重複（ADR-0047）。</summary>
    private async Task RejectBadCodeAsync(CodedMaster master, ModuleData data, long? id)
    {
        if (Text(data, "Code") is not string code)
        {
            return;
        }

        var normalized = MasterCode.Normalize(code);
        if (normalized.Length == 0)
        {
            throw new MasterRejectedException($"「{master.CodeLabel}」を入れてください。");
        }

        if (MasterCode.DescribeProblem(normalized) is string problem)
        {
            throw new MasterRejectedException(problem);
        }

        // **正規化した姿を差分に書き戻す。** 比べるときだけ落とすと、関門が「同じ」と通した値を
        // DB のトリガが「違う」と拒む（関門の受理集合が DB より広い。qa/03 L-14 の型）。
        if (data.Fields["Code"] is TextFieldData text)
        {
            text.Value = normalized;
        }

        var conflict = await store.FindConflictingCodeAsync(
            master, normalized, id, await ParentIdAsync(master, data, id));
        if (conflict is null)
        {
            return;
        }

        // **ぶつかった相手の字を見せる。** 大小だけが違うとき、字を見比べないと理由が分からない。
        var note = string.Equals(conflict, normalized, StringComparison.Ordinal)
            ? string.Empty
            : $"コードは大文字と小文字を区別しないので、「{conflict}」と同じものになります。";

        throw new MasterRejectedException(
            $"「{master.CodeLabel}」{normalized} は既に使われています。{note}別のコードを入れてください。");
    }

    /// <summary>「全社共通」の部門は 1 つだけ（docs/10 §9-1）。</summary>
    private async Task RejectSecondCompanyWideDepartmentAsync(CodedMaster master, ModuleData data, long? id)
    {
        if (master.ModuleName != "Department" || Boolean(data, "IsCompanyWide") is not true)
        {
            return;
        }

        if (await store.CompanyWideDepartmentExistsAsync(id))
        {
            throw new MasterRejectedException(
                "「全社共通」の部門は 1 つだけです。"
                + "いま「全社共通」になっている部門をオフにしてから、こちらをオンにしてください。");
        }
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
                "「課税区分」が課税のときは「税率区分」が要ります。標準税率か軽減税率かを選んでください。");
        }

        if (!taxable && hasRate)
        {
            throw new MasterRejectedException(
                "課税でない「課税区分」に「税率区分」は付けられません。「税率区分」を空にしてください。");
        }
    }

    /// <summary>
    /// 補助科目を使わない勘定科目の下に、補助科目は作れない（ADR-0038 §3 の 2 値）。
    /// </summary>
    /// <remarks>
    /// <b>2026-09-08 の回では明細の側しか塞いでいなかった</b>——
    /// マスタの画面からは、使わない設定の科目にも補助科目を足せた（docs/04 §1 の B-1）。
    /// </remarks>
    private async Task RejectSubAccountUnderPlainAccountAsync(CodedMaster master, ModuleData data)
    {
        if (master.ModuleName != "SubAccount"
            || await ParentIdAsync(master, data, Id(data)) is not long accountId)
        {
            return;
        }

        // **科目が実在しないときは、ここで止めない**——外部キーが拒む。
        // 実在の断りは 1 か所（DB）に置き、ここは 2 値の規則だけを見る。
        if (await store.UsesSubAccountAsync(accountId) is false)
        {
            throw new MasterRejectedException(
                "この「勘定科目」は補助科目を使わない設定です。"
                + "補助科目を作るには、先に勘定科目の「補助科目を使う」をオンにしてください。");
        }
    }

    /// <summary>触られた文字列の欄。<b>触られていなければ <c>null</c></b>（更新は差分しか届かない）。</summary>
    private static string? Text(ModuleData data, string field)
        => data.Fields.TryGetValue(field, out var value)
            ? value switch
            {
                TextFieldData text => text.Value ?? string.Empty,
                SelectFieldData select => select.Value ?? string.Empty,
                _ => null,
            }
            : null;

    /// <summary>触られた真偽の欄。</summary>
    private static bool? Boolean(ModuleData data, string field)
        => data.Fields.TryGetValue(field, out var value) && value is BooleanFieldData boolean
            ? boolean.Value
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
    private async Task<long?> ParentIdAsync(CodedMaster master, ModuleData data, long? id)
    {
        if (master.ParentFieldName is not string field || master.ParentColumn is not string column)
        {
            return null;
        }

        if (data.Fields.TryGetValue(field, out var value))
        {
            var raw = value switch
            {
                LinkFieldData link => link.Value,
                IdFieldData reference => reference.Value,
                _ => null,
            };

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        // 差分に無い（か読めない）ので、保存されている親を読む。新規なら親が要るので届いているはず。
        if (id is not long stored)
        {
            return null;
        }

        var row = await store.FindStoredAsync(master, stored, [column]);
        return long.TryParse(Stored(row, column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var found)
            ? found
            : null;
    }

    /// <summary>保存されている値の字面。NULL は空文字。</summary>
    private static string? Stored(IReadOnlyDictionary<string, object?>? row, string column)
        => row is null || !row.TryGetValue(column, out var value)
            ? null
            : string.Format(CultureInfo.InvariantCulture, "{0}", value);

    /// <summary>保存しようとしている行の識別子。新規（仮の識別子）なら <c>null</c>。</summary>
    private static long? Id(ModuleData data)
        => data.Fields.TryGetValue("Id", out var field) && field is IdFieldData id
           && long.TryParse(id.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// コードを持つマスタ 1 つ。
    /// </summary>
    /// <remarks>
    /// <b>設定を束ねるだけの型なので、レコードにしない</b>——値としての等価も <c>with</c> による複製も使わない。
    /// <b>使わない機能を型に持たせない</b>（持たせると、誰も呼ばない複製コンストラクタがカバレッジの穴になり、
    /// それを埋めるためだけのテストを書くことになる。ADR-0012 がそれを禁じている。
    /// <c>MasterMeaningGate.OneWayColumn</c> と同じ理由）。
    /// </remarks>
    /// <param name="moduleName">CLB のモジュール名。</param>
    /// <param name="table">DB の表（CLB の <c>DbTable</c> の写し）。</param>
    /// <param name="codeLabel">コードの欄の呼び名（CLB の <c>DisplayName</c> の写し）。</param>
    /// <param name="parentColumn">一意の範囲を絞る列（補助科目だけ）。</param>
    /// <param name="parentFieldName">同じものの CLB のフィールド名。</param>
    public sealed class CodedMaster(
        string moduleName,
        string table,
        string codeLabel,
        string? parentColumn = null,
        string? parentFieldName = null)
    {
        /// <summary>CLB のモジュール名。</summary>
        public string ModuleName { get; } = moduleName;

        /// <summary>DB の表。</summary>
        public string Table { get; } = table;

        /// <summary>コードの欄の呼び名。</summary>
        public string CodeLabel { get; } = codeLabel;

        /// <summary>一意の範囲を絞る列（補助科目だけ）。</summary>
        public string? ParentColumn { get; } = parentColumn;

        /// <summary>同じものの CLB のフィールド名。</summary>
        public string? ParentFieldName { get; } = parentFieldName;
    }
}
