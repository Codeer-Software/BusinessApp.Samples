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

        // **この保存で登録年月日が動く行**は、保存済みの値で数えない（下の RejectDuplicateAsync）。
        // 数えると、2 行の日付を入れ替える保存が「既にあります」で誤って止まる。
        // **日付が差分に無い行は入れない**——その行の日付は動かないので、
        // 新しく入る行はそれと衝突してはいけない。
        var moving = registrations
            .Where(d => Date(d, "ValidFrom") is not null)
            .Select(Id)
            .OfType<long>()
            .ToHashSet();

        foreach (var data in registrations)
        {
            await RejectAsync(data, moving);
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
    /// <b>これは登録の入力を取引先の詳細に置くための下ごしらえ</b>である（docs/07 §3-4。
    /// 移設そのものはフェーズ 2.5 の C）。独立した一覧しか無いいまでも、
    /// <b>同じ取引先に 2 件を同時に足す経路は取込（フェーズ 6）で開く</b>ので、無駄にはならない。</para>
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
                    $"この取引先には {validFrom:yyyy/MM/dd} から始まる登録を 2 件入力しています。"
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
        if (SubmittedPartner(data) is string reference)
        {
            return reference;
        }

        return Id(data) is long rowId && await store.FindPartnerOfAsync(rowId) is PartnerId stored
            ? Key(stored.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>
    /// 送られてきた取引先の識別子。<b>参照フィールドでも識別子フィールドでも読む。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>型を 1 つに決め打ちしない。</b> いまは <see cref="LinkFieldData"/> だが、
    /// 登録の入力を取引先の詳細に移すと親 FK は <c>IdFieldDesign</c> になる
    /// （ヘッダ＋明細の正典。qa/01 D-17）。決め打ちにすると、その日に
    /// <b>この関門が丸ごと素通しに落ちる——しかもテストは緑のまま</b>である
    /// （フィクスチャが自分で <see cref="LinkFieldData"/> を組むため。2026-08-31 の自己レビュー）。</para>
    /// <para><b>読めない型で来たときは null を返す</b>（＝突き合わせの対象にしない）。
    /// CLB は宣言した型でしか送らないので、ここに来るのは API を直に叩いた経路だけである。</para>
    /// </remarks>
    private static string? SubmittedPartner(ModuleData data)
        => data.Fields.TryGetValue("Partner", out var field)
            ? Key(field switch
            {
                LinkFieldData link => link.Value,
                IdFieldData id => id.Value,
                _ => null,
            })
            : null;

    /// <summary>
    /// 突き合わせに使う形。<b>数値として読めるものは数値に正規化する。</b>
    /// </summary>
    /// <remarks>
    /// 送られてきた文字列と、保存済みの値から作った文字列を同じ鍵に混ぜるので、
    /// <c>"007"</c> と <c>"7"</c> が別物にならないようにする。
    /// 数値として読めないもの（新規作成の仮の識別子）はそのまま使う——
    /// 同じ保存の中では同じ文字列になるので、それで同一性が判る。
    /// </remarks>
    private static string? Key(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        return long.TryParse(
            reference,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var id)
            ? id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : reference;
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

    private async Task RejectAsync(ModuleData data, IReadOnlySet<long> moving)
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

        await RejectDuplicateAsync(data, moving);
    }

    private async Task RejectDuplicateAsync(ModuleData data, IReadOnlySet<long> moving)
    {
        if (Date(data, "ValidFrom") is not DateOnly validFrom)
        {
            return;
        }

        // **取引先が差分に無ければ、直している行から引く。**
        // ここで諦めると、画面で登録年月日だけを直した保存が検査を素通りする（qa/02）。
        var id = Id(data);
        var partnerId = ReferencedPartner(data) is long fromField
            ? new PartnerId(fromField)
            : id is long rowId ? await store.FindPartnerOfAsync(rowId) : null;

        if (partnerId is not PartnerId partner)
        {
            return;
        }

        // **自分自身と、同じ保存で日付が動く行は数えない**（番号ではなく行の識別子で見分ける）。
        // 動く行の保存済みの値は、この保存が終わった時点でもう無い。
        var occupied = await store.FindRegistrationIdsFromAsync(partner, validFrom);
        if (occupied.Any(rowId => rowId != id && !moving.Contains(rowId)))
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
    /// 送られてきた取引先の識別子を、<b>保存済みの行と突き合わせられる数値</b>として読む。
    /// </summary>
    /// <remarks>
    /// <b>新規作成の相手を指しているときは仮の識別子</b>なので数値として読めない。その場合は
    /// <c>null</c>——保存済みの行との突き合わせには使えないからである
    /// （同じ保存の中の突き合わせは <see cref="PartnerKeyAsync"/> が文字列のまま行う）。
    /// 読む場所は <see cref="SubmittedPartner"/> 1 か所に寄せてある。
    /// </remarks>
    private static long? ReferencedPartner(ModuleData data)
        => long.TryParse(
            SubmittedPartner(data),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var id)
            ? id
            : null;

    private static T? Field<T>(ModuleData data, string name) where T : FieldDataBase
        => data.Fields.TryGetValue(name, out var field) ? field as T : null;
}
