"""SQL のミュータントを数え、1 つだけ当てて書き出す（qa/05 §3-1）。

Stryker が変異させるのは C# のソースだけで、別ファイルにある SQL はその外にいる。
ここは**変異点の列挙と注入**だけを持ち、**殺せたかの判定は持たない**
（判定は qa/05 §3-2 の B 案・§3-3 の A 案が別々に決める）。

    python tools/clb/sql_mutate.py list <SQL のパス>        変異点を人が読む形で印字
    python tools/clb/sql_mutate.py spec <パス> <モジュール名>  掃引が読む形（1 行 1 点。TAB 区切り）
    python tools/clb/sql_mutate.py show <パス> <番号>       その 1 個を当てた SQL を印字
    python tools/clb/sql_mutate.py mask <パス>              注記と文字列を伏せた姿を印字（裏取り用）
    python tools/clb/sql_mutate.py audit                    追跡下の全 SQL を走査して要約
    python tools/clb/sql_mutate.py selftest                 走査そのものが空回りしていないか

**掃引そのものは `tools/clb/sql_sweep.ps1` が回す。ここは数えて印字するだけ**
（制約ノックアウトで `SchemaKnockout` と `knockout.ps1` を分けたのと同じ形）。

**注記と文字列リテラルの中は変異させない。** 当てると、
**振る舞いが変わらないミュータント（等価）ばかりが増えて、生き残りの数が意味を失う**。
SQLite は二重引用符を識別子の引用にも使うので、そこも伏せる
（伏せるだけで、識別子そのものは変異の対象にしていない）。
"""
import re
import subprocess
import sys
from pathlib import Path

# 変異演算子。**このリポジトリで実際に起きた・起きかけた壊れ方**から起こす（qa/05 §3-1）。
# (名前, 見つける形, 置換, 由来) の組。見つける形は伏せ字を当てた文字列に対して当てる。
#
# **空白は `[ \t]` に限る。改行をまたがせない**——またぐと原文に改行が入り、
# **`spec` の 1 行が 2 行に割れて、掃引が幽霊の点を数える**。
# **大文字小文字は区別する。** `DATE(` は拾わないし `and` も拾わない——
# **このリポジトリの SQL は書き方が揃っている**からで、揃わなくなったら母数の下限が鳴る
# （下限の正典は `sql_sweep.ps1` のモジュール別の表）。
OPERATORS = [
    ("boundary-ge", re.compile(r">="), ">", "境界の取り違え（qa/02 ラウンド 4）"),
    ("boundary-le", re.compile(r"<="), "<", "境界の取り違え（qa/02 ラウンド 4）"),
    # **`(?<![\w.])` で揃える。** `\b` だと `t.date(` にも当たり、
    # 当てると `t.(x)` という**構文エラーにしかならない**ミュータントが増える。
    ("date-strip", re.compile(r"(?<![\w.])date[ \t]*\("), "(", "日付を文字列で比べて検査が外れた（qa/03 L-12）"),
    ("and-or", re.compile(r"(?<![\w.])AND(?![\w.])"), "OR", "条件の合成ミス"),
    ("or-and", re.compile(r"(?<![\w.])OR(?![\w.])"), "AND", "条件の合成ミス"),
    ("left-join", re.compile(r"(?<![\w.])LEFT[ \t]+(?:OUTER[ \t]+)?JOIN(?![\w.])"), "JOIN", "任意項目の行落ち"),
    ("is-null", re.compile(r"(?<![\w.])IS[ \t]+NULL(?![\w.])"), "IS NOT NULL", "NULL 分岐の反転"),
    ("is-not-null", re.compile(r"(?<![\w.])IS[ \t]+NOT[ \t]+NULL(?![\w.])"), "IS NULL", "NULL 分岐の反転"),
    ("distinct", re.compile(r"(?<![\w.])DISTINCT[ \t]+"), "", "重複の除去が効いているか"),
    ("desc", re.compile(r"(?<![\w.])DESC(?![\w.])"), "ASC", "並び順"),
    ("asc", re.compile(r"(?<![\w.])ASC(?![\w.])"), "DESC", "並び順"),
    ("nullif", re.compile(r"(?<![\w.])NULLIF[ \t]*\("), "COALESCE(", "空文字を「無い」とみなす扱いの崩れ"),
]

