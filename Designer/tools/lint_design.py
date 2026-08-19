#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""CLB「静かな失敗」デザインリンタ.

仕様書: docs/qa/10_チェックリスト/01_CLB静かな失敗.md（36 ルール）
運用:   designcheck -> lint_design.py -> sql -> 実機

重大度のマッピング（仕様書 §実装優先度 と 終了コード規定に従う）:
    「高」  -> error   （1 件でもあれば終了コード 1）
    「中」  -> warn
    「低」  -> warn
  ただし個別ルールが「warn 止まり」と明記している下位検査は、群に関わらず warn を出す。
  CLB-018 / CLB-024 / CLB-036 は仕様書が warn 専用と明記しているため常に warn。

Python 3.8+ 標準ライブラリのみ。外部依存なし。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from collections import Counter, defaultdict

SEV_ERROR = "error"
SEV_WARN = "warn"
SEV_ORDER = {SEV_ERROR: 0, SEV_WARN: 1}

# ルールID -> (群, 一行タイトル)
RULES = {
    "CLB-001": ("高", "HorizontalAlignment の旧値（Left / Right）"),
    "CLB-002": ("高", "Date/DateTime/Time フィールドが TEXT 列にマップされている"),
    "CLB-003": ("高", "SQL の 'now' が UTC のまま（'localtime' 無し・順序違い）"),
    "CLB-004": ("高", "OnValidateInput が設定されている（無言で保存が止まる）"),
    "CLB-005": ("高", "OrderBy/OrderByDescending のラムダに .Value が無い"),
    "CLB-006": ("高", "スクリプトの ref / out 引数（書き戻しが伝わらない）"),
    "CLB-007": ("高", "スクリプトの try / catch / finally（非対応構文）"),
    "CLB-008": ("高", "DateField.Value へ DateTime を代入している"),
    "CLB-009": ("高", "this.IsViewOnly = true のモジュールでボタンが死ぬ"),
    "CLB-010": ("高", "表示専用モジュールの Detail にボタンがあるのに IsViewOnly 解除が無い"),
    "CLB-011": ("高", "ModulePageType が List なのに詳細遷移を許している"),
    "CLB-012": ("高", "リンクの SearchCondition.ModuleName が遷移先モジュールと食い違う"),
    "CLB-013": ("高", "遷移先フレームに Module×セグメントが未登録（真っ白）"),
    "CLB-014": ("高", "OR 検索フィールドに SearchValue（単数）を代入している"),
    "CLB-015": ("中", "表示専用モジュールで this.Submit() を呼んでいる"),
    "CLB-016": ("中", "表示専用ホストの ListField に CanDelete: true"),
    "CLB-017": ("中", "Boolean の DB 既定が 1 なのに新規作成で未チェックになる"),
    "CLB-018": ("中", "導出値を IsUpdateProtected だけで守っている（warn 専用）"),
    "CLB-019": ("中", "範囲検索フィールドに AllowEmptySearch: true"),
    "CLB-020": ("中", "検索レイアウトにリンク先参照フィールド（A.B 形式）を置いている"),
    "CLB-021": ("中", "ExecuteSqlField の @プレースホルダが Parameters と一致しない"),
    "CLB-022": ("中", "CanCreate: false のモジュールをスクリプトが new している"),
    "CLB-023": ("中", "SQLite の生成列を DbColumn に指定している"),
    "CLB-024": ("中", "子モジュールの FK 列に NOT NULL が付いている（warn 専用）"),
    "CLB-025": ("中", "Delete() の戻り値を検査していない"),
    "CLB-026": ("中", "検索既定を持つ一覧へ GetModuleUrl で遷移している"),
    "CLB-027": ("中", "AnchorTagField に OnClick を設定している"),
    "CLB-028": ("中", "DbTable がビューなのに INSTEAD OF トリガーが無い"),
    "CLB-029": ("低", "レガシー TopPageModule が TopPageModuleDesign.Module と食い違う"),
    "CLB-030": ("低", "検索行に IsWrap: true が付いていない"),
    "CLB-031": ("低", "検索行に入力欄を 4 組以上詰めている"),
    "CLB-032": ("低", "PasswordField の確認欄が意図せず並んでいる"),
    "CLB-033": ("低", "IsVisible = false にした固定幅カラムの穴埋め CSS が無い"),
    "CLB-034": ("低", "CurrentUser.<SelectField>.DisplayText を参照している"),
    "CLB-035": ("低", "LoadingService.StartLoading() が MessageBox.Show() より前にある"),
    "CLB-036": ("低", "自作 SQL の日付比較が date() で正規化されていない（warn 専用）"),
    "CLB-037": ("高", "DataWriteCondition が参照する列がその画面で未ロード（全項目が読み取り専用になる）"),
    "CLB-038": ("高", "資金繰り SQL の複製（ポータル ⇄ 予測画面）が食い違っている"),
    "CLB-039": ("高", "AddRows の引数が List<モジュール型> の .Count 由来（多重定義が外れて実行時に落ちる）"),
    "CLB-040": ("高", "整数どうしの割り算を var で受けている（CLB では小数になり、切り捨て前提の計算が狂う）"),
    "CLB-041": ("高", "`.mod.cs` のイベントハンドラが `.mod.json` から一度も参照されていない（書いたのに呼ばれない）"),
}

# 実装しなかったルール（黙って落とさず実行時に一覧表示する）
NOT_IMPLEMENTED = {
    # 例: "CLB-0xx": "理由",
}

# 意図的に検出条件を絞ったルール（誤検知を出さないための判断。NOT_IMPLEMENTED ではない）
NARROWED = {
    "CLB-006": "呼び出し先がスクリプト内で定義された関数（＝バレ名の呼び出し／定義シグネチャ）のときだけ違反にする。"
               "`int.TryParse(s, out x)` 等の .NET 組み込みは _specs/Scripts.md『out / ref パラメータ』でサポート構文として"
               "明記されているため除外（FB-039 の実測対象はユーザー定義関数 `int PostOne(Draft d, ref int tax)`）。",
    "CLB-009": "AnchorTagField（戻るリンク等）は除外。可視性をスクリプトで制御しているボタン（IsVisible への代入がある＝"
               "式で隠している場合を含む）も除外し、『常に表示されるのに押せない』ボタンだけを報告する。",
    "CLB-010": "FB-035 の実測条件どおり DbTable 空 かつ CanCreate/CanUpdate/CanDelete が全て false のときだけ違反にする。"
               "CanUpdate:true の表示専用モジュール（JournalLineDepartment 等）は同型でも発火しないことが"
               "ADR-0056 の実機検証で確認されているため対象外。",
    "CLB-022": "new と同じメソッド内に Submit( が無いもの（読み取り用インスタンス化）は除外する。",
    "CLB-025": "this.Delete()（自モジュール削除・UI 経路）は除外する。検索インスタンスの Delete() だけを見る。",
}

# 部分的にしか機械検査していないルール（実行時に注記として表示する）
PARTIAL = {
    "CLB-013": "検査 D（GetModuleUrl の全フレーム解決）は誤検知しうるため warn 止まり。",
    "CLB-024": "多段ネスト（孫を持つ子）に限定して報告。単一階層の NOT NULL REFERENCES は報告しない。",
    "CLB-028": "app.clprj に PasswordCheckUserTableInfo が無い環境では、その分の検査は行わない（サーバ側 appsettings.json は対象外）。",
    "CLB-036": "ファイル×列単位のサマリで報告（1 出現ごとには出さない）。",
    "CLB-018": "仕様書どおり DetailLayouts のみを見る。子明細（ListLayouts にしか出ない）の導出値は対象外。",
}


class Finding:
    __slots__ = ("rule", "severity", "file", "loc", "message")

    def __init__(self, rule, severity, file, loc, message):
        self.rule = rule
        self.severity = severity
        self.file = file
        self.loc = str(loc)
        self.message = message

    def key(self):
        return (SEV_ORDER.get(self.severity, 9), self.rule, self.file, self.loc)

    def as_dict(self):
        return {
            "rule": self.rule,
            "severity": self.severity,
            "group": RULES.get(self.rule, ("?", ""))[0],
            "file": self.file,
            "location": self.loc,
            "message": self.message,
        }


# --------------------------------------------------------------------------
# 共通ユーティリティ
# --------------------------------------------------------------------------

def read_text(path):
    with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
        return f.read()


def line_of(text, offset):
    return text.count("\n", 0, offset) + 1


def walk_kv(obj, path=""):
    """全ての (JSONパス, キー名, 値) を再帰で列挙する（深さ無制限＝レイアウトの 3 段入れ子を含む）。"""
    if isinstance(obj, dict):
        for k, v in obj.items():
            p = "{}.{}".format(path, k) if path else k
            yield p, k, v
            if isinstance(v, (dict, list)):
                yield from walk_kv(v, p)
    elif isinstance(obj, list):
        for i, v in enumerate(obj):
            p = "{}[{}]".format(path, i)
            if isinstance(v, (dict, list)):
                yield from walk_kv(v, p)


def walk_dicts(obj, path=""):
    """全ての dict ノードを (JSONパス, dict) で再帰列挙する。"""
    if isinstance(obj, dict):
        yield path, obj
        for k, v in obj.items():
            if isinstance(v, (dict, list)):
                yield from walk_dicts(v, "{}.{}".format(path, k) if path else k)
    elif isinstance(obj, list):
        for i, v in enumerate(obj):
            if isinstance(v, (dict, list)):
                yield from walk_dicts(v, "{}[{}]".format(path, i))


def json_line_index(text):
    """JSON テキストを走査して「JSONパス -> 1 始まりの行番号」を作る。

    json モジュールは位置情報を返さないため、パスと行を対応づける軽量スキャナを自前で持つ。
    walk_kv / walk_dicts と同じパス表記（key / key[0] / key[0].sub）を生成する。
    """
    out = {}
    n = len(text)
    i = 0
    line = 1
    stack = []

    def value_path():
        if not stack:
            return ""
        top = stack[-1]
        if top["kind"] == "obj":
            k = top["key"]
            if k is None:
                return top["path"]
            return "{}.{}".format(top["path"], k) if top["path"] else k
        return "{}[{}]".format(top["path"], top["idx"])

    def end_value():
        if not stack:
            return
        top = stack[-1]
        if top["kind"] == "obj":
            top["key"] = None
        else:
            top["idx"] += 1

    while i < n:
        c = text[i]
        if c == "\n":
            line += 1
            i += 1
            continue
        if c in " \t\r":
            i += 1
            continue
        if c in ":,":
            i += 1
            continue
        if c == '"':
            start_line = line
            j = i + 1
            while j < n:
                ch = text[j]
                if ch == "\\":
                    j += 2
                    continue
                if ch == '"':
                    break
                if ch == "\n":
                    line += 1
                j += 1
            raw = text[i:j + 1]
            i = j + 1
            if stack and stack[-1]["kind"] == "obj" and stack[-1]["key"] is None:
                try:
                    stack[-1]["key"] = json.loads(raw)
                except Exception:
                    stack[-1]["key"] = raw.strip('"')
            else:
                out.setdefault(value_path(), start_line)
                end_value()
            continue
        if c in "{[":
            p = value_path()
            out.setdefault(p, line)
            stack.append({"kind": "obj" if c == "{" else "arr", "path": p, "key": None, "idx": 0})
            i += 1
            continue
        if c in "}]":
            if stack:
                stack.pop()
            end_value()
            i += 1
            continue
        j = i
        while j < n and text[j] not in ",}] \t\r\n":
            j += 1
        out.setdefault(value_path(), line)
        end_value()
        i = j

    return out


