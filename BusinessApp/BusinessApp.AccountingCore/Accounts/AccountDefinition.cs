namespace BusinessApp.AccountingCore.Accounts;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 検証・集計に必要な範囲の勘定科目（docs/04 §6）。
/// 画面表示のための属性（決算書表示区分・並び順など）はここに持ち込まない。
/// </summary>
/// <param name="Id">科目の識別子（DB の主キー）。</param>
/// <param name="Code">科目コード（4 桁）。利用者が見る自然キー。</param>
/// <param name="Name">科目名。</param>
/// <param name="Category">科目区分。部門の要否と決算振替がこれに依存する。</param>
/// <param name="DefaultTaxCategoryId">
/// 入力時の初期値としての税区分。<b>値が入っていない行の穴埋めに使わない</b>（docs/04 §6）。
/// </param>
/// <param name="RequiresSubAccount">補助科目を使う科目か。使う科目では補助科目の指定を必須にする。</param>
/// <param name="IsContra">
/// 評価勘定（控除科目）か。減価償却累計額・貸倒引当金・売上値引戻り高・期末棚卸高のように、
/// <b>通常残高が科目区分と逆</b>の科目がある。
/// </param>
/// <param name="IsActive">入力候補に出すか。false でも過去データの表示・検索は妨げない。</param>
public sealed record AccountDefinition(
    AccountId Id,
    string Code,
    string Name,
    AccountCategory Category,
    TaxCategoryId? DefaultTaxCategoryId = null,
    bool RequiresSubAccount = false,
    bool IsContra = false,
    bool IsActive = true)
{
    /// <summary>
    /// この科目の残高が増える側。評価勘定は科目区分と逆になる。
    /// </summary>
    /// <remarks>
    /// <b>科目区分だけから決めてはいけない。</b> 減価償却累計額は資産だが貸方残、
    /// 売上値引・戻り高は収益だが借方残である。ここを間違えると試算表の異常値判定と
    /// 決算書の控除表示が常に逆になる。
    /// </remarks>
    public DebitCredit NormalBalance
        => IsContra ? Category.NormalBalance().Opposite() : Category.NormalBalance();
}
