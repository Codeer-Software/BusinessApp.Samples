namespace BusinessApp.Partners.Server;

using BusinessApp.Partners;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 適格請求書発行事業者の登録を保存するときの関門（docs/07 §3-2）。
/// </summary>
/// <remarks>
/// <para><b>DB は登録番号の書式を検査しない</b>と決めてある（同 §3-2）ので、ここが唯一の関門である。
/// 書式の壊れた番号を通すと、計上のときに<b>そのまま明細へ焼き込まれる</b>
/// （計上のときに会計側が明細へ写す。ADR-0018）。計上済みは不変（ADR-0004）なので、
/// あとから直せない。</para>
/// <para><b>画面のスクリプトでは検査しない</b>（ADR-0008）。取込（フェーズ 6）も同じ入口を通るので、
/// ここに置けば経路が増えても検査が外れない。</para>
/// <para>止めるのは 2 つだけである。書式と、<b>同じ取引先に同じ日から始まる登録が 2 件できること</b>。
/// 後者を通すと、写しを焼くときに「どれを写すか決められない」で計上が止まる
/// （<see cref="InvoiceRegistrationHistory.InEffectOn"/>）——
/// <b>入力の誤りを、関係の無い計上の場面で見せない</b>。</para>
/// </remarks>
public sealed class PartnerRegistrationSubmitGate(PartnerRegistrationStore store)
{
    public const string ModuleName = "PartnerInvoiceRegistration";

    /// <summary>保存を包む。<paramref name="save"/> は CLB 本来の保存処理。</summary>
    public async Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        ArgumentNullException.ThrowIfNull(transactionData);
        ArgumentNullException.ThrowIfNull(save);

        var registrations = RegistrationsIn(transactionData).ToList();

        foreach (var data in registrations)
        {
            await RejectAsync(data);
        }

        await RejectDuplicatesWithinAsync(registrations);

