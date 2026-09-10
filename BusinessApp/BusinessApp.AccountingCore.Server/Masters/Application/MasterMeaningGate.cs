namespace BusinessApp.AccountingCore.Server.Masters.Application;

using System.Globalization;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.Repository.Data;
using BusinessApp.AccountingCore.Server.Masters.Infrastructure;

/// <summary>
/// 使用中のマスタは、意味を変えられない（ADR-0038）。
/// </summary>
/// <remarks>
/// <para><b>計上済みの仕訳明細が 1 行でも参照しているマスタの行は、意味を決める列を変えられない</b>（ADR-0038）。
/// 関門がここにある理由は同 §4——画面から踏める経路なので、利用者の言葉で断る関門が本体で、
/// DDL のトリガ（<c>trg_*_meaning_frozen_when_posted</c>・<c>trg_*_no_replace_used_*</c>）は取込・CLI・SQL の直打ちへの最後の守り。</para>
/// <para><b>どの列が「意味を決める列」かは <see cref="Guarded"/> が持つ</b>——そこに無い列はこの関門を通る
/// （<b>変えてよいと決まった列</b>は docs/12 §2。まだどちらとも決めていない列もある）。
/// 関門とトリガとデザイン JSON が同じ列を指していることは <c>MasterMeaningGateTests</c> が突き合わせる（docs/20 §4）。
/// 文言の作法は docs/21 §2-6。</para>
/// <para><b>触った列だけを見て、保存されている値と比べる。</b> CLB は変更されたフィールドしか送らない（qa/01 F-12）が、
/// 同じ値に戻した保存まで拒むと、他の欄を直したいだけの利用者を止めることになる。</para>
/// <para><b>新規の行は見ない。</b> 生まれたばかりの行を計上済みの明細が参照していることはない。</para>
/// </remarks>
public sealed class MasterMeaningGate(MasterUsageStore store)
{
    /// <summary>仮の識別子の印（新規作成の行。qa/01 C-08）。</summary>
    private const string TemporaryIdPrefix = "@temporary:";

    /// <summary>守るマスタと、意味を決める列（<b>現在形の正典は docs/12 §2 の表</b>。なぜその列かは ADR-0038 §2）。</summary>
    /// <remarks>
    /// <b>ラベルは CLB の <c>DisplayName</c>、列名は <c>DbColumn</c> の写しである</b>（docs/20 §4 の「已むを得ない重複」）。
    /// 差し戻しの文言に画面と同じ語を出すためで、設計 JSON を実行時に読む依存を持ち込まない。
    /// <b>列の並びは詳細画面の並び</b>——利用者が画面を上から見直す順に名指しする。
    /// 写しと並びがずれていないことは <c>MasterMeaningGateTests</c> がデザイン JSON と DDL のトリガに突き合わせる。
    /// </remarks>
    public static readonly IReadOnlyList<GuardedMaster> Guarded =
    [
        new("Account", "勘定科目", "accounts", "account_id",
            [new("Code", "code", "科目コード"),
             new("Category", "category", "科目区分"),
             new("UsesSubAccount", "uses_sub_account", "補助科目を使う"),
             new("IsContra", "is_contra", "評価勘定")],
            [new(new("RequiresPartner", "requires_partner", "取引先を要する"),
                 "オフにしている間に計上した明細は、取引先が空のまま帳簿に残ってしまいます",
                 "この勘定科目を使う明細には「取引先」を選んでください")]),
        new("SubAccount", "補助科目", "sub_accounts", "sub_account_id",
            [new("Account", "account_id", "勘定科目"),
             new("Code", "code", "補助科目コード")]),
        new("Department", "部門", "departments", "department_id",
            [new("Code", "code", "部門コード"),
             new("IsCompanyWide", "is_company_wide", "全社共通")]),
        new("TaxCategory", "税区分", "tax_categories", "tax_category_id",
            [new("Code", "code", "税区分コード"),
             new("TaxationType", "taxation_type", "課税区分"),
             new("RateKind", "rate_kind", "税率区分")]),
        new("Partner", "取引先", "partners", "partner_id",
            [new("Code", "code", "取引先コード")],
            EntryColumn: "partner_id",
            UsageUnit: "振替伝票",
            UsageCounter: "枚"),
    ];

