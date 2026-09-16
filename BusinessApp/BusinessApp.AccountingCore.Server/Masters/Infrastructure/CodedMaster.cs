namespace BusinessApp.AccountingCore.Server.Masters.Infrastructure;

// コードを持つマスタの記述子。**どのマスタがコードを持つか（一覧）は Application の MasterSubmitGate が持ち**、
// ここは表・欄の呼び名・親を束ねる型だけ（ADR-0050 の決定 7。GuardedMaster と同じ置き方）。

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
/// <param name="texts"><b>長さの上限を持つ文字の欄</b>（docs/12 §2-2）。<b>1 つとは限らない。</b></param>
/// <param name="parent">一意の範囲を絞る親（補助科目だけ）。</param>
public sealed class CodedMaster(
    string moduleName,
    string table,
    string codeLabel,
    IReadOnlyList<CodedText> texts,
    CodedParent? parent = null)
{
    /// <summary>CLB のモジュール名。</summary>
    public string ModuleName { get; } = moduleName;

    /// <summary>DB の表。</summary>
    public string Table { get; } = table;

    /// <summary>コードの欄の呼び名。</summary>
    public string CodeLabel { get; } = codeLabel;

    /// <summary>長さの上限を持つ文字の欄。<b>空にしない。</b></summary>
    /// <remarks>
    /// <b><c>null</c> も空も置けるようにしない。</b> コードを持つ 5 つのマスタは全部が名前を持ち、
    /// <b>踏めない枝はカバレッジの穴になる</b>（ADR-0012）。
    /// <b>1 つとは限らない</b>——勘定科目と補助科目はカナも持つ（2026-09-16。旧 Q-26 の決定）。
    /// <b>上限を置いていない文字の欄がどれかは</b>、`FieldLengthConsistencyTests` の
    /// 除外の表が理由つきで持つ（docs/12 §2-2 の「この表に無い文字の欄」）。
    /// </remarks>
    public IReadOnlyList<CodedText> Texts { get; } = texts;

    /// <summary>一意の範囲を絞る親。無ければ <c>null</c>。</summary>
    public CodedParent? Parent { get; } = parent;
}

/// <summary>
/// 長さの上限を持つ文字の欄 1 つ（docs/12 §2-2）。
/// </summary>
/// <remarks>
/// <para><b>欄名・列・呼び名を 1 つの型にまとめてある。</b> 別々の <c>string?</c> にすると
/// <b>「一部だけ null」という起こりえない組み合わせ</b>が生まれる（<see cref="CodedParent"/> と同じ理由）。</para>
/// <para><b>欄名が <c>Name</c> とは限らない。</b> 会計年度は <c>Label</c> ／ <c>label</c> である
/// ——<b>決め打ちにすると、会計年度だけ関門が黙って素通しになる</b>。</para>
/// <para><b>呼び名は画面の <c>DisplayName</c> の写しである</b>（docs/20 §4 の「已むを得ない重複」）。
/// <b>列と呼び名がデザインとずれていないことは <c>MasterSubmitGateTests</c> が見る。</b></para>
/// <para><b>上限そのものはここに持たない</b>——会計コアのマスタはすべて
/// <c>MasterTextLength.MasterName</c> で、<b>欄ごとに違う数を置けるようにすると
/// 3 者一致の相手が 1 つ増える</b>（取引先だけが欄ごとに違い、そちらは自分の関門が持つ）。</para>
/// <para><b>設定を束ねるだけの型なので、レコードにしない</b>（<see cref="CodedMaster"/> と同じ理由）。</para>
/// </remarks>
/// <param name="fieldName">CLB のフィールド名。</param>
/// <param name="column">DB の列。</param>
/// <param name="label">利用者に見せる呼び名。</param>
/// <param name="maxLength">この欄の上限（<c>MasterTextLength</c>）。</param>
public sealed class CodedText(string fieldName, string column, string label, int maxLength)
{
    /// <summary>CLB のフィールド名。</summary>
    public string FieldName { get; } = fieldName;

    /// <summary>DB の列。</summary>
    public string Column { get; } = column;

    /// <summary>利用者に見せる呼び名。</summary>
    public string Label { get; } = label;

    /// <summary>この欄の上限。</summary>
    /// <remarks>
    /// <b>欄ごとに持つ</b>（2026-09-16。旧 Q-26 の決定でカナが加わった）。
    /// <b>2026-09-16 までは全部が <c>MasterTextLength.MasterName</c> で、関門が直に書いていた</b>——
    /// <b>カナは名前の 2 倍</b>なので、同じ数では持てなくなった。
    /// <b>3 者一致（C# の定数・DDL のトリガ・デザインのプレースホルダ）は
    /// <c>FieldLengthConsistencyTests</c> が毎回突き合わせる。</b>
    /// </remarks>
    public int MaxLength { get; } = maxLength;
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
