namespace BusinessApp.Partners.Server;

using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 取引先を保存するときの関門（docs/07 §1-2）。
/// </summary>
/// <remarks>
/// <para><b>DDL の CHECK が拒むものを、利用者の言葉で先に止める。</b>
/// 個人事業者に法人番号を入れた行と、自分自身を名寄せの親にした行は DB が拒むが、
/// そこまで進むと利用者に見えるのは DB の失敗である（docs/09_画面の原則.md §2 の「内部表現を出さない」）。
/// <b>DB の関門を外すのではない。</b> 最後に守るのは DB のままで、ここは手前に置く網である。</para>
/// <para><b>検査用数字（チェックデジット）を見るのはここだけである。</b>
/// DB は桁と字種しか見ない（docs/07 §1-2 の決定）。打ち間違えた番号を通すと、
/// それが名寄せの自然キーになる（同 §2-2）——別の法人に化けて束なるか、
/// 束なるべきものが束ならないかのどちらかで、どちらも画面には何も出ない。</para>
/// <para><b>画面のスクリプトでは検査しない</b>（ADR-0008）。取込（フェーズ 6）も同じ入口を通る。</para>
/// <para><b>名寄せの連鎖と循環（A→B→C・A→B→A）は見ない。</b> 行をまたぐ検査であり、
/// 解決の規則そのものがフェーズ 3 の仕事である（docs/07 §2-2）。ここで止めるのは、
/// 1 行だけで判定できる自己参照に限る。</para>
/// </remarks>
public sealed class PartnerSubmitGate(PartnerStore store)
{
    public const string ModuleName = "Partner";

    /// <summary>保存を包む。<paramref name="save"/> は CLB 本来の保存処理。</summary>
    public async Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        ArgumentNullException.ThrowIfNull(transactionData);
        ArgumentNullException.ThrowIfNull(save);

        foreach (var data in PartnersIn(transactionData))
        {
            await RejectAsync(data);
        }

        return await save();
    }

    /// <summary>
    /// 追加と更新の両方を見る。
    /// </summary>
    /// <remarks>
    /// <b>更新を見落とすと、正しい番号で作ってから壊した番号に直せる。</b>
    /// 追加だけを守る関門は、守っていないのと同じである。
    /// </remarks>
    private static IEnumerable<ModuleData> PartnersIn(IReadOnlyList<ModuleSubmitData> transactionData)
        => transactionData
            .SelectMany(d => d.Add.Concat(d.Update))
            .Where(d => d.Name == ModuleName);

    private async Task RejectAsync(ModuleData data)
    {
        RejectSelfParent(data);
        RejectMalformedCorporateNumber(data);
        await RejectSoleProprietorWithCorporateNumberAsync(data);
    }

    /// <summary>自分自身を名寄せの親にできない（DDL の CHECK と同じ規則）。</summary>
    /// <remarks>
    /// 新規作成の行はまだ識別子を持たないので、この形は起こりえない。
    /// <b>更新には必ず識別子が載る</b>ので、保存されている値を読みに行く必要はない。
    /// </remarks>
    private static void RejectSelfParent(ModuleData data)
    {
        if (Reference(data, "ParentPartner") is long parent && Id(data) is long id && parent == id)
        {
            throw new PartnerRejectedException(
                "その取引先自身を名寄せの親にはできません。別の取引先を選ぶか、空欄にしてください。");
        }
    }

    /// <summary>法人番号の書式と検査用数字。<b>空欄は通す</b>（任意。docs/07 §2-3）。</summary>
    private static void RejectMalformedCorporateNumber(ModuleData data)
    {
        // CLB は変更されたフィールドしか送ってこない（qa/01 F-11）。
        // 送られていない項目は「変えていない」なので、検査しない。
        if (Field<TextFieldData>(data, "CorporateNumber") is not TextFieldData field)
        {
            return;
        }

        var value = CorporateNumber.Normalize(field.Value);

        // **空欄は空文字ではなく NULL で保存する。**
        // DDL の CHECK は「NULL か、数字 13 桁」しか許さないので、空文字を書き戻すと
        // 「入っていた法人番号を消す」という正規の直し方が DB の失敗になる
        // （2026-08-26 の自己レビューで発見。qa/03）。CLB 自身も消した値は NULL で書く。
        if (value.Length == 0)
        {
            field.Value = null;
            return;
        }

        // **貼り付けで紛れ込んだ空白を落として保存する。** 落とさずに通すと、
        // 同じ番号が 2 通りの文字列で保存されて名寄せの突合が壊れる。
        field.Value = value;

        if (!CorporateNumber.IsWellFormed(value))
        {
            throw new PartnerRejectedException($"{CorporateNumber.FormatDescription}入力し直してください。");
        }

        if (!CorporateNumber.HasValidCheckDigit(value))
        {
            throw new PartnerRejectedException(
                "法人番号が正しくありません。打ち間違いの可能性があります。"
                + "国税庁の法人番号公表サイトで確かめて入力し直してください。");
        }
    }

    /// <summary>個人事業者に法人番号は指定されない（制度事実。docs/07 §1-2）。</summary>
    /// <remarks>
    /// <b>差分に無いほうは、保存されている値で補う。</b> 片方だけ直した保存で検査をやめると、
    /// 「先に個人事業者にしておいて、あとから法人番号を足す」で素通りする。
    /// </remarks>
    private async Task RejectSoleProprietorWithCorporateNumberAsync(ModuleData data)
    {
        var submittedType = Field<SelectFieldData>(data, "EntityType");
        var submittedNumber = Field<TextFieldData>(data, "CorporateNumber");

        // どちらも触っていない保存は、この組み合わせを新しく作れない。
        if (submittedType is null && submittedNumber is null)
        {
            return;
        }

        var stored = Id(data) is long id ? await store.FindProfileAsync(new PartnerId(id)) : null;

        var type = submittedType is null
            ? stored?.EntityType
            : DbValue.ToDefinedEnum<PartnerEntityType>(submittedType.Value);
        var number = submittedNumber is null
            ? CorporateNumber.Normalize(stored?.CorporateNumber)
            : CorporateNumber.Normalize(submittedNumber.Value);

        if (type == PartnerEntityType.SoleProprietor && number.Length > 0)
        {
            throw new PartnerRejectedException(
                $"{PartnerEntityType.SoleProprietor.DisplayName()}に法人番号は指定されません。"
                + "種別を確かめるか、法人番号を空欄にしてください。");
        }
    }

    /// <summary>保存しようとしている取引先の識別子。<b>新規なら仮の値が入る</b>ので、数値でなければ null。</summary>
    private static long? Id(ModuleData data)
        => long.TryParse(
            Field<IdFieldData>(data, "Id")?.Value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var id)
            ? id
            : null;

    /// <summary>
    /// 参照フィールド（<c>LinkFieldDesign</c>）が指している相手の識別子。
    /// </summary>
    /// <remarks>
    /// <b>データ側の型は <see cref="LinkFieldData"/> で、識別子は <c>Value</c> に入る</b>
    /// （<c>ModuleFieldData.Id</c> ではない）。取り違えると、いつも null を見て素通しする。
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