def strip_cs(src):
    """C# のコメント・文字列/文字リテラルを空白で潰す（長さと改行位置は保存する）。"""
    out = list(src)
    n = len(src)
    i = 0

    def blank(a, b):
        for k in range(a, min(b, n)):
            if out[k] != "\n":
                out[k] = " "

    while i < n:
        c = src[i]
        if c == "/" and i + 1 < n and src[i + 1] == "/":
            j = src.find("\n", i)
            j = n if j < 0 else j
            blank(i, j)
            i = j
            continue
        if c == "/" and i + 1 < n and src[i + 1] == "*":
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j)
            i = j
            continue
        if c == "@" and i + 1 < n and src[i + 1] == '"':
            j = i + 2
            while j < n:
                if src[j] == '"':
                    if j + 1 < n and src[j + 1] == '"':
                        j += 2
                        continue
                    j += 1
                    break
                j += 1
            blank(i, j)
            i = j
            continue
        if c == '"':
            j = i + 1
            while j < n:
                if src[j] == "\\":
                    j += 2
                    continue
                if src[j] == '"':
                    j += 1
                    break
                if src[j] == "\n":
                    break
                j += 1
            blank(i, j)
            i = j
            continue
        if c == "'":
            j = i + 1
            while j < n:
                if src[j] == "\\":
                    j += 2
                    continue
                if src[j] == "'":
                    j += 1
                    break
                if src[j] == "\n":
                    break
                j += 1
            blank(i, j)
            i = j
            continue
        i += 1
    return "".join(out)


def strip_sql_comments(src):
    """SQL の -- / * * / コメントを空白で潰す（文字列リテラルは残す）。"""
    out = list(src)
    n = len(src)
    i = 0

    def blank(a, b):
        for k in range(a, min(b, n)):
            if out[k] != "\n":
                out[k] = " "

    while i < n:
        c = src[i]
        if c == "-" and i + 1 < n and src[i + 1] == "-":
            j = src.find("\n", i)
            j = n if j < 0 else j
            blank(i, j)
            i = j
            continue
        if c == "/" and i + 1 < n and src[i + 1] == "*":
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j)
            i = j
            continue
        if c == "'":
            j = i + 1
            while j < n:
                if src[j] == "'":
                    if j + 1 < n and src[j + 1] == "'":
                        j += 2
                        continue
                    j += 1
                    break
                j += 1
            i = j
            continue
        i += 1
    return "".join(out)


def blank_sql_strings(src):
    """SQL の文字列リテラルを空白で潰す（コメントは先に潰しておくこと）。"""
    out = list(src)
    n = len(src)
    i = 0
    while i < n:
        if src[i] == "'":
            j = i + 1
            while j < n:
                if src[j] == "'":
                    if j + 1 < n and src[j + 1] == "'":
                        j += 2
                        continue
                    j += 1
                    break
                j += 1
            for k in range(i, min(j, n)):
                if out[k] != "\n":
                    out[k] = " "
            i = j
            continue
        i += 1
    return "".join(out)


def split_top_level(text, sep=","):
    """括弧の深さを数えながら区切る。"""
    parts = []
    depth = 0
    buf = []
    in_str = False
    for ch in text:
        if in_str:
            buf.append(ch)
            if ch == "'":
                in_str = False
            continue
        if ch == "'":
            in_str = True
            buf.append(ch)
            continue
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        if ch == sep and depth == 0:
            parts.append("".join(buf))
            buf = []
            continue
        buf.append(ch)
    parts.append("".join(buf))
    return parts


def short_type(field):
    return (field.get("TypeFullName") or "").split(".")[-1]


# --------------------------------------------------------------------------
# インデックス構築
# --------------------------------------------------------------------------

class DesignIndex:
    def __init__(self, design_dir, ddl_dir):
        self.design_dir = design_dir
        self.ddl_dir = ddl_dir
        self.modules = {}        # name -> module dict（下記構造）
        self.frames = {}         # name -> frame dict
        self.tables = {}         # table -> {column -> decl(raw)}
        self.views = set()
        self.generated_columns = defaultdict(set)   # table -> {column}
        self.instead_of = defaultdict(set)          # view -> {"INSERT","UPDATE","DELETE"}
        self.app_css = ""
        self.clprj = {}
        self.query_sql_files = []   # (path, text)
        self.load_errors = []

        self._load_modules()
        self._load_frames()
        self._load_ddl()
        self._load_misc()

    # -- modules -----------------------------------------------------------
    def _load_modules(self):
        for path in sorted(iter_files(os.path.join(self.design_dir, "Modules"), ".mod.json")):
            try:
                text = read_text(path)
                data = json.loads(text)
            except Exception as exc:  # noqa: BLE001
                self.load_errors.append("{}: JSON 解析に失敗: {}".format(path, exc))
                continue
            name = data.get("Name") or os.path.basename(path)[:-len(".mod.json")]
            fields = data.get("Fields") or []
            cs_path = path[:-len(".mod.json")] + ".mod.cs"
            cs_raw = read_text(cs_path) if os.path.isfile(cs_path) else None
            self.modules[name] = {
                "name": name,
                "path": path,
                "json": data,
                "text": text,
                "lines": None,          # 遅延生成
                "fields": fields,
                "field_by_name": {f.get("Name"): f for f in fields if f.get("Name")},
                "cs_path": cs_path if cs_raw is not None else None,
                "cs_raw": cs_raw,
                "cs": strip_cs(cs_raw) if cs_raw is not None else None,
                "dir": os.path.dirname(path),
            }

    def module_lines(self, mod):
        if mod["lines"] is None:
            mod["lines"] = json_line_index(mod["text"])
        return mod["lines"]

    # -- frames ------------------------------------------------------------
    def _load_frames(self):
        for path in sorted(iter_files(os.path.join(self.design_dir, "PageFrames"), ".frm.json")):
            try:
                text = read_text(path)
                data = json.loads(text)
            except Exception as exc:  # noqa: BLE001
                self.load_errors.append("{}: JSON 解析に失敗: {}".format(path, exc))
                continue
            name = data.get("Name") or os.path.basename(path)[:-len(".frm.json")]
            links = list((data.get("Left") or {}).get("Links") or [])
            top = data.get("TopPageModuleDesign") or {}
            others = list(data.get("OtherPageModuleDesigns") or [])
            regs = defaultdict(set)
            entries = []   # (JSONパス, ページ定義)
            for i, l in enumerate(links):
                if l.get("Module"):
                    regs[l["Module"]].add(l.get("ModuleUrlSegment") or "")
                entries.append(("Left.Links[{}]".format(i), l))
            if top.get("Module"):
                regs[top["Module"]].add(top.get("ModuleUrlSegment") or "")
            if top:
                entries.append(("TopPageModuleDesign", top))
            for i, o in enumerate(others):
                if o.get("Module"):
                    regs[o["Module"]].add(o.get("ModuleUrlSegment") or "")
                entries.append(("OtherPageModuleDesigns[{}]".format(i), o))
            self.frames[name] = {
                "name": name,
                "path": path,
                "json": data,
                "text": text,
                "lines": None,
                "links": links,
                "top": top,
                "others": others,
                "regs": dict(regs),
                "entries": entries,
            }

    def frame_lines(self, fr):
        if fr["lines"] is None:
            fr["lines"] = json_line_index(fr["text"])
        return fr["lines"]

    # -- ddl ---------------------------------------------------------------
    def _load_ddl(self):
        if not os.path.isdir(self.ddl_dir):
            self.load_errors.append("DDL ディレクトリが見つからない: {}".format(self.ddl_dir))
            return
        for path in sorted(iter_files(self.ddl_dir, ".sql")):
            text = strip_sql_comments(read_text(path))
            self._apply_ddl(text)

    def _apply_ddl(self, text):
        events = []
        for m in re.finditer(r"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?[\"`\[]?(\w+)[\"`\]]?", text, re.I):
            events.append((m.start(), "table", m))
        for m in re.finditer(r"ALTER\s+TABLE\s+[\"`\[]?(\w+)[\"`\]]?\s+ADD\s+COLUMN\s+[\"`\[]?(\w+)[\"`\]]?([^;]*);", text, re.I):
            events.append((m.start(), "addcol", m))
        for m in re.finditer(r"ALTER\s+TABLE\s+[\"`\[]?(\w+)[\"`\]]?\s+DROP\s+COLUMN\s+[\"`\[]?(\w+)", text, re.I):
            events.append((m.start(), "dropcol", m))
        for m in re.finditer(r"CREATE\s+VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?[\"`\[]?(\w+)", text, re.I):
            events.append((m.start(), "view", m))
        for m in re.finditer(r"DROP\s+VIEW\s+(?:IF\s+EXISTS\s+)?[\"`\[]?(\w+)", text, re.I):
            events.append((m.start(), "dropview", m))
        for m in re.finditer(
            r"CREATE\s+TRIGGER\s+(?:IF\s+NOT\s+EXISTS\s+)?[\"`\[]?(\w+)[\"`\]]?\s+INSTEAD\s+OF\s+(INSERT|UPDATE|DELETE)"
            r"(?:\s+OF\s+[\w\s,\"`\[\]]+?)?\s+ON\s+[\"`\[]?(\w+)",
            text, re.I,
        ):
            events.append((m.start(), "instead", m))
        events.sort(key=lambda e: e[0])

        for pos, kind, m in events:
            if kind == "table":
                table = m.group(1)
                open_paren = text.find("(", m.end())
                if open_paren < 0:
                    continue
                depth = 0
                close = -1
                for j in range(open_paren, len(text)):
                    if text[j] == "(":
                        depth += 1
                    elif text[j] == ")":
                        depth -= 1
                        if depth == 0:
                            close = j
                            break
                if close < 0:
                    continue
                cols = self.tables.setdefault(table, {})
                for part in split_top_level(text[open_paren + 1:close]):
                    part = part.strip()
                    if not part:
                        continue
                    head = re.match(r"[\"`\[]?(\w+)[\"`\]]?\s*(.*)$", part, re.S)
                    if not head:
                        continue
                    cname, decl = head.group(1), head.group(2).strip()
                    if cname.upper() in ("PRIMARY", "FOREIGN", "UNIQUE", "CHECK", "CONSTRAINT", "KEY", "EXCLUDE"):
                        continue
                    cols.setdefault(cname, decl)
                    if re.search(r"GENERATED\s+ALWAYS\s+AS", decl, re.I):
                        self.generated_columns[table].add(cname)
            elif kind == "addcol":
                table, cname, decl = m.group(1), m.group(2), m.group(3).strip()
                self.tables.setdefault(table, {})[cname] = decl
                if re.search(r"GENERATED\s+ALWAYS\s+AS", decl, re.I):
                    self.generated_columns[table].add(cname)
                else:
                    self.generated_columns[table].discard(cname)
            elif kind == "dropcol":
                table, cname = m.group(1), m.group(2)
                self.tables.get(table, {}).pop(cname, None)
                self.generated_columns[table].discard(cname)
            elif kind == "view":
                self.views.add(m.group(1))
            elif kind == "dropview":
                pass  # 直後に作り直すのが本リポジトリの慣例。集合からは落とさない。
            elif kind == "instead":
                self.instead_of[m.group(3)].add(m.group(2).upper())

    def column_decl(self, table, column):
        return self.tables.get(table, {}).get(column)

    # -- misc --------------------------------------------------------------
    def _load_misc(self):
        css = os.path.join(self.design_dir, "app.css")
        if os.path.isfile(css):
            self.app_css = read_text(css)
        clprj = os.path.join(self.design_dir, "app.clprj")
        if os.path.isfile(clprj):
            try:
                self.clprj = json.loads(read_text(clprj))
            except Exception:  # noqa: BLE001
                self.clprj = {}
        for path in sorted(iter_files(os.path.join(self.design_dir, "Modules"), ".sql")):
            self.query_sql_files.append((path, read_text(path)))


