namespace BusinessApp.AccountingCore.Tests.Conventions;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>
/// <c>record</c> のコピーコンストラクタ（<c>with</c> のために自動生成される部分）に触れる。
/// </summary>
/// <remarks>
/// <para><b>これは振る舞いの検査ではない。</b> ADR-0012 が禁じている「カバレッジを埋めるための
/// テスト」に、意図的に 1 本だけ設けた例外である。理由を残す。</para>
/// <para>もともとは <c>ExcludeByAttribute=CompilerGeneratedAttribute</c> でこの行ごと
/// 計測から外していた。ところがこの設定は<b>コピーコンストラクタだけでなく、
/// async のステートマシンと <c>yield return</c> のイテレータもまるごと外す</b>。
/// 気づいたときには <c>AccountingCore.Server</c> の非同期メソッドが 1 行も計測されておらず、
/// 「100%」が実際には何も保証していなかった（qa/03 L-07）。</para>
/// <para>そこで除外をやめて計測を正直にした結果、残ったのが
/// 「<c>with</c> を一度も使わない record の、自分で書いていないコピーコンストラクタ」だけになった。
/// <b>ここを埋めるために閾値を下げると、これから増える本物の穴まで一緒に隠れる。</b>
/// 数行の例外を置くほうが安い。</para>
/// <para><b>この一覧に足すのは、コピーコンストラクタが本当に到達不能なときだけ。</b>
/// 「テストが面倒だから」でここに型を足さない。</para>
/// </remarks>
public class GeneratedCopyConstructorTests
{
    [Fact]
    public void 一度も_with_を使わない_record_のコピーコンストラクタに触れる()
    {
        _ = new Violation("E-X", "x") with { Severity = ViolationSeverity.Warning };
        _ = AccountingFixture.Context() with { };
        _ = AccountingFixture.Masters() with { };
        _ = new PartnerDefinition(AccountingFixture.Partner, "株式会社取引先", IsActive: true) with { };
        _ = new PostingResult([]) with { };
        _ = new ReversalResult([]) with { };
        _ = new CorrectionStartResult([]) with { };
        _ = AccountingFixture.Calendar().FindFiscalYear(AccountingFixture.FiscalYear)! with { };
        _ = AccountingFixture.Departments[0] with { };
        _ = AccountingFixture.SubAccounts[0] with { };
        var rate = new TransitionalDeductionRate(
            new EffectivePeriod(new DateOnly(2026, 10, 1), null), 0.5m, new RuleVersion("v1"));
        _ = rate with { };
    }
}