MASK = "\x00"

# **クエリの SQL の変異点の数の下限**（実データ。2026-09-14 の実測は 276）。
# **母数は静かに痩せる**——閉じない引用符が 1 つ入れば伏せ字が以降を全部飲み、
# キーワードの大文字小文字が変われば演算子が当たらない。
# **痩せると生き残りも減り、上限だけの関門は緑になる。**
# **モジュールごとの内訳は `sql_sweep.ps1` の `$ratchet` が持つ**（あちらは毎回は流れない）。
MINIMUM_REAL_POINTS = 276


def mask(sql):
    """注記・文字列リテラル・二重引用符の識別子を伏せる。長さと改行は保つ。

    伏せた文字は MASK に置き換える。位置がずれないので、
    **伏せた側で見つけた位置を、そのまま原本に当てられる**。
    """
    out = []
    i, n = 0, len(sql)
    while i < n:
        c = sql[i]
        if c == "-" and sql.startswith("--", i):
            j = sql.find("\n", i)
            j = n if j < 0 else j
            out.append(MASK * (j - i))
            i = j
        elif c == "/" and sql.startswith("/*", i):
            j = sql.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append(_keep_newlines(sql[i:j]))
            i = j
        elif c in "'\"":
            j = i + 1
            while j < n:
                if sql[j] == c:
                    if j + 1 < n and sql[j + 1] == c:   # '' は文字そのもの
                        j += 2
                        continue
                    j += 1
                    break
                j += 1
            out.append(_keep_newlines(sql[i:j]))
            i = j
        else:
            out.append(c)
            i += 1

    masked = "".join(out)
    assert len(masked) == len(sql), "伏せ字で長さが変わった"
    return masked


def _keep_newlines(text):
    return "".join("\n" if ch == "\n" else MASK for ch in text)


def points(sql):
    """変異点。(番号, 演算子, 開始, 終わり, 置換, 原文) の組を位置の順に返す。"""
    masked = mask(sql)
    found = []
    for name, pattern, replacement, _ in OPERATORS:
        for m in pattern.finditer(masked):
            found.append((name, m.start(), m.end(), replacement, sql[m.start():m.end()]))

    found.sort(key=lambda p: (p[1], p[0]))
    return [(i, *p) for i, p in enumerate(found)]


def apply(sql, index):
    """変異点を 1 つだけ当てた SQL。**同時に 1 箇所しか変えない**のが原則である。"""
    found = points(sql)
    if not 0 <= index < len(found):
        raise SystemExit(f"変異点 {index} は無い（0〜{len(found) - 1}）")

    _, _, start, end, replacement, _ = found[index]
    return sql[:start] + replacement + sql[end:]


def read(path):
    """**`File.ReadAllText` と同じ姿で読む。**

    `Path.read_text()` は **BOM を `﻿` として残し、`
` を `
` に潰す**。
    C# 側は逆に **BOM を捨て、改行はそのまま**返すので、
    **どちらかが 1 文字でも違うと、印字した位置が別の場所を指す**——
    しかも**構文としては通ることがあり、赤くなって「殺した」に数えられる**
    （道具が BOM を付けてコミットまで通った実例がある。qa/03 の L-24）。
    **読み方を揃えたうえで、当たったかを注入側でも確かめる**（`spec` の 5 欄目）。
    """
    return Path(path).read_bytes().decode("utf-8-sig")


def repository_root():
    """リポジトリのルート。**どこから起動しても同じ答えを返す**ためにある。"""
    return Path(__file__).resolve().parents[2]


def query_sql():
    """クエリモジュールの SQL。**掃引が回すのと同じ集合**である。"""
    found = sorted((repository_root() / "Designer" / "Design" / "Modules").rglob("*.Query.sql"))

    if not found:
        raise SystemExit("クエリの SQL が 1 本も見つからない。")

    return found


def tracked_sql():
    """Git が追跡している SQL のうち、デザイナ配布物の見本を除いたもの。"""
    # **どこから起動しても同じ答えを返す。** `git ls-files` は cwd 相対なので、
    # **`tools/` の中から呼ぶと 0 本になり、裏取り用の命令が最良の報告を返す**。
    root = repository_root()
    listed = subprocess.run(
        ["git", "-C", str(root), "ls-files", "--", "*.sql"],
        capture_output=True, text=True, check=True).stdout.split()
    found = [root / p for p in listed
             if "_samples" not in p and "ClaudeCodeForDesigner" not in p]

    if not found:
        raise SystemExit("追跡下の SQL が 1 本も見つからない。")

    return found


