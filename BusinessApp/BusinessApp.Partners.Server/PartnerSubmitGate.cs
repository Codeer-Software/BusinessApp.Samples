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

    /// <summary>法人番号より上にある文字の欄（docs/12 §2-2）。</summary>
    private static readonly (string Field, string Label, int Max)[] AboveCorporateNumber =
    [
        ("Name", "取引先名", MasterTextLength.PartnerName),
        // **カナは名前の 2 倍**（旧 Q-26 の決定。2026-09-16。docs/12 §2-2）——
        // **名前を上限いっぱいまで書いた取引先が、その読みを入れられない**形にしないため。
        ("NameKana", "カナ", MasterTextLength.PartnerNameKana),
    ];

    /// <summary>法人番号より下にある文字の欄（docs/12 §2-2）。</summary>
    private static readonly (string Field, string Label, int Max)[] BelowCorporateNumber =
    [
        ("Address", "所在地", MasterTextLength.Address),
    ];

    /// <summary>保存を包む。<paramref name="save"/> は CLB 本来の保存処理。</summary>
    public async Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        ArgumentNullException.ThrowIfNull(transactionData);
        ArgumentNullException.ThrowIfNull(save);

        var reasons = new List<string>();
        foreach (var (data, adding) in PartnersIn(transactionData))
        {
            reasons.AddRange(await ReasonsForAsync(data, adding));
        }

        if (reasons.Count > 0)
        {
            throw new PartnerRejectedException(reasons);
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
    /// <summary>
    /// この保存に混ざっている取引先の行。<b>追加か更新かも一緒に返す。</b>
    /// </summary>
    /// <remarks>
    /// <b>追加だけに掛ける規則があるので、どちらの箱に入っていたかを落とさない</b>
    /// （コードの必須。<c>MasterSubmitGate</c> と同じ形。2026-09-09 の自己レビュー）。
    /// </remarks>
    private static IEnumerable<(ModuleData Data, bool Adding)> PartnersIn(
        IReadOnlyList<ModuleSubmitData> transactionData)
        => transactionData
            .SelectMany(d => d.Add.Select(r => (Data: r, Adding: true))
                              .Concat(d.Update.Select(r => (Data: r, Adding: false))))
            .Where(x => x.Data.Name == ModuleName);

    /// <summary>
    /// 断る理由を<b>全部</b>集める（docs/21 §2-6 の (b)。開発者の決定。2026-09-20）。
    /// </summary>
    /// <remarks>
    /// <para><b>並びは画面の並びに合わせる</b>（取引先コード → 取引先名 → カナ → 種別 → 法人番号 → 名寄せの親 → 所在地）——
    /// 並びが画面と食い違うと、利用者は「①…②…」を読みながら上と下を往復させられる。</para>
    /// <para><b>1 つの欄については最初に当たった 1 つだけを言う</b>（ADR-0047 の決定 10）——コードが空・書式違いなら重複は数えない（<see cref="CodeProblemsAsync"/>）。
    /// <b>前提の崩れた検査も飛ばす</b>（Claude の判断。docs/21 §2-6）——
    /// 個人事業者に法人番号が入っているなら番号の書式は言わず、
    /// <b>自分自身を親にした保存では、選んだ親を見る検査を飛ばす</b>（その親は付けられないので、親の種別や深さを言っても従いようがない）。
    /// <b>自分が誰かの親であることは、選んだ親に依らない</b>ので、自分自身を親にした文の中で言う（<see cref="SelfParentProblemAsync"/>）。</para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> ReasonsForAsync(ModuleData data, bool adding)
    {
        var reasons = new List<string>();

        // **追加はコードを必ず伴う。** 画面は必ず送ってくるが、**取込は列ごと落とせる**
        // （`code` の無い CSV）。素通しにすると DB の NOT NULL に当たり、
        // 利用者には定型文が出る（qa/03 L-28 に戻る。2026-09-09 の自己レビュー）。
        if (adding && !data.Fields.ContainsKey("Code"))
        {
            reasons.Add("「取引先コード」を入れてください。");
        }

        reasons.AddRange(await CodeProblemsAsync(data));
        reasons.AddRange(LongTextProblems(data, AboveCorporateNumber));

        // 種別（子との食い違い）→ 法人番号 → 名寄せの親 → 所在地（画面の並び）。
        var mismatchedChildren = await MismatchedChildrenAsync(data);
        reasons.AddRange(mismatchedChildren);

        // **個人事業者に法人番号が入っているなら、番号の書式は言わない**——「13 桁で入れ直せ」と「空にせよ」が
        // 1 通の中で逆を言うことになる。
        var soleProprietor = await SoleProprietorWithCorporateNumberAsync(data);
        reasons.AddRange(soleProprietor.Count > 0 ? soleProprietor : CorporateNumberProblems(data));

        if (await SelfParentProblemAsync(data) is string self)
        {
            reasons.Add(self);
        }
        else
        {
            // **選んだ親がさらに親を持つなら、その親との種別の食い違いは言わない**——その親は選べないので、
            // 種別を合わせても通らない（2026-09-24 の自己レビュー）。
            var parentWithParent = await ParentWithParentProblemAsync(data);
            if (parentWithParent is null
                && mismatchedChildren.Count == 0
                && await MismatchedParentAsync(data) is string mismatchedParent)
            {
                // **子との食い違いを既に言ったなら、親との食い違いは重ねない**——同じ文である。
                // 直す先は「この取引先の種別」か「相手の種別」で、どちらの相手でも同じ直し方になる。
                reasons.Add(mismatchedParent);
            }

            if (parentWithParent is not null)
            {
                reasons.Add(parentWithParent);
            }

            if (await ParentOfOthersProblemAsync(data) is string parentOfOthers)
            {
                reasons.Add(parentOfOthers);
            }
        }

        reasons.AddRange(LongTextProblems(data, BelowCorporateNumber));
        return reasons;
    }

    /// <summary>名前・カナ・所在地の長さ（docs/12 §2-2）。</summary>
    /// <remarks>
    /// <para><b>断るのは長すぎるときと、数えられない字が入っているときだけである。</b>
    /// 空を断るのは画面の <c>IsRequired</c> と DB の <c>NOT NULL</c> の仕事で、
    /// <b>長さの関門が「空です」と言い出すと責任の境目がぼやける</b>。</para>
    /// <para><b>更新は差分しか届かない</b>（qa/01 の F-12）ので、
    /// <b>載っていない＝触っていない</b>として素通しする。</para>
    /// <para><b>欄の呼び名は画面の <c>DisplayName</c> の写しである</b>
    /// （docs/20 §4 の「已むを得ない重複」。ずれていないことは <c>PartnerSubmitGateTests</c> が見る）。</para>
    /// <para><b>前後の空白を落とし、落とした姿を差分に書き戻す</b>（コードと同じ。ADR-0047 の決定 5）。
    /// <b>比べるときだけ落とすと、関門が数えた長さと DDL が数える長さが食い違う</b>
    /// （qa/03 の L-14 の型。qa/02 の R45-02 で実際に踏んだ）。</para>
    /// </remarks>
    private static IEnumerable<string> LongTextProblems(ModuleData data, (string Field, string Label, int Max)[] fields)
    {
        foreach (var (field, label, max) in fields)
        {
            // **読む口は 1 つにする。** `ContainsKey` で見てから `Field` で読む形にすると、
            // **通らない枝（載っているのに null）が 1 本残る**——型が読めないときは
            // `Field` が止めるので、`null` は「載っていない」だけを意味する。
            if (Field<TextFieldData>(data, field) is not TextFieldData text)
            {
                continue;
            }

            // **`null` は `null` のままにする**（空文字を書き込むと「無いは NULL」が崩れる。docs/20 §7）。
            if (text.Value is string value)
            {
                text.Value = MasterTextLength.Normalize(value);
            }

            if (MasterTextLength.DescribeProblem(label, text.Value, max) is string problem)
            {
                yield return problem;
            }
        }
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
    /// <para><b>空・書式違いなら、重複は数えない</b>——正規化できない字で DB を引いても、
    /// 利用者が直す先は同じ欄なので、断りが 2 つに割れるだけである（1 つの欄については最初に当たった 1 つだけ——ADR-0047 の決定 10）。</para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> CodeProblemsAsync(ModuleData data)
    {
        if (Field<TextFieldData>(data, "Code") is not TextFieldData field)
        {
            return [];
        }

        var code = MasterCode.Normalize(field.Value);
        if (code.Length == 0)
        {
            return ["「取引先コード」を入れてください。"];
        }

        if (MasterCode.DescribeProblem("取引先コード", code) is string problem)
        {
            return [problem];
        }

        field.Value = code;

        var conflict = await store.FindConflictingCodeAsync(code, Id(data) is long id ? new PartnerId(id) : null);
        if (conflict is null)
        {
            return [];
        }

        // **ぶつかった相手の字を見せる。** 大小だけが違うとき、字を見比べないと理由が分からない。
        var reason = string.Equals(conflict, code, StringComparison.Ordinal)
            ? "既に使われています。"
            : $"大文字と小文字を区別しないので、既にある「{conflict}」と同じコードになります。";

        return [$"「取引先コード」の「{code}」は{reason}別のコードを入れてください。"];
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
    private async Task<string?> MismatchedParentAsync(ModuleData data)
    {
        var submittedType = Field<SelectFieldData>(data, "EntityType");
        var submittedParent = Submitted(data, "ParentPartner");

        // どちらも触っていない保存は、この組み合わせを新しく作れない。
        if (submittedType is null && !submittedParent)
        {
            return null;
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
            return null;
        }

        return AreIncompatible(type, parentProfile.EntityType) ? MismatchedParent : null;
    }

    /// <summary>
    /// <b>子を持つ取引先の種別を変えて、食い違わせていないか</b>（ADR-0028 §1 の親の側）。
    /// </summary>
    /// <remarks>
    /// 種別を触っていない保存は見ない——触っていない行に分類を強制しない（docs/13 §1-2）。
    /// </remarks>
    private async Task<IReadOnlyList<string>> MismatchedChildrenAsync(ModuleData data)
    {
        if (Field<SelectFieldData>(data, "EntityType") is not SelectFieldData submitted
            || Id(data) is not long id)
        {
            return [];
        }

        var type = DbValue.ToDefinedEnum<PartnerEntityType>(submitted.Value);
        return (await store.FindChildEntityTypesAsync(new PartnerId(id))).Any(child => AreIncompatible(type, child))
            ? [MismatchedParent]
            : [];
    }

    /// <summary>
    /// 種別の食い違いの差し戻し。<b>親から見ても子から見ても同じ文言で断る</b>——
    /// 両方に当たった保存では 1 つだけ言う（<see cref="ReasonsForAsync"/>）。
    /// </summary>
    private static readonly string MismatchedParent =
        $"{PartnerEntityType.SoleProprietor.DisplayName()}と法人・人格のない社団等は、"
        + "互いに名寄せの親にできません。同じ事業者なら、どちらかの「種別」が誤っています。";

    /// <summary>
    /// 個人事業者と法人系の組か。<b>未分類（<c>null</c>）と「その他」は通す。</b>
    /// </summary>
    private static bool AreIncompatible(PartnerEntityType? one, PartnerEntityType? other)
        => (one == PartnerEntityType.SoleProprietor && IsCorporateLike(other))
            || (other == PartnerEntityType.SoleProprietor && IsCorporateLike(one));

    private static bool IsCorporateLike(PartnerEntityType? type)
        => type is PartnerEntityType.Corporation or PartnerEntityType.UnincorporatedAssociation;

    /// <summary>
    /// 名寄せの親は必ず根である（深さ 1 の森。ADR-0028 §2）——その 1 つ目: <b>選んだ親が、さらに親を持っている</b>。
    /// </summary>
    /// <remarks>
    /// <para>止めるのは 2 つ——<b>選んだ親が、さらに親を持っている</b>こと（ここ）と、
    /// <b>自分が既に誰かの親になっているのに、自分に親を付けようとしている</b>こと（<see cref="ParentOfOthersProblemAsync"/>）。
    /// この 2 つで循環は構造的に消える（深さ 2 以上が作れないため）。<b>2 つとも当たれば、2 つとも言う</b>——どちらを直しても、もう片方が残る。</para>
    /// <para><b>親を触っていない保存は見ない。</b> 深さは親を付け替えたときにしか変わらず、
    /// 触っていない行まで見ると、既に矛盾している行（トリガより前に入ったもの）を
    /// <b>他の項目を直すだけでも保存できなくする</b>。</para>
    /// <para><b>新規作成の相手を親にしている場合は見ない</b>——仮の識別子は数値として読めず、
    /// そもそも生まれたばかりの行は親を持てない。</para>
    /// </remarks>
    private async Task<string?> ParentWithParentProblemAsync(ModuleData data)
    {
        // **親を触っていない保存は見ない。** 深さは親を付け替えたときにしか変わらない。
        // 空欄に戻す保存（Reference が null）も通す——浅くする操作である。
        if (!Submitted(data, "ParentPartner")
            || Reference(data, "ParentPartner") is not long parent)
        {
            return null;
        }

        // **読めない相手は見ない。** 指した相手が居なければ、深さを判定する材料が無い
        // （外部キーが最後に拒む）。同じ保存で作られる相手は仮の識別子なので Reference が落とす。
        return await store.FindLineageAsync(new PartnerId(parent)) is PartnerLineage { ParentId: not null }
            ? "「名寄せの親」には、さらに親を持つ取引先を選べません。同じ事業者なら、その取引先の親を選んでください。"
            : null;
    }

    /// <summary>
    /// 深さ 1 の森の 2 つ目: <b>自分が誰かの親なら、自分に親は付けられない</b>（<see cref="ParentWithParentProblemAsync"/>）。
    /// </summary>
    private async Task<string?> ParentOfOthersProblemAsync(ModuleData data)
    {
        if (!Submitted(data, "ParentPartner")
            || Reference(data, "ParentPartner") is not long
            || Id(data) is not long own)
        {
            return null;
        }

        return await store.FindLineageAsync(new PartnerId(own)) is PartnerLineage { HasChildren: true }
            ? "この取引先は他の取引先の名寄せの親になっているので、「名寄せの親」は空欄にしてください。"
              + "親を付けるなら、先に、子になっている取引先の「名寄せの親」を付け替えてください。"
            : null;
    }

    /// <summary>自分自身を名寄せの親にできない（DDL の CHECK と同じ規則）。</summary>
    /// <remarks>
    /// <para>新規作成の行はまだ識別子を持たないので、この形は起こりえない。
    /// <b>更新には必ず識別子が載る</b>ので、選んだ親は保存されている値を読みに行く必要はない。</para>
    /// <para><b>子を持つ取引先には、別の取引先も選べない</b>（<see cref="ParentOfOthersProblemAsync"/>）——
    /// 「別の取引先を選ぶか」と言うと、従った 2 回目で断られる。だから文を分ける。</para>
    /// </remarks>
    private async Task<string?> SelfParentProblemAsync(ModuleData data)
    {
        if (Reference(data, "ParentPartner") is not long parent || Id(data) is not long id || parent != id)
        {
            return null;
        }

        return await store.FindLineageAsync(new PartnerId(id)) is PartnerLineage { HasChildren: true }
            ? "この取引先自身は「名寄せの親」に選べません。この取引先は他の取引先の名寄せの親になっているので、「名寄せの親」は空欄にしてください。"
            : "この取引先自身は「名寄せの親」に選べません。同じ事業者の別の取引先があるならそれを選び、無いなら空欄にしてください。";
    }

    /// <summary>法人番号の書式と検査用数字。<b>空欄は通す</b>（任意。docs/13 §2-3）。</summary>
    private static IEnumerable<string> CorporateNumberProblems(ModuleData data)
    {
        // CLB は変更されたフィールドしか送ってこない（qa/01 F-11）。
        // 送られていない項目は「変えていない」なので、検査しない。
        if (Field<TextFieldData>(data, "CorporateNumber") is not TextFieldData field)
        {
            return [];
        }

        var value = CorporateNumber.Normalize(field.Value);

        // **空欄は空文字ではなく NULL で保存する。**
        // DDL の CHECK は「NULL か、数字 13 桁」しか許さないので、空文字を書き戻すと
        // 「入っていた法人番号を消す」という正規の直し方が DB の失敗になる
        // （2026-08-26 の自己レビューで発見。qa/03）。CLB 自身も消した値は NULL で書く。
        if (value.Length == 0)
        {
            field.Value = null;
            return [];
        }

        // **貼り付けで紛れ込んだ空白を落として保存する。** 落とさずに通すと、
        // 同じ番号が 2 通りの文字列で保存されて名寄せの突合が壊れる。
        field.Value = value;

        // 判定と文言は CorporateNumber が 1 か所で持つ（自社情報の関門と同じものを通す）。
        return CorporateNumber.DescribeProblem(value) is string problem ? [problem] : [];
    }

    /// <summary>個人事業者に法人番号は指定されない（制度事実。docs/13 §1-2）。</summary>
    /// <remarks>
    /// <b>差分に無いほうは、保存されている値で補う。</b> 片方だけ直した保存で検査をやめると、
    /// 「先に個人事業者にしておいて、あとから法人番号を足す」で素通りする。
    /// </remarks>
    private async Task<IReadOnlyList<string>> SoleProprietorWithCorporateNumberAsync(ModuleData data)
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
            return [];
        }

        var stored = Id(data) is long id ? await store.FindProfileAsync(new PartnerId(id)) : null;

        var type = submittedType is null
            ? stored?.EntityType
            : DbValue.ToDefinedEnum<PartnerEntityType>(submittedType.Value);
        var number = submittedNumber is null
            ? CorporateNumber.Normalize(stored?.CorporateNumber)
            : CorporateNumber.Normalize(submittedNumber.Value);

        return type == PartnerEntityType.SoleProprietor && number.Length > 0
            ? [$"{PartnerEntityType.SoleProprietor.DisplayName()}に法人番号は指定されません。"
               + "取引先が個人事業者でないなら「種別」を直し、個人事業者なら「法人番号」を空欄にしてください。"]
            : [];
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
    /// <para><b>新規作成の相手を指しているときは仮の識別子</b>なので数値として読めず、null になる。
    /// <b>それと「読めない型」は別</b>——後者は <see cref="UnreadableFieldException"/> で止める
    /// （<c>null</c> に落とすと、この doc が名指しした 3 本がまとめて素通しになる。2026-09-09）。</para>
    /// </remarks>
    private static long? Reference(ModuleData data, string name)
    {
        var value = data.Fields.TryGetValue(name, out var field)
            ? field switch
            {
                LinkFieldData link => link.Value,
                IdFieldData id => id.Value,
                _ => throw UnreadableFieldException.For(data.Name, name, field),
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
            ? field as T ?? throw UnreadableFieldException.For(data.Name, name, field)
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
