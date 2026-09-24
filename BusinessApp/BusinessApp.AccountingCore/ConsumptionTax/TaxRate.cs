namespace BusinessApp.AccountingCore.ConsumptionTax;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// ある期間に、ある税率区分へ当たる税率（docs/11 §1-1）。
/// </summary>
/// <remarks>
/// <para><b>値はここに書かない。</b> 制度ルールのマスタ（<c>tax_rates</c>）が持ち、実行時に注入する
/// （docs/20 §2）。<b>引く日は <c>tax_point</c></b>（課税資産の譲渡等をした日・課税仕入れを行った日）
/// ——取引日でも課税期間でもない（docs/11 §5-2 の 4 つの日付の表）。
/// <b>返還等の行だけはまだ決まっていない</b>——§5-7 が基準日を取引日に固定しているので、
/// この読みがそのままは当たらない（docs/11 の保留リスト）。</para>
/// <para><b>国税と地方消費税は、形の違う 2 つの値である。</b> 国税は<b>課税標準額に対する率</b>
/// （消税法 29。<b>課税標準が対価であること自体は条文を開いていない</b>——税率リサーチ §3）、
/// 地方消費税は<b>消費税額に対する分数</b>（地方税法 72 の 77 二・72 の 83。同 §1-2）である。
/// <b>だから片方を率、もう片方を分数で持つ</b>——22/78 を小数にすると、持った時点で丸めが入る。</para>
/// <para><b>この型は金額を計算しない。</b> 税額の計算には<b>方式</b>（割戻しか積上げか。docs/11 §3）と
/// <b>端数処理の場面</b>（docs/11 §4）が要る。<b>方式は課税期間ごとの会計設定で後から変えられ</b>、
/// <b>端数処理の設定の粒度はまだどこも決めていない</b>（§4 は「用途ごとに分ける」までである）。
/// <b>ここに「国税を出す」「地方を出す」を置くと、場面を知らないまま端数処理を呼び出し側に選ばせることになる</b>
/// ——しかも<b>明細ごとに国税と地方へ分けて丸めると、合計税率で出した仮払消費税額等とずれることがある</b>
/// （切り捨てるなら、税抜 999 円・標準税率で 98 円 対 99 円。四捨五入なら差は出ない。
/// 2026-09-23 の自己レビューで計算。docs/11 §1-1）。</para>
/// <para><b>プロパティに <c>init</c> を付けない。</b> 付けると <c>with</c> 式が
/// <b>コンストラクタを通らない値</b>を作れてしまい、下の不変条件が素通りする。</para>
/// </remarks>
public sealed record TaxRate : IEffectiveDatedRule
{
    /// <summary>
    /// <b>DDL と同じ不変条件を、読み出しの側でも確かめる</b>（ADR-0069 の「2 か所が独立に見る」）。
    /// </summary>
    /// <remarks>
    /// <b>脅威は期間の重なりと同じである</b>——取込・直打ちで <c>Designer/ddl/015</c> の CHECK を
    /// 迂回した行が入りうる。<b>そこを通ると <see cref="CombinedRatePer10000"/> が 0 除算になるか、
    /// 符号の反転した税率を静かに返す</b>。
    /// </remarks>
    /// <param name="kind">税率区分。</param>
    /// <param name="period">有効期間（<c>tax_point</c> で判定する）。</param>
    /// <param name="nationalRatePer10000">国税の税率。<b>万分率の整数</b>（百分の七・八 → 780）。</param>
    /// <param name="localNumerator">地方消費税の税率の分子（七十八分の二十二 → 22）。</param>
    /// <param name="localDenominator">地方消費税の税率の分母（七十八分の二十二 → 78）。</param>
    /// <param name="version">制度ルールの版。</param>
    public TaxRate(
        TaxRateKind kind,
        EffectivePeriod period,
        int nationalRatePer10000,
        int localNumerator,
        int localDenominator,
        RuleVersion version)
    {
        if (nationalRatePer10000 is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nationalRatePer10000),
                nationalRatePer10000,
                "国税の税率は万分率の 1 以上 10000 以下でなければならない");
        }
        if (localNumerator < 1 || localDenominator < 1 || localNumerator >= localDenominator)
        {
            throw new ArgumentException(
                $"地方消費税の税率は 0 より大きく 1 より小さい分数でなければならない: {localNumerator}/{localDenominator}",
                nameof(localNumerator));
        }
        // **long で掛ける。** int のままだと、分母が大きい行で乗算が溢れ、
        // **溢れた値がたまたま割り切れると、この不変条件を素通りする**（2026-09-23 の自己レビュー）。
        // **DB 側は帯（1〜10000）で在りえない分母を断っているが、ここは DB を通らない行も受ける。**
        if ((long)nationalRatePer10000 * (localDenominator + localNumerator) % localDenominator != 0)
        {
            throw new ArgumentException(
                "国税と地方から導く合計税率が万分率の整数にならない:"
                + $" {nationalRatePer10000} × {localDenominator + localNumerator} ÷ {localDenominator}",
                nameof(nationalRatePer10000));
        }

        Kind = kind;
        Period = period;
        NationalRatePer10000 = nationalRatePer10000;
        LocalNumerator = localNumerator;
        LocalDenominator = localDenominator;
        Version = version;
    }

    /// <summary>税率区分。</summary>
    public TaxRateKind Kind { get; }

    /// <summary>有効期間（<c>tax_point</c> で判定する）。</summary>
    public EffectivePeriod Period { get; }

    /// <summary>国税の税率。<b>万分率の整数</b>（百分の七・八 → 780）。</summary>
    public int NationalRatePer10000 { get; }

    /// <summary>地方消費税の税率の分子（七十八分の二十二 → 22）。</summary>
    public int LocalNumerator { get; }

    /// <summary>地方消費税の税率の分母（七十八分の二十二 → 78）。</summary>
    public int LocalDenominator { get; }

    /// <summary>制度ルールの版。</summary>
    public RuleVersion Version { get; }

    /// <summary>
    /// 国税と地方消費税を合わせた税率。<b>万分率の整数</b>（10% → 1000）。<b>持たずに導く。</b>
    /// </summary>
    /// <remarks>
    /// <para>地方消費税は消費税額を課税標準とするので、合計は
    /// <c>国税 ×（分母 ＋ 分子）÷ 分母</c> になる——780 × 100 ÷ 78 ＝ 1000。
    /// <b>割り切れない行はコンストラクタが断る</b>ので、ここで丸めは起きない。</para>
    /// <para><b>整数で返すのは、税込の換算を分数のまま組み立てられるようにするためである</b>——
    /// 税込 → 課税標準額は <c>10000 ÷ (10000 ＋ 合計税率)</c>（＝ 100/110）、
    /// 税込 → 税額は <c>合計税率 ÷ (10000 ＋ 合計税率)</c>（＝ 10/110）で、
    /// どちらも<b>小数に直さずに整数の対で書ける</b>
    /// （消費税リサーチ §5-4 の換算率。<c>TaxRateTests</c> が配っている区分をどれも組み立てて確かめる）。</para>
    /// </remarks>
    public int CombinedRatePer10000 =>
        (int)((long)NationalRatePer10000 * (LocalDenominator + LocalNumerator) / LocalDenominator);
}
