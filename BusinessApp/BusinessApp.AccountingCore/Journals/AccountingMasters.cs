namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Periods;

/// <summary>
/// 全件を読む会計マスタ一式。<b>取引先はまだ持たない</b>——伝票が決まってから、参照している分だけを足して
/// <see cref="PostingContext"/> になる（<see cref="WithPartners"/>）。
/// </summary>
/// <param name="Accounts">勘定科目。</param>
/// <param name="SubAccounts">補助科目。</param>
/// <param name="Departments">部門。</param>
/// <param name="Calendar">会計年度と月次期間。</param>
/// <param name="HasSelectablePartner">取引先マスタに、選べる（有効な）取引先が 1 件でもあるか（有無だけ。一覧は持たない）。</param>
/// <remarks>
/// <para><b>取引先を足す前の型を分ける</b>のは、足し忘れをコンパイルで落とすためである——計上検証（<c>JournalPosting.Post</c>）は
/// <see cref="PostingContext"/> しか受けないので、この型のまま渡すことはできない。空の目録で検証を通すと、
/// 参照している取引先が全部「マスタに無い」になる（2026-09-10 の自己レビュー。実行時の例外で止める案を、
/// 型で止める形に改めた——<c>JournalSubmitGate</c> が「順番は型で保証する」と決めている流儀に合わせる）。</para>
/// <para>会計期間だけを見る処理（削除の関門・取消・訂正の可否）はこの型で足りる。</para>
/// </remarks>
public sealed record AccountingMasters(
    AccountCatalog Accounts,
    SubAccountCatalog SubAccounts,
    DepartmentCatalog Departments,
    FiscalCalendar Calendar,
    bool HasSelectablePartner)
{
    /// <summary>伝票が参照している取引先を足して、計上検証に渡せる文脈にする。</summary>
    public PostingContext WithPartners(PartnerCatalog partners)
    {
        ArgumentNullException.ThrowIfNull(partners);
        return new PostingContext(Accounts, SubAccounts, Departments, Calendar, partners);
    }
}
