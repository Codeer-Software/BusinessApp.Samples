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
/// <para><b><see cref="MasterMeaningGate"/> とは別の規則である。</b>
/// あちらは「使用中の行の意味を変えられない」（ADR-0038）を見る。こちらは
/// <b>値そのものが正しいか・重複していないか</b>を見る。<b>断るのはここ 1 か所で、両方の理由を束ねて 1 回で返す</b>
/// （docs/21 §2-6 の (b)）。<b>並びは意味の凍結が先</b>——
/// あちらは変えた内容のままでは通す手が無い（元に戻すか、新しい行を作るしかない）が、こちらは値を直せば通る。</para>
/// <para><b>追加も更新も見る。</b> 追加だけを守る関門は、正しい値で作ってから壊す経路を残す
/// （<c>PartnerSubmitGate</c> と同じ理由）。<b>更新では触った欄しか届かない</b>ので
/// （qa/01 F-12）、2 つの欄をまたぐ規則は保存されている側と組んで判定する。</para>
/// <para><b>理由は全部集めてから 1 回で返す</b>（docs/21 §2-6 の (b)。開発者の決定。2026-09-20）。
/// <b>ただし 1 つの欄については最初に当たった 1 つだけを言う</b>（ADR-0047 の決定 10）——コードが空・書式違いなら、重複は数えない（<see cref="CodeProblemsAsync"/>）。
/// <b>前提の崩れた検査も飛ばす</b>（Claude の判断。docs/21 §2-6）——意味の凍結で断った欄は見ない（<see cref="MeaningFindings"/>）。
/// <b>同じ保存の別の行が凍結で断られたときは、その行の保存されている値で判定する</b>（<see cref="ChangedInSubmit"/>）。</para>
/// </remarks>
public sealed class MasterSubmitGate(MasterCodeStore store, MasterMeaningGate meaning)
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
        // **カナは名前の 2 倍**（旧 Q-26 の決定。2026-09-16）——
        // 「株式会社」4 字の読みは「カブシキガイシャ」8 字で、名前と同じ数にすると
        // **名前を上限いっぱいまで書いた科目が、その読みを入れられない**。
        new("Account", "accounts", "科目コード",
            [new("Name", "name", "科目名", MasterTextLength.MasterName),
             new("NameKana", "name_kana", "カナ", MasterTextLength.MasterNameKana)]),
        new("SubAccount", "sub_accounts", "補助科目コード",
            [new("Name", "name", "補助科目名", MasterTextLength.MasterName),
             new("NameKana", "name_kana", "カナ", MasterTextLength.MasterNameKana)],
            new("account_id", "Account")),
        // **部門・税区分・会計年度はカナを持たない**（デザインに欄が無い）。
        new("Department", "departments", "部門コード",
            [new("Name", "name", "部門名", MasterTextLength.MasterName)]),
        new("TaxCategory", "tax_categories", "税区分コード",
            [new("Name", "name", "税区分名", MasterTextLength.MasterName)]),
        // **会計年度の欄は `Name` ではなく `Label` である。** 決め打ちにすると、
        // ここだけ関門が黙って素通しになる（旧 Q-20 の問いは会計年度を「マスタの名前」に含めていた）。
        new("FiscalYear", "fiscal_years", "年度コード",
            [new("Label", "label", "年度名", MasterTextLength.MasterName)]),
    ];

    /// <summary>部品の組み立て。</summary>
    public static MasterSubmitGate Create(IDbAccessor dbAccessor, string dataSourceName)
        => new(new MasterCodeStore(dbAccessor, dataSourceName), MasterMeaningGate.Create(dbAccessor, dataSourceName));

    /// <summary>保存を包む。<paramref name="save"/> は CLB 本来の保存処理。</summary>
    public async Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        ArgumentNullException.ThrowIfNull(transactionData);
        ArgumentNullException.ThrowIfNull(save);

        // **意味の凍結の理由を先に置く**（上の注記）。
        var findings = await meaning.FindAsync(transactionData);
        var reasons = new List<string>(findings.Reasons);

        // **入れ物の名前ではなく、中身の名前で担当を決める**（qa/02 R16-16 の型）。
        foreach (var (data, adding) in transactionData.SelectMany(
                     d => d.Add.Select(r => (Row: r, Adding: true))
                           .Concat(d.Update.Select(r => (Row: r, Adding: false)))))
        {
            if (Coded.FirstOrDefault(m => m.ModuleName == data.Name) is CodedMaster master)
            {
                reasons.AddRange(await ReasonsForAsync(master, data, adding, findings, transactionData));
            }
        }

        if (reasons.Count > 0)
        {
            throw new MasterRejectedException(reasons);
        }

        return await save();
    }

    /// <param name="findings">
    /// 意味の凍結が見つけたもの。<b>凍結で断った欄を入力に持つ検査は飛ばす</b>——
    /// 変えられない値を検査して「こう直せ」と言うと、従っても通らない一手になる（<see cref="MeaningFindings"/>）。
    /// <b>行ごとに持つ</b>ので、補助科目の行を見るときに、同じ保存の勘定科目の行が凍結されたかも引ける。
    /// </param>
    private async Task<IReadOnlyList<string>> ReasonsForAsync(
        CodedMaster master, ModuleData data, bool adding, MeaningFindings findings,
        IReadOnlyList<ModuleSubmitData> transactionData)
    {
        var id = Id(data);
        var reasons = new List<string>();
        var frozen = findings.FrozenFieldsOf(data);
        var parentFrozen = master.Parent is CodedParent parent && frozen.Contains(parent.FieldName);

        // **補助科目の画面は「勘定科目」が最上段**なので、2 値の断りを先に言う（docs/21 §2-6「並びは画面の並び」）。
        // **同じ保存で勘定科目の値を変えていれば、変えたあとの値で判定する。ただし凍結で断った変更は数えない**（<see cref="ChangedInSubmit"/>）。
        if (!parentFrozen)
        {
            reasons.AddRange(await SubAccountUnderPlainAccountAsync(master, data, adding, findings, transactionData));
        }

        // **追加はコードを必ず伴う。** 画面は必ず送ってくるが、**API と取込（フェーズ 6。未設計）は列ごと落としうる**
        // （`code` の無い CSV）——**取込こそこの関門が守る経路である**（2026-09-09 の自己レビュー）。
        // 更新は差分しか届かない（qa/01 F-12）ので、載っていないことが正常である。
        if (adding && !data.Fields.ContainsKey("Code"))
        {
            reasons.Add($"「{master.CodeLabel}」を入れてください。");
        }

        // **コードの一意の範囲は親で決まる**（補助科目）ので、親を変えられない行ではコードの重複も数えない。
        if (!frozen.Contains("Code") && !parentFrozen)
        {
            reasons.AddRange(await CodeProblemsAsync(master, data, id));
        }

        reasons.AddRange(LongTextProblems(master, data));

        // **勘定科目の画面では「補助科目を使う」は名前より下**にある。
        if (!frozen.Contains("UsesSubAccount"))
        {
            reasons.AddRange(await SubAccountsLeftUnderAsync(master, data, id));
        }

        if (!frozen.Contains("IsCompanyWide"))
        {
            reasons.AddRange(await SecondCompanyWideDepartmentAsync(master, data, id));
        }

        if (!frozen.Contains("TaxationType") && !frozen.Contains("RateKind"))
        {
            reasons.AddRange(await InconsistentTaxCategoryAsync(master, data, id));
        }

        return reasons;
    }

    /// <summary>名前の長さ（docs/12 §2-2）。</summary>
    /// <remarks>
    /// <para><b>断るのは長すぎるときと、数えられない字が入っているときだけである。</b>
    /// 空を断るのは画面の <c>IsRequired</c> と DB の <c>NOT NULL</c> の仕事で、
    /// <b>長さの関門が「空です」と言い出すと責任の境目がぼやける</b>。</para>
    /// <para><b>更新は差分しか届かない</b>（qa/01 の F-12）ので、
    /// <b>載っていない＝触っていない</b>として素通しする。</para>
    /// <para><b>読む口は 1 つである。</b> 型で受けてから <c>ContainsKey</c> で見直す形にすると、
    /// <b>「載っているのに読めない」という通らない枝</b>が残る（<c>PartnerSubmitGate</c> と同じ作法）。
    /// <b>読めない型なら止める</b>——黙って <c>null</c> にすると、この検査だけが丸ごと素通しになる。</para>
    /// <para><b>前後の空白を落とし、落とした姿を差分に書き戻す</b>（コードと同じ。ADR-0047 の決定 5）。
    /// <b>比べるときだけ落とすと、関門が数えた長さと DDL が数える長さが食い違う</b>
    /// （qa/03 の L-14 の型。qa/02 の R45-02 で実際に踏んだ）。</para>
    /// </remarks>
    private static IEnumerable<string> LongTextProblems(CodedMaster master, ModuleData data)
    {
        // **欄ごとに上限が違う**（名前は 30、カナは 60。旧 Q-26 の決定。2026-09-16）。
        // **記述子から回す**——ここで欄名を決め打ちにすると、カナを足した日に片方だけ守られる。
        foreach (var coded in master.Texts)
        {
            if (!data.Fields.TryGetValue(coded.FieldName, out var found))
            {
                continue;
            }

            if (found is not TextFieldData text)
            {
                throw UnreadableFieldException.For(data.Name, coded.FieldName, found);
            }

            // **`null` は `null` のままにする**（空文字を書き込むと「無いは NULL」が崩れる。docs/20 §7）。
            if (text.Value is string value)
            {
                text.Value = MasterTextLength.Normalize(value);
            }

            if (MasterTextLength.DescribeProblem(coded.Label, text.Value, coded.MaxLength) is string problem)
            {
                yield return problem;
            }
        }
    }

    /// <summary>コードの書式と、大小を無視した重複（ADR-0047）。</summary>
    /// <remarks>
    /// <b>コードを触っていなくても、親が動いたら数え直す。</b> 補助科目を別の勘定科目へ移すと、
    /// コードは 1 字も変わらないのに<b>一意の範囲（勘定科目, コード）が変わる</b>ので、
    /// 移した先に同じコードがあれば重複になる。差分にはコードが載らない（qa/01 F-12）から、
    /// <b>保存されている字を読み直して数える</b>（2026-09-09 の自己レビュー。
    /// 親を読み直す穴と同じ家系で、こちらだけ残っていた）。
    /// <para><b>空・書式違いなら、重複は数えない</b>——正規化できない字で DB を引いても、
    /// 利用者が直す先は同じ欄なので、断りが 2 つに割れるだけである（1 つの欄については最初に当たった 1 つだけ——ADR-0047 の決定 10）。</para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> CodeProblemsAsync(CodedMaster master, ModuleData data, long? id)
    {
        var touched = Text(data, "Code") is string code ? MasterCode.Normalize(code) : null;
        var normalized = touched ?? await StoredCodeForParentMoveAsync(master, data, id);
        if (normalized is null)
        {
            return [];
        }

        if (touched is not null)
        {
            if (normalized.Length == 0)
            {
                return [$"「{master.CodeLabel}」を入れてください。"];
            }

            if (MasterCode.DescribeProblem(master.CodeLabel, normalized) is string problem)
            {
                return [problem];
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
            return [];
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

        return [$"「{master.CodeLabel}」の「{normalized}」は{reason}{howToFix}"];
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

    /// <summary>「全社共通」の部門は 1 つだけ（docs/15 §4-1）。</summary>
    /// <remarks>
    /// 見るのは DB に保存済みの行だけである。<b>同じ保存に「全社共通」の行を 2 つ載せる経路（取込・API）は関門では数えず、
    /// DDL の部分 UNIQUE インデックス（<c>ux_departments_company_wide</c>）が定型文で拒む</b>——画面は 1 行ずつしか保存しない。
    /// </remarks>
    private async Task<IReadOnlyList<string>> SecondCompanyWideDepartmentAsync(CodedMaster master, ModuleData data, long? id)
    {
        if (master.ModuleName != "Department" || Boolean(data, "IsCompanyWide") is not true)
        {
            return [];
        }

        var current = await store.CompanyWideDepartmentAsync(id);
        if (current is null)
        {
            return [];
        }

        // **ぶつかった相手を教える**（qa/02 R57-06）。コードの重複の断りは相手の字を見せているのに、
        // こちらだけ「いまなっている部門」と言うのは非対称で、利用者は一覧を探しに行くことになる。
        return [$"「全社共通」の部門は 1 つだけです。いま「全社共通」になっているのは部門名「{current.Value.Name}」（部門コード {current.Value.Code}）です。"
                + "そちらをオフにしてから、こちらをオンにしてください。"];
    }

    /// <summary>
    /// 課税の区分には税率区分が要り、課税でない区分には付かない（docs/11 §1。DDL の 2 本の <c>CHECK</c>）。
    /// </summary>
    /// <remarks>
    /// <b>2 つの欄をまたぐので、触っていない側は保存されている値を引く</b>（qa/01 F-12）。
    /// <b>新規は届いた値だけで判定できる</b>——両方が必ず載っているからである。
    /// </remarks>
    private async Task<IReadOnlyList<string>> InconsistentTaxCategoryAsync(CodedMaster master, ModuleData data, long? id)
    {
        if (master.ModuleName != "TaxCategory")
        {
            return [];
        }

        var taxationType = Text(data, "TaxationType");
        var rateKind = Text(data, "RateKind");
        if (taxationType is null && rateKind is null)
        {
            return [];
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
            return ["「課税区分」が「課税売上」「課税仕入」のときは「税率区分」が要ります。「税率区分」を選んでください。"];
        }

        return !taxable && hasRate
            ? ["「課税区分」が「課税売上」「課税仕入」でないので、「税率区分」は空にしてください。"]
            : [];
    }

    /// <summary>
    /// 補助科目を使わない勘定科目の下に、補助科目は作れない（docs/12 §2——ADR-0038 §3 の 2 値をマスタにも当てる読み）。
    /// </summary>
    /// <remarks>
    /// <b>2026-09-08 の回では明細の側しか塞いでいなかった</b>——
    /// マスタの画面からは、使わない設定の科目にも補助科目を足せた（2026-09-09 に塞いだ。qa/03 L-27）。
    /// </remarks>
    private async Task<IReadOnlyList<string>> SubAccountUnderPlainAccountAsync(
        CodedMaster master, ModuleData data, bool adding, MeaningFindings findings, IReadOnlyList<ModuleSubmitData> transactionData)
    {
        // **見るのは、新しい行と、別の勘定科目へ移ってくる行だけ**（2026-09-24 に改めた）。
        // その科目に残ったままの行は、科目の側（<see cref="SubAccountsLeftUnderAsync"/>）が数える——ここでも見ると、
        // 同じ食い違いを 2 度言い、片方は従っても通らない（科目の側は DB の行を数えるので、ここに従って移しても消えない）。
        // **規則より前に作られた行**（オフの科目の下の補助科目。開発機に実在する）**も、移さない更新なら通す**——
        // その行が使用中なら「勘定科目」は凍結されていて移せないので、見ると名前も「有効」も直せない行き止まりになる。
        if (master.ModuleName != "SubAccount" || master.Parent is not CodedParent parent)
        {
            return [];
        }

        // **親は差分の字面から直に解く**——<see cref="ParentAsync"/> は数値で読めない更新を保存されている親へ落とす（重複の範囲のための作法）ので、
        // 同じ保存で作る科目（仮の識別子）へ移す更新を、移す前の親で判定してしまう（2026-09-24 の自己レビュー。オフの新しい科目へ移せた）。
        // **欄が無い・空なら、移っていない**（更新は差分しか届かない——qa/01 F-12。新しい行なら外部キーと必須の検査が止める）。
        var key = ParentKey(parent, data);
        if (string.IsNullOrEmpty(key))
        {
            return [];
        }

        // **新しい行は、更新の側に混ざっていても新しい行**（仮の識別子——<c>MasterMeaningGate</c> と同じ扱い）。
        var id = Id(data);
        var target = long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (long?)null;
        if (!adding && id is long existing && target is long moving && await StoredParentAsync(master, parent, existing) == moving)
        {
            return [];
        }

        // **科目が実在しないときは、ここで止めない**——外部キーが拒む。
        // 実在の断りは 1 か所（DB）に置き、ここは 2 値の規則だけを見る。
        var host = target is long accountId
            ? ChangedInSubmit(await store.SubAccountHostAsync(accountId), accountId, findings, transactionData)
            : HostInSubmit(key, transactionData);

        // **結果（「補助科目を作れません」）は言わない**——見出し（「登録できません」）の繰り返しになる（docs/21 §2-6 の「各文が結果を繰り返さない」）。
        // **勘定科目を名指す**——束ねた断りの中では「この勘定科目」が何を指すか読めない。
        // **一手は「別の勘定科目を選ぶ」**——「補助科目を使う」は使用中の科目では変えられない（ADR-0038 §2）ので、
        // 「オンにしてください」は通らないことがある。
        if (host is not { UsesSubAccount: false } plain)
        {
            return [];
        }

        // **名指しはあるものだけで組む**——API と取込（未設計）は名前の欄を落としうる（同じ保存で作る科目は、差分に載った字しか無い）。
        var label = string.Join(" ", new[] { plain.Code, plain.Name }.Where(part => !string.IsNullOrEmpty(part)));
        return [$"「勘定科目」の「{label}」は「補助科目を使う」がオフです。「補助科目を使う」がオンの勘定科目を選んでください。"];
    }

    /// <summary>
    /// 既にある勘定科目の「補助科目を使う」を<b>同じ保存で変えているなら、変えたあとの値</b>で判定する（取込・API）。
    /// </summary>
    /// <remarks>
    /// <para><b>DB の値だけで判定すると、変える前の値で決まる</b>——未使用の科目をオンにする行とその下に補助科目を足す行は、足す行が断られ、
    /// オフにする行と足す行は両方通っていた（2026-09-24 の自己レビュー。docs/12 の保留リストにあった穴）。</para>
    /// <para><b>凍結で断った変更は数えない</b>——使用中の科目の「補助科目を使う」は変えられない（ADR-0038 §2）ので、
    /// DB の値が残る。変えたあとの値で判定すると、断りに従って元に戻した 2 回目に初めて 2 値の断りが出る
    /// （docs/21 §2-6「前提の崩れた検査は飛ばす」の逆向き——前提そのものを凍結が決める）。</para>
    /// </remarks>
    /// <remarks>
    /// <para><b>識別子は数で突き合わせる</b>——字面で比べると、補助科目の側が「05」のように書いた親と、勘定科目の行の「5」が別物になり、
    /// 変えたあとの値が見えない（2026-09-24 の自己レビュー）。</para>
    /// </remarks>
    private static (bool UsesSubAccount, string? Code, string? Name)? ChangedInSubmit(
        (bool UsesSubAccount, string? Code, string? Name)? stored, long accountId, MeaningFindings findings,
        IReadOnlyList<ModuleSubmitData> transactionData)
    {
        var account = transactionData
            .SelectMany(d => d.Update)
            .FirstOrDefault(d => d.Name == "Account" && Id(d) == accountId);

        return stored is not null
               && account is not null
               && !findings.FrozenFieldsOf(account).Contains("UsesSubAccount")
               && Boolean(account, "UsesSubAccount") is bool uses
            ? (uses, stored.Value.Code, stored.Value.Name)
            : stored;
    }

    /// <summary>
    /// <b>既にある勘定科目の「補助科目を使う」をオフにするとき、その下に補助科目が残っていれば断る</b>（docs/12 §2 のマスタの読みの、科目の側）。
    /// </summary>
    /// <remarks>
    /// <para><b>2026-09-24 まで、この規則は補助科目の側からしか見ていなかった</b>——補助科目を持つ未使用の科目をオフにでき、
    /// マスタの側の 2 値（docs/12 §2）が崩れていた（帳簿は計上の関門が守っていた）。</para>
    /// <para><b>断るのは、保存されている値がオンで、それをオフにする保存だけ</b>——既にオフの科目（規則より前に作られた行を持つもの）に
    /// 「オンのままにしてください」と言うと事実に反する（2026-09-24 の自己レビュー）。</para>
    /// <para><b>数えるのは DB の行だけ</b>——同じ保存で補助科目を足す行・移ってくる行は、補助科目の側（<see cref="SubAccountUnderPlainAccountAsync"/>）が断るので、
    /// ここでも数えると同じ食い違いを 2 度言う。<b>同じ保存で補助科目を別の科目へ移す行も数えたまま</b>断る——
    /// <b>CLB が同じ保存の追加と更新をどの順に当てるかは実測していない</b>（qa/01 F-41 の ⑤）。この規則に DB のトリガを置くなら、
    /// 科目の更新が先に当たっても拒まれない形にしておく。**守りの向きは補助科目の側と逆である**——あちらは同じ保存の科目の変更を判定に入れて通す
    /// （オンにする科目の下に足す）が、こちらは同じ保存の補助科目の変更を判定に入れずに断る。
    /// 断りの一手は「先に移す」で、別の保存に分ければ通る（API と取込（未設計）だけの形。画面では補助科目は勘定科目とは別の画面で保存する）。</para>
    /// <para><b>無効の補助科目も数える</b>（<see cref="MasterCodeStore.CountSubAccountsUnderAsync"/>）。
    /// <b>補助科目は画面から削除できない</b>（`SubAccount.mod.json` の <c>CanDelete</c> が <c>false</c>）ので、一手は「別の科目へ移す」だけを言う——
    /// 未使用の科目の補助科目は未使用なので、「勘定科目」を変えられる（明細は科目と補助科目の組を持つ。<b>補助科目が明細の勘定科目に属する限り</b>——
    /// この規則は関門だけが守るので、CLB を通さない書き込みで崩されると移せない行が生まれうる）。</para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> SubAccountsLeftUnderAsync(CodedMaster master, ModuleData data, long? id)
    {
        if (master.ModuleName != "Account"
            || id is not long account
            || Boolean(data, "UsesSubAccount") is not false
            || await store.SubAccountHostAsync(account) is not { UsesSubAccount: true })
        {
            return [];
        }

        // **「この勘定科目」で言う**——画面は 1 回の保存で 1 つの科目しか送らない。API で複数の科目を束ねた断りでは何を指すか曖昧になるが、
        // 意味の凍結の文（<see cref="MasterMeaningGate"/>）と同じ形に揃えた（2026-09-24 の自己レビューで挙がり、直さずに残した）。
        // **無効の補助科目も数えると言う**——伝票の候補に出ない補助科目の数は、利用者が画面で数えても合わない。
        // **直しに行く画面は「補助科目マスタ」と名指す**——メニューの「マスタ/」の下に無い補助科目も「〜マスタ」と呼ぶ（docs/21 §5。開発者の決定）。
        // 2026-09-24 の自己レビューで一度メニューの道順（「会計」の「補助科目」の画面）に変えたが、その決定に反していたので戻した。
        var count = await store.CountSubAccountsUnderAsync(account);
        return count == 0
            ? []
            : [$"この勘定科目の下には補助科目が {count.ToString("N0", CultureInfo.InvariantCulture)} 件あります（「有効」がオフのものも数えています）。"
               + "「補助科目を使う」はオンのままにしてください。"
               + "オフにするなら、先に補助科目マスタで、それらの補助科目の「勘定科目」を「補助科目を使う」がオンの別の勘定科目に変えてください。"
               + "要らない補助科目も、先に移してから、その補助科目の「有効」をオフにしてください。"];
    }

    /// <summary>
    /// 保存されている補助科目の親（勘定科目）の識別子。<b>数で比べる</b>——差分の「05」と保存の「5」を同じ親と見る。
    /// </summary>
    private async Task<long?> StoredParentAsync(CodedMaster master, CodedParent parent, long id)
        => long.TryParse(
               Stored(await store.FindStoredAsync(master, id, [parent.Column]), parent.Column),
               NumberStyles.Integer,
               CultureInfo.InvariantCulture,
               out var stored)
            ? stored
            : null;

    /// <summary>
    /// 同じ保存の中で作られている勘定科目の「補助科目を使う」とコード・名前。見つからなければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>科目と補助科目を同じ保存で作る形（API。取込は未設計）では、親がまだ DB に無い</b>
    /// （仮の識別子。qa/01 C-08）。DB を引くだけだと <b>マスタの側の 2 値（docs/12 §2）が丸ごと消える</b>——
    /// この規則には DB 側の受け皿が無い（<c>uses_sub_account</c> を見るトリガは明細の側だけ）ので、
    /// <b>ここが唯一の守りである</b>（2026-09-09 の自己レビュー）。
    /// </remarks>
    /// <param name="key">親の字面。<b>空でない</b>——空なら呼び手が先に見送る（移っていない）。</param>
    private static (bool UsesSubAccount, string? Code, string? Name)? HostInSubmit(
        string key, IReadOnlyList<ModuleSubmitData> transactionData)
    {
        // **入れ物の名前ではなく、中身の名前で探す**（qa/02 R16-16 の型）。
        var parent = transactionData
            .SelectMany(d => d.Add.Concat(d.Update))
            .FirstOrDefault(d => d.Name == "Account" && IdText(d) == key);

        // **欄が載っていなければ、DB の既定と同じオフとみなす**（`uses_sub_account ... DEFAULT 0`）——
        // 載っていない新しい科目はオフで作られる——CLB が欄を書かなければ DB の既定（0）、画面の初期値（false。qa/01 F-10）を書いても同じオフ（新しい行の送信の中身は未実測）。
        // 見送ると、その下に補助科目ができる（2026-09-24 の自己レビュー）。
        return parent is null
            ? null
            : (Boolean(parent, "UsesSubAccount") ?? false, Text(parent, "Code"), Text(parent, "Name"));
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