# **12 個の演算子が 1 度ずつ当たる検体。** 演算子ごとの小さな検体を別に持つと
# **表の 2 つ目の写しになり、正規表現を書き換える人が両方を直してしまって、死んだ形が捕まらない**。
SPECIMEN = """-- 注記の中の date( と >= と AND と DESC は変異させない。'閉じない引用符 も無害。
/* 囲みの注記でも同じ。 IS NULL / LEFT JOIN / DISTINCT */
SELECT DISTINCT "列 >= の名前" AS a, 'それ''は >= 文字 -- も含む' AS b,
       NULLIF(t.c, '') AS c
  FROM t
  LEFT JOIN u ON u.id = t.u_id
 WHERE date(t.d) >= date(@p) AND t.x IS NULL AND t.y IS NOT NULL
   AND t.date(t.e) = 1
    OR t.z <= @q
 ORDER BY t.c DESC, t.d ASC;
"""


def selftest():
    """**中身を空にしても緑**という状態を作らないための検査。

    見るのは 6 つ。

    1. 伏せ字が位置と行数をずらさないこと
    2. **伏せなければ拾ってしまう形が検体に実在し**、拾わなかったものが全部伏せた位置にあること
    3. **拾ってはいけない形**（`t.date(` のような修飾子の後ろ）を拾っていないこと
    4. 1 つ当てると 1 行だけ変わること
    5. **12 個の演算子が 1 つ残らず検体に当たり、当てると SQL が変わること**
    6. **実データ（クエリの SQL）の変異点が下限を割っていないこと**
    """
    ng = []
    masked = mask(SPECIMEN)

    if len(masked) != len(SPECIMEN):
        ng.append("伏せ字で長さが変わった")
    if masked.count("\n") != SPECIMEN.count("\n"):
        ng.append("伏せ字で行数が変わった")

    found = points(SPECIMEN)
    # **「注記の中を数えていない」を、行番号ではなく中身で見る。**
    # 伏せ字を当てた文字列から拾っている以上、`points()` の結果に伏せた位置は出ようがない
    # ——**それを検査しても同語反復**である。見るべきは
    # **①伏せなければ拾ってしまう形が検体に実在すること**と、
    # **②実際に拾わなかったものが全部、伏せた位置にあること**の 2 つ。
    taken = {(p[1], p[2]) for p in found}
    excluded = []
    for name, pattern, _replacement, _ in OPERATORS:
        for m in pattern.finditer(SPECIMEN):
            if (name, m.start()) not in taken:
                excluded.append((name, m.start(), SPECIMEN[m.start():m.end()]))

    if not excluded:
        ng.append("伏せなければ拾ってしまう形が検体に無い（注記か文字列に演算子を足すこと）")
    for name, start, text in excluded:
        if masked[start] != MASK:
            ng.append(f"伏せていない位置の {name}（{text!r}）を数え落とした: {start}")

    # **拾ってはいけない形**。`t.date(` に当てると `t.(x)` という
    # **構文エラーにしかならない**ミュータントが増え、理由なく「殺した」が増える。
    for _index, name, start, _end, _replacement, _original in found:
        if SPECIMEN[max(0, start - 1):start] == ".":
            ng.append(f"修飾子の後ろの {name} を拾った: {start}")

    if not found:
        ng.append("検体から変異点を 1 つも拾えていない")
    else:
        mutated = apply(SPECIMEN, 0)
        if mutated == SPECIMEN:
            ng.append("当てても SQL が変わらない")
        differing = sum(1 for a, b in zip(SPECIMEN.split("\n"), mutated.split("\n")) if a != b)
        if differing != 1:
            ng.append(f"1 つ当てて {differing} 行が変わった（1 行であるべき）")

    # **演算子の表が全部生きているか。** 死んだ正規表現があっても、
    # 実物の SQL では「その形が無かった」と区別が付かない。
    # **検体は 1 つだけにする**——演算子ごとの小さな検体を別に持つと表の写しになり、
    # **正規表現を書き換える人が写しも一緒に直してしまって、死んだ形が捕まらない**。
    for name, _pattern, _replacement, _ in OPERATORS:
        mine = [p for p in found if p[1] == name]
        if not mine:
            ng.append(f"演算子 {name} が検体に 1 度も当たらない（SPECIMEN に形を足すこと）")
            continue
        if apply(SPECIMEN, mine[0][0]) == SPECIMEN:
            ng.append(f"演算子 {name} は当てても SQL が変わらない")

    # **実データの母数にも下限を置く。** 検体だけを見ていると、
    # **実物の SQL の書き方が変わって演算子が当たらなくなった日に、何も鳴らない**
    # ——生き残りは母数と一緒に減るので、**上限だけの関門は緑になる**。
    # **掃引（`sql_sweep.ps1`）はモジュールごとに同じ下限を持つが、あれは毎回は流れない。**
    # **ここはコミット前フックで毎回流れる**（`tools/git-hooks/pre-commit` の「CLB デザインの検査」の段）。
    real = 0
    for path in sorted(query_sql()):
        real += len(points(read(path)))

    if real < MINIMUM_REAL_POINTS:
        ng.append(
            f"クエリの SQL の変異点が {real} 個しかない（下限 {MINIMUM_REAL_POINTS}）。"
            "書き方が変わって演算子が当たらなくなっていないか。")

    for line in ng:
        # **Windows のコンソール（cp932）で出せない字があっても落とさない。**
        # **関門の失敗が「印字で落ちた」に化けると、何が悪かったのかが分からなくなる。**
        encoding = sys.stdout.encoding or "utf-8"
        print("NG\t" + line.encode(encoding, "replace").decode(encoding))

    print(f"selftest: {'NG ' + str(len(ng)) + ' 件' if ng else 'OK'}"
          f"（検体の変異点 {len(found)} 個 / 演算子 {len(OPERATORS)} 個）")
    return 1 if ng else 0