def iter_files(root, suffix):
    if not os.path.isdir(root):
        return
    for dirpath, _dirs, files in os.walk(root):
        for fn in files:
            if fn.endswith(suffix):
                yield os.path.join(dirpath, fn)


def base_type(decl):
    if not decl:
        return ""
    m = re.match(r"\s*([A-Za-z_]\w*)(\s*\([^)]*\))?", decl)
    return (m.group(1) if m else "").upper()


def layout_field_names(node):
    """レイアウト木（任意の深さ）に配置されたフィールド名の集合。"""
    names = set()
    for _p, k, v in walk_kv(node):
        if k == "FieldName" and isinstance(v, str) and v:
            names.add(v)
    return names


# --------------------------------------------------------------------------
# ルール実装
# --------------------------------------------------------------------------

DATE_TYPE_EXPECT = {
    "DateFieldDesign": ("DATE", {"DATE"}),
    "DateTimeFieldDesign": ("DATETIME", {"DATETIME", "TIMESTAMP"}),
    "TimeFieldDesign": ("TIME", {"TIME"}),
}
STRINGY_TYPES = {"TEXT", "VARCHAR", "CHAR", "NVARCHAR", "NCHAR", "CLOB", "STRING", ""}
BUTTON_TYPES = {"ButtonFieldDesign", "SubmitButtonFieldDesign"}
RANGE_SEARCH_TYPES = {"DateFieldDesign", "DateTimeFieldDesign", "NumberFieldDesign", "TimeFieldDesign"}
VALID_HALIGN = {"Start", "Center", "End", "Stretch", ""}


def rule_001(idx, add):
    """HorizontalAlignment の旧値。"""
    for holder in list(idx.modules.values()) + list(idx.frames.values()):
        lines = idx.module_lines(holder) if "fields" in holder else idx.frame_lines(holder)
        for path, k, v in walk_kv(holder["json"]):
            if k != "HorizontalAlignment":
                continue
            if isinstance(v, str) and v in VALID_HALIGN:
                continue
            add("CLB-001", SEV_ERROR, holder["path"], lines.get(path, path),
                "HorizontalAlignment='{}' は 1.3 系で無効。既定 Start に化ける（{}）".format(v, path))


def rule_002(idx, add):
    """Date/DateTime/Time フィールドの列型。"""
    for mod in idx.modules.values():
        table = (mod["json"].get("DbTable") or "").strip()
        if not table:
            continue
        lines = idx.module_lines(mod)
        is_view = table in idx.views
        known_table = table in idx.tables
        for i, f in enumerate(mod["fields"]):
            st = short_type(f)
            if st not in DATE_TYPE_EXPECT:
                continue
            if f.get("IsYearMonthOnly") is True:
                continue
            col = (f.get("DbColumn") or "").strip()
            if not col:
                continue
            path = "Fields[{}].DbColumn".format(i)
            loc = lines.get(path, path)
            expect_name, expect_set = DATE_TYPE_EXPECT[st]
            if is_view or not known_table:
                add("CLB-002", SEV_WARN, mod["path"], loc,
                    "[確認不能] {}.{}（{}）: DbTable '{}' は{}のため列型を追えない（違反ではない）".format(
                        mod["name"], f.get("Name"), st, table,
                        "ビュー" if is_view else "DDL に無い"))
                continue
            decl = idx.column_decl(table, col)
            if decl is None:
                add("CLB-002", SEV_WARN, mod["path"], loc,
                    "[確認不能] {}.{}: 列 {}.{} が DDL に見つからない（違反ではない）".format(
                        mod["name"], f.get("Name"), table, col))
                continue
            bt = base_type(decl)
            if bt in expect_set:
                continue
            if bt in STRINGY_TYPES:
                add("CLB-002", SEV_ERROR, mod["path"], loc,
                    "{}.{}（{}）が {}.{} {} にマップ。{} でないと US 書式で保存され date() が NULL になる".format(
                        mod["name"], f.get("Name"), st, table, col, bt or "(型なし)", expect_name))
            else:
                add("CLB-002", SEV_WARN, mod["path"], loc,
                    "{}.{}（{}）が {}.{} {} にマップ。想定型は {}".format(
                        mod["name"], f.get("Name"), st, table, col, bt, expect_name))

    # 補助検出: DDL 単体で日付らしい列名なのに TEXT
    mapped = set()
    for mod in idx.modules.values():
        t = (mod["json"].get("DbTable") or "").strip()
        for f in mod["fields"]:
            c = (f.get("DbColumn") or "").strip()
            if t and c:
                mapped.add((t, c))
    date_name = re.compile(r"(_date$|_at$|_on$|^date_)")
    for table, cols in sorted(idx.tables.items()):
        for col, decl in sorted(cols.items()):
            if not date_name.search(col):
                continue
            if base_type(decl) not in STRINGY_TYPES:
                continue
            if (table, col) in mapped:
                continue  # 上のモジュール突合で扱い済み
            add("CLB-002", SEV_WARN, os.path.join(idx.ddl_dir, "*.sql"), "{}.{}".format(table, col),
                "日付らしい列名だが宣言型が {}（DDL 単体の補助検出）".format(base_type(decl) or "(型なし)"))


NOW_RE = re.compile(r"'now'", re.I)
CURRENT_RE = re.compile(r"\bCURRENT_(TIMESTAMP|DATE|TIME)\b", re.I)


def enclosing_call_args(text, pos):
    depth = 0
    start = -1
    i = pos - 1
    while i >= 0:
        ch = text[i]
        if ch == ")":
            depth += 1
        elif ch == "(":
            if depth == 0:
                start = i
                break
            depth -= 1
        i -= 1
    if start < 0:
        return None
    depth = 0
    for j in range(start + 1, len(text)):
        ch = text[j]
        if ch == "(":
            depth += 1
        elif ch == ")":
            if depth == 0:
                return text[start + 1:j]
            depth -= 1
    return None


def rule_003(idx, add):
    """SQL の 'now' が UTC のまま。DDL は対象外。"""
    for path, raw in idx.query_sql_files:
        text = strip_sql_comments(raw)
        for m in NOW_RE.finditer(text):
            args = enclosing_call_args(text, m.start())
            ln = line_of(text, m.start())
            if args is None:
                add("CLB-003", SEV_ERROR, path, ln,
                    "'now' が関数呼び出しの外にある（'localtime' を付けられない形）")
                continue
            parts = [p.strip() for p in split_top_level(args)]
            lowered = [p.lower() for p in parts]
            if "'localtime'" not in lowered:
                add("CLB-003", SEV_ERROR, path, ln,
                    "date/datetime(...) に 'localtime' が無く UTC のまま: ({})".format(args.strip()))
                continue
            try:
                i_now = lowered.index("'now'")
            except ValueError:
                i_now = next((i for i, p in enumerate(lowered) if "'now'" in p), 0)
            i_local = lowered.index("'localtime'")
            if i_local != i_now + 1:
                add("CLB-003", SEV_ERROR, path, ln,
                    "'localtime' が 'now' の直後の修飾子でない（他の修飾子が UTC のまま適用される）: ({})".format(args.strip()))
        for m in CURRENT_RE.finditer(text):
            add("CLB-003", SEV_ERROR, path, line_of(text, m.start()),
                "{} は UTC。date('now','localtime') 系に置き換える".format(m.group(0)))


def rule_004(idx, add):
    for mod in idx.modules.values():
        lines = idx.module_lines(mod)
        for path, k, v in walk_kv(mod["json"]):
            if k == "OnValidateInput" and isinstance(v, str) and v.strip():
                add("CLB-004", SEV_ERROR, mod["path"], lines.get(path, path),
                    "OnValidateInput='{}' は false を返すと無言で保存が止まる（{}）".format(v, path))


ORDERBY_RE = re.compile(r"OrderBy(?:Descending)?\(\s*(\w+)\s*=>\s*([^()]*?)\s*\)")


def rule_005(idx, add):
    for mod in idx.modules.values():
        if not mod["cs"]:
            continue
        for m in ORDERBY_RE.finditer(mod["cs"]):
            body = m.group(2)
            if body.endswith(".Value"):
                continue
            add("CLB-005", SEV_ERROR, mod["cs_path"], line_of(mod["cs"], m.start()),
                "OrderBy ラムダ '{}' が .Value で終わっていない（ソートが無効になる）".format(body))


REFOUT_RE = re.compile(r"[(,]\s*(?:ref|out)\s+\w")
# CLB スクリプトの関数定義（列 0 から「戻り型 名前(」で始まる）
SCRIPT_FUNC_DEF = re.compile(r"(?m)^(?:[\w<>\[\],\.\?]+\s+)+(\w+)\s*\(")


def script_function_names(idx):
    if getattr(idx, "_script_funcs", None) is None:
        names = set()
        for mod in idx.modules.values():
            if mod["cs"]:
                names.update(m.group(1) for m in SCRIPT_FUNC_DEF.finditer(mod["cs"]))
        idx._script_funcs = names
    return idx._script_funcs


def callee_before(text, pos):
    """pos を含む引数リストの直前の呼び出し名を (名前, レシーバ付きか) で返す。"""
    depth = 0
    i = pos - 1
    while i >= 0:
        ch = text[i]
        if ch == ")":
            depth += 1
        elif ch == "(":
            if depth == 0:
                break
            depth -= 1
        i -= 1
    if i < 0:
        return None, False
    j = i
    while j > 0 and text[j - 1] in " \t\r\n":
        j -= 1
    k = j
    while k > 0 and (text[k - 1].isalnum() or text[k - 1] == "_"):
        k -= 1
    name = text[k:j]
    if not name:
        return None, False
    qualified = k > 0 and text[k - 1] == "."
    return name, qualified


def rule_006(idx, add):
    """ref/out の書き戻しが伝わらないのは『スクリプト内で定義された関数』の場合。

    .NET 組み込み（int.TryParse など）は _specs/Scripts.md がサポート構文として明記しているため除外する。
    """
    funcs = script_function_names(idx)
    for mod in idx.modules.values():
        if not mod["cs"]:
            continue
        for m in REFOUT_RE.finditer(mod["cs"]):
            name, qualified = callee_before(mod["cs"], m.start())
            if not name or qualified or name not in funcs:
                continue   # レシーバ付き呼び出し／未知の名前＝.NET 組み込みとみなす
            add("CLB-006", SEV_ERROR, mod["cs_path"], line_of(mod["cs"], m.start()),
                "スクリプト定義関数 {}(...) の ref/out は書き戻されない（値が静かに落ちる・FB-039）".format(name))


TRY_RE = re.compile(r"\b(try|catch|finally)\s*[({]")


def rule_007(idx, add):
    for mod in idx.modules.values():
        if not mod["cs"]:
            continue
        for m in TRY_RE.finditer(mod["cs"]):
            add("CLB-007", SEV_ERROR, mod["cs_path"], line_of(mod["cs"], m.start()),
                "'{}' は CLB スクリプト非対応。再入ガードのフラグは DB 検索の外側に置く".format(m.group(1)))


def rule_008(idx, add):
    for mod in idx.modules.values():
        if not mod["cs"]:
            continue
        names = [f.get("Name") for f in mod["fields"] if short_type(f) == "DateFieldDesign" and f.get("Name")]
        for name in names:
            for m in re.finditer(r"\b" + re.escape(name) + r"\.Value\s*=\s*([^;]+);", mod["cs"]):
                rhs = m.group(1)
                if "DateTime" in rhs and "DateOnly" not in rhs:
                    add("CLB-008", SEV_ERROR, mod["cs_path"], line_of(mod["cs"], m.start()),
                        "{}.Value（DateField）へ DateTime を代入。DateOnly.FromDateTime() が必須: {}".format(
                            name, rhs.strip()[:80]))


