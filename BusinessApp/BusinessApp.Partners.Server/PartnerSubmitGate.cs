namespace BusinessApp.Partners.Server;

using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 取引先を保存するときの関門（docs/13 §1-2）。
/// </summary>
/// <remarks>
/// <para><b>DDL の CHECK が拒むものを、利用者の言葉で先に止める。</b>
/// 個人事業者に法人番号を入れた行と、自分自身を名寄せの親にした行は DB が拒むが、
/// そこまで進むと利用者に見えるのは DB の失敗である（docs/21_画面の原則.md §2 の「内部表現を出さない」）。
/// <b>DB の関門を外すのではない。</b> 最後に守るのは DB のままで、ここは手前に置く網である。</para>
/// <para><b>検査用数字（チェックデジット）を見るのはここだけである。</b>
/// DB は桁と字種しか見ない（docs/13 §1-2 の決定）。打ち間違えた番号を通すと、
/// それが名寄せの自然キーになる（同 §2-2）——別の法人に化けて束なるか、
/// 束なるべきものが束ならないかのどちらかで、どちらも画面には何も出ない。</para>
/// <para><b>画面のスクリプトでは検査しない</b>（ADR-0008）。取込（フェーズ 6）も同じ入口を通る。</para>
/// <para><b>名寄せの親は深さ 1 の森に固定する</b>（ADR-0028 §2。2026-08-31 に足した）。
/// 連鎖（A→B→C）を作れなくすれば、循環（A→B→A）も構造的に作れない。
/// 同じ規則を DDL のトリガも持っている——ここは手前に置く網である。</para>
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
        await RejectBadCodeAsync(data);
        RejectMalformedCorporateNumber(data);
        await RejectSoleProprietorWithCorporateNumberAsync(data);
        await RejectMismatchedParentAsync(data);
        await RejectMismatchedChildrenAsync(data);
        await RejectDeepParentAsync(data);
    }

    /// <summary>
    /// コードの書式と、大小を無視した重複（ADR-0047）。
    /// </summary>
    /// <remarks>
    /// <para><b>書式の判定は <see cref="MasterCode"/> が持つ。</b> 会計コアのマスタと同じ規則で、
    /// 同じ実装を両方が参照する（docs/12 §2-1）。<b>取引先だけを載せるホストでも効く</b>——
    /// <c>BusinessApp.ServerSupport</c> は依存ゼロで、この部品が既に参照している。</para>
    /// <para><b>正規化した姿を差分に書き戻す。</b> 比べるときだけ落とすと、関門が「同じ」と通した値を
    /// DDL のトリガが「違う」と拒む（関門の受理集合が DB より広い。qa/03 L-14 の型）。</para>
    /// <para><b>型を決め打ちして黙って抜けない。</b> <c>is not TextFieldData</c> で帰る形にすると、
    /// <b>欄の型が変わった日に書式も重複も丸ごと素通しに落ちる</b>——しかもフィクスチャが自分で
    /// <see cref="TextFieldData"/> を組むのでテストは緑のままである。<see cref="Reference"/> が
    /// 同じ戒めを書いているのに、こちらに残っていた（2026-09-09 の自己レビュー）。
    /// <b>届いているのに読めないなら、素通しではなく止める</b>——その判定は <see cref="Field{T}"/> が持つ。</para>
    /// </remarks>
    private async Task RejectBadCodeAsync(ModuleData data)
    {
        if (Field<TextFieldData>(data, "Code") is not TextFieldData field)
        {
            return;
        }

        var code = MasterCode.Normalize(field.Value);
        if (code.Length == 0)
        {
            throw new PartnerRejectedException("「取引先コード」を入れてください。");
        }

        if (MasterCode.DescribeProblem("取引先コード", code) is string problem)
        {
            throw new PartnerRejectedException(problem);
        }

        field.Value = code;

        var conflict = await store.FindConflictingCodeAsync(code, Id(data) is long id ? new PartnerId(id) : null);
        if (conflict is null)
        {
            return;
        }

        // **ぶつかった相手の字を見せる。** 大小だけが違うとき、字を見比べないと理由が分からない。
        var reason = string.Equals(conflict, code, StringComparison.Ordinal)
            ? "既に使われています。"
            : $"大文字と小文字を区別しないので、既にある「{conflict}」と同じコードになります。";

        throw new PartnerRejectedException(
            $"「取引先コード」の「{code}」は{reason}別のコードを入れてください。");
    }

    /// <summary>
    /// 個人事業者と、法人・人格のない社団等は互いに親にできない（ADR-0028 §1）。
    /// </summary>
    /// <remarks>
    /// <para><b>すべての種別違いを止めるのではない。</b> 適格請求書発行事業者公表システムの人格区分は
    /// 「1 個人／2 法人（人格のない社団等を含む）」の 2 値で、本プロジェクトの 4 値より粗い。
    /// 法人と人格のない社団等を止めると、<b>取込（フェーズ 6）で入った行どうしが機械的に弾かれる</b>
    /// ——取込元がその 2 つを区別できないからである。「その他」は人格を何も表していない。</para>
    /// <para><b>止めることで生まれるのは「束ね漏れ」の側である。</b> 経過措置の上限は
    /// 「一の免税事業者等ごと」の合計に掛かるので、束ね漏れは控除が過大になる方向
    /// （＝過少申告のリスク）。それでも止めるのは、<b>種別が食い違う組は、束ねるべきなら
    /// 種別のどちらかが誤っている</b>からである——関門は「束ねるな」ではなく「先に種別を直せ」と言っている。</para>
    /// <para><b>差分に無いほうは保存されている値で補う</b>（他の検査と同じ作法）。
    /// 種別だけを直した保存で検査をやめると、「先に親を付けておいて、あとから種別を食い違わせる」で素通りする。</para>
    /// <para><b>親の側を直す方向も見る。</b> 子から親を見るだけだと、
    /// 「子を持つ取引先の種別を変えて食い違わせる」保存が素通りする——
    /// ADR-0028 の帰結が「親と子のどちらを直す場合も検査が要る」と名指ししていた方向である。</para>
    /// </remarks>
    private async Task RejectMismatchedParentAsync(ModuleData data)
    {
        var submittedType = Field<SelectFieldData>(data, "EntityType");
        var submittedParent = Submitted(data, "ParentPartner");

        // どちらも触っていない保存は、この組み合わせを新しく作れない。
        if (submittedType is null && !submittedParent)
        {
            return;
        }

        var stored = Id(data) is long id ? await store.FindProfileAsync(new PartnerId(id)) : null;

        var type = submittedType is null
            ? stored?.EntityType
            : DbValue.ToDefinedEnum<PartnerEntityType>(submittedType.Value);

        // **親は差分から読む。差分に無ければ保存されている親を読み直す**——
        // 種別だけを直した保存でも、保存済みの親と突き合わせる必要がある。
        var parentId = submittedParent
            ? (Reference(data, "ParentPartner") is long p ? new PartnerId(p) : null)
            : (Id(data) is long own ? (await store.FindLineageAsync(new PartnerId(own)))?.ParentId : null);

        if (parentId is not PartnerId parent
            || await store.FindProfileAsync(parent) is not PartnerProfile parentProfile)
        {
            return;
        }

        if (AreIncompatible(type, parentProfile.EntityType))
        {
            throw MismatchedParent();
        }
    }

    /// <summary>
    /// <b>子を持つ取引先の種別を変えて、食い違わせていないか</b>（ADR-0028 §1 の親の側）。
    /// </summary>
    /// <remarks>
    /// 種別を触っていない保存は見ない——触っていない行に分類を強制しない（docs/13 §1-2）。
    /// </remarks>
    private async Task RejectMismatchedChildrenAsync(ModuleData data)
    {
        if (Field<SelectFieldData>(data, "EntityType") is not SelectFieldData submitted
            || Id(data) is not long id)
        {
            return;
        }

        var type = DbValue.ToDefinedEnum<PartnerEntityType>(submitted.Value);
        foreach (var child in await store.FindChildEntityTypesAsync(new PartnerId(id)))
        {
            if (AreIncompatible(type, child))
            {
                throw MismatchedParent();
            }
        }
    }

    /// <summary>種別の食い違いの差し戻し。<b>親から見ても子から見ても同じ文言で断る。</b></summary>
    private static PartnerRejectedException MismatchedParent()
        => new($"{PartnerEntityType.SoleProprietor.DisplayName()}と法人・人格のない社団等は、"
               + "互いに名寄せの親にできません。同じ事業者なら、どちらかの種別が誤っています。");

    /// <summary>
    /// 個人事業者と法人系の組か。<b>未分類（<c>null</c>）と「その他」は通す。</b>
    /// </summary>
    private static bool AreIncompatible(PartnerEntityType? one, PartnerEntityType? other)
        => (one == PartnerEntityType.SoleProprietor && IsCorporateLike(other))
            || (other == PartnerEntityType.SoleProprietor && IsCorporateLike(one));

    private static bool IsCorporateLike(PartnerEntityType? type)
        => type is PartnerEntityType.Corporation or PartnerEntityType.UnincorporatedAssociation;

    /// <summary>
    /// 名寄せの親は必ず根である（深さ 1 の森。ADR-0028 §2）。
    /// </summary>
    /// <remarks>
    /// <para>止めるのは 2 つ——<b>選んだ親が、さらに親を持っている</b>ことと、
    /// <b>自分が既に誰かの親になっているのに、自分に親を付けようとしている</b>こと。
    /// この 2 つで循環は構造的に消える（深さ 2 以上が作れないため）。</para>
    /// <para><b>親を触っていない保存は見ない。</b> 深さは親を付け替えたときにしか変わらず、
    /// 触っていない行まで見ると、既に矛盾している行（トリガより前に入ったもの）を
    /// <b>他の項目を直すだけでも保存できなくする</b>。</para>
    /// <para><b>新規作成の相手を親にしている場合は見ない</b>——仮の識別子は数値として読めず、
    /// そもそも生まれたばかりの行は親を持てない。</para>
    /// </remarks>
    private async Task RejectDeepParentAsync(ModuleData data)
    {
        // **親を触っていない保存は見ない。** 深さは親を付け替えたときにしか変わらない。
        // 空欄に戻す保存（Reference が null）も通す——浅くする操作である。
        if (!Submitted(data, "ParentPartner")
            || Reference(data, "ParentPartner") is not long parent)
        {
            return;
        }

        // **読めない相手は見ない。** 指した相手が居なければ、深さを判定する材料が無い
        // （外部キーが最後に拒む）。同じ保存で作られる相手は仮の識別子なので Reference が落とす。
        if (await store.FindLineageAsync(new PartnerId(parent)) is PartnerLineage parentLineage
            && parentLineage.ParentId is not null)
        {
            throw new PartnerRejectedException(
                "名寄せの親には、さらに親を持つ取引先を選べません。"
                + "同じ事業者なら、その取引先の親を選んでください。");
        }

        // 自分が誰かの親なら、自分に親は付けられない。
        if (Id(data) is not long own
            || await store.FindLineageAsync(new PartnerId(own)) is not PartnerLineage lineage
            || !lineage.HasChildren)
        {
            return;
        }

        throw new PartnerRejectedException(
            "この取引先は他の取引先の名寄せの親になっているので、親を付けられません。"
            + "先に、子になっている取引先の親を付け替えてください。");
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

    /// <summary>法人番号の書式と検査用数字。<b>空欄は通す</b>（任意。docs/13 §2-3）。</summary>
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

        // 判定と文言は CorporateNumber が 1 か所で持つ（自社情報の関門と同じものを通す）。
        if (CorporateNumber.DescribeProblem(value) is string problem)
        {
            throw new PartnerRejectedException(problem);
        }
    }

    /// <summary>個人事業者に法人番号は指定されない（制度事実。docs/13 §1-2）。</summary>
    /// <remarks>
    /// <b>差分に無いほうは、保存されている値で補う。</b> 片方だけ直した保存で検査をやめると、
    /// 「先に個人事業者にしておいて、あとから法人番号を足す」で素通りする。
    /// </remarks>
    private async Task RejectSoleProprietorWithCorporateNumberAsync(ModuleData data)
    {
        var submittedType = Field<SelectFieldData>(data, "EntityType");
        var submittedNumber = Field<TextFieldData>(data, "CorporateNumber");

        // どちらも触っていない保存は、この組み合わせを新しく作れない。
        //
        // **ここを消しても結果が変わらない**（ミューテーションが生き残る）。理由は
        // <b>DDL の CHECK が「個人事業者 ＋ 法人番号」の行を作らせない</b>ことであって、
        // 上の 1 文ではない——保存済みの行がその組み合わせを持つことはありえないので、
        // 読み直しても違反にはならない（2026-08-31 の自己レビュー）。
        // **CHECK が緩んだ日には、この早期 return が意味を持つ。**
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
    /// 参照フィールドが指している相手の識別子。<b>参照でも識別子でも読む。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>データ側の型は <see cref="LinkFieldData"/> で、識別子は <c>Value</c> に入る</b>
    /// （<c>ModuleFieldData.Id</c> ではない）。取り違えると、いつも null を見て素通しする。</para>
    /// <para><b>型を 1 つに決め打ちしない。</b> 決め打ちにすると、フィールドの型が変わった日に
    /// <b>自己親・種別の食い違い・深さ 1 の 3 本がまとめて素通しに落ちる</b>——
    /// しかもフィクスチャが自分で <see cref="LinkFieldData"/> を組むのでテストは緑のまま。
    /// 登録の関門で同じ穴を同じ日に直したのに、こちらに残っていた
    /// （2026-08-31 の自己レビュー）。</para>
    /// <para><b>新規作成の相手を指しているときは仮の識別子</b>なので数値として読めず、null になる。</para>
    /// </remarks>
    private static long? Reference(ModuleData data, string name)
    {
        var value = data.Fields.TryGetValue(name, out var field)
            ? field switch
            {
                LinkFieldData link => link.Value,
                IdFieldData id => id.Value,
                _ => null,
            }
            : null;

        return long.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// 触られた欄。<b>載っていなければ <c>null</c></b>（更新は差分しか届かない。qa/01 F-12）。
    /// </summary>
    /// <remarks>
    /// <b>載っているのに型が違うときは止める。</b> <c>as T</c> のまま <c>null</c> を返すと
    /// 「触られていない」と見分けがつかず、<b>欄の型が変わった日に、その欄を見る検査が
    /// まとめて素通しへ落ちる</b>——しかもフィクスチャが自分で正しい型を組むので
    /// テストは緑のままである（<see cref="Reference"/> が名指しする形。2026-09-09 の自己レビュー）。
    /// <b>文言はホストが定型文へ差し替える</b>（<see cref="UnreadableFieldException"/>）。
    /// </remarks>
    private static T? Field<T>(ModuleData data, string name) where T : FieldDataBase
        => data.Fields.TryGetValue(name, out var field)
            ? field as T ?? throw UnreadableFieldException.For(name, field)
            : null;

    /// <summary>
    /// その項目が差分に載っているか。<b>型を問わない。</b>
    /// </summary>
    /// <remarks>
    /// 「触ったかどうか」を型付きで判定すると、<b>フィールドの型が変わった日に
    /// 「触っていない」と読んで検査ごと飛ばす</b>（2026-08-31 の自己レビュー。
    /// <see cref="Reference"/> だけを型に強くしても、その手前の早期 return が残っていた）。
    /// </remarks>
    private static bool Submitted(ModuleData data, string name) => data.Fields.ContainsKey(name);
}
