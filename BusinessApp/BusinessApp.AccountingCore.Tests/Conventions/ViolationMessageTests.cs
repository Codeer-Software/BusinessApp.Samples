namespace BusinessApp.AccountingCore.Tests.Conventions;

using System.Reflection;
using System.Text.RegularExpressions;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>
/// 差し戻しの文は、<b>見出しが言う結果を繰り返さない</b>（docs/21 §2-6）。
/// </summary>
/// <remarks>
/// <para>利用者が読むのは <c>JournalPostingRejectedException</c> が組み立てた 1 本の文で、
/// 見出し（「計上できません」「保存できません」ほか）＋「①…②…」の形になる。
/// 各文が結果を繰り返すと <b>1 つの断りに同じ語が 2 回出る</b>——実機で見た（2026-09-08）。</para>
/// <para><b>ソースの字面</b>と、<b>実際に検証を走らせて集めた文</b>の両方を見る。
/// 字面だけだと組み立てた文（補間）を見落とし、走らせるだけだと
/// <b>その経路を通らない文</b>（取消・訂正・計上の各所で作る <c>Violation</c>）を見落とす。</para>
/// <para>見出しの語は <c>AccountingCore.Server</c> にあり、こちらからは参照できない
/// （依存は一方通行。ADR-0013）ので、<b>語をここに写している</b>。
/// 写しがずれても困らない——**増える側に倒してある**（禁じたい語を並べただけ）。</para>
/// </remarks>
public class ViolationMessageTests
{
    private static readonly string[] Headlines =
        ["計上できません", "保存できません", "取り消せません", "訂正できません", "削除できません", "登録できません"];

    [Fact]
    public void 検証が返す文は見出しの語を繰り返さない()
    {
        foreach (var message in AllMessages())
        {
            foreach (var headline in Headlines)
            {
                Assert.False(
                    message.Contains(headline, StringComparison.Ordinal),
                    $"「{headline}」は見出しが言う。文の側では繰り返さない（docs/21 §2-6）: {message}");
            }
        }
    }

    [Fact]
    public void 集めた文が空でない()
    {
        // **この網が何も見ていない状態を許さない。** 集め方を壊すと、上のテストは黙って緑になる。
        Assert.True(AllMessages().Count > 15, "検証の文を集められていない。集め方を確かめること。");
    }

    /// <summary>関門が出しうる文を集める（ソースの字面と、実際に検証を走らせて出たもの）。</summary>
    private static List<string> AllMessages()
    {
        var messages = typeof(JournalLineRules)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        // **ソースの字面も見る。** `new Violation(...)` に直接書いた文は
        // 定数にも、走らせた経路にも出てこないことがある（AmendmentRules ほか 4 ファイル）。
        foreach (var file in ProjectPaths.SourceFiles(ProjectPaths.SourceProject))
        {
            messages.AddRange(Literals(File.ReadAllText(file)));
        }

        foreach (var entry in BrokenEntries())
        {
            messages.AddRange(
                JournalEntryValidator.ValidateForPosting(entry, AccountingFixture.Context())
                    .Select(v => v.Message));
        }

        return messages;
    }

    /// <summary>ソースの中の文字列リテラル。<b>コメントは除く</b>（規則そのものを語る行が引っかかる）。</summary>
    private static IEnumerable<string> Literals(string source)
    {
        var withoutComments = string.Join(
            "\n",
            source.Split('\n').Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment < 0 ? line : line[..comment];
            }));

        return Regex.Matches(withoutComments, "\"((?:[^\"\\\\\\n]|\\\\.)*)\"")
            .Select(m => m.Groups[1].Value);
    }

    /// <summary>違反を出させるための伝票。<b>種類の違う壊し方</b>を並べる。</summary>
    private static IEnumerable<JournalEntry> BrokenEntries()
    {
        var ordinary = new DateOnly(2026, 5, 20);

        // 摘要なし・明細なし
        yield return AccountingFixture.Entry(ordinary) with { Description = null };

        // 計上済み・番号つき（状態の違反）
        yield return AccountingFixture.CashSale(ordinary) with
        {
            Status = EntryStatus.Posted,
            EntryNo = 1,
        };

        // 締め済み期間・貸借不一致・部門なし・行番号の重複
        yield return AccountingFixture.Entry(
            new DateOnly(2026, 4, 10),
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(1, DebitCredit.Credit, AccountingFixture.Sales, 999));

        // 会計期間の外
        yield return AccountingFixture.CashSale(new DateOnly(2030, 1, 1));

        // 訂正なのに原仕訳が無い
        yield return AccountingFixture.CashSale(ordinary) with { EntryType = EntryType.Correction };
    }
}
