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
    /// <summary>
    /// 利用者に見せる名前。
    /// </summary>
    /// <remarks>
    /// <para><b>列挙子をそのまま文言に混ぜない</b>（CLAUDE.md §2-7）。
    /// <c>$"種別が「{entry.EntryType}」の…"</c> と書くと画面に <c>Reversal</c> と出る。
    /// 実際に出した（2026-08-25 の指摘）。</para>
    /// <para>同じ名前を CLB のデザイン enum（<c>Enums/EntryTypes.enum.json</c>）も持っている。
    /// <b>両者が一致することは <c>EnumConsistencyTests</c> が検査する</b>ので、
    /// ここだけ直しても画面だけ直してもテストが落ちる。</para>
    /// </remarks>
    public static string DisplayName(this EntryType type) => type switch
    {
        EntryType.Normal => "通常",
        EntryType.Correction => "訂正",
        EntryType.Reversal => "取消",
        EntryType.Opening => "期首残高",
        EntryType.Closing => "決算振替",
        EntryType.Carryover => "繰越",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "知らない仕訳の種別。"),
    };

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
