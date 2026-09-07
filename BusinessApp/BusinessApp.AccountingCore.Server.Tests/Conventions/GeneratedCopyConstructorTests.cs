namespace BusinessApp.AccountingCore.Server.Tests.Conventions;

using BusinessApp.AccountingCore.Server.Journals;

/// <summary>
/// <c>record</c> のコピーコンストラクタ（<c>with</c> のために自動生成される部分）に触れる。
/// </summary>
/// <remarks>
/// <para><b>これは振る舞いの検査ではない。</b> ADR-0012 が禁じている「カバレッジを埋めるための
/// テスト」に、意図的に設けた例外である。<c>AccountingCore.Tests</c> の同名のテストと同じ理由で、
/// そちらに書いてある経緯（<c>ExcludeByAttribute</c> をやめて計測を正直にした結果、
/// これだけが残った。qa/03 L-07）がそのまま当てはまる。</para>
/// <para>ここに挙げてあるのは<b>読んで渡すだけの型</b>——画面との受け渡し（ADR-0016 の
/// Web API の DTO）と、DB から読んだ値をそのまま束ねたものである。
/// どちらも値を組み替える場面が無いので <c>with</c> を一度も使わない。</para>
/// <para><b>この一覧に足すのは、コピーコンストラクタが本当に到達不能なときだけ。</b>
/// 「テストが面倒だから」でここに型を足さない。</para>
/// </remarks>
public class GeneratedCopyConstructorTests
{
    [Fact]
    public void 一度も_with_を使わない_record_のコピーコンストラクタに触れる()
    {
        _ = new AmendRequest("1") with { };
        _ = AmendResult.Ok(1, 2) with { };
        _ = new AmendViolation("E-01", "理由", null) with { };
        // 関門の定数表（ADR-0038 §2 の写し）。組み替える場面は無い
        _ = new BusinessApp.AccountingCore.Server.Masters.MasterMeaningGate.GuardedColumn("Code", "code", "コード") with { };
        _ = new BusinessApp.AccountingCore.Server.Masters.MasterMeaningGate.GuardedMaster("Account", "勘定科目", "accounts", "account_id", []) with { };
    }
}
