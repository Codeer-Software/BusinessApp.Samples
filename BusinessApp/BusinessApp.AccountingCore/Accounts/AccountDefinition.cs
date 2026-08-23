namespace BusinessApp.AccountingCore.Accounts;

/// <summary>
/// 検証・集計に必要な範囲の勘定科目（docs/04 §6）。
/// 画面表示のための属性（決算書表示区分・並び順など）はここに持ち込まない。
/// </summary>
/// <param name="Id">科目 ID。</param>
/// <param name="Code">科目コード（4 桁）。</param>
/// <param name="Name">科目名。</param>
/// <param name="Category">科目区分。部門の要否と決算振替がこれに依存する。</param>
/// <param name="IsActive">入力候補に出すか。false でも過去データの表示・検索は妨げない。</param>
public sealed record AccountDefinition(
    string Id,
    string Code,
    string Name,
    AccountCategory Category,
    bool IsActive = true);
