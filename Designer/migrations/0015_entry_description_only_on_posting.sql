-- 摘要の関門を「下書き → 計上」のときだけ鳴らす（docs/10 §4-2-1）。
--
-- 0013・0014 のトリガは NEW.status しか見ていなかったので、
-- **規則より前に計上された摘要のない行**（稼働 DB に 2 件。伝票番号 1・12）を触ると
-- 「摘要のない仕訳は計上できない」が鳴り、**本来の「計上済みの仕訳は変更できない」を隠していた**
-- （2026-09-08 の自己レビューで指摘され、インメモリ SQLite で再現した）。
--
-- **あわせて、貼り直しで末尾へ移す。** SQLite は同じ表のトリガを後に作ったものから発火させるので、
-- 正典（ddl/）の途中に置いたままだと、正典から作った DB と baseline+migrations を再生した DB で
-- 鳴る順が入れ替わる（同値テストは並べ替えてから比べるので気づけない）。
--
-- 正典（ddl/005_journals.sql）からの逐語の写し。

DROP TRIGGER trg_journal_entries_description_required_when_posted;

-- 摘要のない仕訳を計上させない（docs/10 §4-2-1。法税規則 55 ① の仕訳帳の記載事項「内容」）。
-- **下書き → 計上の UPDATE だけを見る。** OLD.status を見ないと、
-- **規則より前に計上された摘要のない行**（稼働 DB に 2 件）を触ったときにもこれが鳴り、
-- 本来出るべき trg_journal_entries_posted_no_update の「計上済みの仕訳は変更できない」を隠す
-- （2026-09-08 の自己レビューで指摘され、インメモリ SQLite で再現した）。
-- status = 'posted' の INSERT は trg_journal_entries_no_posted_insert が拒む。
--
-- **空白だけも空とみなす。** trim の第 2 引数は **C# の char.IsWhiteSpace と同じ符号位置**を並べてある。
-- **関門と同じ広さにする理由**: 関門（JournalEntryValidator）が走らない経路——CSV 取込・sql CLI・
-- 手作業の SQL——では**このトリガだけが守り**なので、狭いと「関門なら弾く値」が計上できてしまう。
-- **広すぎても困る**（関門を通った値を DB が拒み、枠組みの言葉で失敗する）ので、
-- **JournalDescriptionGuardTests が両方向で突き合わせる。**
--
-- **このファイルの末尾に置く。** SQLite は同じ表のトリガを**後に作ったものから**発火させるので、
-- 途中に挿すと、正典から作った DB と baseline+migrations を再生した DB で**鳴る順が入れ替わる**
-- （同値テストは並べ替えてから比べるので気づけない。2026-09-08 に実測）。
CREATE TRIGGER trg_journal_entries_description_required_when_posted
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.status = 'posted' AND OLD.status <> 'posted'
             AND trim(coalesce(NEW.description, ''),
                      char(9, 10, 11, 12, 13, 32, 133, 160, 5760,
                       8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202,
                       8232, 8233, 8239, 8287, 12288)) = ''
BEGIN
    SELECT RAISE(ABORT, '摘要のない仕訳は計上できない。帳簿の記載事項「内容」を欠くため。');
END;
