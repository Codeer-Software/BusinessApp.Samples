namespace BusinessApp.AccountingCore.Masters;

/// <summary>
/// 借方・貸方（docs/04 §3）。金額は常に正で持ち、向きはこの区分で表す。
/// 符号で持つと「マイナスの借方」と「貸方」が混在し、集計のたびに解釈が要る。
/// </summary>
public enum DebitCredit
{
    /// <summary>借方。</summary>
    Debit,

    /// <summary>貸方。</summary>
    Credit,
}

public static class DebitCreditExtensions
{
    public static DebitCredit Opposite(this DebitCredit side)
        => side == DebitCredit.Debit ? DebitCredit.Credit : DebitCredit.Debit;
}
