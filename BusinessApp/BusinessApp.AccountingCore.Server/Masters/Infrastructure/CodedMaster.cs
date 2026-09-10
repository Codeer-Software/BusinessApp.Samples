namespace BusinessApp.AccountingCore.Server.Masters.Infrastructure;

// コードを持つマスタの記述子。**どのマスタがコードを持つか（一覧）は Application の MasterSubmitGate が持ち**、
// ここは表・欄の呼び名・親を束ねる型だけ（ADR-0050 の決定 4。GuardedMaster と同じ置き方）。

/// <summary>
/// コードを持つマスタ 1 つ。
/// </summary>
/// <remarks>
/// <b>設定を束ねるだけの型なので、レコードにしない</b>——値としての等価も <c>with</c> による複製も使わない。
/// <b>使わない機能を型に持たせない</b>（持たせると、誰も呼ばない複製コンストラクタがカバレッジの穴になり、
/// それを埋めるためだけのテストを書くことになる。ADR-0012 がそれを禁じている。
/// <c>OneWayColumn</c> と同じ理由）。
/// </remarks>
/// <param name="moduleName">CLB のモジュール名。</param>
/// <param name="table">DB の表（CLB の <c>DbTable</c> の写し）。</param>
/// <param name="codeLabel">コードの欄の呼び名（CLB の <c>DisplayName</c> の写し）。</param>
/// <param name="moduleName">CLB のモジュール名。</param>
/// <param name="table">DB の表（CLB の <c>DbTable</c> の写し）。</param>
/// <param name="codeLabel">コードの欄の呼び名（CLB の <c>DisplayName</c> の写し）。</param>
/// <param name="parent">一意の範囲を絞る親（補助科目だけ）。</param>
public sealed class CodedMaster(
    string moduleName,
    string table,
    string codeLabel,
    CodedParent? parent = null)
{
    /// <summary>CLB のモジュール名。</summary>
    public string ModuleName { get; } = moduleName;

    /// <summary>DB の表。</summary>
    public string Table { get; } = table;

    /// <summary>コードの欄の呼び名。</summary>
    public string CodeLabel { get; } = codeLabel;

    /// <summary>一意の範囲を絞る親。無ければ <c>null</c>。</summary>
    public CodedParent? Parent { get; } = parent;
}

/// <summary>
/// 一意の範囲を絞る親（補助科目の勘定科目）。
/// </summary>
/// <remarks>
/// <para><b>列とフィールド名を 1 つの型にまとめてある。</b> 別々の <c>string?</c> にすると
/// <b>「片方だけ null」という起こりえない組み合わせ</b>を毎回検査することになり、
/// その枝はどのテストからも踏めない（ADR-0012 がカバレッジの穴を禁じている。
/// 2026-09-09 の自己レビュー）。</para>
/// <para><b>設定を束ねるだけの型なので、レコードにしない</b>（<see cref="CodedMaster"/> と同じ理由）。</para>
/// </remarks>
/// <param name="column">一意の範囲を絞る列。</param>
/// <param name="fieldName">同じものの CLB のフィールド名。</param>
public sealed class CodedParent(string column, string fieldName)
{
    /// <summary>一意の範囲を絞る列。</summary>
    public string Column { get; } = column;

    /// <summary>同じものの CLB のフィールド名。</summary>
    public string FieldName { get; } = fieldName;
}
