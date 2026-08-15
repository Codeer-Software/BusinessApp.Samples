// PasswordPolicy.mod.cs — パスワードポリシー（ADR-0059。システム管理者のみ・1 行のみ）
// 責務: 設定内容から「利用者に見せる規則の文章」を組み立てて確認できるようにする。
//
// 検証そのものはここに書かない。サーバ側の PasswordPolicyService が唯一の実装で、
// CLB 経由の全パスワード書き込み（ユーザー管理）と自己変更 API の両方がそこを通る。
// 画面に出す説明文だけは、体験のために同じ規則をここでも組み立てている
// （ズレたときに困るのは文章なので、判定の複製とは性質が違う）。

void Detail_OnAfterInit()
{
    UpdatePreview();
}

void Policy_OnDataChanged()
{
    UpdatePreview();
}

// 「いま保存されている値だとこう見える」を先に見せる（保存してから画面を移動して確認、をさせない）
void UpdatePreview()
{
    var rules = new List<string>();
    var min = MinLength.Value ?? 0;
    rules.Add($"{min} 文字以上");

    var kinds = RequiredKinds.Value ?? 0;
    if (kinds > 0)
    {
        rules.Add($"英大文字・英小文字・数字・記号のうち {kinds} 種類以上を含む");
    }
    if (AllowSameAsUserName.Value != true)
    {
        rules.Add("ユーザー識別名と同じものは使えません");
    }
    if (ForbidReuseCurrent.Value == true)
    {
        rules.Add("現在のパスワードと同じものには変更できません");
    }
    PreviewLabel.Text = $"利用者への表示: {string.Join(" ／ ", rules)}";
}
