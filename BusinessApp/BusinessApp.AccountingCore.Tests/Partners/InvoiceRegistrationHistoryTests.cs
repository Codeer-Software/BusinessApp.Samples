namespace BusinessApp.AccountingCore.Tests.Partners;

using BusinessApp.AccountingCore.Partners;

public class InvoiceRegistrationHistoryTests
{
    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static InvoiceRegistration Registration(string no, DateOnly from, DateOnly? ended = null)
        => new(no, from, ended);

    [Fact]
    public void 登録が無ければ引けない()
        => Assert.Null(InvoiceRegistrationHistory.InEffectOn([], D(8, 1)));

    [Fact]
    public void 登録より前の日は引けない()
    {
        var registrations = new[] { Registration("T1000000000001", D(8, 10)) };

        Assert.Null(InvoiceRegistrationHistory.InEffectOn(registrations, D(8, 9)));
        Assert.Equal("T1000000000001", InvoiceRegistrationHistory.InEffectOn(registrations, D(8, 10))?.RegistrationNo);
    }

    [Fact]
    public void 終わっていない登録は以後ずっと引ける()
    {
        var registrations = new[] { Registration("T1000000000001", D(1, 1)) };

        Assert.Equal("T1000000000001", InvoiceRegistrationHistory.InEffectOn(registrations, D(12, 31))?.RegistrationNo);
    }

    [Fact]
    public void 終わった翌日は引けない()
    {
        var registrations = new[] { Registration("T1000000000001", D(1, 1), D(8, 20)) };

        Assert.Null(InvoiceRegistrationHistory.InEffectOn(registrations, D(8, 21)));
    }

    /// <summary>
    /// <b>境界の 1 日。</b> 終わりの日そのものは引ける。
    /// ここを落とすと、計上済みは不変なので後から補えない（<c>InvoiceRegistrationHistory</c> の注記）。
    /// </summary>
    [Fact]
    public void 終わりの日そのものは引ける()
    {
        var registrations = new[] { Registration("T1000000000001", D(1, 1), D(8, 20)) };

        Assert.Equal("T1000000000001", InvoiceRegistrationHistory.InEffectOn(registrations, D(8, 20))?.RegistrationNo);
    }

    [Fact]
    public void 再登録は新しいほうを引く()
    {
        var registrations = new[]
        {
            Registration("T1000000000001", D(1, 1), D(6, 30)),
            Registration("T2000000000002", D(9, 1)),
        };

        Assert.Equal("T1000000000001", InvoiceRegistrationHistory.InEffectOn(registrations, D(6, 30))?.RegistrationNo);
        Assert.Null(InvoiceRegistrationHistory.InEffectOn(registrations, D(7, 1)));
        Assert.Equal("T2000000000002", InvoiceRegistrationHistory.InEffectOn(registrations, D(9, 1))?.RegistrationNo);
    }

    /// <summary>
    /// 前の登録が終わった日に次が始まると、境界日は候補が 2 件になる。<b>新しいほうを採る。</b>
    /// </summary>
    [Fact]
    public void 終わりと始まりが同じ日なら新しいほうを引く()
    {
        var registrations = new[]
        {
            Registration("T1000000000001", D(1, 1), D(8, 20)),
            Registration("T2000000000002", D(8, 20)),
        };

        Assert.Equal("T2000000000002", InvoiceRegistrationHistory.InEffectOn(registrations, D(8, 20))?.RegistrationNo);
    }

    [Fact]
    public void 同じ日から始まる登録が_2_件あれば止まる()
    {
        var registrations = new[]
        {
            Registration("T1000000000001", D(8, 1)),
            Registration("T2000000000002", D(8, 1)),
        };

        var thrown = Assert.Throws<InvalidOperationException>(
            () => InvoiceRegistrationHistory.InEffectOn(registrations, D(8, 10)));

        Assert.Contains("2026-08-01", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("T1000000000001", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("T2000000000002", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 古いほうが重なっていても、<b>採るのは新しい 1 件</b>なので止まらない。
    /// 止めるのは「どれを写すか決められない」ときだけである。
    /// </summary>
    [Fact]
    public void 古い側が重なっていても新しい_1_件が決まれば止まらない()
    {
        var registrations = new[]
        {
            Registration("T1000000000001", D(1, 1)),
            Registration("T2000000000002", D(1, 1)),
            Registration("T3000000000003", D(8, 1)),
        };

        Assert.Equal("T3000000000003", InvoiceRegistrationHistory.InEffectOn(registrations, D(8, 10))?.RegistrationNo);
    }

    [Fact]
    public void 一覧を渡さなければ止まる()
        => Assert.Throws<ArgumentNullException>(() => InvoiceRegistrationHistory.InEffectOn(null!, D(8, 1)));
}