MODULE_VIEWONLY_TRUE = re.compile(r"(?<![\w.])(?:this\.)?IsViewOnly\s*=\s*true")


def detail_layout_fields(mod):
    return layout_field_names(mod["json"].get("DetailLayouts") or {})


def rule_009(idx, add):
    for mod in idx.modules.values():
        if not mod["cs"]:
            continue
        m = MODULE_VIEWONLY_TRUE.search(mod["cs"])
        if not m:
            continue
        placed = detail_layout_fields(mod)
        # AnchorTagField（戻るリンク等）は除外＝NARROWED
        buttons = [f.get("Name") for f in mod["fields"]
                   if short_type(f) in BUTTON_TYPES and f.get("Name") in placed]
        # 閲覧専用解除があるもの、および可視性をスクリプトで制御しているもの（式で隠している場合を含む）は除外。
        # 残るのは「常に表示されるのに押せない」ボタンだけ。
        dead = [b for b in buttons
                if not re.search(r"\b" + re.escape(b) + r"\.IsViewOnly\s*=\s*false", mod["cs"])
                and not re.search(r"\b" + re.escape(b) + r"\.IsVisible\s*=", mod["cs"])]
        if dead:
            add("CLB-009", SEV_ERROR, mod["cs_path"], line_of(mod["cs"], m.start()),
                "this.IsViewOnly=true でボタンが pointer-events:none になる。明示解除の無いボタン: {}".format(
                    ", ".join(sorted(dead))))


def is_display_only(mod):
    j = mod["json"]
    return not (j.get("DbTable") or "").strip() or not (j.get("DataSourceName") or "").strip()


def rule_010(idx, add):
    for mod in idx.modules.values():
        if not is_display_only(mod):
            continue
        # FB-035 の実測条件は「DbTable 空 かつ CanCreate/CanUpdate/CanDelete が全て false」。
        # CanUpdate:true の表示専用モジュールは同型でも発火しないことが実機検証済み（ADR-0056）＝NARROWED
        j = mod["json"]
        if j.get("CanCreate") or j.get("CanUpdate") or j.get("CanDelete"):
            continue
        placed = detail_layout_fields(mod)
        buttons = [f.get("Name") for f in mod["fields"]
                   if short_type(f) in BUTTON_TYPES and f.get("Name") in placed]
        if not buttons:
            continue
        if mod["cs"] and re.search(r"IsViewOnly\s*=\s*false", mod["cs"]):
            continue
        add("CLB-010", SEV_ERROR, mod["path"], "DetailLayouts",
            "表示専用モジュール（DbTable/DataSourceName 空）の Detail にボタン {} があるが IsViewOnly=false の解除が無い（無反応になる）".format(
                ", ".join(sorted(buttons))))


def link_list_design(entry):
    return ((entry.get("ListPageDesign") or {}).get("ListFieldDesign") or {})


def rule_011(idx, add):
    for fr in idx.frames.values():
        lines = idx.frame_lines(fr)
        for path, entry in fr["entries"]:
            if (entry.get("ModulePageType") or "") != "List":
                continue
            lfd = link_list_design(entry)
            if lfd.get("CanNavigateToDetail") is True:
                add("CLB-011", SEV_ERROR, fr["path"], lines.get(path, path),
                    "{}: ModulePageType='List' かつ CanNavigateToDetail=true。詳細 URL が真っ白になる（'Auto' にする）".format(
                        entry.get("Module") or "?"))


def rule_012(idx, add):
    for fr in idx.frames.values():
        lines = idx.frame_lines(fr)
        for path, entry in fr["entries"]:
            module = entry.get("Module") or ""
            if not module:
                continue
            lfd = link_list_design(entry)
            sc = lfd.get("SearchCondition") or {}
            sc_mod = (sc.get("ModuleName") or "").strip()
            if sc_mod and sc_mod != module:
                add("CLB-012", SEV_ERROR, fr["path"], lines.get(path + ".ListPageDesign.ListFieldDesign.SearchCondition.ModuleName", path),
                    "リンク '{}' の SearchCondition.ModuleName='{}' が遷移先 Module='{}' と不一致。条件が黙って捨てられ全件表示になる".format(
                        entry.get("Title") or module, sc_mod, module))
                continue
            target = idx.modules.get(module)
            if not target:
                continue
            for p2, k2, v2 in walk_kv(sc):
                if k2 != "SearchTargetVariable" or not isinstance(v2, str) or not v2:
                    continue
                head = v2.split(".")[0]
                if head not in target["field_by_name"]:
                    add("CLB-012", SEV_ERROR, fr["path"], lines.get(path, path),
                        "リンク '{}' の SearchTargetVariable='{}' が モジュール {} に存在しない".format(
                            entry.get("Title") or module, v2, module))


NAV_LIT = re.compile(r'NavigateTo\("/(\w+)(?:/(\w+))?')
NAV_INTERP = re.compile(r'NavigateTo\(\$"/\{([^}]+)\}(?:/(\w+))?')
FRAME_VAR = re.compile(r'frame = "(\w+)"')
FRAME_RET = re.compile(r'return "(\w+)";')
GET_URL = re.compile(r'GetModule(?:Data)?Url\("(\w+)"')


def rule_013(idx, add):
    """check_navigation.py 相当の検査 A〜D。"""
    frames = idx.frames
    modules = set(idx.modules)

    frame_segments = {}
    for fname, fr in frames.items():
        segs = set()
        for mod, ss in fr["regs"].items():
            for s in ss:
                segs.add(s if s else mod)
        frame_segments[fname] = segs

    # A / B
    for fname, fr in frames.items():
        lines = idx.frame_lines(fr)
        for path, entry in fr["entries"]:
            mod = entry.get("Module")
            if not mod:
                continue
            loc = lines.get(path, path)
            if mod not in modules:
                add("CLB-013", SEV_ERROR, fr["path"], loc, "[A] モジュール '{}' が存在しない".format(mod))
            target = entry.get("PageFrame") or ""
            if not target:
                continue
            if target not in frames:
                add("CLB-013", SEV_ERROR, fr["path"], loc,
                    "[B] 切替リンク '{}' の遷移先フレーム '{}' が存在しない".format(entry.get("Title"), target))
                continue
            seg = entry.get("ModuleUrlSegment") or ""
            tregs = frames[target]["regs"]
            if mod not in tregs or seg not in tregs[mod]:
                add("CLB-013", SEV_ERROR, fr["path"], loc,
                    "[B] 切替リンク '{}' → {}/{} (seg='{}') が遷移先に未登録（真っ白）".format(
                        entry.get("Title"), target, mod, seg))

    mod_frames = defaultdict(set)
    for fname, fr in frames.items():
        for mod in fr["regs"]:
            mod_frames[mod].add(fname)

    seen_d = set()
    for mod in idx.modules.values():
        src = mod["cs_raw"]
        if not src:
            continue
        name = mod["name"]
        candidates = set()
        for m in FRAME_VAR.finditer(src):
            t = m.group(1)
            if t not in frames:
                add("CLB-013", SEV_ERROR, mod["cs_path"], line_of(src, m.start()),
                    "[C] フレーム名 '{}' が存在しない".format(t))
            else:
                candidates.add(t)
        for m in FRAME_RET.finditer(src):
            if m.group(1) in frames:
                candidates.add(m.group(1))

        for m in NAV_LIT.finditer(src):
            target, seg = m.group(1), m.group(2)
            if target in ("login",):
                continue
            ln = line_of(src, m.start())
            if target not in frames:
                add("CLB-013", SEV_ERROR, mod["cs_path"], ln,
                    "[C] NavigateTo 先フレーム '{}' が存在しない".format(target))
                continue
            if seg and seg not in frame_segments[target]:
                add("CLB-013", SEV_ERROR, mod["cs_path"], ln,
                    "[C] NavigateTo '/{}/{}' のセグメントが {} に未登録（真っ白）".format(target, seg, target))

        def resolver_frames(fn_name):
            fm = re.search(r"string " + re.escape(fn_name) + r"\(\)\s*\{(.*?)\n\}", src, re.S)
            if not fm:
                return None
            return {x for x in re.findall(r'"(\w+)"', fm.group(1)) if x in frames}

        for m in NAV_INTERP.finditer(src):
            expr, seg = m.group(1), m.group(2)
            if not seg:
                continue
            cands = None
            if expr.endswith("()"):
                cands = resolver_frames(expr[:-2])
            if cands is None:
                cands = candidates
            for target in sorted(cands):
                if seg not in frame_segments[target]:
                    add("CLB-013", SEV_ERROR, mod["cs_path"], line_of(src, m.start()),
                        "[C] 補間遷移 '/{{{}}}/{}' が候補フレーム {} に未登録（真っ白）".format(expr, seg, target))

        callers = mod_frames.get(name, set())
        for m in GET_URL.finditer(src):
            target_mod = m.group(1)
            if target_mod not in modules:
                add("CLB-013", SEV_ERROR, mod["cs_path"], line_of(src, m.start()),
                    "[D] GetModuleUrl 対象 '{}' が存在しない".format(target_mod))
                continue
            for cf in sorted(callers):
                if target_mod not in frames[cf]["regs"]:
                    key = (name, cf, target_mod)
                    if key in seen_d:
                        continue
                    seen_d.add(key)
                    add("CLB-013", SEV_WARN, mod["cs_path"], line_of(src, m.start()),
                        "[D] {} は {} にも登録されているが GetModuleUrl('{}') は {} で解決不能（経路ガードの確認要）".format(
                            name, cf, target_mod, cf))


def rule_014(idx, add):
    or_fields = defaultdict(set)   # module -> {field}
    for mod in idx.modules.values():
        for f in mod["fields"]:
            if f.get("AllowOrSearch") is True and f.get("Name"):
                or_fields[mod["name"]].add(f["Name"])
    if not or_fields:
        return
    for mod in idx.modules.values():
        if not mod["cs"]:
            continue
        for owner, names in or_fields.items():
            sev = SEV_ERROR if owner == mod["name"] else SEV_WARN
            for name in sorted(names):
                for m in re.finditer(r"(?<![\w])" + re.escape(name) + r"\.SearchValue\s*=", mod["cs"]):
                    add("CLB-014", sev, mod["cs_path"], line_of(mod["cs"], m.start()),
                        "{}.{} は AllowOrSearch:true。SearchValue（単数）では既定条件が効かない。SearchValues にする".format(
                            owner, name))


SUBMIT_RE = re.compile(r"(^|[^.\w])(this\.)?Submit\s*\(", re.M)


def rule_015(idx, add):
    for mod in idx.modules.values():
        if not is_display_only(mod) or not mod["cs"]:
            continue
        for m in SUBMIT_RE.finditer(mod["cs"]):
            add("CLB-015", SEV_WARN, mod["cs_path"], line_of(mod["cs"], m.start()),
                "表示専用モジュール（DbTable 空）の Submit() は戻り値も例外も無く何も起きない。行の保存は row.Submit()")


def rule_016(idx, add):
    for mod in idx.modules.values():
        if not is_display_only(mod):
            continue
        lines = idx.module_lines(mod)
        has_delete = bool(mod["cs"] and re.search(r"\.Delete\s*\(", mod["cs"]))
        for i, f in enumerate(mod["fields"]):
            if short_type(f) != "ListFieldDesign" or f.get("CanDelete") is not True:
                continue
            path = "Fields[{}].CanDelete".format(i)
            if has_delete:
                add("CLB-016", SEV_WARN, mod["path"], lines.get(path, path),
                    "{}.{}: 表示専用ホストの ListField に CanDelete:true。mod.cs に Delete() があるので差分同期の実装を確認する".format(
                        mod["name"], f.get("Name")))
            else:
                add("CLB-016", SEV_WARN, mod["path"], lines.get(path, path),
                    "{}.{}: 表示専用ホストの ListField に CanDelete:true。削除が DB に届かず再読込で行が復活する".format(
                        mod["name"], f.get("Name")))


