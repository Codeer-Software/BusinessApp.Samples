namespace BusinessApp.AccountingCore.Departments;

/// <summary>
/// 検証・集計に必要な範囲の部門（docs/10 §9・ADR-0010）。単一階層なので親部門を持たない。
/// </summary>
/// <param name="Id">部門の識別子。</param>
/// <param name="Code">部門コード。</param>
/// <param name="Name">部門名。</param>
/// <param name="IsCompanyWide">
/// 「全社共通」か。利用者が意図して選ぶときだけ使う枠であり、空欄の穴埋めには使わない（docs/10 §9-1）。
/// </param>
/// <param name="IsActive">入力候補に出すか。false でも過去データの表示・検索は妨げない。</param>
public sealed record DepartmentDefinition(
    DepartmentId Id,
    string Code,
    string Name,
    bool IsCompanyWide = false,
    bool IsActive = true);
