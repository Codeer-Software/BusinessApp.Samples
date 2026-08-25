namespace BusinessApp.AccountingCore.Journals;

/// <summary>仕訳の種別（docs/04 §4-1）。</summary>
public enum EntryType
{
    /// <summary>通常の仕訳。</summary>
    Normal,

    /// <summary>訂正（正しい内容の再計上）。原仕訳を指す。</summary>
    Correction,

    /// <summary>取消（原仕訳の貸借を反転した反対仕訳）。原仕訳を指す。</summary>
    Reversal,

    /// <summary>期首残高。</summary>
    Opening,

    /// <summary>決算振替。</summary>
    Closing,

    /// <summary>繰越。</summary>
    Carryover,
}

public static class EntryTypeExtensions
{
    /// <summary>原仕訳の指定が必須の種別か（I-06）。</summary>
    public static bool RequiresOriginalEntry(this EntryType type)
        => type is EntryType.Correction or EntryType.Reversal;

    /// <summary>
    /// 取消・訂正の<b>対象</b>にできる種別か（ADR-0015）。
    /// </summary>
    /// <remarks>
    /// 訂正を含めるのは、<b>訂正を間違えたときに詰まないため</b>である。
    /// 訂正の訂正を禁じると、直した内容がまた誤っていたときに手が無くなる。
    /// 取消を除くのは、取消の取消が何も表現しないため（原仕訳をもう一度生かす操作は
    /// 訂正であって、取消の取消ではない）。
    /// </remarks>
    public static bool IsAmendable(this EntryType type)
        => type is EntryType.Normal or EntryType.Correction;
}