def rule_017(idx, add):
    for mod in idx.modules.values():
        table = (mod["json"].get("DbTable") or "").strip()
        if not table or table not in idx.tables:
            continue
        # **新規作成できないモジュールは対象外**（2026-08-19）。
        # 参照専用のピッカー／ビュー（AccountLookup・PartnerView など）は
        # 同じテーブルを指していても行を作らないので、「新規時に未チェック」が起こらない。
        # 実体を作る側のモジュール（Account・Partner）で別途検査される
        if mod["json"].get("CanCreate") is False:
            continue
        lines = idx.module_lines(mod)
        for i, f in enumerate(mod["fields"]):
            if short_type(f) != "BooleanFieldDesign":
                continue
            col = (f.get("DbColumn") or "").strip()
            if not col:
                continue
            decl = idx.column_decl(table, col)
            if not decl or not re.search(r"DEFAULT\s*\(?\s*(1|TRUE)\s*\)?", decl, re.I):
                continue
            name = f.get("Name") or ""
            if mod["cs"] and re.search(r"\b" + re.escape(name) + r"\.Value\s*=\s*true", mod["cs"]):
                continue
            path = "Fields[{}].DbColumn".format(i)
            add("CLB-017", SEV_WARN, mod["path"], lines.get(path, path),
                "{}.{}: DB 既定 1 の列 {}.{} だが CLB は新規時に常に未チェック。IsNewData 分岐で .Value=true が要る".format(
                    mod["name"], name, table, col))


def rule_018(idx, add):
    """warn 専用（仕様書明記）。"""
    for mod in idx.modules.values():
        lines = idx.module_lines(mod)
        detail = mod["json"].get("DetailLayouts") or {}
        elements = defaultdict(list)   # field -> [layout element dict]
        for _p, d in walk_dicts(detail):
            fn = d.get("FieldName")
            if isinstance(fn, str) and fn:
                elements[fn].append(d)
        for i, f in enumerate(mod["fields"]):
            if f.get("IsUpdateProtected") is not True:
                continue
            name = f.get("Name") or ""
            els = elements.get(name)
            if not els:
                continue
            if any(e.get("IsViewOnly") is True for e in els):
                continue
            path = "Fields[{}].IsUpdateProtected".format(i)
            add("CLB-018", SEV_WARN, mod["path"], lines.get(path, path),
                "{}.{}: IsUpdateProtected は更新時のみの保護。新規作成時は手入力できるが意図どおりか".format(
                    mod["name"], name))


def rule_019(idx, add):
    for mod in idx.modules.values():
        lines = idx.module_lines(mod)
        for i, f in enumerate(mod["fields"]):
            if short_type(f) in RANGE_SEARCH_TYPES and f.get("AllowEmptySearch") is True:
                path = "Fields[{}].AllowEmptySearch".format(i)
                add("CLB-019", SEV_WARN, mod["path"], lines.get(path, path),
                    "{}.{}（{}）: 範囲検索フィールドの AllowEmptySearch は効果が無い。NULL 行は常に落ちる".format(
                        mod["name"], f.get("Name"), short_type(f)))


def rule_020(idx, add):
    for mod in idx.modules.values():
        lines = idx.module_lines(mod)
        link_names = set(mod["json"].get("LinkFieldNames") or [])
        for path, k, v in walk_kv({"SearchLayouts": mod["json"].get("SearchLayouts") or {}}):
            if k != "FieldName" or not isinstance(v, str) or "." not in v:
                continue
            add("CLB-020", SEV_WARN, mod["path"], lines.get(path, path),
                "{}: 検索レイアウトのリンク先参照 '{}' は候補がロードされず実質検索不能{}".format(
                    mod["name"], v, "（LinkFieldNames 登録あり）" if v in link_names else ""))


PLACEHOLDER_RE = re.compile(r"@(\w+)")


def rule_021(idx, add):
    for mod in idx.modules.values():
        lines = idx.module_lines(mod)
        for i, f in enumerate(mod["fields"]):
            if short_type(f) != "ExecuteSqlFieldDesign":
                continue
            name = f.get("Name") or ""
            setting = f.get("ExecuteSqlSetting") or {}
            sql = setting.get("SqlText") or ""
            src_label = mod["path"]
            if not sql.strip():
                sql_path = os.path.join(mod["dir"], "{}.{}.sql".format(mod["name"], name))
                if os.path.isfile(sql_path):
                    sql = read_text(sql_path)
                    src_label = sql_path
                else:
                    add("CLB-021", SEV_WARN, mod["path"], lines.get("Fields[{}]".format(i), "Fields[{}]".format(i)),
                        "{}.{}: SqlText も {} も見つからない".format(mod["name"], name, os.path.basename(sql_path)))
                    continue
            clean = blank_sql_strings(strip_sql_comments(sql))
            used = set(PLACEHOLDER_RE.findall(clean))
            declared = {p.get("Name") for p in (setting.get("Parameters") or []) if p.get("Name")}
            db_columns = {(x.get("DbColumn") or "") for x in mod["fields"]}
            field_names = {(x.get("Name") or "") for x in mod["fields"]}
            loc = lines.get("Fields[{}]".format(i), "Fields[{}]".format(i))
            missing = used - declared
            if missing:
                add("CLB-021", SEV_WARN, src_label, loc,
                    "{}.{}: プレースホルダ {} が Parameters に無い。Submit 全体がロールバックする".format(
                        mod["name"], name, sorted(missing)))
            for p in sorted(declared):
                if p in db_columns:
                    continue
                hint = "（フィールド名と一致しているが @名 は DB 列名で解決される）" if p in field_names else ""
                add("CLB-021", SEV_WARN, mod["path"], loc,
                    "{}.{}: Parameters '{}' がどの DbColumn とも一致しない{}".format(mod["name"], name, p, hint))
            unused = declared - used
            if unused:
                add("CLB-021", SEV_WARN, mod["path"], loc,
                    "{}.{}: 未使用パラメータ {}".format(mod["name"], name, sorted(unused)))


def method_bounds(src):
    """CLB スクリプトをメソッド単位に粗く分割した境界（"\\n    }" を区切りに使う）。"""
    return [0] + [m.end() for m in re.finditer(r"\n    \}", src)] + [len(src)]


def enclosing_block(src, pos):
    bounds = method_bounds(src)
    for a, b in zip(bounds, bounds[1:]):
        if a <= pos < b:
            return a, b
    return 0, len(src)


def rule_022(idx, add):
    no_create = {m["name"] for m in idx.modules.values() if m["json"].get("CanCreate") is False}
    if not no_create:
        return
    pattern = re.compile(r"new\s+(" + "|".join(sorted(re.escape(x) for x in no_create)) + r")\s*\(")
    for mod in idx.modules.values():
        if not mod["cs"]:
            continue
        for m in pattern.finditer(mod["cs"]):
            # new と同じメソッド内に Submit( が無ければ読み取り用インスタンス化＝対象外（NARROWED）
            _s, end = enclosing_block(mod["cs"], m.start())
            if "Submit(" not in mod["cs"][m.end():end]:
                continue
            add("CLB-022", SEV_WARN, mod["cs_path"], line_of(mod["cs"], m.start()),
                "CanCreate:false の {} を new して同じメソッド内で Submit している。CanCreate は UI とスクリプトの両方を塞ぐ".format(
                    m.group(1)))


def rule_023(idx, add):
    for mod in idx.modules.values():
        table = (mod["json"].get("DbTable") or "").strip()
        if not table:
            continue
        gen = idx.generated_columns.get(table) or set()
        if not gen:
            continue
        lines = idx.module_lines(mod)
        for i, f in enumerate(mod["fields"]):
            col = (f.get("DbColumn") or "").strip()
            if col and col in gen:
                path = "Fields[{}].DbColumn".format(i)
                add("CLB-023", SEV_WARN, mod["path"], lines.get(path, path),
                    "{}.{}: {}.{} は SQLite の生成列。PRAGMA table_info に出ず CLB から見えない（実列＋トリガーにする）".format(
                        mod["name"], f.get("Name"), table, col))


def rule_024(idx, add):
    """warn 専用（仕様書明記）。多段ネスト（孫を持つ子）に限定して報告する。"""
    child_of = defaultdict(set)     # child module -> {parent module}
    has_child = set()               # module that hosts a ListField/DetailListField child
    for mod in idx.modules.values():
        for f in mod["fields"]:
            if short_type(f) not in ("ListFieldDesign", "DetailListFieldDesign"):
                continue
            cm = ((f.get("SearchCondition") or {}).get("ModuleName") or "").strip()
            if cm:
                child_of[cm].add(mod["name"])
                has_child.add(mod["name"])

    for child, parents in sorted(child_of.items()):
        if child not in has_child:
            continue   # 単一階層は正当な NOT NULL が多いので報告しない
        cmod = idx.modules.get(child)
        if not cmod:
            continue
        table = (cmod["json"].get("DbTable") or "").strip()
        if not table or table not in idx.tables:
            continue
        parent_tables = {(idx.modules[p]["json"].get("DbTable") or "").strip()
                         for p in parents if p in idx.modules}
        for col, decl in sorted(idx.tables[table].items()):
            if not re.search(r"NOT\s+NULL", decl, re.I):
                continue
            ref = re.search(r"REFERENCES\s+[\"`\[]?(\w+)", decl, re.I)
            if not ref or ref.group(1) not in parent_tables:
                continue
            add("CLB-024", SEV_WARN, os.path.join(idx.ddl_dir, "*.sql"), "{}.{}".format(table, col),
                "多段ネストの子 {} の FK 列が NOT NULL REFERENCES {}。一括 Submit の INSERT 時点で NULL になり落ちうる".format(
                    child, ref.group(1)))


BARE_DELETE_RE = re.compile(r"^\s*([\w\.\[\]\(\)]+)\.Delete\(\)\s*;\s*$", re.M)


def rule_025(idx, add):
    parents_with_children = set()
    for mod in idx.modules.values():
        for f in mod["fields"]:
            if short_type(f) in ("ListFieldDesign", "DetailListFieldDesign"):
                cm = ((f.get("SearchCondition") or {}).get("ModuleName") or "").strip()
                if cm:
                    parents_with_children.add(cm)
    for mod in idx.modules.values():
        if not mod["cs"]:
            continue
        for m in BARE_DELETE_RE.finditer(mod["cs"]):
            recv = m.group(1)
            if recv == "this" or recv.startswith("this."):
                continue   # 自モジュール削除は UI 経路で成功前提に書ける＝NARROWED
            add("CLB-025", SEV_WARN, mod["cs_path"], line_of(mod["cs"], m.start()),
                "{}.Delete() の戻り値を捨てている。検索インスタンスの削除は子の FK 制約で false を返し『成功トースト・DB 残存』になる".format(
                    recv))


def rule_026(idx, add):
    with_init = {m["name"] for m in idx.modules.values()
                 if any((v or {}).get("OnSearchInitialization")
                        for v in (m["json"].get("SearchLayouts") or {}).values())}
    if not with_init:
        return
    pattern = re.compile(r'GetModuleUrl\("(' + "|".join(sorted(re.escape(x) for x in with_init)) + r')"\)')
    for mod in idx.modules.values():
        if not mod["cs"]:
            continue
        for m in pattern.finditer(mod["cs"]):
            add("CLB-026", SEV_WARN, mod["cs_path"], line_of(mod["cs"], m.start()),
                "{} は OnSearchInitialization を持つ。GetModuleUrl 経由の遷移では ?initialize_search=true が付かず既定条件が効かない".format(
                    m.group(1)))


