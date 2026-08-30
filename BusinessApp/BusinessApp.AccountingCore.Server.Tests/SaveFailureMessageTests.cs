namespace BusinessApp.AccountingCore.Server.Tests;

using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using Codeer.LowCode.Blazor.DataIO;

/// <summary>
/// 保存の失敗を、利用者の語に差し替える最後の網（qa/01 F-16）。
/// </summary>
/// <remarks>
/// <b>文言そのものを固定する。</b> 定数どうしを比べる表明では、
/// 文言が空になっても緑のままになる——そのとき CLB は何も表示せず、
/// <b>保存が成功したように見える</b>（qa/01 の主題そのもの）。
/// </remarks>
public class SaveFailureMessageTests
{
    /// <summary>
    /// 実機で観測した 3 通り（2026-08-30。1.3.20。qa/01 F-16）。<b>どれも利用者の語ではない。</b>
    /// </summary>
    private const string SqliteFailure = "SQLite Error 19: 'NOT NULL constraint failed: journal_lines.account_id'.";

    /// <summary><c>IsUpdateProtected</c> の拒否（qa/01 F-26）。<b>何度やっても通らない。</b></summary>
    private const string ProtectedFieldFailure = "Partner This field cannot be modified";

    /// <summary>楽観ロックの競合。<b>開き直せば通る</b>のに、理由が何も入っていない。</summary>
    private const string ConflictFailure = "Update failed";

    [Fact]
    public void 差し替える文言を完全一致で固定する()
    {
        // **完全一致で固定する。** 観測した 3 通りは「次にすべきこと」がそれぞれ違うので、
        // 開き直す → もう一度試す → それでも駄目なら人に聞く、の順で全部を包む（docs/09 §2-3）。
        Assert.Equal(
            "保存できませんでした。入力内容を確かめ、画面を開き直してもう一度お試しください。"
            + "同じことが続くときは、管理者にお知らせください。",
            SaveFailureMessage.Text);

        // 改行はトーストに出ない（qa/01 D-12）。
        Assert.DoesNotContain("\n", SaveFailureMessage.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SqliteFailure)]
    [InlineData(ProtectedFieldFailure)]
    [InlineData(ConflictFailure)]
    public void 枠組みの言葉は中身を見ずに差し替える(string original)
    {
        var results = SaveFailureMessage.ToUserLanguage([SubmitData.Failure(original)]);

        Assert.Equal([SaveFailureMessage.Text], results.Select(r => r.ExceptionMessage));
    }

    /// <summary>
    /// <b>成功した結果には触らない。</b> 仮 ID の対応表を消すと、計上が ID を解決できなくなる。
    /// </summary>
    [Fact]
    public void 成功した結果は触らない()
    {
        var success = SubmitData.Result("@temporary:0f0a", "1");

        var results = SaveFailureMessage.ToUserLanguage([success]);

        Assert.Equal(string.Empty, results[0].ExceptionMessage ?? string.Empty);
        Assert.Equal("1", results[0].DestinationId);
    }

    /// <summary>
    /// <b>差し替えた原文を渡す。</b> qa/01 F-26 は生のメッセージが見えたから見つかったもので、
    /// 黙って捨てるとこの網が次の F-26 を隠す。
    /// </summary>
    [Fact]
    public void 差し替えた原文を呼び出し側へ渡す()
    {
        var seen = new List<string>();

        SaveFailureMessage.ToUserLanguage(
            [SubmitData.Failure(SqliteFailure), SubmitData.Result("@temporary:1", "2"), SubmitData.Failure(ConflictFailure)],
            seen.Add);

        // 失敗した分だけ、差し替える前の文言がそのまま渡る。
        Assert.Equal([SqliteFailure, ConflictFailure], seen);
    }

    /// <summary>渡さなくても動く（ホストがログの口を持たない構成を壊さない）。</summary>
    [Fact]
    public void 原文の受け取り口は省略できる()
    {
        var results = new List<ModuleSubmitResult> { SubmitData.Failure(SqliteFailure) };

        Assert.Equal([SaveFailureMessage.Text], SaveFailureMessage.ToUserLanguage(results).Select(r => r.ExceptionMessage));
    }
}
