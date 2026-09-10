namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Periods;

/// <summary>
/// 計上検証に必要なマスタ一式。AccountingCore は DB を知らないので、呼び出し側が読んで渡す（ADR-0008）。
/// </summary>
/// <param name="Accounts">勘定科目。</param>
/// <param name="SubAccounts">補助科目。</param>
/// <param name="Departments">部門。</param>
/// <param name="Calendar">会計年度と月次期間。</param>
/// <param name="Partners">
/// 取引先。<b>伝票が参照している分だけ</b>と、選べる（有効な）取引先が 1 件でもあるか（<see cref="PartnerCatalog"/>）。
/// </param>
/// <remarks>
/// <para><b>取引先だけ全件ではなく、参照している分だけ持つ。</b> 取引先は数千件になりうるので全件は読まない
/// （他のマスタは科目 100 件・部門数件・税区分 10 件の規模）。呼び出し側が伝票と明細の取引先の識別子を集めて読む
/// （<c>JournalPoster</c> が計上の直前に足す）。</para>
/// <para><b>実在と有効は科目・補助科目・部門と同じ形で見る</b>（<c>E-PARTNER-UNKNOWN</c> / <c>E-PARTNER-INACTIVE</c>。
/// 2026-09-10。それまでは有無しか持たず、マスタに無い識別子は DB の外部キーの生の失敗になり、
/// 無効にした取引先も新たな計上に使えた——qa/03 L-14 と同じ型。docs/10 §6-3）。</para>
/// </remarks>
public sealed record PostingContext(
    AccountCatalog Accounts,
    SubAccountCatalog SubAccounts,
    DepartmentCatalog Departments,
    FiscalCalendar Calendar,
    PartnerCatalog Partners)
{
    /// <summary>取引先マスタに、選べる（有効な）取引先が 1 件でもあるか（「選んでください」と言ってよいか。docs/21 §2-3）。</summary>
    public bool HasSelectablePartner => Partners.HasSelectable;
}
