namespace BusinessApp.ServerSupport;

/// <summary>
/// 関門が読もうとした欄が、<b>差分に載っているのに読めない型で届いた</b>ことを表す。
/// </summary>
/// <remarks>
/// <para><b>これは利用者の誤りではない。</b> デザインで欄の型を変えたか、
/// 想定していない経路が送ってきたかで、<b>直すのは画面を作る側</b>である。
/// だから文言は<b>ホストが定型文へ差し替え</b>、中身はログへ回す
/// （<c>AccountingSubmitPipeline</c> が <c>SaveFailureMessage</c> と同じ形で扱う）。</para>
/// <para><b>なぜ黙って素通ししないか。</b> <c>as T</c> で <c>null</c> に落とすと
/// 「触られていない」と見分けが付かず、<b>欄の型が変わった日に、その欄を見る検査が
/// まとめて素通しへ落ちる</b>——しかもフィクスチャが自分で正しい型を組むので
/// テストは緑のままである（<c>PartnerSubmitGate.Reference</c> が名指しする形。
/// 2026-09-09 の自己レビュー）。</para>
/// <para><b>専用の型にしてあるのは、ホストがこれだけを捕まえるためである。</b>
/// <c>InvalidOperationException</c> のままだと、包む側が「どの例外なら定型文に差し替えてよいか」を
/// 判断できず、想定外の失敗まで一緒に握り潰すことになる。</para>
/// </remarks>
public sealed class UnreadableFieldException(string module, string field, string typeName)
    : Exception($"{module} の「{field}」が読めない型 {typeName} で届いた。関門が守れないので止める。")
{
    /// <summary>
    /// 読めなかった欄を持つモジュール。
    /// </summary>
    /// <remarks>
    /// <b>欄の名前だけでは直す先が決まらない。</b> <c>Code</c> は 6 つのモジュールにあるので、
    /// ログに出したときにどのデザインを見ればよいか分からなくなる（2026-09-09 の自己レビュー）。
    /// </remarks>
    public string Module { get; } = module;

    /// <summary>読めなかった欄の名前。</summary>
    public string Field { get; } = field;

    /// <summary>届いた値の型（<c>null</c> なら "null"）。</summary>
    public string TypeName { get; } = typeName;

    /// <summary>差分に載っている値から作る。<b><c>null</c> も「読めない」側に入れる。</b></summary>
    /// <remarks>
    /// 値そのものが <c>null</c> でも落ちないようにする——入口はクライアント由来の
    /// デシリアライズで、<c>NullReferenceException</c> になると<b>この型を作った意味が消える</b>
    /// （定型文へ差し替える口を通らない。2026-09-09 の自己レビュー）。
    /// </remarks>
    public static UnreadableFieldException For(string module, string field, object? value)
        => new(module, field, value?.GetType().Name ?? "null");
}
