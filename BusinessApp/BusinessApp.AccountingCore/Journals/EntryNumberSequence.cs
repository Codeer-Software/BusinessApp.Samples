namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Periods;

/// <summary>
/// 伝票番号の採番（I-17）。
/// </summary>
/// <remarks>
/// <para><b>次に出す番号を進めるだけ</b>で、戻さない・埋め直さない。
/// 下書きを消しても番号は消費されない（番号は計上時にしか採らないため）。</para>
/// <para>実際の永続化は <c>journal_entry_sequences</c> テーブルが行う。この型は
/// 「読んだ値から次の番号と次の状態を決める」純粋な計算だけを持ち、
/// 呼び出し側が同じトランザクションの中で読み書きする。</para>
/// </remarks>
/// <param name="FiscalYearId">採番の単位となる会計年度。</param>
/// <param name="NextValue">次に払い出す番号。</param>
public readonly record struct EntryNumberSequence(FiscalYearId FiscalYearId, int NextValue)
{
    /// <summary>会計年度の採番を 1 番から始める。</summary>
    public static EntryNumberSequence StartOf(FiscalYearId fiscalYearId) => new(fiscalYearId, 1);

    /// <summary>次の番号を払い出し、進めた状態を返す。</summary>
    public (EntryNumber Number, EntryNumberSequence Next) Allocate()
    {
        if (NextValue < 1)
        {
            throw new InvalidOperationException($"採番の次番号が壊れている: {NextValue}");
        }

        return (new EntryNumber(FiscalYearId, NextValue), this with { NextValue = NextValue + 1 });
    }
}
