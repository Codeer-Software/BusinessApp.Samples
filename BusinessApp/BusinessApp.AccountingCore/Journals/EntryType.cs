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
}
