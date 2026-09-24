namespace BusinessApp.Partners.Server;

using BusinessApp.Partners;
using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 適格請求書発行事業者の登録を保存するときの関門（docs/14 §2）。
/// </summary>
/// <remarks>
/// <para><b>DB は登録番号の書式を検査しない</b>と決めてある（同 §2）ので、ここが唯一の関門である。
/// 書式の壊れた番号を通すと、計上のときに<b>そのまま明細へ焼き込まれる</b>
/// （計上のときに会計側が明細へ写す。ADR-0018）。計上済みは不変（ADR-0004）なので、
/// あとから直せない。</para>
/// <para><b>画面のスクリプトでは検査しない</b>（ADR-0008）。取込（フェーズ 6）も同じ入口を通るので、
/// ここに置けば経路が増えても検査が外れない。</para>
/// <para>止めるのは docs/14 §5 の不変条件である——書式（R-I7）・取引先の付け替え（R-I6）・
/// 同じ日から始まる 2 件（R-I3）・期間の重なりと「終わりのない行のあとの行」（R-I4・R-I5）・
/// 終わりと理由の対（R-I1）・登録より前に終わる行（R-I2）・行の削除（R-I8）・
/// 実在しない取引先への新規（R-I9）。
/// 通すと、写しを焼くときに「どれを写すか決められない」で計上が止まるか、誤った番号が焼き込まれる
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

        // **削除は束ねない**——別の操作（行を消す）で、他の違反と並べても直す先が無い。
        RejectDeletions(transactionData);

        var registrations = RegistrationsIn(transactionData).ToList();
        var rows = registrations.ConvertAll(r => r.Data);

        // **この保存で登録年月日が動く行**は、保存済みの値で数えない（下の DuplicateProblemAsync）。
        // 数えると、2 行の日付を入れ替える保存が「既にあります」で誤って止まる。
        // **日付が差分に無い行は入れない**——その行の日付は動かないので、
        // 新しく入る行はそれと衝突してはいけない。
        var moving = rows
            .Where(d => Date(d, "ValidFrom") is not null)
            .Select(Id)
            .OfType<long>()
            .ToHashSet();

        // **理由は全部集めてから 1 回で返す**（docs/21 §2-6 の (b)。開発者の決定。2026-09-20）。
        // **前提の崩れた検査は飛ばす**（Claude の判断。docs/21 §2-6。2026-09-24 の自己レビューで範囲を詰めた）——
        // ①**付け替えを断った行は、同じ日から始まる 2 件（R-I3）の検査に入れない**——R-I3 は送られてきた取引先で数えるので、
        //   移れない先の取引先の話になる（従っても通らない）。
        // ②**同じ日から始まる 2 件に当たった取引先は、重なり（R-I4・R-I5）を見ない**——重なりは始まりの日が重ならないことを
        //   前提に組んである。**当たった取引先だけ**で、他の取引先の重なりは見る。
        // ③**行の中だけで決まる規則**（登録年月日の必須・R-I1・R-I2）**は飛ばさない**——始まりの日の重なりに依らない。
        //   **取引先の実在（R-I9）も飛ばさない**（R-I3 の結果に依らない）。**ただし取引先の鍵が引けない行は、③ も見ない**（`PeriodProblemsAsync`）。
        // ⑤**期間そのものが崩れた取引先（登録年月日が空・R-I2）と、実在しない取引先では、重なり（R-I4・R-I5）を見ない**——期間が組めない。
        //   **R-I1 は期間を崩さない**ので、重なりと一緒に言う（`PeriodProblemsAsync`）。
        // ④**付け替えを断った行は、もとの取引先の履歴に入れて見る**——断りに従って取引先を戻せば、
        //   この保存の残りの変更（日付・終わり）はもとの取引先に当たるから。送られてきた取引先の履歴には、その行が無い。
        var reasons = new List<string>();
        var repointed = new Dictionary<ModuleData, string>(ReferenceEqualityComparer.Instance);
        var clashing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var data in rows)
        {
            reasons.AddRange(NumberProblems(data));
            if (await RepointProblemAsync(data) is (string repoint, string storedPartner))
            {
                reasons.Add(repoint);
                repointed[data] = storedPartner;
            }
        }

        var countable = rows.Where(data => !repointed.ContainsKey(data)).ToList();
        foreach (var data in countable)
        {
            if (await DuplicateProblemAsync(data, moving) is (string partner, string duplicate))
            {
                reasons.Add(duplicate);
                clashing.Add(partner);
            }
        }

        foreach (var (partner, duplicate) in await DuplicatesWithinAsync(countable))
        {
            reasons.Add(duplicate);
            clashing.Add(partner);
        }

        reasons.AddRange(await PeriodProblemsAsync(registrations, clashing, repointed));

        if (reasons.Count > 0)
        {
            throw new PartnerRegistrationRejectedException(reasons);
        }

        return await save();
    }

    /// <summary>
    /// <b>同じ保存の中に</b>、同じ取引先の同じ日から始まる登録が 2 件ないかを見る。
    /// </summary>
    /// <remarks>
    /// <para><see cref="DuplicateProblemAsync"/> は<b>保存済みの行としか突き合わせられない</b>。
    /// 同じ保存で入る 2 件はどちらもまだ DB に無いので、片方ずつ見るかぎり両方が通る。</para>
    /// <para><b>DB も止められない。</b> <c>UNIQUE (partner_id, registration_no, valid_from)</c> は
    /// 登録番号まで含むので、<b>番号が違えば同じ日の 2 件が入る</b>。
    /// 入ってしまうと、計上のときに写しを焼く段で「どれを写すか決められない」で止まり、
    /// <b>入力の誤りが、関係の無い計上の場面で出る</b>。</para>
    /// <para><b>仮の識別子でも突き合わせられる。</b> 取引先の識別子を数値に直さず、
    /// 送られてきた文字列のまま鍵に使う——新規作成の取引先は仮の識別子だが、
    /// 同じ保存の中では同じ文字列になるので、それで同一性が判る。
    /// <b>これは登録の入力を取引先の詳細に置くための下ごしらえ</b>である（docs/14 §4。
    /// 移設そのものはフェーズ 2.5 の C）。独立した一覧しか無いいまでも、
    /// <b>同じ取引先に 2 件を同時に足す経路は取込（フェーズ 6）で開く</b>ので、無駄にはならない。</para>
    /// </remarks>
    /// <returns>当たった取引先の鍵と断り。</returns>
    private async Task<IReadOnlyList<(string Partner, string Reason)>> DuplicatesWithinAsync(IReadOnlyList<ModuleData> registrations)
    {
        var seen = new HashSet<(string Partner, DateOnly ValidFrom)>();
        var reasons = new List<(string Partner, string Reason)>();

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
                reasons.Add((partner,
                    $"この取引先には {validFrom:yyyy/MM/dd} から始まる登録を 2 件入力しています。"
                    + "どちらかの「登録年月日」を直してください。"));
            }
        }

        return reasons;
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
    /// <para><b>型を 1 つに決め打ちしない。</b> 画面が送ってくるのは <see cref="IdFieldData"/> である
    /// （2026-08-31 に入力を取引先の詳細へ移し、親 FK を <c>IdFieldDesign</c> にした。
    /// ヘッダ＋明細の正典。qa/01 D-17）。それでも <see cref="LinkFieldData"/> も読むのは、
    /// <b>取込（フェーズ 6）と API を直に叩く経路が同じ入口を通る</b>からである。
    /// 決め打ちにすると、型が動いた日に<b>この関門が丸ごと素通しに落ちる——
    /// しかもフィクスチャが型を自分で組むのでテストは緑のまま</b>になる。</para>
    /// <para><b>読めない型で来たときは止める</b>（<see cref="UnreadableFieldException"/>）。
    /// <c>null</c> に落とすと<b>保存済みの取引先へ落ちる</b>ので、登録期間の重なりを
    /// <b>別の取引先の行と突き合わせる</b>ことになる。
    /// 「CLB は宣言した型でしか送らない」は<b>素通しの理由にならない</b>——
    /// 型を変えるのはデザインを触る人であって、CLB ではない（2026-09-09 の自己レビュー）。</para>
    /// </remarks>
    private static string? SubmittedPartner(ModuleData data)
        => data.Fields.TryGetValue("Partner", out var field)
            ? Key(field switch
            {
                LinkFieldData link => link.Value,
                IdFieldData id => id.Value,
                _ => throw UnreadableFieldException.For(data.Name, "Partner", field),
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
    /// 追加と更新の両方を、<b>由来（新規か更新か）つき</b>で見る。
    /// </summary>
    /// <remarks>
    /// <para><b>更新を見落とすと、正しい番号で作ってから壊した番号に直せる。</b>
    /// 追加だけを守る関門は、守っていないのと同じである。</para>
    /// <para>由来をここで持ち回るのは、あとから集合で見分けられないからである——
    /// <see cref="ModuleData"/> は <c>Equals</c> を持つので、参照の同一性を期待した
    /// <c>HashSet</c> は<b>値がたまたま一致した別の行を同じ行と見なす</b>（2026-09-02 のレビュー指摘）。</para>
    /// </remarks>
    private static IEnumerable<(ModuleData Data, bool IsAdd)> RegistrationsIn(
        IReadOnlyList<ModuleSubmitData> transactionData)
        => transactionData
            .SelectMany(d => d.Add.Select(x => (Data: x, IsAdd: true))
                .Concat(d.Update.Select(x => (Data: x, IsAdd: false))))
            .Where(t => t.Data.Name == ModuleName);

    /// <summary>登録の行の削除を止める（docs/14 §5 R-I8）。</summary>
    /// <remarks>
    /// <para>取消・失効は「終わり」を記録して残すものであって、行ごと消すものではない。
    /// 画面は <c>CanDelete: false</c> で消す手を出さないが、**画面の形は守りではない**
    /// （qa/01 F-24。API を直に叩く経路と取込（フェーズ 6）が同じ入口を通る）。
    /// 削除を素通しすると、期間の検査（<see cref="PeriodProblemsAsync"/>）が
    /// 消えるはずの行を「保存済み」として数え、誤って断ることにもなる。</para>
    /// <para><b>規則は「計上済みの明細が写していない行は消せる」と決まっている</b>（ADR-0063。開発者の決定。2026-09-16）。
    /// <b>いまは規則より狭く、全部を断っている</b>——実装はフェーズ 6（公表システムの処理区分 99——登録簿からの削除——を
    /// 取込が受ける形と同じ関門になるので、別に作ると 2 度作る。2026-08-25 リサーチ §3-3）。解くときは docs/14 §5 と一緒に動かす。</para>
    /// </remarks>
    private static void RejectDeletions(IReadOnlyList<ModuleSubmitData> transactionData)
    {
        if (transactionData.SelectMany(d => d.Delete).Any(d => d.ModuleName == ModuleName))
        {
            // **結果（「削除できません」）は見出しが言う**——本文は次の一手だけ（docs/21 §2-6）。
            // **「行は消さない」とは言わない**——帳簿に写っていない行は消せると決まっている（ADR-0063。実装はフェーズ 6）。
            // **一手を 2 つに分ける**——入力の誤りに取消・失効の記録を付けさせると、架空の終わりが計上の写しに焼き込まれる
            // （ADR-0063 が案 C を退けた理由「誤入力は登録の出来事ではない」）。
            // **入れる日付は公表サイトの字で言う**（docs/21 §2-3）——「終わった」とだけ言うと、廃止日や期末日を入れて 1 日ずれる。
            throw new PartnerRegistrationRejectedException(
                ["入力を誤った行なら、取引先の詳細の「登録番号の履歴」でその行の「編集」を開き、正しい値に直してください。"
                 + "登録が取り消されたか失効したのなら、同じ「編集」で、国税庁の公表サイトの取消年月日か失効年月日をそのまま"
                 + "「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。"],
                PartnerRegistrationRejectedException.DeletionHeadline);
        }
    }

    /// <summary>登録番号の書式（R-I7）。</summary>
    private static IEnumerable<string> NumberProblems(ModuleData data)
    {
        // CLB は変更されたフィールドしか送ってこない（qa/01 F-11）。
        // 送られていない項目は「変えていない」なので、検査しない。
        if (Field<TextFieldData>(data, "RegistrationNo") is not TextFieldData field)
        {
            return [];
        }

        if (!InvoiceRegistrationNumber.IsWellFormed(field.Value))
        {
            return [$"「登録番号」の形が違います。{InvoiceRegistrationNumber.FormatDescription}入力し直してください。"];
        }

        // **貼り付けで紛れ込んだ空白を落として保存する。** 落とさずに通すと、
        // 同じ番号が 2 通りの文字列で保存されて突合が壊れる（小文字を弾いたのと同じ理由）。
        field.Value = InvoiceRegistrationNumber.Normalize(field.Value);
        return [];
    }

    /// <summary>
    /// <b>既にある登録の取引先を、別の相手へ付け替える保存を止める。</b>
    /// </summary>
    /// <remarks>
    /// <para>付け替えると <b>A 社の履歴に穴が空き、B 社に他人の登録番号が生える</b>（docs/14 §4）。
    /// しかも<b>計上済みの明細に焼き込んだ写しと食い違い</b>、計上済みは直せない（ADR-0004）。
    /// 二重登録の関門は「同じ取引先に同じ日から始まる 2 件」しか見ないので、付け替えは素通りする。</para>
    /// <para><b>2026-08-31 まで、これを止めていたのは画面側の <c>IsUpdateProtected: true</c> だった。</b>
    /// 入力を取引先の詳細へ移して親 FK を <c>IdFieldDesign</c> にしたとき、その設定ごと外した——
    /// 「取引先を選ぶ欄が画面に無い」ことを守りに数えてしまったが、
    /// <b>画面の形は守りではない</b>（qa/01 F-24。<c>*.mod.cs</c> も JSON も、
    /// API を直に叩く経路には効かない）。同じ日の自己レビューで見つけて、ここへ移した。</para>
    /// <para><b>差分に無ければ何もしない。</b> CLB は変更されたフィールドしか送ってこない（qa/01 F-11）ので、
    /// 取引先を触っていない保存では、そもそも比べるものが無い。</para>
    /// </remarks>
    /// <returns>断りと、もとの取引先の鍵。当たらなければ <c>null</c>。</returns>
    private async Task<(string Reason, string StoredPartner)?> RepointProblemAsync(ModuleData data)
    {
        if (Id(data) is not long rowId || SubmittedPartner(data) is not string submitted)
        {
            return null;
        }

        // **保存済みの相手が引けないなら黙って通す。** 行が消えているだけで、
        // その保存は別の理由（外部キー）で失敗する。ここで別の言葉を被せない。
        if (await store.FindPartnerOfAsync(rowId) is not PartnerId stored)
        {
            return null;
        }

        var storedKey = Key(stored.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))!;
        return storedKey != submitted
            ? ("登録の「取引先」は、保存したあとは変更できません。"
               + "別の取引先の登録にするときは、その取引先の画面で入力し直してください。", storedKey)
            : null;
    }

    /// <returns>当たった取引先の鍵と断り。当たらなければ <c>null</c>。</returns>
    private async Task<(string Partner, string Reason)?> DuplicateProblemAsync(ModuleData data, IReadOnlySet<long> moving)
    {
        if (Date(data, "ValidFrom") is not DateOnly validFrom)
        {
            return null;
        }

        // **取引先が差分に無ければ、直している行から引く。**
        // ここで諦めると、画面で登録年月日だけを直した保存が検査を素通りする（qa/02）。
        var id = Id(data);
        var partnerId = ReferencedPartner(data) is long fromField
            ? new PartnerId(fromField)
            : id is long rowId ? await store.FindPartnerOfAsync(rowId) : null;

        if (partnerId is not PartnerId partner)
        {
            return null;
        }

        // **自分自身と、同じ保存で日付が動く行は数えない**（番号ではなく行の識別子で見分ける）。
        // 動く行の保存済みの値は、この保存が終わった時点でもう無い。
        var occupied = await store.FindRegistrationIdsFromAsync(partner, validFrom);
        return occupied.Any(rowId => rowId != id && !moving.Contains(rowId))
            ? (Key(partner.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))!,
               $"この取引先には {validFrom:yyyy/MM/dd} から始まる登録が既にあります。"
               + "入力している登録の登録年月日が国税庁の公表サイトと違うなら「登録年月日」を直し、"
               + "合っているなら、先にある登録を取引先の詳細の「登録番号の履歴」から直してください。")
            : null;
    }

    /// <summary>
    /// <b>保存後にできあがる履歴</b>を取引先ごとに組み立てて、期間の不変条件
    /// （docs/14 §5 の R-I1・R-I2・R-I4・R-I5）を見る。
    /// </summary>
    /// <remarks>
    /// <para><b>差分ではなく「結果」を検査する。</b> CLB は変更されたフィールドしか送ってこない
    /// （qa/01 F-11）ので、差分だけを見ると「取消・失効年月日だけ直した保存」の登録年月日が読めない。
    /// 保存済みの行に差分を重ねてから判定する。日付を入れ替える保存も、両方の行が差分に載るので
    /// 正しく通る（<c>moving</c> の除外と同じ問題への、こちらは組み立てで答える形）。</para>
    /// <para><b>咎めるのは、この保存が触った行が絡む違反だけ。</b> 保存済みの行どうしの違反
    /// （トリガ導入前に入ったデータ）で、無関係な保存を止めない。</para>
    /// </remarks>
    /// <param name="clashing">同じ日から始まる 2 件（R-I3）に当たった取引先の鍵。<b>その取引先の重なりは見ない。</b></param>
    /// <param name="repointed">付け替えを断った行と、もとの取引先の鍵。<b>その行はもとの取引先の履歴で見る。</b></param>
    private async Task<IReadOnlyList<string>> PeriodProblemsAsync(
        IReadOnlyList<(ModuleData Data, bool IsAdd)> registrations,
        IReadOnlySet<string> clashing,
        IReadOnlyDictionary<ModuleData, string> repointed)
    {
        var byPartner = new Dictionary<string, List<(ModuleData Data, bool IsAdd)>>();
        foreach (var item in registrations)
        {
            var key = repointed.TryGetValue(item.Data, out var storedPartner)
                ? storedPartner
                : await PartnerKeyAsync(item.Data);
            if (key is not string partner)
            {
                continue;
            }

            if (!byPartner.TryGetValue(partner, out var list))
            {
                byPartner[partner] = list = [];
            }

            list.Add(item);
        }

        var reasons = new List<string>();
        foreach (var (partner, batch) in byPartner)
        {
            // **取引先が無くても、行の中だけで決まる規則は見る**（R-I1・R-I2 は取引先に依らない）。
            // **重なりは見ない**——履歴を組み立てる相手がいない。
            var missing = await MissingPartnerProblemAsync(partner, batch);
            if (missing is not null)
            {
                reasons.Add(missing);
            }

            // **期間そのものが崩れていれば、重なりは見ない**——登録年月日が無い・終わりが始まりより前の行で
            // 期間を比べても、従いようのない重なりを言うことになる。
            // **終わりと理由の対（R-I1）は期間を崩さない**ので、重なりと一緒に言う——理由だけ残して終わりを消した保存に、
            // 対の断りだけを先に返すと、従った 2 回目で初めて「空にすると重なる」が出る（2026-09-24 の自己レビュー）。
            var resulting = await ResultingRowsAsync(partner, batch);
            var broken = BrokenRowProblems(resulting).ToList();
            reasons.AddRange(broken.Select(problem => problem.Reason));
            if (missing is null && !broken.Any(problem => problem.BreaksPeriod) && !clashing.Contains(partner))
            {
                reasons.AddRange(OverlapProblems(resulting));
            }
        }

        return reasons;
    }

    /// <summary><b>実在しない取引先への新規の行</b>を言葉で断る。</summary>
    /// <remarks>
    /// <para>画面からは来ない（取引先の詳細のボタンが自分の識別子を渡す）が、
    /// URL の <c>?partner=</c> の書き換えと API 直叩きで来る。DB の外部キーに任せると
    /// 生の SQL エラーになるうえ、稼働側で SQLite の外部キー検査が有効かは確認できていない——
    /// 効いていなければ、一覧の JOIN に出ない<b>孤児行が黙って入る</b>（2026-09-02 のレビュー指摘）。</para>
    /// <para>仮の識別子（同じ保存で作る取引先）は対象外。更新の行は保存済みの行に
    /// 紐づいた時点で取引先の実在が判っているので見ない。</para>
    /// </remarks>
    private async Task<string?> MissingPartnerProblemAsync(
        string partner, List<(ModuleData Data, bool IsAdd)> batch)
    {
        if (!batch.Any(b => b.IsAdd))
        {
            return null;
        }

        if (!long.TryParse(
                partner,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var id))
        {
            return null;
        }

        return await store.FindNameAsync(new PartnerId(id)) is null
            ? "取引先が見つかりません。取引先の詳細の「登録番号を追加する」から入り直してください。"
            : null;
    }

    /// <summary>保存済みの行に、この保存の差分を重ねた「保存後の履歴」。</summary>
    private async Task<List<PeriodRow>> ResultingRowsAsync(
        string partner, List<(ModuleData Data, bool IsAdd)> batch)
    {
        var stored = long.TryParse(
            partner,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var partnerId)
            ? await store.LoadRegistrationRowsAsync(new PartnerId(partnerId))
            : [];

        var rows = stored.ToDictionary(
            r => r.Id,
            r => new PeriodRow(r.ValidFrom, r.EndedOn, r.EndReason is not null, RowOrigin.Stored));
        var added = new List<PeriodRow>();

        foreach (var (data, isAdd) in batch)
        {
            if (isAdd)
            {
                added.Add(new PeriodRow(
                    MergedDate(data, "ValidFrom", null),
                    MergedDate(data, "EndedOn", null),
                    MergedReason(data, false),
                    RowOrigin.Added));
                continue;
            }

            // **更新なのに保存済みの行へ紐づかない行は、判定しない**（材料が無い。
            // 「行を特定できなければ見ない」——付け替え・二重登録の検査と同じ倒し方）。
            if (Id(data) is not long rid || !rows.TryGetValue(rid, out var current))
            {
                continue;
            }

            var endedOn = MergedDate(data, "EndedOn", current.EndedOn);
            rows[rid] = new PeriodRow(
                MergedDate(data, "ValidFrom", current.ValidFrom),
                endedOn,
                MergedReason(data, current.HasReason),
                // **終わりを消したかは、保存済みの値と差分を重ねた値で決める**——差分に欄が載っていても、
                // 保存済みが既に空なら「消した」ではない（R-I5 の文を選ぶためだけに使う。OpenRowBefore）。
                current.EndedOn is not null && endedOn is null ? RowOrigin.EndCleared : RowOrigin.Updated);
        }

        return [.. rows.Values, .. added];
    }

    /// <summary><b>差分に載っていれば差分の値（空にする変更を含む）、無ければ保存済みの値。</b></summary>
    private static DateOnly? MergedDate(ModuleData data, string name, DateOnly? storedValue)
        => Field<DateFieldData>(data, name) is DateFieldData field ? field.Value : storedValue;

    private static bool MergedReason(ModuleData data, bool storedValue)
        => Field<SelectFieldData>(data, "EndReason") is SelectFieldData field
            ? !string.IsNullOrEmpty(field.Value)
            : storedValue;

    /// <summary>触った行そのものの検査（R-I1・R-I2 と、登録年月日の必須）。</summary>
    /// <remarks>
    /// <para>R-I1・R-I2 は DDL の CHECK も守っているが、CHECK の違反は生の SQL エラーで返る。
    /// 利用者が編集できる欄になった（2026-09-02 の画面の作り直し）ので、ここで先に言葉で断る。</para>
    /// <para><b>登録年月日が無い行は、そこで止めて次の行へ移る</b>——終わりが始まりより前かは、始まりが無ければ比べられない。
    /// <b>終わりと理由の対（R-I1）と、終わりが始まりより前（R-I2）は両方言う</b>——別々の欄の誤りで、片方を直してももう片方が残る。</para>
    /// </remarks>
    /// <returns>断りと、<b>期間そのものを崩すか</b>（崩すなら、その取引先の重なりは見ない）。</returns>
    private static IEnumerable<(string Reason, bool BreaksPeriod)> BrokenRowProblems(List<PeriodRow> resulting)
    {
        foreach (var row in resulting.Where(r => r.Touched))
        {
            if (row.ValidFrom is not DateOnly validFrom)
            {
                yield return ("「登録年月日」を入力してください。", true);
                continue;
            }

            if (row.EndedOn is null != !row.HasReason)
            {
                yield return ("「取消・失効年月日」と「取消・失効の理由」は、両方入力するか、両方空にしてください。", false);
            }

            if (row.EndedOn is DateOnly ended && ended < validFrom)
            {
                yield return (
                    $"「取消・失効年月日」（{ended:yyyy/MM/dd}）が「登録年月日」（{validFrom:yyyy/MM/dd}）より"
                    + "前になっています。日付を確かめてください。", true);
            }
        }
    }

    /// <summary>期間の重なり（R-I4）と、終わりのない行のあとの行（R-I5）。</summary>
    /// <remarks>
    /// <para><b>隣接（前の行の終わりの日＝次の行の登録年月日）は通す。</b>
    /// 計上時の引き当て（<see cref="InvoiceRegistrationHistory.InEffectOn"/>）が
    /// 「終わりの日を含み、同日は新しいほうを採る」と決めており、どちらの制度解釈でも
    /// 決定的に引ける形だからである（隣接が再登録の正常形かは未確認——docs/14 §5）。</para>
    /// <para><b>登録年月日で並べて、全ペアを比べる。</b> 隣どうしだけでは足りない——
    /// トリガ導入（2026-09-02）前の違反データが間に挟まると、「隣が良ければ離れた 2 行も良い」
    /// という帰納が破れる（レビュー指摘）。行数は 1 取引先あたり多くて数件なので全ペアでよい。
    /// **古いデータどうしの違反は握りつぶす**（触らない保存を止めない）。
    /// 同じ日から始まる 2 行は、ふつうはここに来ない（R-I3 に当たった取引先では、この検査ごと飛ばしている）。
    /// <b>例外は、「取引先」を変えた行（R-I6 で断る。API だけ）が同じ保存で「登録年月日」も変え、もとの取引先の別の行と同じ日にした形</b>——
    /// その行は R-I3 に数えないので、ここで重なりの文になる。<b>断ることは変わらない</b>（R-I6 の理由が既にある）ので、文の正確さのために数え分けてはいない。</para>
    /// <para><b>早い行 1 つにつき、断りは 1 つだけ言う</b>——いちばん近いあとの行との組である。
    /// 終わりをその行の始まりまでに入れれば、それより遅い行との組も同時に解ける。
    /// 同じ早い行について遅い行を全部並べると、<b>同じ直し先の断りが行数だけ並ぶ</b>（docs/21 §2-6）。
    /// <b>別の早い行の違反は別に言う</b>——直す先が違う。</para>
    /// </remarks>
    private static IEnumerable<string> OverlapProblems(List<PeriodRow> resulting)
    {
        var ordered = resulting
            .Where(r => r.ValidFrom is not null)
            .OrderBy(r => r.ValidFrom)
            .Select(r => (From: r.ValidFrom!.Value, r.EndedOn, r.Origin))
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (OverlapProblem(ordered[i], ordered[j]) is string problem)
                {
                    yield return problem;
                    break;
                }
            }
        }
    }

    /// <summary>早い行 <paramref name="a"/> と遅い行 <paramref name="b"/> の組の断り。無ければ <c>null</c>。</summary>
    private static string? OverlapProblem(
        (DateOnly From, DateOnly? EndedOn, RowOrigin Origin) a,
        (DateOnly From, DateOnly? EndedOn, RowOrigin Origin) b)
    {
        if (a.Origin == RowOrigin.Stored && b.Origin == RowOrigin.Stored)
        {
            return null;
        }

        if (a.EndedOn is not DateOnly aEnd)
        {
            return OpenRowBefore(a.From, a.Origin, b.From);
        }

        return b.From < aEnd
            ? $"登録の期間が重なっています。{a.From:yyyy/MM/dd} からの登録の「取消・失効年月日」（{aEnd:yyyy/MM/dd}）が、"
              + $"次の登録の「登録年月日」（{b.From:yyyy/MM/dd}）より後になっています。"
              + "「登録年月日」と「取消・失効年月日」を確かめてください。"
            : null;
    }

    /// <summary>終わりのない行のあとに行がある（R-I5）ときの断り。<b>終わりのない行の由来で文を選ぶ。</b></summary>
    /// <remarks>
    /// <para><b>名指す「あとの登録」は、終わりのない行の直後の行である</b>（<see cref="OverlapProblems"/> が遅い行を昇順に回し、最初の組で止める）。
    /// 終わりをその日までに入れれば、この組は隣接か重なりなしになる——だから日付を 1 つだけ言えばよい。</para>
    /// <para><b>終わりのない行がこの保存で触った行なら、いまの入力を直す文にする。</b>
    /// 「一覧の「編集」から開け」と言うと、新しく足す行は一覧に無く、編集中の行は既に開いている
    /// ——<b>文言どおりの次の一手が取れない</b>（2026-09-24 の全件の REG-24。qa/03 L-66）。</para>
    /// <para><b>直し方を 2 つ並べるときは、選ぶ目安を言う</b>（docs/21 §2-3）。
    /// 目安を言わずに「終わりを入れるか、登録年月日を確かめるか」と並べると、
    /// 登録年月日の打ち間違いなのに<b>いま使っている登録に誤った取消・失効年月日を入れて通してしまう</b>——
    /// 計上済みの写しは直せないので、取り返しがつかない（2026-09-24 の自己レビュー。3 人が独立に挙げた）。</para>
    /// <para><b>目安は、国税庁の公表サイトに載る取消年月日・失効年月日そのもので言い、どこで見るかも言う。</b>
    /// 「… までに終わった登録なら」と言うと、最後に有効だった日と読まれて 1 日ずれた日を誘う
    /// ——この欄は効力がなくなる最初の日である（docs/14 §6）。<b>入れる値も代名詞で指さない</b>——
    /// 「以前なら、その日を入れて」は直前の日付（あとの登録の始まりの日）を指して読め、それを入れると隣接で通ってしまう。
    /// 「その取消年月日か失効年月日をそのまま」と言う（2026-09-24 の自己レビュー）。</para>
    /// <list type="bullet">
    /// <item><b>終わりを消した更新</b>——消したこと自体が重なりを作った。<b>元に戻す</b>のが先で、あとの登録のほうが誤りならそちらを直す</item>
    /// <item><b>新規・その他の更新</b>——入力している登録の取消年月日か失効年月日があとの登録の始まり以前ならその日を入れ、そうでなければ登録年月日の誤り</item>
    /// <item><b>保存済みの行</b>（あとの行を入力した）——保存済みの登録の取消年月日か失効年月日があとの登録の始まり以前なら先にそちらを閉じ、そうでなければ入力した登録年月日の誤り（REG-23 の再登録の形）</item>
    /// </list>
    /// </remarks>
    private static string OpenRowBefore(DateOnly openFrom, RowOrigin openRow, DateOnly laterFrom)
        => openRow switch
        {
            RowOrigin.EndCleared =>
                "登録の期間が重なっています。"
                + $"「取消・失効年月日」を空にすると、{laterFrom:yyyy/MM/dd} からの登録と期間が重なります。"
                + "「取消・失効年月日」と「取消・失効の理由」を元に戻してください。"
                + $"{laterFrom:yyyy/MM/dd} からの登録のほうが誤りなら、先に取引先の詳細の「登録番号の履歴」でその行の「編集」から直してください。",
            RowOrigin.Stored =>
                "登録の期間が重なっています。"
                + $"この取引先には「取消・失効年月日」が空の登録（{openFrom:yyyy/MM/dd} から）があり、"
                + $"入力している登録（{laterFrom:yyyy/MM/dd} から）はそのあとに始まります。"
                + $"国税庁の公表サイトで、{openFrom:yyyy/MM/dd} からの登録の取消年月日か失効年月日が {laterFrom:yyyy/MM/dd} 以前なら、"
                + "先に取引先の詳細の「登録番号の履歴」でその行の「編集」を開き、"
                + "その取消年月日か失効年月日をそのまま「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。"
                + "そうでなければ、入力している登録の「登録年月日」を確かめてください。",
            _ =>
                "登録の期間が重なっています。"
                + $"入力している登録（{openFrom:yyyy/MM/dd} から）は「取消・失効年月日」が空ですが、"
                + $"そのあとに {laterFrom:yyyy/MM/dd} からの登録があります。"
                + $"国税庁の公表サイトで、入力している登録の取消年月日か失効年月日が {laterFrom:yyyy/MM/dd} 以前なら、"
                + "その取消年月日か失効年月日をそのまま「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。"
                + "そうでなければ、入力している登録の「登録年月日」を確かめてください。",
        };

    /// <summary>保存後の履歴の 1 行。<c>Origin</c> はこの保存がその行に何をしたか。</summary>
    private readonly record struct PeriodRow(
        DateOnly? ValidFrom, DateOnly? EndedOn, bool HasReason, RowOrigin Origin)
    {
        /// <summary>この保存が触った行か（保存済みのまま触っていない行でない）。</summary>
        public bool Touched => Origin != RowOrigin.Stored;
    }

    /// <summary>保存後の履歴の 1 行が、この保存でどうなったか。断りの文を選ぶためにだけ使う。</summary>
    private enum RowOrigin
    {
        /// <summary>保存済みで、この保存は触っていない。</summary>
        Stored,

        /// <summary>この保存で足す行。</summary>
        Added,

        /// <summary>保存済みの行の「取消・失効年月日」を、この保存で空にした。</summary>
        EndCleared,

        /// <summary>保存済みの行を、この保存で直した（終わりを消した以外）。</summary>
        Updated,
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

    /// <summary>差分の欄を型付きで読む。<b>載っていなければ <c>null</c>、載っているのに型が違えば止める。</b></summary>
    /// <remarks>
    /// <c>as T</c> のまま <c>null</c> を返すと「触られていない」と見分けがつかず、
    /// <b>登録番号が別の型で届いた日に、書式の検査（R-I7）が黙って素通しになる</b>——DB は書式を見ないので、
    /// 壊れた番号が保存され、計上時の写しに焼き込まれる（2026-09-24 の自己レビュー。<c>PartnerSubmitGate.Field</c> と同じ形）。
    /// </remarks>
    private static T? Field<T>(ModuleData data, string name) where T : FieldDataBase
        => data.Fields.TryGetValue(name, out var field)
            ? field as T ?? throw UnreadableFieldException.For(data.Name, name, field)
            : null;
}
