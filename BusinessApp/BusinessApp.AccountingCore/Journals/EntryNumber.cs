namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Periods;

/// <summary>
/// 伝票番号（I-17）。会計年度ごとの連番で、欠番を埋め直さず再利用もしない。
/// </summary>
/// <remarks>
/// <para>弥生会計も会計期間（年度）単位で採番しており、番号の付け方を強制する法令は無い
/// （docs/research/2026-08-24_市販会計ソフト比較_伝票番号の採番.md）。</para>
/// <para><b>再付番の機能は作らない。</b> 伝票番号は帳簿間の相互関連性（電帳規則 5 ⑤一ロ）を
/// 担保する一連番号なので、後から振り直すと出力済みの帳簿との対応が切れる。
/// 欠番があること自体は要件上の問題にならない。</para>
/// </remarks>
/// <param name="FiscalYearId">採番の単位となる会計年度。</param>
/// <param name="Value">その年度の中での連番（1 から始まる）。</param>
public readonly record struct EntryNumber(FiscalYearId FiscalYearId, int Value)
{
    /// <summary>画面や帳簿に出す表記。</summary>
    public override string ToString() => $"{Value:000000}";
}
