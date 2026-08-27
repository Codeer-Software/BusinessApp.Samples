namespace BusinessApp.Partners.Server.Tests.Conventions;

/// <summary>
/// <c>record</c> のコピーコンストラクタ（<c>with</c> のために自動生成される部分）に触れる。
/// </summary>
/// <remarks>
/// <para><b>これは振る舞いの検査ではない。</b> ADR-0012 が禁じている「カバレッジを埋めるための
/// テスト」に、意図的に設けた例外である。経緯は <c>AccountingCore.Tests</c> の同名のテストにある
/// （<c>ExcludeByAttribute</c> をやめて計測を正直にした結果、これだけが残った。qa/03 L-07）。</para>
/// <para><see cref="PartnerProfile"/> は DB から読んだ素性をそのまま束ねた型で、
/// 値を組み替える場面が無いので <c>with</c> を一度も使わない。</para>
/// <para><b>この一覧に足すのは、コピーコンストラクタが本当に到達不能なときだけ。</b>
/// 「テストが面倒だから」でここに型を足さない。</para>
/// </remarks>
public class GeneratedCopyConstructorTests
{
    [Fact]
    public void 一度も_with_を使わない_record_のコピーコンストラクタに触れる()
        => _ = new PartnerProfile(PartnerEntityType.Corporation, "8700110005901") with { };
}
