// 仕訳明細の画面。
//
// **会計の判断はここに書かない**（ADR-0008）。ここにあるのは打鍵を減らすための補助だけで、
// 税区分・部門・貸借の正しさはサーバ側の関門が AccountingCore を呼んで判定する。

// 勘定科目を選んだら、その科目の既定税区分を入れる。
//
// **入っている税区分は上書きしない。** 同じ科目でも取引ごとに税区分は変わりうるので
// （docs/06 §1）、既定はあくまで初回の入力を省くためのものである。
// 利用者が選び直したものを、科目を触るたびに戻してはいけない。
void Account_OnDataChanged()
{
    if (string.IsNullOrEmpty(Account.Value)) return;
    if (!string.IsNullOrEmpty(TaxCategory.Value)) return;

    var searcher = new ModuleSearcher<Account>();
    searcher.AddEquals(e => e.Id.Value, Account.Value);

    foreach (var account in searcher.Execute())
    {
        if (string.IsNullOrEmpty(account.DefaultTaxCategory.Value)) return;

        TaxCategory.Value = account.DefaultTaxCategory.Value;
        TaxCategory.DisplayText = account.DefaultTaxCategory.DisplayText;
        return;
    }
}