    /// <summary>部品の組み立て。</summary>
    public static MasterMeaningGate Create(IDbAccessor dbAccessor, string dataSourceName)
        => new(new MasterUsageStore(dbAccessor, dataSourceName));

    /// <summary>保存を包む。<paramref name="save"/> は CLB 本来の保存処理。</summary>
    public async Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        ArgumentNullException.ThrowIfNull(transactionData);
        ArgumentNullException.ThrowIfNull(save);

        // **入れ物の名前ではなく、中身の名前で担当を決める**（qa/02 R16-16 の型。
        // 親子の保存は 1 つの ModuleSubmitData に混ざって届く——qa/01 F-11）。
        // 更新だけを見る——新規の行は計上済みの明細から参照されえない。
        foreach (var data in transactionData.SelectMany(d => d.Update))
        {
            if (Guarded.FirstOrDefault(g => g.ModuleName == data.Name) is GuardedMaster master)
            {
                await RejectChangedMeaningAsync(master, data);
            }
        }

        return await save();
    }

    private async Task RejectChangedMeaningAsync(GuardedMaster master, ModuleData data)
    {
        var touched = master.Columns.Concat(master.OneWay.Select(o => o.Column))
            .Where(c => data.Fields.ContainsKey(c.FieldName)).ToList();
        if (touched.Count == 0)
        {
            return;
        }

        // **識別子が読めない更新は止める**（値の側と同じく fail-closed）。更新には必ず数値の識別子が載る。
        // 仮の識別子（新規作成の行が更新の側に混ざった形）だけは通す——計上済みの明細から参照されえない。
        // 画面からは作れない壊れた要求なので、開き直して直る保証は無い——SaveFailureMessage と同じ逃げ道を付ける。
        var id = Id(data);
        if (id is null)
        {
            if (IsTemporary(data))
            {
                return;
            }

            throw new MasterRejectedException(
                "登録する行を特定できませんでした。画面を開き直してもう一度お試しください。"
                + "同じことが続くときは、管理者にお知らせください。");
        }

        var stored = await store.FindStoredAsync(master, id.Value, touched);
        if (stored is null)
        {
            return;
        }

        // ラベルは鉤括弧で括る——「補助科目を使う」のような動詞句のラベルは、裸だと文に溶ける。
        var changed = touched
            .Where(c => !string.Equals(Normalize(stored[c.Column]), Submitted(data.Fields[c.FieldName]), StringComparison.Ordinal))
            .ToList();
        if (changed.Count == 0)
        {
            return;
        }

        var used = await store.CountPostedLinesAsync(master, id.Value);
        if (used == 0)
        {
            return;
        }

        // **一方通行の列は、緩める向きだけを拒む**（docs/10 §6-2）。
        // **意味を決める列の断りを先に返す**（docs/21 §2-6 の (a)——マスタの関門は理由を 1 つだけ返す）。
        // あちらは<b>直す手立てが無い</b>（新しい行を作るしかない）が、こちらは
        // **オンに戻せば通る**ので、先に重いほうを見せる。両方を触った保存は 1 度で全部は言えない。
        var frozen = changed.Where(c => !master.OneWay.Any(o => o.Column == c)).ToList();
        if (frozen.Count == 0)
        {
            // **ここに来る `changed` は一方通行の列だけである**（意味を決める列は上で抜いた）。
            // **「1」以外はすべて緩めたと見なす**（fail-closed）。読めない型・空の真偽で
            // 素通りすると、フィールドの型が変わった日にこの規則だけが静かに消える
            // （Submitted の注記と同じ理由。qa/02 R26-03）。
            // **変わった列を 1 つずつ見る**——1 度の保存で片方をオン・片方をオフにされても取りこぼさない。
            if (changed.FirstOrDefault(c => Submitted(data.Fields[c.FieldName]) != "1") is GuardedColumn loosened)
            {
                throw new MasterRejectedException(
                    Loosening(master, master.OneWay.Single(o => o.Column == loosened), used));
            }

            return;
        }

        changed = frozen;

        // 文言の形は ADR-0038 §4——**何件あるか**と**次に何をすればよいか**を入れる。
        // 理由と結果は「〜ので」で 1 文にする（取引先・仕訳の関門と同じ形）。
        // 数える単位は「仕訳明細」（伝票ではない。ADR-0017）。**「仕訳」を単独で画面に出さない**（同 ADR）ので、
        // 締めは「以後の振替伝票ではそちらを選ぶ」と言う——部門・税区分は「記帳する先」ではなく明細で選ぶものなので、
        // 4 マスタで成り立つ動詞にする。
        throw new MasterRejectedException(
            $"この{master.Label}は計上済みの{master.UsageUnit} {used.ToString("N0", CultureInfo.InvariantCulture)} {master.UsageCounter}で使われているので、"
            + $"{string.Join("・", changed.Select(c => $"「{c.Label}」"))}は変えられません。"
            + $"新しい{master.Label}を作って、以後の振替伝票ではそちらを選んでください。");
    }

    /// <summary>
    /// 一方通行の列を緩めようとしたときの断り。
    /// </summary>
    /// <remarks>
    /// <para><b>「変えられません」とは言わない。</b> 厳しくする向き（オフ → オン）はいつでも通るので、
    /// 両方できないと読まれると、規則を採り入れようとする利用者まで止めてしまう（docs/21 §2-3）。</para>
    /// <para><b>「オンにするのはいつでもできる」はここに書かない。</b> それを知りたいのは
    /// <b>これからオンにする人</b>で、この断りに出会うのはオフを押した人である——
    /// 置き場所は画面の注記のほう（勘定科目の詳細）。</para>
    /// </remarks>
    private static string Loosening(GuardedMaster master, OneWayColumn column, long used)
        => $"この{master.Label}は計上済みの{master.UsageUnit} {used.ToString("N0", CultureInfo.InvariantCulture)} {master.UsageCounter}で使われているので、"
           + $"「{column.Column.Label}」をオフにできません。"
           + $"{column.Harm}。"
           + $"{column.Instead}。";

    /// <summary>
    /// 差分に載った値を、保存されている値と比べられる字面にする。
    /// </summary>
    /// <remarks>
    /// <para><b>読めない型・空の真偽は「変えた」と見なす</b>（<c>null</c> を返し、どの保存値とも一致しない）。
    /// 差分に載っている以上その欄は触られており、読めないからと素通しにすると、
    /// フィールドの型が変わった日に関門ごと消える（取引先の関門で実際に起きた型。qa/02 R26-03）。</para>
    /// <para><b>文字列は前後の空白を落として、差分に書き戻す。</b> 画面は空白をそのまま送る
    /// （<c>ShouldTrimAfterEdit: false</c>）ので、比べるときだけ落とすと「同じ」と通した値を
    /// DDL のトリガが「違う」と拒む（関門の受理集合が DB より広い。qa/03 L-14 の型）。
    /// 取引先の法人番号と同じ作法である。</para>
    /// </remarks>
    private static string? Submitted(FieldDataBase field)
        => field switch
        {
            TextFieldData text => text.Value = Text(text.Value),
            SelectFieldData select => Text(select.Value),
            LinkFieldData link => Text(link.Value),
            // 真偽は DB では 0/1 で持つ（CHECK (x IN (0, 1))）
            BooleanFieldData boolean => boolean.Value is bool value ? (value ? "1" : "0") : null,
            _ => null,
        };

    /// <summary>空欄と前後の空白を落とした字面。NULL と空文字を同じ「無い」に倒す。</summary>
    private static string Text(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>
    /// 保存されている値の字面。整数・文字列・NULL を同じ物差しに乗せる
    /// （<c>string.Format</c> は <c>null</c> も <c>DBNull</c>（<c>ToString()</c> が空文字）も空文字にし、
    /// 整数を不変カルチャで書く。<c>SqliteDbAccessor</c> が返すのは <c>DBNull</c> のほうである）。
    /// </summary>
    private static string Normalize(object? stored)
        => string.Format(CultureInfo.InvariantCulture, "{0}", stored).Trim();

    /// <summary>保存しようとしている行の識別子。数値でなければ <c>null</c>。</summary>
    private static long? Id(ModuleData data)
        => data.Fields.TryGetValue("Id", out var field) && field is IdFieldData id
           && long.TryParse(id.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>新規作成の行か（仮の識別子を持つ）。</summary>
    private static bool IsTemporary(ModuleData data)
        => data.Fields.TryGetValue("Id", out var field) && field is IdFieldData id
           && id.Value is string value && value.StartsWith(TemporaryIdPrefix, StringComparison.Ordinal);
}
