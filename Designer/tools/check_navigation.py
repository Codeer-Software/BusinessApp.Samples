# -*- coding: utf-8 -*-
# FB-023 静的全数検査: 「遷移先フレームに Module×セグメントが未登録だと無言で真っ白」を機械検査する。
# 検査対象:
#  A. 各フレームの Links / TopPageModuleDesign / OtherPageModuleDesigns が指すモジュールが実在するか
#  B. PageFrame 指定つきリンク（切替リンク）の遷移先フレームに、その Module×セグメントが登録されているか
#  C. スクリプト内の NavigateTo の URL（リテラル "/Frame/Seg" と補間 $"/{frame}/Seg"）について、
#     フレーム名が実在し、セグメントが遷移先フレームに登録されているか。
#     補間形のフレーム候補は同一ファイル内の frame = "X" 代入と return "X"; を収集して総当たりする
#     （ADR-0045 でフレーム素の URL "/Frame" は既定業務画面=TopPageModuleDesign に着地する。旧 /Top 規約は廃止）
#  D. スクリプト内の GetModuleUrl/GetModuleDataUrl("X") の X が、そのスクリプトのモジュールを登録している
#     全フレームに登録されているか（現在フレーム解決のため、呼び出し元が居るフレーム全てで解決可能である必要）
import json, os, glob, re, sys, argparse

# パスは lint_design.py と同じ流儀: --design-dir の既定はスクリプト位置からの相対解決（../Design）
_HERE = os.path.dirname(os.path.abspath(__file__))
_parser = argparse.ArgumentParser(description="FB-023/FB-042 遷移先未登録の静的全数検査")
_parser.add_argument("--design-dir", default=os.path.join(_HERE, os.pardir, "Design"),
                     help="デザインディレクトリ（既定: スクリプト位置からの ../Design）")
_args = _parser.parse_args()

DESIGN = os.path.abspath(_args.design_dir)
if not os.path.isdir(DESIGN):
    print(f"デザインディレクトリが見つからない: {DESIGN}", file=sys.stderr)
    sys.exit(2)

frames = {}
modules = set()

for path in glob.glob(os.path.join(DESIGN, "Modules", "**", "*.mod.json"), recursive=True):
    with open(path, encoding="utf-8") as f:
        modules.add(json.load(f)["Name"])

for path in glob.glob(os.path.join(DESIGN, "PageFrames", "*.frm.json")):
    with open(path, encoding="utf-8") as f:
        fr = json.load(f)
    regs = {}  # module -> set(segments)
    def add(module, seg):
        if module:
            regs.setdefault(module, set()).add(seg or "")
    top = fr.get("TopPageModuleDesign") or {}
    add(top.get("Module"), top.get("ModuleUrlSegment"))
    links = []
    for l in (fr.get("Left") or {}).get("Links", []):
        add(l.get("Module"), l.get("ModuleUrlSegment"))
        links.append(l)
    for o in fr.get("OtherPageModuleDesigns", []) or []:
        add(o.get("Module"), o.get("ModuleUrlSegment"))
    frames[fr["Name"]] = {"regs": regs, "links": links, "top": top}

errors, warns = [], []

# A/B
for fname, fr in frames.items():
    for l in fr["links"] + [fr["top"]]:
        mod = l.get("Module")
        if not mod:
            continue
        if mod not in modules:
            errors.append(f"[A] {fname}: モジュール '{mod}' が存在しない")
        target = l.get("PageFrame") or ""
        if target:
            if target not in frames:
                errors.append(f"[B] {fname}: 切替リンク '{l.get('Title')}' の遷移先フレーム '{target}' が存在しない")
            else:
                seg = l.get("ModuleUrlSegment") or ""
                tregs = frames[target]["regs"]
                if mod not in tregs or seg not in tregs[mod]:
                    errors.append(f"[B] {fname}: 切替リンク '{l.get('Title')}' → {target}/{mod} (seg='{seg}') が遷移先に未登録（FB-023 真っ白）")

# モジュール → 登録フレーム一覧
mod_frames = {}
for fname, fr in frames.items():
    for mod in fr["regs"]:
        mod_frames.setdefault(mod, set()).add(fname)

