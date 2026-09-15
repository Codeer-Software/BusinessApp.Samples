// 仕訳明細の画面。
//
// **会計の判断はここに書かない**（ADR-0008）。ここにあるのは打鍵を減らすための補助だけで、
// 税区分・部門・貸借の正しさはサーバ側の関門が AccountingCore を呼んで判定する。

// 勘定科目を選んだら、その科目に属さない補助科目を落とし、既定税区分を入れる。
void Account_OnDataChanged()
{
    DropForeignSubAccount();
    FillDefaultTaxCategory();
}

// 選んである補助科目が、いまの勘定科目のものでないと**分かったとき**に空にする。
//
// **補助科目の候補は勘定科目で絞ってある**（`SubAccount` の検索条件の
// `FieldVariableMatchCondition`）。絞りは候補を出すときにしか効かないので、
// **科目を選び直したあとの古い値は自分で落とす**。落とさないと、
// 候補に出ていない補助科目が欄に残ったまま保存でき、計上の関門
// （`E-SUBACCOUNT-MISMATCH`）に当たるまで気づけない。
//
// **「確かめられなかった」ときは落とさない**（2026-09-02 の自己レビュー）。
// `ModuleSearcher` はモジュールの読み取り条件を通るので、`SubAccount` を閉じた日
// （フェーズ 4）に 0 件が返る。0 件を「属していない」と読むと、
// **利用者が選んだ値を静かに消す**——補助科目は必須ではないので、消えても誰も何も言わない。
// 属していないと分かったときだけ落とし、分からないときは関門に任せる。
//
// **科目を空にしたときは落とす。** 補助科目は科目にぶら下がるものなので、
// 親が無いのに子だけ残っている状態を作らない。
void DropForeignSubAccount()
{
    if (string.IsNullOrEmpty(SubAccount.Value)) return;

    if (!string.IsNullOrEmpty(Account.Value))
    {
        var searcher = new ModuleSearcher<SubAccount>();
        searcher.AddEquals(e => e.Id.Value, SubAccount.Value);

        var found = false;
        foreach (var subAccount in searcher.Execute())
        {
            found = true;

            // 識別子の比較は文字列に寄せる（qa/01 A-08。動的型なので型が違うと黙って false になる）。
            if ($"{subAccount.Account.Value}" == $"{Account.Value}") return;
        }

        if (!found) return;
    }

    SubAccount.Value = "";
    SubAccount.DisplayText = "";
}

// その科目の既定税区分を入れる。
//
// **入っている税区分は上書きしない。** 同じ科目でも取引ごとに税区分は変わりうるので
// （docs/11 §1）、既定はあくまで初回の入力を省くためのものである。
// 利用者が選び直したものを、科目を触るたびに戻してはいけない。
void FillDefaultTaxCategory()
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

// 計上済みの明細では、取引先を計上時の写しで見せる（ADR-0037 を明細にも当てる。ADR-0062。2026-09-16）。
//
// **写しがあれば計上済みである**——写しを書くのは計上のときだけで（ADR-0018）、
// **再計上の下書きは写しを持ち込まない**（`JournalCorrection.Draft`。同日に落とした。
// 落とす前は、行の取引先を選び直した訂正の下書きが原仕訳の古い名前を出していた）。
// だから伝票の状態を別に見に行かなくてよい。
// **取消の下書きは写しを引き継ぐ**が（ADR-0018）、その行の取引先も原仕訳のものなので食い違わない。
//
// **写しは `DataOnlyFields` で読む**（qa/01 F-34）。一覧のレイアウトに出していない欄は
// CLB が取ってこないので、書かないと**全行で空**になる。
//
// **写しに焼くのは実効値である**（`JournalEntry.PartnerOf`）——行が取引先を持たなくても
// 伝票の取引先の名前が全行に焼かれるので、**計上済みを開くと、行で選んでいない列にも名前が出る。**
// **それが帳簿に載っている姿**なので、そのまま見せる。
//
// **配線したのは一覧のレイアウトだけである。** 明細の詳細レイアウトには同じ配線をしていない——
// 伝票の `Lines` は `CanNavigateToDetail: false` で**行の詳細を開けない**からである。
// **開けるようにする日には、詳細にも同じ配線が要る**（一覧は写しの名前・詳細は現在のマスタ名、になる）。
//
// **写しが無ければ触らない。** 空になるのは「この列より前に計上された明細」と
// 「伝票にも明細にも取引先が無い明細」で、前者で空欄にすると**記録が無いのか出せないのかが読めない**
// （伝票のヘッダと同じ見方。`JournalEntry.mod.cs` の `ShowSnapshotPartner`）。
void ShowSnapshotPartner()
{
    if (string.IsNullOrEmpty(PartnerNameSnapshot.Value)) return;

    Partner.DisplayText = PartnerNameSnapshot.Value;
}
