// SalesHome.mod.cs — 営業業務部品のトップ（SalesStaff / SalesBilling 共通）
// ショートカット（同一フレーム内解決の AnchorTag）を提供する。
// ほかの業務への切替はサイドバー先頭の「ホーム」→ 業務ポータル（PortalHome）に一元化（ADR-0042）。
// 参照するモジュールは営業部品内のみ（部品独立性の維持）。

void Detail_OnAfterInit()
{
    var name = "";
    if (CurrentUser != null && CurrentUser.表示名.Value != null)
    {
        name = CurrentUser.表示名.Value;
    }
    GreetingLabel.Text = $"こんにちは、{name} さん";
    DateLabel.Text = $"{DateTime.Today:yyyy年M月d日}";
}
