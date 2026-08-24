namespace BusinessApp.AccountingCore.Shared;

/// <summary>
/// 整数円の金額（docs/04 §3）。
/// </summary>
/// <remarks>
/// <para>浮動小数点を使わず <see cref="decimal"/> で保持し、円未満を持てないことを型で保証する。</para>
/// <para>残高や差額は負になりうるため符号は許す。仕訳明細の金額が正であることは検証側で担保する。</para>
/// <para>比率の乗算は <see cref="Multiply"/> で端数処理を明示しないと書けない。
/// 「どこかで暗黙に丸められていた」という事故を型で防ぐためである（docs/06 §4）。</para>
/// </remarks>
public readonly record struct Yen
{
    private Yen(decimal value) => Value = value;

    /// <summary>金額。常に整数（小数部を持たない）。</summary>
    public decimal Value { get; }

    public static Yen Zero => default;

    /// <summary>整数の <see cref="decimal"/> から金額を作る。小数部があれば例外。</summary>
    public static Yen From(decimal value)
    {
        if (value != decimal.Truncate(value))
        {
            throw new ArgumentException($"金額は整数円でなければならない: {value}", nameof(value));
        }
        return new Yen(decimal.Truncate(value));
    }

    public static Yen From(long value) => new(value);

    public bool IsPositive => Value > 0m;

    /// <summary>比率を掛けて端数処理する。税額・控除額の計算はすべてこの経路を通す。</summary>
    public Yen Multiply(decimal ratio, RoundingMode mode) => From(Rounding.Apply(Value * ratio, mode));

    public static Yen operator +(Yen left, Yen right) => new(left.Value + right.Value);

    public static Yen operator -(Yen left, Yen right) => new(left.Value - right.Value);

    public override string ToString() => Value.ToString("0");
}
