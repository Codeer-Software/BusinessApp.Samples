namespace BusinessApp.AccountingCore.Calendar;

/// <summary>締めの状態（docs/04 §7）。会計年度と月次期間で同じ値を使う。</summary>
public enum PeriodStatus
{
    /// <summary>未締め。仕訳を計上できる。</summary>
    Open,

    /// <summary>締め済み。計上・訂正・取消はできない（I-04）。</summary>
    Closed,
}
