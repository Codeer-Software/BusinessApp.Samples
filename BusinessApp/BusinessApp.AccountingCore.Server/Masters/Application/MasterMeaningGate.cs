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
            UsageCounter: "枚",
            NewRowCaution: "ただし、新しい取引先にすると、同じ相手の残高と登録番号の履歴が 2 つに分かれます"),
    ];

    /// <summary>部品の組み立て。</summary>
    public static MasterMeaningGate Create(IDbAccessor dbAccessor, string dataSourceName)
        => new(new MasterUsageStore(dbAccessor, dataSourceName));

    /// <summary>
    /// この保存で断る理由を<b>全部</b>集め、<b>変えられないと断った欄</b>を行ごとに添える（docs/21 §2-6 の (b)）。
    /// </summary>
    /// <remarks>
    /// <para><b>利用者が直せる違反は投げない。</b> 断るのは <see cref="MasterSubmitGate"/> で、値の断りと束ねて 1 回で返す——
    /// ここで投げると、意味の凍結を直して保存し直した利用者が、次に値の断りを受ける。</para>
    /// <para><b>並びは意味の凍結が先</b>——こちらは変えた内容のままでは通す手が無い（元に戻すか、新しい行を作るしかない）が、
    /// 値の断りは値を直せば通るので、重いほうを先に読ませる。</para>
    /// <para><b>断った欄を返すのは、<see cref="MasterSubmitGate"/> の値の検査がその欄を見ないため</b>（docs/21 §2-6 の「前提の崩れた検査は飛ばす」）——
    /// 使用中の税区分の「課税区分」を変えた保存に「「税率区分」を選んでください」まで言うと、
    /// <b>従っても通らない一手</b>を並べることになる（「課税区分」そのものが変えられない）。</para>
    /// <para><b>壊れた要求（識別子が読めない更新）はその場で止める。</b> 利用者が直せる違反ではないので束ねない——
    /// 値の検査まで進めると、同じ識別子を読み損ねて別の例外になる。</para>
    /// </remarks>
    public async Task<MeaningFindings> FindAsync(IReadOnlyList<ModuleSubmitData> transactionData)
    {
        ArgumentNullException.ThrowIfNull(transactionData);

        var findings = new MeaningFindings();

        // **入れ物の名前ではなく、中身の名前で担当を決める**（qa/02 R16-16 の型。
        // 親子の保存は 1 つの ModuleSubmitData に混ざって届く——qa/01 F-11）。
        // 更新だけを見る——新規の行は計上済みの明細から参照されえない。
        foreach (var data in transactionData.SelectMany(d => d.Update))
        {
            if (Guarded.FirstOrDefault(g => g.ModuleName == data.Name) is GuardedMaster master)
            {
                await FindInAsync(master, data, findings);
            }
        }

        return findings;
    }

    private async Task FindInAsync(GuardedMaster master, ModuleData data, MeaningFindings findings)
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

        var frozen = changed.Where(c => !master.OneWay.Any(o => o.Column == c)).ToList();

        // **一方通行の列は、緩める向きだけを拒む**（docs/15 §1-2）。
        // **「1」以外はすべて緩めたと見なす**（fail-closed）。読めない型・空の真偽で
        // 素通りすると、フィールドの型が変わった日にこの規則だけが静かに消える
        // （Submitted の注記と同じ理由。qa/02 R26-03）。
        // **変わった列を 1 つずつ見る**——1 度の保存で片方をオン・片方をオフにされても取りこぼさない。
        var loosened = changed
            .Where(c => master.OneWay.Any(o => o.Column == c) && Submitted(data.Fields[c.FieldName]) != "1")
            .Select(c => master.OneWay.Single(o => o.Column == c))
            .ToList();

        if (frozen.Count == 0 && loosened.Count == 0)
        {
            return;
        }

        findings.Add(
            data,
            frozen.Select(c => c.FieldName).Concat(loosened.Select(o => o.Column.FieldName)),
            InUse(master, used, frozen, loosened));
    }

    /// <summary>
    /// 使用中の行の意味を変えようとしたときの断り。<b>1 つの行には 1 つの文で言う。</b>
    /// </summary>
    /// <remarks>
    /// <para>文言の形は ADR-0038 §4——<b>何件あるか</b>と<b>次に何をすればよいか</b>を入れる。
    /// 数える単位は「仕訳明細」（伝票ではない。ADR-0017）。**「仕訳」を単独で画面に出さない**（同 ADR）ので、
    /// 締めは「以後の振替伝票ではそちらを選ぶ」と言う——部門・税区分は「記帳する先」ではなく明細で選ぶものなので、
    /// 4 マスタで成り立つ動詞にする。</para>
    /// <para><b>まず「この保存を通す一手」を言う</b>（元に戻す・オンのままにする）。束ねた断りの中では、
    /// 別の欄を直して保存し直した利用者が、この断りにもう一度当たる——<b>別の道（新しい行を作る）だけを言うと、
    /// この保存を通す手が読めない</b>（2026-09-24 の自己レビュー）。</para>
    /// <para><b>意味を決める列と一方通行の列を同じ行で触ったら、1 つの文で言う。</b> 2 つに分けると同じ書き出し
    /// （「この勘定科目は計上済みの…で使われている」）が 2 回並び、しかも「新しい勘定科目を使え」と
    /// 「この勘定科目を使う明細には…」が食い違って読める。<b>一方通行の列の締め（その列が要る理由）は、
    /// 意味を決める列を触っていないときだけ言う</b>——触っていれば、締めは新しい行を作る道になる。</para>
    /// <para><b>一方通行の列に「変えられません」とは言わない。</b> 厳しくする向き（オフ → オン）はいつでも通るので、
    /// 両方できないと読まれると、規則を採り入れようとする利用者まで止めてしまう（docs/21 §2-3）。
    /// <b>「オンにするのはいつでもできる」もここに書かない。</b> それを知りたいのは<b>これからオンにする人</b>で、
    /// この断りに出会うのはオフを押した人である——置き場所は画面の注記のほう（勘定科目の詳細）。</para>
    /// </remarks>
    private static string InUse(
        GuardedMaster master, long used, IReadOnlyList<GuardedColumn> frozen, IReadOnlyList<OneWayColumn> loosened)
    {
        var text = new System.Text.StringBuilder(
            $"この{master.Label}は計上済みの{master.UsageUnit} {used.ToString("N0", CultureInfo.InvariantCulture)} {master.UsageCounter}で使われています。");

        if (frozen.Count > 0)
        {
            text.Append($"{string.Join("・", frozen.Select(c => $"「{c.Label}」"))}は変えられないので、元に戻してください。");
        }

        foreach (var column in loosened)
        {
            text.Append($"「{column.Column.Label}」はオフにできないので、オンのままにしてください。{column.Harm}。");
        }

        if (frozen.Count == 0)
        {
            text.Append(string.Concat(loosened.Select(column => $"{column.Instead}。")));
            return text.ToString();
        }

        text.Append($"変えた内容で使うなら、新しい{master.Label}を作って、以後の振替伝票ではそちらを選んでください。");

        // **一方通行の列も緩めていたら、新しい行でもそれはオンにすると言う**——「変えた内容」にオフが含まれ、
        // 新しい行をオフで作ると、上で述べた害がそのまま起きる（2026-09-24 の自己レビュー）。
        if (loosened.Count > 0)
        {
            text.Append(
                $"新しい{master.Label}でも{string.Join("・", loosened.Select(c => $"「{c.Column.Label}」"))}はオンにしてください。");
        }

        if (master.NewRowCaution is string caution)
        {
            text.Append($"{caution}。");
        }

        return text.ToString();
    }


    /// <summary>
    /// 差分に載った値を、保存されている値と比べられる字面にする。
    /// </summary>
    /// <remarks>
    /// <para><b>読めない型・空の真偽は「変えた」と見なす</b>（<c>null</c> を返し、どの保存値とも一致しない）。
    /// 差分に載っている以上その欄は触られており、読めないからと素通しにすると、
    /// フィールドの型が変わった日に関門ごと消える（取引先の関門で実際に起きた型。qa/02 R26-03）。</para>
    /// <para><b>文字列は前後の空白を落として、差分に書き戻す。</b>
    /// 画面も <c>ShouldTrimAfterEdit</c> で落とすが、<b>画面は経路の 1 本でしかない</b>——
    /// 投入 API から空白つきで来たとき、比べるときだけ落とすと「同じ」と通した値を
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