def rule_027(idx, add):
    for mod in idx.modules.values():
        lines = idx.module_lines(mod)
        for i, f in enumerate(mod["fields"]):
            if short_type(f) != "AnchorTagFieldDesign":
                continue
            if not (f.get("OnClick") or "").strip():
                continue
            path = "Fields[{}].OnClick".format(i)
            add("CLB-027", SEV_WARN, mod["path"], lines.get(path, path),
                "{}.{}: AnchorTag は href を持つため OnClick がナビゲーションとのレースに負けうる。LabelField + OnClick に統一する".format(
                    mod["name"], f.get("Name")))


def rule_028(idx, add):
    for mod in idx.modules.values():
        table = (mod["json"].get("DbTable") or "").strip()
        if not table or table not in idx.views:
            continue
        triggers = idx.instead_of.get(table) or set()
        if mod["json"].get("CanCreate") is True and "INSERT" not in triggers:
            add("CLB-028", SEV_WARN, mod["path"], "DbTable",
                "{}: DbTable '{}' はビューで CanCreate:true だが INSTEAD OF INSERT トリガーが無い".format(mod["name"], table))
        if mod["json"].get("CanUpdate") is True and "UPDATE" not in triggers:
            add("CLB-028", SEV_WARN, mod["path"], "DbTable",
                "{}: DbTable '{}' はビューで CanUpdate:true だが INSTEAD OF UPDATE トリガーが無い".format(mod["name"], table))
    pw = (idx.clprj.get("PasswordCheckUserTableInfo") or {}).get("TableName")
    if pw and pw in idx.views:
        triggers = idx.instead_of.get(pw) or set()
        if "UPDATE" not in triggers:
            add("CLB-028", SEV_WARN, os.path.join(idx.design_dir, "app.clprj"), "PasswordCheckUserTableInfo.TableName",
                "パスワード検査テーブル '{}' はビューだが INSTEAD OF UPDATE トリガーが無い".format(pw))


def rule_029(idx, add):
    for fr in idx.frames.values():
        legacy = (fr["json"].get("TopPageModule") or "").strip()
        current = (fr["top"].get("Module") or "").strip()
        if legacy and current and legacy != current:
            lines = idx.frame_lines(fr)
            add("CLB-029", SEV_WARN, fr["path"], lines.get("TopPageModule", "TopPageModule"),
                "レガシー TopPageModule='{}' が TopPageModuleDesign.Module='{}' と食い違う（rename-module が追従しない）".format(
                    legacy, current))


def rule_030(idx, add):
    for mod in idx.modules.values():
        lines = idx.module_lines(mod)
        for lname, layout in (mod["json"].get("SearchLayouts") or {}).items():
            rows = ((layout or {}).get("Layout") or {}).get("Rows") or []
            for ri, row in enumerate(rows):
                if not isinstance(row, dict):
                    continue
                if row.get("IsWrap") is True:
                    continue
                path = "SearchLayouts.{}.Layout.Rows[{}].IsWrap".format(lname, ri)
                add("CLB-030", SEV_WARN, mod["path"], lines.get(path, path),
                    "{}: 検索行 {} に IsWrap:true が無い（1344〜1514px 幅で検索欄が右に見切れる）".format(mod["name"], ri))


def rule_031(idx, add):
    for mod in idx.modules.values():
        lines = idx.module_lines(mod)
        by_name = mod["field_by_name"]
        for lname, layout in (mod["json"].get("SearchLayouts") or {}).items():
            rows = ((layout or {}).get("Layout") or {}).get("Rows") or []
            for ri, row in enumerate(rows):
                if not isinstance(row, dict):
                    continue
                names = [v for _p, k, v in walk_kv(row)
                         if k == "FieldName" and isinstance(v, str) and v]
                inputs = [n for n in names
                          if short_type(by_name.get(n, {})) != "LabelFieldDesign"]
                path = "SearchLayouts.{}.Layout.Rows[{}]".format(lname, ri)
                if len(inputs) >= 4:
                    add("CLB-031", SEV_WARN, mod["path"], lines.get(path, path),
                        "{}: 検索行 {} に入力欄が {} 組（4 組以上でラベルと入力が泣き別れる）: {}".format(
                            mod["name"], ri, len(inputs), ", ".join(inputs)))
                or_fields = [n for n in inputs
                             if by_name.get(n, {}).get("AllowOrSearch") is True]
                if or_fields and len(inputs) > len(or_fields):
                    add("CLB-031", SEV_WARN, mod["path"], lines.get(path, path),
                        "{}: 検索行 {} に OR 検索（縦長チェックボックス群）{} が他の入力欄と同居している（単独行が推奨）".format(
                            mod["name"], ri, ", ".join(or_fields)))


def rule_032(idx, add):
    for mod in idx.modules.values():
        placed = detail_layout_fields(mod)
        pw = [f.get("Name") for f in mod["fields"]
              if short_type(f) == "PasswordFieldDesign" and f.get("Name") in placed]
        if len(pw) < 2:
            continue
        selector = '[data-module="{}"]'.format(mod["name"])
        if selector in idx.app_css and "password-confirm" in idx.app_css:
            continue
        add("CLB-032", SEV_WARN, mod["path"], "DetailLayouts",
            "{}: PasswordField を {} 個配置（各 2 欄描画で計 {} 欄になる）が app.css に確認欄を隠すルールが無い".format(
                mod["name"], len(pw), len(pw) * 2))


def rule_033(idx, add):
    for mod in idx.modules.values():
        if not mod["cs"]:
            continue
        hidden = {m.group(1) for m in re.finditer(r"\b(\w+)\.IsVisible\s*=\s*false", mod["cs"])}
        if not hidden:
            continue
        fixed_width = set()
        for _p, d in walk_dicts(mod["json"].get("DetailLayouts") or {}):
            lay = d.get("Layout")
            if isinstance(lay, dict) and lay.get("FieldName") in hidden and d.get("Width"):
                fixed_width.add(lay["FieldName"])
        if not fixed_width:
            continue
        selector = '[data-module="{}"]'.format(mod["name"])
        if selector in idx.app_css and ":not(:has(.field-layout))" in idx.app_css:
            continue
        add("CLB-033", SEV_WARN, mod["path"], "DetailLayouts",
            "{}: 固定幅カラムのフィールド {} を IsVisible=false にしているが、空 div を畳む CSS が無い".format(
                mod["name"], ", ".join(sorted(fixed_width))))


CURRENT_USER_DISPLAY = re.compile(r"CurrentUser\.(\w+)\.DisplayText")


def rule_034(idx, add):
    cu_name = (idx.clprj.get("CurrentUserModuleDesignName") or "AppUser")
    cu = idx.modules.get(cu_name)
    for mod in idx.modules.values():
        if not mod["cs"]:
            continue
        for m in CURRENT_USER_DISPLAY.finditer(mod["cs"]):
            fname = m.group(1)
            ftype = short_type((cu or {}).get("field_by_name", {}).get(fname, {})) if cu else ""
            if cu and ftype and ftype != "SelectFieldDesign":
                continue
            add("CLB-034", SEV_WARN, mod["cs_path"], line_of(mod["cs"], m.start()),
                "CurrentUser.{}.DisplayText{} は候補未ロードだと空文字になる。ModuleSearcher で取り直す".format(
                    fname, "（{}）".format(ftype) if ftype else ""))


def rule_035(idx, add):
    for mod in idx.modules.values():
        if not mod["cs"]:
            continue
        src = mod["cs"]
        bounds = method_bounds(src)   # 仕様書のとおり "\n    }" を区切りにした粗い分割
        for a, b in zip(bounds, bounds[1:]):
            block = src[a:b]
            i_load = block.find("StartLoading")
            i_box = block.find("MessageBox.Show")
            if i_load >= 0 and i_box >= 0 and i_load < i_box:
                add("CLB-035", SEV_WARN, mod["cs_path"], line_of(src, a + i_load),
                    "StartLoading() が MessageBox.Show() より前にある。オーバーレイがダイアログを覆い操作が詰む（粗いブロック分割のため要目視）")


def rule_036(idx, add):
    """warn 専用（仕様書明記）。ファイル×列単位のサマリで出す。"""
    date_cols = set()
    for table, cols in idx.tables.items():
        for col, decl in cols.items():
            if base_type(decl) in ("DATE", "DATETIME"):
                date_cols.add(col)
    if not date_cols:
        return
    pattern = re.compile(
        r"(?<![\w.])((?:\w+\.)?)(" + "|".join(sorted(re.escape(c) for c in date_cols)) + r")\b\s*(=|>=|<=|>|<|BETWEEN\b)",
        re.I)
    group_by = re.compile(
        r"GROUP\s+BY\s+([^\n;]*)", re.I)
    for path, raw in idx.query_sql_files:
        text = blank_sql_strings(strip_sql_comments(raw))
        hits = Counter()
        first_line = {}
        for m in pattern.finditer(text):
            col = m.group(2)
            start = m.start(1)
            prefix = text[max(0, start - 12):start].lower()
            if re.search(r"(date|datetime|strftime)\(\s*$", prefix):
                continue
            # strftime('%Y-%m', col) 形式（前に書式引数がある）も正規化済みとみなす
            if re.search(r"strftime\([^()]*$", text[max(0, start - 60):start], re.I):
                continue
            hits[col] += 1
            first_line.setdefault(col, line_of(text, m.start()))
        for m in group_by.finditer(text):
            for token in m.group(1).split(","):
                t = token.strip().split(".")[-1].strip()
                if t in date_cols:
                    hits[t] += 1
                    first_line.setdefault(t, line_of(text, m.start()))
        for col, n in sorted(hits.items()):
            add("CLB-036", SEV_WARN, path, first_line[col],
                "DATE/DATETIME 列 '{}' が date() 正規化なしで比較/GROUP BY されている（{} 箇所）。境界日だけ外れうる".format(col, n))


def rule_037(idx, add):
    """CLB-037: DataWriteCondition が参照する自モジュールの列が、その画面で読み込まれていない。

    書き込み条件は**画面側で評価される**ため、条件が見る列がレイアウトにも DataOnlyFields にも
    無いと値が null になり、条件が常に偽＝**全項目が読み取り専用に描画される**。
    エラーもトーストも出ないので「なぜか直せない」という形でしか気づけない（BUG-0323 で実害）。
    読み取り条件（DataReadCondition）は SQL の WHERE で効くため、未ロードでも問題ない。
    """
    def fields_in(node):
        found = set()
        stack = [node]
        while stack:
            n = stack.pop()
            if isinstance(n, dict):
                fn = n.get("FieldName")
                if fn:
                    found.add(fn)
                stack.extend(n.values())
            elif isinstance(n, list):
                stack.extend(n)
        return found

    for mod in idx.modules.values():
        data = mod["json"]
        cond_holder = data.get("DataWriteCondition") or {}
        if cond_holder.get("ModuleName"):
            continue          # 他モジュール基準の条件は自モジュールの列を見ない
        cond = cond_holder.get("Condition") or {}
        targets = set()
        stack = [cond]
        while stack:
            n = stack.pop()
            if isinstance(n, dict):
                v = n.get("SearchTargetVariable")
                if v:
                    targets.add(v.split(".")[0])
                stack.extend(n.values())
            elif isinstance(n, list):
                stack.extend(n)
        if not targets:
            continue
        for kind in ("DetailLayouts", "ListLayouts"):
            for lay_name, lay in (data.get(kind) or {}).items():
                loaded = fields_in(lay.get("Layout") or lay.get("Elements"))
                loaded |= set(lay.get("DataOnlyFields") or [])
                missing = sorted(t for t in targets if t not in loaded)
                if not missing:
                    continue
                add("CLB-037", SEV_ERROR, mod["path"], 1,
                    "{}: {} '{}' で DataWriteCondition が参照する {} が未ロード"
                    "（レイアウトにも DataOnlyFields にも無い）。"
                    "条件が常に偽になり全項目が読み取り専用に描画される".format(
                        mod["name"], kind, lay_name or "(既定)", "/".join(missing)))


