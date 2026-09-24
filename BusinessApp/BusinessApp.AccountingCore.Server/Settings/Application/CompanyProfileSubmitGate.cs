namespace BusinessApp.AccountingCore.Server.Settings.Application;

using BusinessApp.AccountingCore.Settings;
using BusinessApp.Partners;
using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 自社情報を保存するときの関門（docs/12 の自社情報）。
/// </summary>
/// <remarks>
/// <para><b>自社の法人番号を、取引先の法人番号と同じ検査に通す</b>のがこの関門の役目である
/// （2026-08-27 の自己レビュー R12-11。「事業所情報の画面を触る回に」と期限を置いてあった）。
/// それまでは書式も検査用数字も誰も見ておらず、<b>打ち間違えた番号がそのまま保存できた</b>。</para>
/// <para><b>放置できない理由は取引先側と違う。</b> 取引先の法人番号は名寄せの自然キーだが、
/// 自社の法人番号は<b>帳簿と申告書に載る値</b>である。誤ったまま出ても画面には何も出ない。</para>
/// <para><b>判定と文言は <see cref="CorporateNumber"/> が持つ。</b> ここは
/// 「空欄をどう保存するか」と「どの例外で止めるか」だけを決める。
/// 同じ判定を 2 か所に書くと、片方だけ厳しくなる。</para>
/// <para><b>取引先部品の型を使うが、依存の向きは正しい</b>——会計コア → 取引先の一方通行である
/// （ADR-0029 §2）。法人番号は制度が定める値で、取引先部品が最初に必要としたので今そこに在る。</para>
/// </remarks>
public sealed class CompanyProfileSubmitGate
{
    /// <summary>自社情報のモジュール名。</summary>
    public const string ModuleName = "CompanyProfile";

    /// <summary>
    /// <b>長さの上限を持つ文字の欄</b>（docs/12 §2-2。旧 Q-26 の決定。2026-09-16）。
    /// </summary>
    /// <remarks>
    /// <para><b>欄の呼び名と列は画面の写しである</b>
    /// （docs/20 §4 の「已むを得ない重複」。ずれていないことは
    /// <c>CompanyProfileSubmitGateTests.写した呼び名と列はデザインと一致する</c> が見る）。</para>
    /// <para><b>3 つに割ってあるのは、断りの並びを画面の並びに合わせるためである</b>
    /// （<c>PartnerSubmitGate</c> と同じ理由——並びが画面と食い違うと、利用者は「①…②…」を読みながら上と下を往復させられる）。
    /// 画面の並びは 会社名 → カナ → 法人番号 → 代表者名 → 郵便番号 → 住所 → 電話番号 → 決算月 である。</para>
    /// <para><b>郵便番号はここに載せない</b>——長さではなく<b>書式</b>の規則だからである（<see cref="PostalCode"/>）。
    /// <b>電話番号は逆に長さだけ</b>で、書式は置かない（内線・国番号・区切り記号の形が割れる）。</para>
    /// <para><b>法人番号もここに載せない</b>——桁ちょうどの別の規則で、<see cref="CorporateNumber"/> が持つ。</para>
    /// </remarks>
    /// <summary>法人番号より上にある文字の欄。</summary>
    private static readonly (string Field, string Column, string Label, int Max)[] BeforeCorporateNumber =
    [
        ("Name", "name", "会社名", MasterTextLength.CompanyName),
        ("NameKana", "name_kana", "カナ", MasterTextLength.CompanyNameKana),
    ];

    /// <summary>法人番号と郵便番号の間にある文字の欄。</summary>
    private static readonly (string Field, string Column, string Label, int Max)[] BetweenCorporateNumberAndPostalCode =
    [
        ("RepresentativeName", "representative_name", "代表者名", MasterTextLength.RepresentativeName),
    ];

    /// <summary>郵便番号より下にある文字の欄。</summary>
    private static readonly (string Field, string Column, string Label, int Max)[] AfterPostalCode =
    [
        ("Address", "address", "住所", MasterTextLength.CompanyAddress),
        ("PhoneNumber", "phone_number", "電話番号", MasterTextLength.PhoneNumber),
    ];

    /// <summary>
    /// 3 つを繋いだ全体（デザインとの突き合わせが使う）。
    /// </summary>
    /// <remarks>
    /// <b>3 つの後に宣言する。</b> 静的フィールドの初期化は<b>宣言順</b>なので、
    /// 先に置くと中身が <c>null</c> のまま繋ぐことになる。
    /// </remarks>
    public static readonly (string Field, string Column, string Label, int Max)[] TextFields =
    [
        .. BeforeCorporateNumber, .. BetweenCorporateNumberAndPostalCode, .. AfterPostalCode,
    ];

