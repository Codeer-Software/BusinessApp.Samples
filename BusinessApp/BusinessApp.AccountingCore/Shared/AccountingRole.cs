namespace BusinessApp.AccountingCore.Shared;

/// <summary>
/// 会計アプリの役割（ADR-0034）。<b>軸の中は階段で、上位は下位ができることをできる。</b>
/// </summary>
/// <remarks>
/// <para><b>置き場所の理由。</b> 権限を判定するのは原則として CLB
/// （<c>app.clprj</c> とフレームとモジュールの条件）であって C# ではない。
/// それでも会計コアがこの列挙子を持つのは、<b>CLB の条件が届かない書き込み経路が 1 本ある</b>
/// からである——取消・訂正の Web API（ADR-0016）は <c>IDbAccessor</c> を直に使い、
/// モジュールの <c>UserWriteCondition</c> を 1 つも通らない
/// （2026-09-02 の自己レビューで見つけた。qa/03 L-22）。</para>
/// <para><b>「会計コアが役割を読むと依存方向が逆になる」は、取引先の軸の話である</b>
/// （ADR-0034）。会計コアが<b>自分の軸</b>を確かめるのは逆流ではない——
/// 逆流になるのは「取引先を触るのに会計の役割を確かめる」ような、他の部品の軸を見る形である。</para>
/// <para>値は DB の <c>app_users.accounting_role</c>・CLB のデザイン enum
/// <c>AccountingRoles</c> と一致する（<c>EnumConsistencyTests</c> が 3 者一致を守る）。</para>
/// </remarks>
public enum AccountingRole
{
    /// <summary>帳簿閲覧。読むだけ（効かせるのはフェーズ 4）。</summary>
    Viewer,

    /// <summary>経理担当。伝票を起こして計上する。</summary>
    Staff,

    /// <summary>経理責任者。担当ができることに加えて、設定とマスタを触る。</summary>
    Manager,
}

public static class AccountingRoleExtensions
{
    /// <summary>
    /// 伝票に対する操作をしてよい役割か（取消・訂正・複製）。
    /// </summary>
    /// <remarks>
    /// <para><b>担当と責任者。</b> 取消・訂正は「伝票を起こす」のと同じ人の仕事で、
    /// 画面でも同じ役割に開いてある（<c>JournalEntry</c> の書き込み条件）。
    /// <b>帳簿閲覧は含めない</b>——読むための役割である。</para>
    /// <para><b>複製もここで見る。</b> ただし<b>理由が違う</b>——
    /// 複製は計上済みを 1 行も動かさないので、「起票できるか」で決まる（ADR-0048 の決定 9）。
    /// <b>集合が一致しているのはいまだけ</b>なので、役割を増やす日はここを分ける
    /// （2026-09-09 の自己レビュー）。</para>
    /// </remarks>
    public static bool CanAmendJournals(this AccountingRole role)
        => role is AccountingRole.Staff or AccountingRole.Manager;
}
