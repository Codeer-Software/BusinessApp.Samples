namespace BusinessApp.ServerSupport.Tests;

/// <summary>
/// 差し戻しの文の形（docs/21 §2-6 の (b)）。<b>期待の字は検体に直に書く</b>——
/// 番号の定数を自分と比べると、定数を壊したときに期待も一緒に壊れて釣り合う（self-review スキル §9 の 4）。
/// </summary>
public class RejectionMessageTests
{
    [Fact]
    public void 一件なら件数も番号も付けない()
        => Assert.Equal(
            "登録できません。「勘定科目コード」を入れてください。",
            RejectionMessage.Compose("登録できません", ["「勘定科目コード」を入れてください。"]));

    [Fact]
    public void 二件以上は件数と丸数字で並べる()
        => Assert.Equal(
            "保存できません（2 件）。①「会社名」を入れてください。②「決算月」を入れてください。",
            RejectionMessage.Compose("保存できません", ["「会社名」を入れてください。", "「決算月」を入れてください。"]));

    /// <summary>
    /// <b>同じ文もまとめない</b>——件数が場所の唯一の手がかりになる断りがある（仕訳の保存の関門。行番号の載らない明細の違反）。
    /// </summary>
    [Fact]
    public void 同じ文もまとめずに並べる()
        => Assert.Equal(
            "保存できません（2 件）。①理由ア。②理由ア。",
            RejectionMessage.Compose("保存できません", ["理由ア。", "理由ア。"]));

    [Fact]
    public void 二十件を超えた分は番号なしで続ける()
    {
        var reasons = Enumerable.Range(1, 21).Select(i => $"理由{i}。").ToList();

        Assert.Equal(
            "登録できません（21 件）。①理由1。②理由2。③理由3。④理由4。⑤理由5。⑥理由6。⑦理由7。⑧理由8。⑨理由9。⑩理由10。"
            + "⑪理由11。⑫理由12。⑬理由13。⑭理由14。⑮理由15。⑯理由16。⑰理由17。⑱理由18。⑲理由19。⑳理由20。理由21。",
            RejectionMessage.Compose("登録できません", reasons));
    }

    [Fact]
    public void 理由が無ければ見出しだけを返して止めない()
        => Assert.Equal("計上できません。", RejectionMessage.Compose("計上できません", []));

    [Fact]
    public void 見出しが空なら組まない()
        => Assert.Throws<ArgumentException>(() => RejectionMessage.Compose("", ["理由。"]));
}
