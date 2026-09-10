namespace BusinessApp.AccountingCore.Server.Shared.Infrastructure;

using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 1 つの操作をトランザクションで包む。<b>成功なら確定、例外なら巻き戻して投げ直す。</b>
/// </summary>
/// <remarks>
/// <para>会計コアの安全性は「途中で失敗したら丸ごと無かったことになる」ことに全面的に
/// 依存している（ADR-0004）。訂正は「取消を計上する」「再計上の下書きを作る」の 2 つで
/// 1 操作であり、<b>途中で失敗したときに取消だけが残ってはいけない</b>（ADR-0015）。</para>
/// <para><b>この 6 行を写さない。</b> 以前はコントローラとテストのフィクスチャに同じものが
/// 書き写されていて、<b>コントローラ側の <c>RollbackAsync</c> を <c>CommitAsync</c> に
/// 変えても全テストが緑だった</b>（qa/02 R4-04）。写しがあるかぎり、検査しているのは
/// 写しのほうである。</para>
/// </remarks>
public static class DbTransactionScope
{
    /// <param name="accessor">トランザクションを持つ DB アクセサ。</param>
    /// <param name="operation">包む操作。</param>
    public static async Task<T> RunAsync<T>(IDbAccessor accessor, Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(operation);

        accessor.StartTransaction();
        try
        {
            var result = await operation();
            await accessor.CommitAsync();
            return result;
        }
        catch
        {
            await accessor.RollbackAsync();
            throw;
        }
    }
}
