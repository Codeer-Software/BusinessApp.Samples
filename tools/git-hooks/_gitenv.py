#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""_gitenv.py — **フックの中から、別のリポジトリで git を撃つための環境**（`tools/git-hooks/` の共有の小道具）.

**単体では動かない。** `pre_merge_commit_selftest.py` と `docs_only.py` が使う。

git はフックに `GIT_DIR`・`GIT_INDEX_FILE` などを渡し、**マージのときは `GITHEAD_<sha>` も渡す**。
**それを落とさずに使い捨てのリポジトリで git を撃つと、本番のリポジトリに向く**
——`githooks(5)` が「他のリポジトリで git を呼ぶなら、これらの環境変数を消せ」と明記している。
**`git commit -a` は `GIT_INDEX_FILE` に絶対パスを渡す**ので、落とし忘れると**本番の索引を壊す**
（2026-09-22 に実測。qa/03 の L-63）。
"""

from __future__ import annotations

import os
import subprocess


def clean_env(extra: dict[str, str] | None = None) -> dict[str, str]:
    """git のローカル環境変数と `GITHEAD_*`、フック自身の合図を落とした環境を返す。"""
    names = subprocess.run(
        ["git", "rev-parse", "--local-env-vars"],
        capture_output=True, text=True, check=True,
    ).stdout.split()
    env = {k: v for k, v in os.environ.items()
           if k not in names and not k.startswith("GITHEAD_")}
    env.pop("PRE_MERGE_COMMIT_SELFTEST", None)
    env.pop("PRE_MERGE_COMMIT_EXPLAIN", None)
    if extra:
        env.update(extra)
    return env