def rule_038(idx, add):
    """CLB-038: 資金繰り SQL の複製（PortalAlertData ⇄ CashFlowForecastData）が食い違っている。

    プロジェクト固有の規則。この 2 本は共通 CTE（cash_now / inv_in / rec_in / ap_now / exp_now /
    vend_out / sal_out / flows / cash_final）を逐語コピーで共有しており、片方だけ直すと
    ポータルの「今後4ヶ月中 N ヶ月」と画面の警告行数が**黙ってずれる**（BUG-0257・ADR-0060）。
    tasks/04 でビュー化して 1 か所に畳むまでの保険として、CTE 本文の一致を機械で見る。
    **最終 SELECT（警告条件など）は CTE ではないので比較できない**——そこは
    「片方だけが CASH_ALERT_BALANCE を見ている」形だけを別に検出する。
    """
    import re as _re
    a = None
    b = None
    for path, raw in idx.query_sql_files:
        p = path.replace("\\", "/")
        if p.endswith("Shell/PortalAlertData.Query.sql"):
            a = (path, raw)
        elif p.endswith("Management/CashFlowForecastData.Query.sql"):
            b = (path, raw)
    if a is None or b is None:
        return

    names = ["cash_now", "inv_in", "rec_in", "ap_now", "exp_now", "vend_out", "sal_out", "flows"]

    def cte_body(text, name):
        # "name AS (" から対応する括弧までを取り出す
        m = _re.search(r"(?<![\w.])" + _re.escape(name) + r"\s+AS\s*\(", text, _re.I)
        if not m:
            return None
        i = m.end() - 1
        depth = 0
        for j in range(i, len(text)):
            if text[j] == "(":
                depth += 1
            elif text[j] == ")":
                depth -= 1
                if depth == 0:
                    return _re.sub(r"\s+", " ", strip_sql_comments(text[i + 1:j])).strip()
        return None

    # 危険水域の閾値（BUG-0249）は最終 SELECT 側にあるので CTE の比較では拾えない。
    # 「片方だけが閾値を見ている」状態だけは検出できるので、それを別に見る
    ta = "CASH_ALERT_BALANCE" in a[1]
    tb = "CASH_ALERT_BALANCE" in b[1]
    if ta != tb:
        only = "PortalAlertData" if ta else "CashFlowForecastData"
        add("CLB-038", SEV_ERROR, a[0], 1,
            "危険水域の閾値 CASH_ALERT_BALANCE を {} だけが見ている。"
            "ポータルと予測画面で警告の条件がずれる（BUG-0249/0257）".format(only))

    for name in names:
        ba = cte_body(a[1], name)
        bb = cte_body(b[1], name)
        if ba is None or bb is None:
            continue
        if ba != bb:
            add("CLB-038", SEV_ERROR, a[0], line_of(a[1], a[1].lower().find(name.lower())),
                "資金繰り SQL の複製が食い違っている: CTE '{}' が "
                "CashFlowForecastData.Query.sql と一致しない。"
                "片方だけ直すとポータルと画面の警告件数が黙ってずれる（BUG-0257）".format(name))


def rule_039(idx, add):
    """CLB-039: `AddRows(...)` の引数が `List<モジュール型>` の `.Count` から作られている。

    **実測（2026-08-18・ISSUE-0006 の真因）**: 同じ画面・同じ行で 3 通り試した結果——

    | 書き方 | 結果 |
    |---|---|
    | `AddRows(rows.Count * 2)`（rows は `List<WipCandidate>`） | **落ちる** |
    | `var n = rows.Count * 2; AddRows(n);` | **落ちる** |
    | `AddRows(dcList.Count)`（dcList は `List<string>`） | 動く |

    例外は `Value cannot be null. (Parameter 'source')`。
    `AddRows` には **`AddRows(int count)` と `AddRows(List<Module> src)` の 2 つの多重定義**があり
    （フィールドカタログ）、引数の静的な型が int と決まらないと**リスト側の多重定義に流れて
    null が渡る**——パラメータ名が 'source' なのはそのためだと考えられる。

    8/17 に 3 回失敗して撤回した BUG-0127 の真因もこれだった可能性が高い。
    当時の見立て「AddRows の引数は 1 文で確定させる」は**外れ**である
    （変数に受けても落ちる。逆に `List<string>` の `.Count` はループで積み上げたものでも動く）。

    **`.Count` そのものは壊れていない。** モジュール型のリストでも比較や表示には普通に使える
    （このリポジトリの経費・承認まわりで実際に動いている）。問題は **AddRows の多重定義解決**だけなので、
    このルールも `AddRows(...)` の引数に限って報告する。

    回避: 行の内容をプリミティブの並行リスト（`List<string>` 等）に組み、その `.Count` を渡す。
    """
    import re as _re
    var_decl = _re.compile(r"\bvar\s+(\w+)\s*=\s*new\s+List<\s*(\w+)\s*>\s*\(\s*\)")
    fn_decl = _re.compile(r"^\s*List<\s*(\w+)\s*>\s+(\w+)\s*\(", _re.M)
    call_assign = _re.compile(r"\bvar\s+(\w+)\s*=\s*(\w+)\s*\(")
    addrows = _re.compile(r"\.AddRows\s*\(([^;]*?)\)\s*;")
    assign = _re.compile(r"\bvar\s+(\w+)\s*=\s*([^;]*?);")
    for mod in idx.modules.values():
        cs = mod["cs"]
        if not cs:
            continue
        module_names = set(idx.modules.keys())
        risky = {}
        for m in var_decl.finditer(cs):
            if m.group(2) in module_names:
                risky[m.group(1)] = m.group(2)
        fn_ret = {m.group(2): m.group(1) for m in fn_decl.finditer(cs) if m.group(1) in module_names}
        for m in call_assign.finditer(cs):
            if m.group(2) in fn_ret:
                risky[m.group(1)] = fn_ret[m.group(2)]
        if not risky:
            continue
        # 「モジュール型リストの .Count から作った int 変数」も追う（1 段だけ）
        tainted = {}
        for m in assign.finditer(cs):
            rhs = m.group(2)
            for var, mtype in risky.items():
                if _re.search(r"\b" + _re.escape(var) + r"\.Count\b", rhs):
                    tainted[m.group(1)] = mtype
        for m in addrows.finditer(cs):
            arg = m.group(1)
            hit = None
            for var, mtype in list(risky.items()) + list(tainted.items()):
                if _re.search(r"\b" + _re.escape(var) + r"(\.Count)?\b", arg):
                    hit = (var, mtype)
                    break
            if hit:
                add("CLB-039", SEV_ERROR, mod["cs_path"], line_of(cs, m.start()),
                    "AddRows の引数が {}（List<{}>）由来。モジュール型リストの .Count を渡すと "
                    "多重定義が AddRows(List<Module>) 側に流れ、実行時に "
                    "'Value cannot be null. (Parameter \'source\')' で落ちる（ISSUE-0006・実測）。"
                    "プリミティブの並行リストの .Count を渡すこと".format(hit[0], hit[1]))


def rule_040(idx, add):
    """CLB-040: 整数どうしの割り算を `var` で受けている。

    **実測（2026-08-19・BUG-0410）**: `SesBilling.mod.cs` で

    ```csharp
    var minutes = 0;
    foreach (...) { minutes = minutes + t.Minutes.Value; }
    var hours = minutes / 60;   // ← C# のつもりなら整数除算
    ```

    と書いたところ、`hours` が **160.98333…** になった。CLB のスクリプトは
    **動的な値を代入した変数が小数型に化ける**ため、`/` が整数除算にならない。

    実害（実機で確認）:

    - 実績時間の表示が `160.98333…h59m` になる
    - `hours > upper` の判定が小数で行われ、**160h59m が「上限超過」と判定される**
    - 下限側は `int shortage = (int)lower - hours;` の代入で切り捨てられ、
      **控除時間が 1 時間ぶん少なくなる**（130h30m の控除が 10h → 9h＝請求 5,000 円ぶん過大）

    **金額に効くのに例外も警告も出ない**。切り捨てを期待する割り算は
    **`int` で受ける**（`int hours = minutes / 60;`）。代入時に切り捨てられるので、
    `/` が小数を返しても結果は正しくなる。

    誤検知を避けるため、**右辺が割り算だけの単純な代入**に限って報告する
    （`Math.Max(...)` などの中に入っている割り算や、明示的に `(int)` へキャストしている式は対象外）。
    """
    import re as _re
    # var x = <式> / <式>;
    decl = _re.compile(r"\bvar\s+(\w+)\s*=\s*([^;=]*?/[^;]*?);")
    # var で宣言しておいて、後から x = <式> / <式>; と代入する形。
    # 宣言行には割り算が無いので decl では拾えないが、変数の型は var で決まっており小数に化ける。
    # **初期値の形は問わない**——リテラル（`var tax = 0;`＝BUG-0421）だけでなく
    # 別の変数（`var amount = full;`＝BUG-0437）でも同じことが起きる。
    # CLB のスクリプトでは値がすべて動的なので、「var で受けた変数に割り算を代入する」こと自体が罠
    var_any_decl = _re.compile(r"\bvar\s+(\w+)\s*=\s*[^;]*;")
    # 代入の検出は「文の先頭」に限る。行頭から見ないと `int tax = a / b;` の後半にマッチして、
    # 正しく int で受けている側を誤検知する。ただし**波括弧なしの単文 if / else の中も文の先頭**——
    # `if (pct > 0) tax = gross * pct / (100 + pct);` を取りこぼすと、
    # 内税分解でいちばん多い書き方が丸ごと盲点になる（BUG-0437。実際に 2 件生き残っていた）
    assign = _re.compile(r"(?:^|[;{})]|\belse)\s*(\w+)\s*=\s*([^;=<>!+\-*/][^;=]*?/[^;]*?);", _re.M)

    def _plain_arithmetic(rhs):
        """関数呼び出し・キャスト・文字列を含まない素の四則演算か。
        `(100 + pct)` のような**grouping だけの括弧は対象に含める**——
        BUG-0421 の `diff * pct / (100 + pct)` は「括弧がある」だけの理由で素通りしていた
        （内税分解の定番の形なので、取りこぼすと痛い）"""
        # 素の四則演算だけを対象にする**ホワイトリスト**。
        # 文字列補間（`$" ... / ..."`）は正規表現の都合で引用符の手前まで切り出されることがあり、
        # `"` の有無だけでは弾けない（CashEntry の `$" ［{string.Join(" / ", …)}］"` で誤検知した）
        if not _re.fullmatch(r"[\w\s.()*/+\-]+", rhs):
            return False
        if _re.search(r"[\w\]]\s*\(", rhs):   # 識別子の直後の ( ＝ メソッド呼び出し
            return False
        if _re.search(r"\(\s*(?:int|long|short|byte|decimal|double|float|[A-Z]\w*)\s*\)", rhs):
            return False   # 明示キャスト（(int)x など）は切り捨てが保証されるので対象外
        return True

    _tail = ("CLB のスクリプトは**整数どうしでも小数になる**ことがあり、"
             "切り捨て前提の計算（時間・年数・按分）が静かに狂う（BUG-0410／BUG-0421・実測）。"
             "切り捨てたいなら `int {} = ...;` と型で受ける")

    for mod in idx.modules.values():
        cs = mod["cs"]
        if not cs:
            continue
        for m in decl.finditer(cs):
            rhs = m.group(2)
            if not _plain_arithmetic(rhs):
                continue
            add("CLB-040", SEV_ERROR, mod["cs_path"], line_of(cs, m.start()),
                ("`var {} = {};` — " + _tail).format(m.group(1), rhs.strip(), m.group(1)))
        varnames = set(var_any_decl.findall(cs))
        for m in assign.finditer(cs):
            name = m.group(1)
            if name not in varnames:
                continue
            rhs = m.group(2)
            if not _plain_arithmetic(rhs):
                continue
            add("CLB-040", SEV_ERROR, mod["cs_path"], line_of(cs, m.start()),
                ("`{} = {};` — `var` で宣言した変数なので小数を受け取れてしまう。" + _tail)
                .format(name, rhs.strip(), name, name))