def main(argv):
    if len(argv) < 2:
        raise SystemExit(__doc__)

    # **`--selftest` でも受ける。** 他の道具（`lint_docs.py` など）はフラグ形で、
    # **呼び方が揃っていないと、揃っているつもりで打った人がトレースバックを見る**。
    command = argv[1]
    if command in ("selftest", "--selftest"):
        return selftest()

    if command == "audit":
        files = tracked_sql()
        total = 0
        for path in files:
            found = points(read(path))
            total += len(found)
            # **絶対パスを印字しない**（公開リポジトリ。CLAUDE.md §5 の作法に揃える）。
            print(f"{len(found):4d}\t{path.relative_to(repository_root()).as_posix()}")

        print(f"\n{len(files)} ファイル / 変異点 {total} 個")
        return 0

    sql = read(argv[2])

    if command == "list":
        for index, name, start, end, replacement, original in points(sql):
            line = sql.count("\n", 0, start) + 1
            print(f"{index}\t{name}\t{line} 行\t{original!r} → {replacement!r}")
        return 0

    if command == "spec":
        # 掃引が読む形。**位置で指す**——`date(` も `OR` も 1 本の SQL に何十個もあり、
        # 字面では 1 つを名指せない。最後の欄は空でよい（`DISTINCT` の削除など）。
        module = argv[3]
        for index, name, start, end, replacement, original in points(sql):
            line = sql.count("\n", 0, start) + 1
            print("\t".join([
                str(index), name, str(line), original, replacement,
                # **原文を最後に載せる。** 注入側が「そこに本当にその字があるか」を確かめられるようにする
                # ——**位置だけだと、ずれたまま別の場所を壊しても気づけない**。
                # **置換後に `:` を入れないこと**（注入側は 5 つに割り、最後の欄だけが `:` を含んでよい）。
                f"{module}:{start}:{end - start}:{replacement}:{original}"]))
        return 0

    if command == "show":
        sys.stdout.write(apply(sql, int(argv[3])))
        return 0

    if command == "mask":
        sys.stdout.write(mask(sql).replace(MASK, "."))
        return 0

    raise SystemExit(f"知らない命令である: {command}")


if __name__ == "__main__":
    sys.exit(main(sys.argv))
