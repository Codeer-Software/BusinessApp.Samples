-- 摘要の「空白だけ」の判定を、C# の char.IsWhiteSpace と同じ広さに揃える（docs/10 §4-2-1）。
--
-- 0013 は半角空白・タブ・改行・復帰・全角空白の 5 文字しか落としておらず、
-- **NBSP（U+00A0）だけの摘要が計上できた**。関門（JournalEntryValidator）は
-- string.IsNullOrWhiteSpace なので弾くが、**関門が走らない経路**——CSV 取込・sql CLI・
-- 手作業の SQL——では**このトリガだけが守り**なので、狭いと記載事項「内容」を欠いた行が帳簿に載る
-- （2026-09-08 の自己レビューで指摘され、実測で確かめた）。
--
-- 正典（ddl/005_journals.sql）からの逐語の写し。

DROP TRIGGER trg_journal_entries_description_required_when_posted;

-- 摘要のない仕訳を計上させない（docs/10 §4-2-1。法税規則 55 ① の仕訳帳の記載事項「内容」）。
-- 計上は draft → posted の UPDATE なので UPDATE にだけ張る
-- （status = 'posted' の INSERT は trg_journal_entries_no_posted_insert が拒む）。
--
-- **空白だけも空とみなす。** trim の第 2 引数は **C# の char.IsWhiteSpace と同じ符号位置**を並べてある。
-- **関門と同じ広さにする理由**: 関門（JournalEntryValidator）が走らない経路——CSV 取込・sql CLI・
-- 手作業の SQL——では**このトリガだけが守り**なので、狭いと「関門なら弾く値」が計上できてしまう。
-- **ずれたら赤くなる**（JournalDescriptionGuardTests が char.IsWhiteSpace を総なめして突き合わせる）。
CREATE TRIGGER trg_journal_entries_description_required_when_posted
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.status = 'posted'
             AND trim(coalesce(NEW.description, ''),
                      char(9, 10, 11, 12, 13, 32, 133, 160, 5760,
                       8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202,
                       8232, 8233, 8239, 8287, 12288)) = ''
BEGIN
    SELECT RAISE(ABORT, '摘要のない仕訳は計上できない。帳簿の記載事項「内容」を欠くため。');
END;