def rule_041(idx, add):
    """CLB-041: `.mod.cs` に書いたハンドラが `.mod.json` のどこからも参照されていない。

    **実測（2026-08-19・BUG-0045）**: `PartnerBank.mod.cs` に口座 5 項目の整合検証つきの
    `Register_OnClick()` と `Delete_OnClick()` が実装されていたのに、画面のボタンは
    CLB 標準の `SubmitButtonFieldDesign` だった。**この型には `OnClick` が無く、
    押すとスクリプトを一切通らずに保存される。** 結果、銀行コードだけ入れた
    部分入力の口座がそのまま保存でき、不備に気づくのは振込データ作成のときだった。

    「書いたのに呼ばれていない」は静かな失敗の典型で、`designcheck` も緑のまま通る。
    ハンドラ名が `.mod.json`（同じモジュール）のどこにも文字列として現れなければ報告する。

    誤検知を避けるため:

    - CLB が名前で呼ぶ規約ハンドラ（`OnAfterInitialization` など）は対象外
    - 他モジュールから `xx.Foo()` の形で呼ばれるヘルパは、**リポジトリ全体の `.mod.cs`**
      に呼び出しがあれば対象外（`JournalEntry.ValidateBalanced` のような共有ヘルパ）
    - `_OnClick` / `_OnDataChanged` / `_OnSelectedIndexChanged` / `_Completed` /
      `_OnInitialization` で終わる名前だけを見る（純粋なヘルパは対象外）
    """
    import re as _re

    # CLB が名前規約で直接呼ぶもの（json に書かれない）
    CONVENTION = {
        "OnAfterInitialization", "OnBeforeSubmit", "OnAfterSubmit",
        "OnLoaded", "OnInitialized",
    }
    SUFFIXES = ("_OnClick", "_OnDataChanged", "_OnSelectedIndexChanged",
                "_Completed", "_OnInitialization", "_OnValidateInput",
                "_OnSearchDataChanged", "_OnDoubleClickRow")

    decl = _re.compile(r"^\s*(?:void|bool|int|string|decimal)\s+(\w+)\s*\(", _re.M)

    # 全モジュールの .cs を連結しておく（他モジュールからの呼び出し検出用）
    all_cs = chr(10).join((m["cs"] or "") for m in idx.modules.values())

    for mod in idx.modules.values():
        cs = mod["cs"]
        if not cs:
            continue
        import json as _json
        raw_json = _json.dumps(mod.get("json") or {}, ensure_ascii=False)
        for m in decl.finditer(cs):
            name = m.group(1)
            if name in CONVENTION:
                continue
            if not name.endswith(SUFFIXES):
                continue
            if '"' + name + '"' in raw_json:
                continue
            # 他モジュール・自モジュールのスクリプトから明示的に呼ばれていれば良しとする
            if all_cs.count(name) > 1:
                continue
            add("CLB-041", SEV_ERROR, mod["cs_path"], line_of(cs, m.start()),
                "`{}()` は `.mod.json` のどこからも参照されていない（書いたのに呼ばれない）。"
                "とくに CLB 標準の `SubmitButtonFieldDesign` は `OnClick` を持たず"
                "**スクリプトを通らずに保存する**ので、検証を書いても効かない（BUG-0045・実測）。"
                "`ButtonFieldDesign` に変えて `OnClick` を張るか、不要なら削除する".format(name))


RULE_FUNCS = [
    ("CLB-001", rule_001), ("CLB-002", rule_002), ("CLB-003", rule_003), ("CLB-004", rule_004),
    ("CLB-005", rule_005), ("CLB-006", rule_006), ("CLB-007", rule_007), ("CLB-008", rule_008),
    ("CLB-009", rule_009), ("CLB-010", rule_010), ("CLB-011", rule_011), ("CLB-012", rule_012),
    ("CLB-013", rule_013), ("CLB-014", rule_014), ("CLB-015", rule_015), ("CLB-016", rule_016),
    ("CLB-017", rule_017), ("CLB-018", rule_018), ("CLB-019", rule_019), ("CLB-020", rule_020),
    ("CLB-021", rule_021), ("CLB-022", rule_022), ("CLB-023", rule_023), ("CLB-024", rule_024),
    ("CLB-025", rule_025), ("CLB-026", rule_026), ("CLB-027", rule_027), ("CLB-028", rule_028),
    ("CLB-029", rule_029), ("CLB-030", rule_030), ("CLB-031", rule_031), ("CLB-032", rule_032),
    ("CLB-033", rule_033), ("CLB-034", rule_034), ("CLB-035", rule_035), ("CLB-036", rule_036),
    ("CLB-037", rule_037), ("CLB-038", rule_038), ("CLB-039", rule_039),
    ("CLB-040", rule_040), ("CLB-041", rule_041),
]

# 仕様書が warn 専用と明記しているルール（群に関わらず error にしない）
WARN_ONLY = {"CLB-018", "CLB-024", "CLB-036"}


# --------------------------------------------------------------------------
# 実行
# --------------------------------------------------------------------------

def relpath(path, base):
    try:
        return os.path.relpath(path, base).replace("\\", "/")
    except ValueError:
        return path.replace("\\", "/")


def main(argv=None):
    # Windows コンソール既定（cp932）では日本語メッセージが化けるので UTF-8 に固定する
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except Exception:  # noqa: BLE001  (リダイレクト先によっては reconfigure できない)
            pass

    here = os.path.dirname(os.path.abspath(__file__))
    parser = argparse.ArgumentParser(
        description="CLB「静かな失敗」デザインリンタ（docs/qa/10_チェックリスト/01_CLB静かな失敗.md）")
    parser.add_argument("--design-dir", default=os.path.join(here, os.pardir, "Design"),
                        help="デザインディレクトリ（既定: スクリプト位置からの ../Design）")
    parser.add_argument("--ddl-dir", default=os.path.join(here, os.pardir, "ddl"),
                        help="DDL ディレクトリ（既定: スクリプト位置からの ../ddl）")
    parser.add_argument("--format", choices=["text", "json"], default="text")
    parser.add_argument("--out", default=None, help="出力先ファイル（既定: 標準出力）")
    parser.add_argument("--severity", choices=["error", "warn", "all"], default="all")
    parser.add_argument("--rule", default=None, help="対象ルールのカンマ区切り（例: CLB-001,CLB-003）")
    args = parser.parse_args(argv)

    design_dir = os.path.abspath(args.design_dir)
    ddl_dir = os.path.abspath(args.ddl_dir)
    if not os.path.isdir(design_dir):
        print("デザインディレクトリが見つからない: {}".format(design_dir), file=sys.stderr)
        return 2

    only = None
    if args.rule:
        only = {r.strip().upper() for r in args.rule.split(",") if r.strip()}
        unknown = only - set(RULES)
        if unknown:
            print("未知のルールID: {}".format(", ".join(sorted(unknown))), file=sys.stderr)
            return 2

    idx = DesignIndex(design_dir, ddl_dir)

    findings = []

    def make_add(rule_id):
        def add(rule, severity, path, loc, message):
            if rule in WARN_ONLY:
                severity = SEV_WARN
            findings.append(Finding(rule, severity, path, loc, message))
        return add

    for rule_id, func in RULE_FUNCS:
        if rule_id in NOT_IMPLEMENTED:
            continue
        if only and rule_id not in only:
            continue
        func(idx, make_add(rule_id))

    if args.severity != "all":
        findings = [f for f in findings if f.severity == args.severity]
    findings.sort(key=lambda f: f.key())

    base = os.getcwd()
    n_err = sum(1 for f in findings if f.severity == SEV_ERROR)
    n_warn = sum(1 for f in findings if f.severity == SEV_WARN)
    by_rule = Counter(f.rule for f in findings)
    by_group = Counter(RULES.get(f.rule, ("?",))[0] for f in findings)

    if args.format == "json":
        payload = {
            "designDir": relpath(design_dir, base),
            "ddlDir": relpath(ddl_dir, base),
            "scanned": {
                "modules": len(idx.modules),
                "frames": len(idx.frames),
                "tables": len(idx.tables),
                "views": len(idx.views),
                "sqlFiles": len(idx.query_sql_files),
            },
            "findings": [dict(f.as_dict(), file=relpath(f.file, base)) for f in findings],
            "summary": {
                "error": n_err,
                "warn": n_warn,
                "byRule": dict(sorted(by_rule.items())),
                "byGroup": dict(sorted(by_group.items())),
                "notImplemented": NOT_IMPLEMENTED,
                "narrowed": NARROWED,
                "partial": PARTIAL,
                "loadErrors": idx.load_errors,
            },
        }
        out_text = json.dumps(payload, ensure_ascii=False, indent=2)
    else:
        lines = []
        for f in findings:
            lines.append("{} {} {}:{} {}".format(
                f.severity, f.rule, relpath(f.file, base), f.loc, f.message))
        lines.append("")
        lines.append("--- 集計 ---")
        lines.append("走査: モジュール {} / フレーム {} / テーブル {} / ビュー {} / SQL {}".format(
            len(idx.modules), len(idx.frames), len(idx.tables), len(idx.views), len(idx.query_sql_files)))
        lines.append("重大度別: error={} warn={} 合計={}".format(n_err, n_warn, len(findings)))
        lines.append("群別: " + " ".join("{}={}".format(g, by_group.get(g, 0)) for g in ("高", "中", "低")))
        lines.append("ルール別:")
        for rule_id, _f in RULE_FUNCS:
            n = by_rule.get(rule_id, 0)
            if not n:
                continue
            grp, title = RULES[rule_id]
            lines.append("  {} [{}] {:>4} 件  {}".format(rule_id, grp, n, title))
        implemented = [r for r, _ in RULE_FUNCS if r not in NOT_IMPLEMENTED]
        lines.append("実装ルール: {}/{}".format(len(implemented), len(RULES)))
        lines.append("未実装: {}件".format(len(NOT_IMPLEMENTED)))
        for rule_id, reason in sorted(NOT_IMPLEMENTED.items()):
            lines.append("  {} {} — {}".format(rule_id, RULES.get(rule_id, ("", ""))[1], reason))
        if NARROWED:
            lines.append("意図的に検出条件を絞ったルール: {}件".format(len(NARROWED)))
            for rule_id, reason in sorted(NARROWED.items()):
                lines.append("  {} {}".format(rule_id, reason))
        if PARTIAL:
            lines.append("部分実装の注記: {}件".format(len(PARTIAL)))
            for rule_id, note in sorted(PARTIAL.items()):
                lines.append("  {} {}".format(rule_id, note))
        if idx.load_errors:
            lines.append("読み込みエラー: {}件".format(len(idx.load_errors)))
            for e in idx.load_errors:
                lines.append("  " + e)
        out_text = "\n".join(lines)

    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(out_text + "\n")
        print("出力: {} （error={} warn={} 未実装={}件）".format(args.out, n_err, n_warn, len(NOT_IMPLEMENTED)))
    else:
        try:
            print(out_text)
        except UnicodeEncodeError:
            sys.stdout.buffer.write(out_text.encode("utf-8", "replace") + b"\n")

    return 1 if n_err else 0


if __name__ == "__main__":
    sys.exit(main())
