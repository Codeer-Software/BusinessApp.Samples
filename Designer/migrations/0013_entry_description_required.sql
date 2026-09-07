-- 摘要のない仕訳を計上させない（docs/10 §4-2-1。04 §1 の A-2）。
--
-- 法税規則 55 ① が仕訳帳に求める記載事項「内容」を持つのは伝票の摘要である。
-- いまは空のまま計上でき、記載事項を欠いた行が帳簿に載る（実際に 2 件ある。計上済みは直せない）。
-- **下書き保存では求めない**（開発者の決定。2026-09-05）ので、UPDATE で posted になる瞬間だけを見る。
--
-- **既存の計上済みの行には当たらない。** BEFORE UPDATE なので、既に posted の行を
-- 触らないかぎり評価されず、そもそも trg_journal_entries_posted_no_update が先に止める。
--
-- 正典（ddl/005_journals.sql）からの逐語の写し。

-- 摘要のない仕訳を計上させない（docs/10 §4-2-1。法税規則 55 ① の仕訳帳の記載事項「内容」）。
-- 計上は draft → posted の UPDATE なので UPDATE にだけ張る
-- （status = 'posted' の INSERT は trg_journal_entries_no_posted_insert が先に止める）。
-- **空白だけも空とみなす。** trim の第 2 引数に落とす字を並べてある
-- （半角空白・タブ・改行・復帰・全角空白 U+3000。2026-09-08 に実測）。
-- **関門（C# の JournalEntryValidator）のほうが本体で、こちらは取込・CLI・SQL の直打ちへの最後の守り。**
-- 関門は string.IsNullOrWhiteSpace なので NBSP のような字まで見る。ここはそこまで追わない
-- ——**守りの範囲は関門 ⊇ トリガ**でよく、逆にすると関門を通った値をトリガが拒んで定型文になる。
CREATE TRIGGER trg_journal_entries_description_required_when_posted
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.status = 'posted'
             AND trim(coalesce(NEW.description, ''), char(32, 9, 10, 13, 12288)) = ''
BEGIN
    SELECT RAISE(ABORT, '摘要のない仕訳は計上できない。帳簿の記載事項「内容」を欠くため。');
END;