    /// <summary>決算月の下限（1 月）。</summary>
    private const int FirstMonth = 1;

    /// <summary>決算月の上限（12 月）。</summary>
    private const int LastMonth = 12;

    /// <summary>保存を包む。<paramref name="save"/> は CLB 本来の保存処理。</summary>
    public async Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        ArgumentNullException.ThrowIfNull(transactionData);
        ArgumentNullException.ThrowIfNull(save);

        var reasons = ProfilesIn(transactionData).SelectMany(Reasons).ToList();
        if (reasons.Count > 0)
        {
            throw new CompanyProfileRejectedException(reasons);
        }

        return await save();
    }

    /// <summary>
    /// 追加と更新の両方を見る。
    /// </summary>
    /// <remarks>
    /// <b>入れ物の名前ではなく、中身の名前で担当を決める</b>（<c>ModuleSubmitData.ModuleName</c> で
    /// 絞ると、別モジュールの保存に混ざった自社情報を見落とす。qa/02 R16-16 と同じ型）。
    /// 自社情報は 1 行しか持てない（<c>CHECK (id = 1)</c>。ADR-0005）ので実際に来るのは更新だが、
    /// <b>追加を見ない関門は、初期データを入れ直した環境で穴になる</b>。
    /// </remarks>
    private static IEnumerable<ModuleData> ProfilesIn(IReadOnlyList<ModuleSubmitData> transactionData)
        => transactionData
            .SelectMany(d => d.Add.Concat(d.Update))
            .Where(d => d.Name == ModuleName);

    /// <summary>
    /// 断る理由を<b>全部</b>集める。<b>並びは画面の並びに合わせる</b>（<c>PartnerSubmitGate</c> と同じ理由）。
    /// </summary>
    /// <remarks>
    /// <para><b>欄をまたいで束ねる</b>（docs/21 §2-6 の (b)。開発者の決定。2026-09-20。寄せたのは 2026-09-24）。
    /// <b>欄どうしに依存は無い</b>ので、全部を見る。</para>
    /// <para><b>1 つの欄については、最初に当たった 1 つだけを言う</b>（開発者の指示。2026-09-08。逐語「即エラー。次へ進まない」。
    /// 欄ごとと読むのは ADR-0047 の 10——Claude の決め）。束ねるのは欄をまたぐ断りだけで、ここは変わらない。</para>
    /// </remarks>
    private static IEnumerable<string> Reasons(ModuleData data)
        => [
            .. LongTextProblems(data, BeforeCorporateNumber),
            .. CorporateNumberProblems(data),
            .. LongTextProblems(data, BetweenCorporateNumberAndPostalCode),
            .. PostalCodeProblems(data),
            .. LongTextProblems(data, AfterPostalCode),
            .. FiscalYearEndMonthProblems(data),
        ];

    /// <summary>
    /// 文字の欄の上限（docs/12 §2-2）。
    /// </summary>
    /// <remarks>
    /// <para><b>載っていない欄は触っていない。</b> CLB は変更されたフィールドしか送ってこない（qa/01 F-11）。</para>
    /// <para><b>前後の空白を落とし、落とした姿を差分に書き戻す</b>（取引先の関門と同じ）。
    /// <b>比べるときだけ落とすと、関門が数えた長さと DDL が数える長さが食い違う</b>（qa/03 の L-14）。</para>
    /// </remarks>
    private static IEnumerable<string> LongTextProblems(ModuleData data, (string Field, string Column, string Label, int Max)[] fields)
    {
        foreach (var (field, _, label, max) in fields)
        {
            if (!data.Fields.TryGetValue(field, out var found))
            {
                continue;
            }

            // **読めない型は断る**（<c>MasterSubmitGate</c> と同じ。fail-open にしない）——
            // `as` で `null` に落とすと「触られていない」と見分けが付かず、
            // **欄の型を変えた日に、その欄を見る検査がまとめて素通しへ落ちる**（`UnreadableFieldException` の注記）。
            if (found is not TextFieldData text)
            {
                throw UnreadableFieldException.For(data.Name, field, found);
            }

            // **`null` は `null` のままにする**（空文字を書き込むと「無いは NULL」が崩れる。docs/20 §7）。
            // **空白だけの値を NULL へ寄せるのは <c>BlankTextNormalizer</c> の仕事**で、
            // 会計コアの入口で関門より先に走っている——ここで二重に寄せない。
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
    /// 郵便番号の書式（<see cref="PostalCode"/>）。
    /// </summary>
    /// <remarks>
    /// <b>空欄は NULL に倒す</b>（法人番号と同じ作法）——空文字と「入っていない」を DB で区別させない。
    /// <b>書式が合わない値は断る。</b> 書き換えて直すことはしない
    /// （「1234567」に「-」を足さない。ADR-0047 の線）。
    /// </remarks>
    private static IEnumerable<string> PostalCodeProblems(ModuleData data)
    {
        if (!data.Fields.TryGetValue("PostalCode", out var found))
        {
            return [];
        }

        // **読めない型は断る**（上の文字の欄と同じ）。
        if (found is not TextFieldData text)
        {
            throw UnreadableFieldException.For(data.Name, "PostalCode", found);
        }

        var value = PostalCode.Normalize(text.Value);
        if (value.Length == 0)
        {
            text.Value = null;
            return [];
        }

        text.Value = value;

        return PostalCode.DescribeProblem(value) is string problem ? [problem] : [];
    }

    /// <summary>
    /// 決算月は 1〜12 の月である（DDL の <c>CHECK (fiscal_year_end_month BETWEEN 1 AND 12)</c>）。
    /// </summary>
    /// <remarks>
    /// <para><b>13 を入れると定型文になっていた</b>（qa/03 L-28。2026-09-04 の探索的テストで実測）。
    /// 画面の <c>Min</c> / <c>Max</c> で止める手もあるが、<b>そちらは CLB 自身の文言が出る</b>
    /// （qa/01 A-10。CLB 側の話で FB-015）ので、利用者の語で断るにはここが要る。</para>
    /// <para><b>小数も断る。</b> 「1.5 月」は月ではない。CLB の数値欄は小数を受け取れるので、
    /// ここで見ないと DB の <c>CHECK</c>（<c>BETWEEN</c> は 1.5 を通す）も素通りする。</para>
    /// <para><b>空も断る。</b> DB の <c>NOT NULL</c> に投げると定型文になり、
    /// 「13 は利用者の語で断るのに、空は枠組みの言葉」という食い違いが同じ欄で起きる。</para>
    /// </remarks>
    private static IEnumerable<string> FiscalYearEndMonthProblems(ModuleData data)
    {
        if (!data.Fields.TryGetValue("FiscalYearEndMonth", out var field))
        {
            return [];
        }

        // **空にした保存も断る**（2026-09-09 の自己レビュー）。DB の NOT NULL に投げると定型文になり、
        // 「13 は利用者の語で断るのに、空は枠組みの言葉」という食い違いが同じ欄で起きる。
        // **読めない型も断る**——検査できない値を通すのは fail-open である。
        if (field is not NumberFieldData month || month.Value is not decimal value)
        {
            return ["「決算月」を入れてください。"];
        }

        return value != decimal.Truncate(value) || value < FirstMonth || value > LastMonth
            ? [$"「決算月」は {FirstMonth} から {LastMonth} までの整数で入れてください。"]
            : [];
    }

    private static IEnumerable<string> CorporateNumberProblems(ModuleData data)
    {
        // CLB は変更されたフィールドしか送ってこない（qa/01 F-11）。
        // 送られていない項目は「変えていない」なので、検査しない。
        if (!data.Fields.TryGetValue("CorporateNumber", out var field))
        {
            return [];
        }

        // **読めない型は断る**（2026-09-16 に揃えた。それまでここだけ素通しだった）。
        if (field is not TextFieldData number)
        {
            throw UnreadableFieldException.For(data.Name, "CorporateNumber", field);
        }

        var value = CorporateNumber.Normalize(number.Value);

        // **空欄は空文字ではなく NULL で保存する。**
        // **company_profile.corporate_number に CHECK は無い**（partners にはある。
        // 揃えるのはフェーズ 2.5 の C。qa/02 R25-11）ので、空文字でも DB は受け取ってしまう。
        // それでも NULL に倒すのは、**空文字と「入っていない」を DB で区別させないため**である
        // ——区別が付かない 2 通りの「空」が混ざると、突合も表示も両方を扱うことになる。
        // 取引先側は CHECK があるので、同じ形にしておくと移送しても壊れない（qa/03）。
        if (value.Length == 0)
        {
            number.Value = null;
            return [];
        }

        // **貼り付けで紛れ込んだ空白を落として保存する。** 落とさずに通すと、
        // 同じ番号が 2 通りの文字列で保存される。
        number.Value = value;

        return CorporateNumber.DescribeProblem(value) is string problem ? [problem] : [];
    }
}
