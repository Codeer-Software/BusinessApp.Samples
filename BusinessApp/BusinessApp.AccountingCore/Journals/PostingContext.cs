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
/// <param name="HasSelectablePartner">
/// 取引先マスタに、<b>選べる（有効な）取引先が 1 件でもあるか</b>。
/// </param>
/// <remarks>
/// <para><b>取引先だけ一覧ではなく有無を持つ。</b> 取引先は数千件になりうるので全件は読まない
/// （他のマスタは科目 100 件・部門数件・税区分 10 件の規模）。
/// <b>使い道は「選んでください」と言ってよいかの判定だけ</b>である（docs/21 §2-3。
/// 1 件も無ければ候補ダイアログが 0 件で開き、案内が踏めない）。</para>
/// <para><b>取引先が実在するか・有効かは、いまどこも見ていない。</b> ここが持つのは有無だけなので、
/// マスタに無い識別子は<b>DB の外部キーの生の失敗</b>になり、<b>無効にした取引先も新たな計上に使える</b>——
/// 科目・補助科目・部門を <c>E-*-INACTIVE</c> で止めているのと<b>非対称</b>である
/// （qa/03 L-14 と同じ型。<b>直すのは docs/04 §1 の B-1</b>。取引先は会計コアのマスタではない（ADR-0029）ので、
/// 一覧をどう持つかから決め直す）。</para>
/// </remarks>
public sealed record PostingContext(
    AccountCatalog Accounts,
    SubAccountCatalog SubAccounts,
    DepartmentCatalog Departments,
    FiscalCalendar Calendar,
    bool HasSelectablePartner);