# フレームごとの「解決可能なセグメント名」集合（ModuleUrlSegment が空なら既定=モジュール名）
frame_segments = {}
for fname, fr in frames.items():
    segs = set()
    for mod, ss in fr["regs"].items():
        for s in ss:
            segs.add(s if s else mod)
    frame_segments[fname] = segs

# C/D: スクリプト検査
nav_lit = re.compile(r'NavigateTo\("/(\w+)(?:/(\w+))?')
nav_interp = re.compile(r'NavigateTo\(\$"/\{([^}]+)\}(?:/(\w+))?')
frame_var = re.compile(r'frame = "(\w+)"')
frame_ret = re.compile(r'return "(\w+)";')
get_url = re.compile(r'GetModule(?:Data)?Url\("(\w+)"')
for path in glob.glob(os.path.join(DESIGN, "Modules", "**", "*.mod.cs"), recursive=True):
    mod_name = os.path.basename(path).replace(".mod.cs", "")
    with open(path, encoding="utf-8") as f:
        src = f.read()
    # C-1: frame 変数に入るフレーム名の実在（return "X"; は X がフレーム名のときだけ候補扱い）
    candidates = set()
    for m in frame_var.finditer(src):
        target = m.group(1)
        if target not in frames:
            errors.append(f"[C] {mod_name}: フレーム名 '{target}' が存在しない")
        else:
            candidates.add(target)
    for m in frame_ret.finditer(src):
        if m.group(1) in frames:
            candidates.add(m.group(1))
    # C-2: リテラル URL の検査（"/Frame" はフレーム素=TopPageModuleDesign 着地なのでセグメント検査なし）
    for m in nav_lit.finditer(src):
        target, seg = m.group(1), m.group(2)
        if target in ("login",):
            continue
        if target not in frames:
            errors.append(f"[C] {mod_name}: NavigateTo 先フレーム '{target}' が存在しない")
            continue
        if seg and seg not in frame_segments[target]:
            errors.append(f"[C] {mod_name}: NavigateTo '/{target}/{seg}' のセグメントが {target} に未登録（FB-023 真っ白）")
    # C-3: 補間 URL の検査。補間式が Resolve〇〇() ならその関数本体の戻り値だけを候補にする
    # （ファイル全体の frame 候補で総当たりすると、別リゾルバの組み合わせで誤検知するため）
    def resolver_frames(fn_name):
        fm = re.search(r'string ' + re.escape(fn_name) + r'\(\)\s*\{(.*?)\n\}', src, re.S)
        if not fm:
            return None
        return {x for x in re.findall(r'"(\w+)"', fm.group(1)) if x in frames}
    for m in nav_interp.finditer(src):
        expr, seg = m.group(1), m.group(2)
        if not seg:
            continue  # $"/{frame}" 素の遷移は TopPageModuleDesign 着地
        cands = None
        if expr.endswith("()"):
            cands = resolver_frames(expr[:-2])
        if cands is None:
            cands = candidates
        for target in cands:
            if seg not in frame_segments[target]:
                errors.append(f"[C] {mod_name}: 補間遷移 '/{{{expr}}}/{seg}' が候補フレーム {target} に未登録（FB-023 真っ白）")
    # D: GetModuleUrl の解決可能性
    callers = mod_frames.get(mod_name, set())
    for m in get_url.finditer(src):
        target_mod = m.group(1)
        if target_mod not in modules:
            errors.append(f"[D] {mod_name}: GetModuleUrl 対象 '{target_mod}' が存在しない")
            continue
        for cf in callers:
            if target_mod not in frames[cf]["regs"]:
                warns.append(f"[D] {mod_name} (登録先 {cf}): GetModuleUrl('{target_mod}') が {cf} で解決不能の可能性（{cf} に未登録。スクリプトの実行経路がガードされているかの確認要）")

print(f"frames={len(frames)} modules={len(modules)}")
print(f"errors={len(errors)}")
for e in errors:
    print("  ERROR", e)
print(f"warns={len(warns)}")
for w in sorted(set(warns)):
    print("  WARN", w)
