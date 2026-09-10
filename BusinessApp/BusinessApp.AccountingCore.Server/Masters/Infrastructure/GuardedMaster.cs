namespace BusinessApp.AccountingCore.Server.Masters.Infrastructure;

// 表と列の記述子。**どのマスタを守るか（一覧）は Application の MasterMeaningGate が持ち**、
// ここは「表・列・呼び名」を束ねる型だけ——Infrastructure の口（MasterUsageStore）が生の文字列を受けないための型である
//（ADR-0050 の決定 7。Infrastructure は Application を参照しないので、型はこちらに置く）。

/// <summary>守るマスタ 1 つ。</summary>
/// <param name="ModuleName">CLB のモジュール名。</param>
/// <param name="Label">利用者に見せる呼び名。</param>
/// <param name="Table">DB の表（CLB の <c>DbTable</c> の写し）。</param>
/// <param name="LineColumn"><c>journal_lines</c> でこのマスタを指す列。</param>
/// <param name="Columns">意味を決める列。</param>
/// <param name="OneWayColumns">
/// <b>緩める向きだけを拒む列</b>（真偽値。オフ → オンは通し、オン → オフを拒む）。
/// </param>
/// <param name="UsageUnit">断りで数える単位の呼び名（既定は仕訳明細）。</param>
/// <param name="UsageCounter">その単位の助数詞（既定は行）。</param>
/// <param name="EntryColumn">
/// <b>伝票（<c>journal_entries</c>）の側でもこのマスタを指す列</b>（取引先だけ）。
/// <b>明細が空なら伝票の値が実効値になる</b>ので、明細だけを数えると
/// 「伝票にだけ取引先を入れた計上済みの伝票」を取りこぼす（docs/10 §6-2 の実効値）。
/// </param>
public sealed record GuardedMaster(
    string ModuleName,
    string Label,
    string Table,
    string LineColumn,
    IReadOnlyList<GuardedColumn> Columns,
    IReadOnlyList<OneWayColumn>? OneWayColumns = null,
    string? EntryColumn = null,
    string UsageUnit = "仕訳明細",
    string UsageCounter = "行")
{
    /// <summary>緩める向きだけを拒む列（未指定なら空）。</summary>
    public IReadOnlyList<OneWayColumn> OneWay => OneWayColumns ?? [];
}

/// <summary>意味を決める列 1 つ。</summary>
/// <param name="FieldName">CLB のフィールド名。</param>
/// <param name="Column">DB の列（CLB の <c>DbColumn</c> の写し）。</param>
/// <param name="Label">利用者に見せる呼び名（CLB の <c>DisplayName</c> の写し）。</param>
public sealed record GuardedColumn(string FieldName, string Column, string Label);

/// <summary>
/// <b>緩める向きだけを拒む列</b>（オフ → オンは通す。docs/10 §6-2）。
/// </summary>
/// <remarks>
/// <para><b>「使用中は変えられない」（<see cref="GuardedMaster.Columns"/>）とは別の規則である。</b>
/// あちらは<b>過去の記録の意味が変わる</b>から止めるが、こちらは意味を変えない——
/// 止めるのは、<b>止めないと二層の守りをまとめて外せる</b>からである。</para>
/// <para><b>設定を束ねるだけの型なので、レコードにしない</b>——値としての等価も
/// <c>with</c> による複製も使わない。<b>使わない機能を型に持たせない</b>
/// （持たせると、誰も呼ばない複製コンストラクタがカバレッジの穴になり、
/// それを埋めるためだけのテストを書くことになる。ADR-0012 がそれを禁じている）。</para>
/// </remarks>
/// <param name="column">守る列（意味を決める列と同じ形で持つ）。</param>
/// <param name="harm">オフにすると何が起きるかの 1 文。</param>
/// <param name="instead">代わりに何をすればよいかの 1 文（docs/21 §2-3）。</param>
public sealed class OneWayColumn(GuardedColumn column, string harm, string instead)
{
    /// <summary>守る列。</summary>
    public GuardedColumn Column { get; } = column;

    /// <summary>オフにすると何が起きるか。</summary>
    public string Harm { get; } = harm;

    /// <summary>
    /// 代わりに何をすればよいか。
    /// </summary>
    /// <remarks>
    /// <b>「新しい行を作ってそちらを使う」とは言えない</b>（意味の凍結の断りと違うところ）——
    /// <b>新しい勘定科目は「取引先を要する」がオフなので、断りが述べた害がそのまま起きる</b>
    /// （自己レビューで見つけた。2026-09-08）。
    /// </remarks>
    public string Instead { get; } = instead;
}
