namespace BusinessApp.AccountingCore.Server.Settings.Application;

using BusinessApp.Partners;

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

        foreach (var data in ProfilesIn(transactionData))
        {
            Reject(data);
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

    private static void Reject(ModuleData data)
    {
        RejectBadFiscalYearEndMonth(data);
        RejectBadCorporateNumber(data);
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
    private static void RejectBadFiscalYearEndMonth(ModuleData data)
    {
        if (!data.Fields.TryGetValue("FiscalYearEndMonth", out var field))
        {
            return;
        }

        // **空にした保存も断る**（2026-09-09 の自己レビュー）。DB の NOT NULL に投げると定型文になり、
        // 「13 は利用者の語で断るのに、空は枠組みの言葉」という食い違いが同じ欄で起きる。
        // **読めない型も断る**——検査できない値を通すのは fail-open である。
        if (field is not NumberFieldData month || month.Value is not decimal value)
        {
            throw new CompanyProfileRejectedException("「決算月」を入れてください。");
        }

        if (value != decimal.Truncate(value) || value < FirstMonth || value > LastMonth)
        {
            throw new CompanyProfileRejectedException(
                $"「決算月」は {FirstMonth} から {LastMonth} までの整数で入れてください。");
        }
    }

    private static void RejectBadCorporateNumber(ModuleData data)
    {
        // CLB は変更されたフィールドしか送ってこない（qa/01 F-11）。
        // 送られていない項目は「変えていない」なので、検査しない。
        if (!data.Fields.TryGetValue("CorporateNumber", out var field)
            || field is not TextFieldData number)
        {
            return;
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
            return;
        }

        // **貼り付けで紛れ込んだ空白を落として保存する。** 落とさずに通すと、
        // 同じ番号が 2 通りの文字列で保存される。
        number.Value = value;

        if (CorporateNumber.DescribeProblem(value) is string problem)
        {
            throw new CompanyProfileRejectedException(problem);
        }
    }
}