        return await save();
    }

    /// <summary>
    /// <b>同じ保存の中に</b>、同じ取引先の同じ日から始まる登録が 2 件ないかを見る。
    /// </summary>
    /// <remarks>
    /// <para><see cref="RejectDuplicateAsync"/> は<b>保存済みの行としか突き合わせられない</b>。
    /// 同じ保存で入る 2 件はどちらもまだ DB に無いので、片方ずつ見るかぎり両方が通る。</para>
    /// <para><b>DB も止められない。</b> <c>UNIQUE (partner_id, registration_no, valid_from)</c> は
    /// 登録番号まで含むので、<b>番号が違えば同じ日の 2 件が入る</b>。
    /// 入ってしまうと、計上のときに写しを焼く段で「どれを写すか決められない」で止まり、
    /// <b>入力の誤りが、関係の無い計上の場面で出る</b>。</para>
    /// <para><b>仮の識別子でも突き合わせられる。</b> 取引先の識別子を数値に直さず、
    /// 送られてきた文字列のまま鍵に使う——新規作成の取引先は仮の識別子だが、
    /// 同じ保存の中では同じ文字列になるので、それで同一性が判る。
    /// これが要るのは<b>登録の入力を取引先の詳細に置いたから</b>である（docs/07 §3-4）。
    /// 独立した一覧しか無かったときは、取引先は必ず先に保存されていた。</para>
    /// </remarks>
    private async Task RejectDuplicatesWithinAsync(IReadOnlyList<ModuleData> registrations)
    {
        var seen = new HashSet<(string Partner, DateOnly ValidFrom)>();

        foreach (var data in registrations)
        {
            if (Date(data, "ValidFrom") is not DateOnly validFrom)
            {
                continue;
            }

            if (await PartnerKeyAsync(data) is not string partner)
            {
                continue;
            }

            if (!seen.Add((partner, validFrom)))
            {
                throw new PartnerRegistrationRejectedException(
                    $"同じ取引先に、{validFrom:yyyy/MM/dd} から始まる登録を 2 件入力しています。"
                    + "どちらかの登録年月日を直してください。");
            }
        }
    }

    /// <summary>
    /// 突き合わせに使う取引先の鍵。<b>差分に無ければ、直している行の保存済みの値から引く。</b>
    /// </summary>
    /// <remarks>
    /// 数値に直さないのは、<b>新規作成の取引先が仮の識別子で来る</b>ためである
    /// （<see cref="Reference"/> の注記）。同じ保存の中で同じ相手を指していれば同じ文字列になる。
    /// </remarks>
    private async Task<string?> PartnerKeyAsync(ModuleData data)
    {
        if (Field<LinkFieldData>(data, "Partner")?.Value is string reference
            && !string.IsNullOrEmpty(reference))
        {
            return reference;
        }

        return Id(data) is long rowId && await store.FindPartnerOfAsync(rowId) is PartnerId stored
            ? stored.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// 追加と更新の両方を見る。
    /// </summary>
    /// <remarks>
    /// <b>更新を見落とすと、正しい番号で作ってから壊した番号に直せる。</b>
    /// 追加だけを守る関門は、守っていないのと同じである。
    /// </remarks>
    private static IEnumerable<ModuleData> RegistrationsIn(IReadOnlyList<ModuleSubmitData> transactionData)
        => transactionData
            .SelectMany(d => d.Add.Concat(d.Update))
            .Where(d => d.Name == ModuleName);

    private async Task RejectAsync(ModuleData data)
    {
        // CLB は変更されたフィールドしか送ってこない（qa/01 F-11）。
        // 送られていない項目は「変えていない」なので、検査しない。
        if (Field<TextFieldData>(data, "RegistrationNo") is TextFieldData field)
        {
            if (!InvoiceRegistrationNumber.IsWellFormed(field.Value))
            {
                throw new PartnerRegistrationRejectedException(
                    $"登録番号の形が違います。{InvoiceRegistrationNumber.FormatDescription}"
                    + "入力し直してください。");
            }

            // **貼り付けで紛れ込んだ空白を落として保存する。** 落とさずに通すと、
            // 同じ番号が 2 通りの文字列で保存されて突合が壊れる（小文字を弾いたのと同じ理由）。
            field.Value = InvoiceRegistrationNumber.Normalize(field.Value);
        }

        await RejectDuplicateAsync(data);
    }

    private async Task RejectDuplicateAsync(ModuleData data)
    {
        if (Date(data, "ValidFrom") is not DateOnly validFrom)
        {
            return;
        }

        // **取引先が差分に無ければ、直している行から引く。**
        // ここで諦めると、画面で登録年月日だけを直した保存が検査を素通りする（qa/02）。
        var id = Id(data);
        var partnerId = Reference(data, "Partner") is long fromField
            ? new PartnerId(fromField)
            : id is long rowId ? await store.FindPartnerOfAsync(rowId) : null;

        if (partnerId is not PartnerId partner)
        {
            return;
        }

        // 自分自身は数えない（番号ではなく行の識別子で見分ける）。
        if (await store.HasOtherRegistrationFromAsync(partner, validFrom, id))
        {
            throw new PartnerRegistrationRejectedException(
                $"この取引先には {validFrom:yyyy/MM/dd} から始まる登録が既にあります。"
                + "登録年月日を直すか、先にある登録を直してください。");
        }
    }

    /// <summary>保存しようとしている登録の識別子。<b>新規なら仮の値が入る</b>ので、数値でなければ null。</summary>
    private static long? Id(ModuleData data)
        => long.TryParse(
            Field<IdFieldData>(data, "Id")?.Value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var id)
            ? id
            : null;

    private static DateOnly? Date(ModuleData data, string name)
        => Field<DateFieldData>(data, name)?.Value;

    /// <summary>
    /// 参照フィールド（<c>LinkFieldDesign</c>）が指している相手の識別子。
    /// </summary>
    /// <remarks>
    /// <para><b>データ側の型は <see cref="LinkFieldData"/> で、識別子は <c>Value</c> に入る</b>
    /// （<c>ModuleFieldData.Id</c> ではない。名前が似ているので取り違えやすい）。
    /// 取り違えると<b>いつも null になり、二重登録の検査が丸ごと素通りする</b>。</para>
    /// <para><b>新規作成の相手を指しているときは仮の識別子が入る</b>ので、数値として読めない。
    /// その場合は <c>null</c>——取引先は先に登録されている前提なので、
    /// 仮の識別子が来る経路をここでは扱わない。</para>
    /// </remarks>
    private static long? Reference(ModuleData data, string name)
        => long.TryParse(
            Field<LinkFieldData>(data, name)?.Value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var id)
            ? id
            : null;

    private static T? Field<T>(ModuleData data, string name) where T : FieldDataBase
        => data.Fields.TryGetValue(name, out var field) ? field as T : null;
}
