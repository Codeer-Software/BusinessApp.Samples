namespace BusinessApp.AccountingCore.Validation;

using BusinessApp.AccountingCore.Calendar;
using BusinessApp.AccountingCore.Masters;

/// <summary>
/// 計上検証に必要なマスタ一式。AccountingCore は DB を知らないので、呼び出し側が読んで渡す（ADR-0008）。
/// </summary>
/// <param name="Accounts">勘定科目。</param>
/// <param name="Calendar">会計年度と月次期間。</param>
public sealed record PostingContext(IAccountLookup Accounts, FiscalCalendar Calendar);
