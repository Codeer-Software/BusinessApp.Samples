namespace BusinessApp.AccountingCore.Server.Tests.Journals.Application;

using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Server.Journals.Application;

/// <summary>
/// 計上を止めたときの伝え方。
/// </summary>
/// <remarks>
/// <b>利用者が読む唯一の文面である。</b> 1 件だけ見せると、直しては弾かれを繰り返す。
/// </remarks>
public class JournalPostingRejectedExceptionTests
{
    [Fact]
    public void 違反を全件並べる()
    {
        var error = new JournalPostingRejectedException(
        [
            new Violation("I-01", "借方合計と貸方合計が一致していない。"),
            new Violation("I-13", "部門が要る。", LineNo: 2),
        ]);

        // **完全一致で固定する。** 部分一致だと、項目を束ねる区切りが無防備になる（qa/02 R8-09）。
        // **改行を入れない。** トースト内の文字列は改行できないので（qa/01 D-12）、
        // 件数と番号で区切る——1 行に繋がっても「あと何を直すか」が読み取れる。
        Assert.Equal(
            "計上できません（2 件）。①借方合計と貸方合計が一致していない。②行 2: 部門が要る。",
            error.Message);
        Assert.DoesNotContain("\n", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 違反が_1_件なら件数を数えない()
    {
        var error = new JournalPostingRejectedException([new Violation("I-01", "貸借が合っていない。")]);

        // 1 件のときは件数も番号も付けない（21 §2。開発者の決定 2026-09-10。qa/02 R69-31）。
        Assert.Equal("計上できません。貸借が合っていない。", error.Message);
    }

    [Fact]
    public void 番号を使い切ったら番号なしで続ける()
    {
        var error = new JournalPostingRejectedException(
            [.. Enumerable.Range(1, 11).Select(i => new Violation($"I-{i:D2}", $"{i} 件目。"))]);

        // **番号は 10 個すべてを固定する。** 端だけ見ていると、間の記号が化けても気づけない。
        Assert.Equal(
            "計上できません（11 件）。①1 件目。②2 件目。③3 件目。④4 件目。⑤5 件目。"
            + "⑥6 件目。⑦7 件目。⑧8 件目。⑨9 件目。⑩10 件目。11 件目。",
            error.Message);
    }

    /// <summary>
    /// 見出しは操作に合わせる。<b>計上していない操作を、計上の言葉で断らない。</b>
    /// </summary>
    /// <remarks>
    /// 保存の手前の関門（<c>JournalSubmitRequirements</c>）は<b>下書き保存でも走る</b>——
    /// 入っていない値は状態に関わらず DB に拒まれるからである。
    /// </remarks>
    [Fact]
    public void 保存を止めたときは保存の言葉で断る()
    {
        var error = new JournalPostingRejectedException(
            [new Violation("E-LINE-REQUIRED", "勘定科目を選んでください。", LineNo: 1)],
            JournalPostingRejectedException.SavingHeadline);

        // **「行 1:」は画面の「行」列の値。** 「1 行目」だと番号が飛んだ伝票で上から数えさせる（docs/21 §3）。
        Assert.Equal("保存できません。行 1: 勘定科目を選んでください。", error.Message);
    }

    [Fact]
    public void 警告は文面に混ぜない()
    {
        var error = new JournalPostingRejectedException(
        [
            new Violation("I-01", "止める理由。"),
            new Violation("W-01", "気にとめるだけの話。", Severity: ViolationSeverity.Warning),
        ]);

        Assert.DoesNotContain("気にとめるだけの話。", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, error.Violations.Count);
    }
}
