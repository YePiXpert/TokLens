#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
知微 · TokLens — Linux VPS / 跨平台零依赖采集与同步探针 (toklens_agent.py)
纯 Python 3 标准库实现（无需 pip 安装任何依赖），自动遍历 26+ 种 AI Agent Harness，
生成不含任何隐私对话原文的轻量统计快照，并通过 GitHub 私有 Gist 或 WebDAV 自动同步。
"""

import argparse
import base64
import datetime
import json
import os
import platform
import re
import shutil
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

SCHEMA_VERSION = "toklens.node.v2"

# 内置主流模型百万 Token 美元单价: (input, output, cache_read, cache_write)
BUILTIN_RATES = {
    "claude-fable-5-2": (8.0, 40.0, 0.2, 10.0),
    "claude-fable-5-1": (10.0, 50.0, 0.25, 12.5),
    "claude-fable-5": (10.0, 50.0, 1.0, 12.5),
    "claude-mythos-5": (10.0, 50.0, 1.0, 12.5),
    "claude-opus-5-5": (4.0, 20.0, 0.2, 5.0),
    "claude-opus-5": (5.0, 25.0, 0.5, 6.25),
    "claude-sonnet-5-5": (2.0, 10.0, 0.1, 2.5),
    "claude-sonnet-5": (2.0, 10.0, 0.2, 2.5),
    "claude-opus-4-8": (5.0, 25.0, 0.5, 6.25),
    "claude-opus-4-7": (5.0, 25.0, 0.5, 6.25),
    "claude-opus-4-6": (5.0, 25.0, 0.5, 6.25),
    "claude-opus-4-5": (5.0, 25.0, 0.5, 6.25),
    "claude-opus-4-1": (15.0, 75.0, 1.5, 18.75),
    "claude-opus-4": (15.0, 75.0, 1.5, 18.75),
    "claude-sonnet-4-6": (3.0, 15.0, 0.3, 3.75),
    "claude-sonnet-4-5": (3.0, 15.0, 0.3, 3.75),
    "claude-sonnet-4": (3.0, 15.0, 0.3, 3.75),
    "claude-3-7-sonnet": (3.0, 15.0, 0.3, 3.75),
    "claude-3-5-sonnet": (3.0, 15.0, 0.3, 3.75),
    "claude-haiku-4-5": (1.0, 5.0, 0.1, 1.25),
    "claude-3-5-haiku": (0.8, 4.0, 0.08, 1.0),
    "gpt-6-astra": (10.0, 50.0, 1.0, 12.5),
    "gpt-6.1-sol": (2.0, 10.0, 0.1, 2.5),
    "gpt-6-sol": (2.0, 10.0, 0.2, 2.5),
    "gpt-6.1-terra": (1.0, 5.0, 0.05, 1.25),
    "gpt-6-luna": (0.1, 0.5, 0.01, 0.125),
    "gpt-5.4": (2.5, 15.0, 0.25, 0.0),
    "gpt-5.3-codex": (1.75, 14.0, 0.175, 0.0),
    "gpt-5.2-codex": (1.75, 14.0, 0.175, 0.0),
    "gpt-5.2": (1.75, 14.0, 0.175, 0.0),
    "gpt-5.1-codex": (1.25, 10.0, 0.125, 0.0),
    "gpt-5.1": (1.25, 10.0, 0.125, 0.0),
    "gpt-5-codex": (1.25, 10.0, 0.125, 0.0),
    "gpt-5": (1.25, 10.0, 0.125, 0.0),
    "gpt-5-mini": (0.25, 2.0, 0.025, 0.0),
    "gpt-4.1": (2.0, 8.0, 0.5, 0.0),
    "gpt-4.1-mini": (0.4, 1.6, 0.1, 0.0),
    "gpt-4o": (2.5, 10.0, 1.25, 0.0),
    "gpt-4o-mini": (0.15, 0.6, 0.075, 0.0),
    "o3": (2.0, 8.0, 0.5, 0.0),
    "o4-mini": (1.1, 4.4, 0.275, 0.0),
    "gemini-4-carbon": (2.0, 10.0, 0.2, 0.0),
    "gemini-4-neon": (0.4, 2.0, 0.04, 0.0),
    "gemini-4-argon": (2.0, 10.0, 0.2, 0.0),
    "orchestrator": (2.0, 10.0, 0.2, 0.0),
    "gemini-3.1-pro": (2.0, 12.0, 0.2, 0.0),
    "gemini-3-pro": (2.0, 12.0, 0.2, 0.0),
    "gemini-3-flash": (0.5, 3.0, 0.05, 0.0),
    "gemini-2.5-pro": (1.25, 10.0, 0.125, 0.0),
    "gemini-2.5-flash": (0.3, 2.5, 0.03, 0.0),
    "deepseek-v4.1-flash": (0.3, 1.2, 0.006, 0.0),
    "deepseek-flash": (0.15, 0.6, 0.003, 0.0),
    "deepseek-v4-pro": (0.66, 1.98, 0.022, 0.0),
    "deepseek-v4-flash": (0.14, 0.28, 0.0028, 0.0),
    "deepseek-v3.2": (0.28, 0.42, 0.028, 0.0),
    "deepseek-v3.1": (0.55, 1.67, 0.055, 0.0),
    "deepseek-v3": (0.28, 1.11, 0.028, 0.0),
    "deepseek-chat": (0.28, 1.11, 0.028, 0.0),
    "deepseek-reasoner": (0.55, 2.19, 0.14, 0.0),
    "kimi-for-coding": (0.95, 4.0, 0.14, 0.0),
    "k3": (1.2, 5.0, 0.18, 0.0),
    "k3-256k": (1.2, 5.0, 0.18, 0.0),
    "kimi-k3.1-pro-highspeed": (4.0, 20.0, 0.2, 4.0),
    "kimi-k3.1-pro": (2.0, 10.0, 0.2, 2.0),
    "kimi-k3": (1.2, 5.0, 0.18, 0.0),
    "workbuddy-default": (0.6, 2.4, 0.06, 0.0),
    "kimi-k2.7-code": (0.95, 4.0, 0.19, 0.0),
    "kimi-k2.6": (0.95, 4.0, 0.16, 0.0),
    "kimi-k2.5": (0.6, 3.0, 0.1, 0.0),
    "kimi-k2": (0.55, 2.2, 0.1, 0.0),
    "glm-5.3": (1.4, 4.4, 0.26, 0.0),
    "glm-5.3-flash": (0.15, 0.5, 0.03, 0.0),
    "glm-5.2": (1.4, 4.4, 0.26, 0.0),
    "glm-5.1": (1.4, 4.4, 0.26, 0.0),
    "glm-5": (1.0, 3.2, 0.2, 0.0),
    "glm-4.7": (0.6, 2.2, 0.11, 0.0),
    "glm-4.5": (0.6, 2.2, 0.11, 0.0),
    "qwen3-coder-plus": (1.0, 4.0, 0.2, 0.0),
    "qwen3-coder": (0.5, 2.0, 0.1, 0.0),
    "qwen-max": (1.6, 6.4, 0.32, 0.0),
    "qwen-plus": (0.4, 1.2, 0.08, 0.0),
    "devstral": (0.3, 0.9, 0.03, 0.0),
    "codestral": (0.3, 0.9, 0.03, 0.0),
    "mistral-large": (2.0, 6.0, 0.2, 0.0),
    "grok-4": (3.0, 15.0, 0.75, 0.0),
    "grok-3": (3.0, 15.0, 0.75, 0.0),
    "grok-code-fast": (0.2, 1.5, 0.02, 0.0),
}

UUID_RE = re.compile(r"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})", re.I)
AGY_MODEL_RE = re.compile(r"<USER_SETTINGS_CHANGE>[\s\S]*?`Model Selection`\s+from\s+.+?\s+to\s+([^\r\n.]+)")


def sanitize_machine_id(raw: str) -> str:
    s = re.sub(r"[^a-zA-Z0-9_-]+", "-", (raw or "").strip().lower()).strip("-")
    return s[:48] if s else "vps-node"


def normalize_model(raw, fallback="unknown") -> str:
    if not raw or not isinstance(raw, str):
        return fallback
    m = raw.strip()
    if m in ("<synthetic>", "agent-loop", "unknown", "none", "null", ""):
        return fallback
    if "/" in m:
        m = m.rsplit("/", 1)[-1].strip()
    if not m.lower().startswith("ft:") and ":" in m:
        m = m.rsplit(":", 1)[-1].strip()
    m = re.sub(r"\s*\([^)]*\)\s*", "", m).strip()
    slug = re.sub(r"[\s_]+", "-", m.lower())
    return slug or fallback


def to_local_date(val, fallback_ts: float) -> str:
    if val is not None:
        if isinstance(val, (int, float)) and val > 0:
            sec = val / 1000.0 if val > 1e11 else float(val)
            try:
                return datetime.datetime.fromtimestamp(sec).strftime("%Y-%m-%d")
            except Exception:
                pass
        elif isinstance(val, str):
            s = val.strip()
            if s.isdigit() and int(s) > 0:
                return to_local_date(int(s), fallback_ts)
            if len(s) >= 10 and s[4] == "-" and s[7] == "-":
                try:
                    iso = s.replace("Z", "+00:00")
                    dt = datetime.datetime.fromisoformat(iso)
                    if dt.tzinfo is not None:
                        dt = dt.astimezone()
                    return dt.strftime("%Y-%m-%d")
                except Exception:
                    return s[:10]
    return datetime.datetime.fromtimestamp(fallback_ts).strftime("%Y-%m-%d")


def safe_int(v) -> int:
    if v is None:
        return 0
    try:
        return int(float(v))
    except Exception:
        return 0


def safe_float(v) -> float:
    if v is None:
        return 0.0
    if isinstance(v, dict):
        return safe_float(v.get("total") or v.get("amount") or v.get("usd"))
    try:
        return float(v)
    except Exception:
        return 0.0


def open_sqlite_ro(db_path: Path):
    try:
        uri = f"file:{urllib.request.pathname2url(str(db_path.resolve()))}?mode=ro"
        conn = sqlite3.connect(uri, uri=True, timeout=2.0)
        conn.execute("SELECT 1 FROM sqlite_master LIMIT 1").fetchone()
        return conn, None
    except Exception:
        pass
    tmp_dir = tempfile.mkdtemp(prefix="toklens_")
    try:
        dst = Path(tmp_dir) / db_path.name
        shutil.copy2(db_path, dst)
        for suf in ("-wal", "-shm"):
            side = Path(str(db_path) + suf)
            if side.exists():
                try:
                    shutil.copy2(side, Path(str(dst) + suf))
                except Exception:
                    pass
        conn = sqlite3.connect(str(dst), timeout=2.0)
        return conn, tmp_dir
    except Exception:
        shutil.rmtree(tmp_dir, ignore_errors=True)
        return None, None


def close_sqlite(conn, tmp_dir):
    if conn is not None:
        try:
            conn.close()
        except Exception:
            pass
    if tmp_dir:
        shutil.rmtree(tmp_dir, ignore_errors=True)


def os_path_str(p: Path) -> str:
    s = str(p)
    if os.name == "nt" and len(s) >= 245 and not s.startswith("\\\\?\\"):
        s = os.path.abspath(s)
        if s.startswith("\\\\"):
            return "\\\\?\\UNC\\" + s[2:]
        return "\\\\?\\" + s
    return s


def safe_mtime(p: Path) -> float:
    try:
        return os.stat(os_path_str(p)).st_mtime
    except Exception:
        return time.time()


def safe_open_text(p: Path):
    return open(os_path_str(p), "r", encoding="utf-8", errors="ignore")


def safe_read_text(p: Path) -> str:
    with safe_open_text(p) as f:
        return f.read()


def safe_walk_files(root: Path, suffix: str = "", exact_name: str = ""):
    try:
        if not os.path.exists(os_path_str(root)):
            return
    except Exception:
        return
    skip_dirs = {"node_modules", ".git", "Cache", "Code Cache", "__pycache__", "venv", ".venv"}
    for dirpath, dirnames, filenames in os.walk(os_path_str(root)):
        dirnames[:] = [d for d in dirnames if d not in skip_dirs]
        clean_dir = dirpath[4:] if (os.name == "nt" and dirpath.startswith("\\\\?\\") and not dirpath.startswith("\\\\?\\UNC\\")) else dirpath
        for fn in filenames:
            if exact_name and fn != exact_name:
                continue
            if suffix and not fn.endswith(suffix):
                continue
            yield Path(clean_dir) / fn


class PricingLookup:
    def __init__(self, home: Path):
        self.rates = dict(BUILTIN_RATES)
        self._load_or_sync_cloud_cache(home)
        custom_fp = home / ".toklens" / "custom_prices.json"
        try:
            if custom_fp.exists():
                cmap = json.loads(custom_fp.read_text(encoding="utf-8"))
                if isinstance(cmap, dict):
                    for mid, item in cmap.items():
                        if isinstance(item, dict):
                            ic = safe_float(item.get("input"))
                            oc = safe_float(item.get("output"))
                            if ic > 0 or oc > 0:
                                self.rates[str(mid).strip().lower()] = (
                                    ic,
                                    oc,
                                    safe_float(item.get("cache_read")),
                                    safe_float(item.get("cache_write")),
                                )
        except Exception:
            pass

    def _load_or_sync_cloud_cache(self, home: Path):
        cache_fp = home / ".toklens" / "pricing_cache.json"
        models_map = None
        now_ts = int(time.time())
        try:
            if cache_fp.exists():
                cached = json.loads(cache_fp.read_text(encoding="utf-8"))
                if isinstance(cached.get("models"), dict):
                    models_map = cached["models"]
                    if now_ts - int(cached.get("ts_utc") or 0) < 86400:
                        self._merge_cloud_map(models_map)
                        return
        except Exception:
            pass

        fresh = {}
        # 1. LiteLLM CDN -> GitHub Raw
        for url in (
            "https://cdn.jsdelivr.net/gh/BerriAI/litellm@main/model_prices_and_context_window.json",
            "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json",
        ):
            try:
                req = urllib.request.Request(url, headers={"User-Agent": "TokLens-Agent/2.1"})
                with urllib.request.urlopen(req, timeout=7) as r:
                    d = json.loads(r.read().decode("utf-8", errors="ignore"))
                if isinstance(d, dict) and len(d) > 100:
                    for k, item in d.items():
                        if k == "sample_spec" or str(k).lower().endswith(":free") or not isinstance(item, dict):
                            continue
                        ic = safe_float(item.get("input_cost_per_token")) * 1e6
                        oc = safe_float(item.get("output_cost_per_token")) * 1e6
                        if ic <= 0 or oc <= 0 or ic > 500 or oc > 1500:
                            continue
                        crc = safe_float(item.get("cache_read_input_token_cost")) * 1e6
                        cw = safe_float(item.get("cache_creation_input_token_cost")) * 1e6
                        slug = normalize_model(k, "")
                        if len(slug) >= 2:
                            fresh[slug] = [round(ic, 4), round(oc, 4), round(crc, 4), round(cw, 4)]
                    break
            except Exception:
                pass

        # 2. OpenRouter Public Models API
        try:
            req = urllib.request.Request("https://openrouter.ai/api/v1/models", headers={"User-Agent": "TokLens-Agent/2.1"})
            with urllib.request.urlopen(req, timeout=7) as r:
                or_data = json.loads(r.read().decode("utf-8", errors="ignore"))
            for m in (or_data.get("data") if isinstance(or_data, dict) else []) or []:
                rid = str(m.get("id") or "")
                if rid.lower().endswith(":free") or not isinstance(m.get("pricing"), dict):
                    continue
                pr = m["pricing"]
                ic = safe_float(pr.get("prompt")) * 1e6
                oc = safe_float(pr.get("completion")) * 1e6
                if ic <= 0 or oc <= 0 or ic > 500 or oc > 1500:
                    continue
                crc = safe_float(pr.get("input_cache_read")) * 1e6
                cw = safe_float(pr.get("input_cache_write")) * 1e6
                slug = normalize_model(rid, "")
                if len(slug) >= 2:
                    fresh[slug] = [round(ic, 4), round(oc, 4), round(crc, 4), round(cw, 4)]
        except Exception:
            pass

        # 3. models.dev Open AI Coding Model Registry (USD per 1M directly)
        try:
            req = urllib.request.Request("https://models.dev/api.json", headers={"User-Agent": "TokLens-Agent/2.1"})
            with urllib.request.urlopen(req, timeout=7) as r:
                md_data = json.loads(r.read().decode("utf-8", errors="ignore"))
            if isinstance(md_data, dict):
                for prov_obj in md_data.values():
                    if not isinstance(prov_obj, dict) or not isinstance(prov_obj.get("models"), dict):
                        continue
                    for mid, mobj in prov_obj["models"].items():
                        cost_obj = mobj.get("cost") if isinstance(mobj, dict) else None
                        if not isinstance(cost_obj, dict):
                            continue
                        ic = safe_float(cost_obj.get("input"))
                        oc = safe_float(cost_obj.get("output"))
                        if ic <= 0 or oc <= 0 or ic > 500 or oc > 1500:
                            continue
                        crc = safe_float(cost_obj.get("cache_read"))
                        cw = safe_float(cost_obj.get("cache_write"))
                        slug = normalize_model(str(mid), "")
                        if len(slug) >= 2:
                            fresh[slug] = [round(ic, 4), round(oc, 4), round(crc, 4), round(cw, 4)]
        except Exception:
            pass

        if fresh:
            models_map = fresh
            try:
                cache_fp.parent.mkdir(parents=True, exist_ok=True)
                cache_fp.write_text(json.dumps({"ts_utc": now_ts, "models": fresh}, ensure_ascii=False), encoding="utf-8")
            except Exception:
                pass

        if models_map:
            self._merge_cloud_map(models_map)

    def _merge_cloud_map(self, models_map: dict):
        for k, arr in models_map.items():
            if not isinstance(arr, list) or len(arr) < 4:
                continue
            ic, oc, crc, cwc = safe_float(arr[0]), safe_float(arr[1]), safe_float(arr[2]), safe_float(arr[3])
            if k in self.rates:
                ex = self.rates[k]
                if crc <= 0 and ex[2] > 0:
                    crc = ex[2]
                if cwc <= 0 and ex[3] > 0:
                    cwc = ex[3]
            self.rates[k] = (ic, oc, crc, cwc)

    def lookup(self, raw_model: str):
        slug = normalize_model(raw_model, "unknown")
        if slug in self.rates:
            return self.rates[slug]
        stripped = re.sub(r"-(high|medium|low|minimal|thinking|preview|latest)$", "", slug, flags=re.I)
        if stripped in self.rates:
            return self.rates[stripped]
        no_date = re.sub(r"-\d{8}$", "", stripped)
        if no_date in self.rates:
            return self.rates[no_date]
        best = None
        for k in self.rates:
            if len(k) < 4:
                continue
            if (slug.startswith(k) and (len(slug) == len(k) or slug[len(k)] in "-._")) or (
                no_date.startswith(k) and (len(no_date) == len(k) or no_date[len(k)] in "-._")
            ):
                if best is None or len(k) > len(best):
                    best = k
        if best:
            return self.rates[best]
        if "kimi" in slug or slug.startswith("k3"):
            return self.rates["kimi-for-coding"]
        if "glm" in slug:
            return self.rates["glm-5.3-flash" if "flash" in slug else "glm-5.3"]
        if "deepseek" in slug:
            return self.rates["deepseek-v4.1-flash" if "flash" in slug else "deepseek-v3.2"]
        if "qwen" in slug:
            return self.rates["qwen3-coder-plus"]
        if "opus" in slug:
            return self.rates["claude-opus-5-5"]
        if "sonnet" in slug:
            return self.rates["claude-sonnet-4-6"]
        if "haiku" in slug:
            return self.rates["claude-haiku-4-5"]
        if "gemini" in slug:
            return self.rates["gemini-4-carbon"]
        if "gpt" in slug or "codex" in slug:
            return self.rates["gpt-5.2-codex"]
        return (1.0, 4.0, 0.1, 0.0)


def extract_generic_usage(obj: dict, cur_model: str, fallback_ts: float):
    """通用提取器：兼容 OpenClaw / Pi / OMP / Qwen / Droid / Amp / Vibe / OpenHands 等 JSON/JSONL 结构"""
    if not isinstance(obj, dict):
        return None
    msg = obj.get("message") if isinstance(obj.get("message"), dict) else None
    role = (msg.get("role") if msg else None) or obj.get("role")
    if role and role not in ("assistant", "model", "ai", "agent"):
        return None

    u = None
    for cand in (
        obj.get("usage"),
        msg.get("usage") if msg else None,
        obj.get("tokens"),
        msg.get("tokens") if msg else None,
        obj.get("tokenUsage"),
        obj.get("usageMetadata"),
    ):
        if isinstance(cand, dict):
            u = cand
            break
    if not u:
        return None

    raw_model = (
        (msg.get("model") if msg else None)
        or (msg.get("modelId") if msg else None)
        or obj.get("model")
        or obj.get("modelId")
        or obj.get("modelID")
        or cur_model
    )
    if raw_model == "<synthetic>":
        return None
    mdl = normalize_model(str(raw_model), cur_model)

    # 区分 Fresh vs Inclusive
    has_anthropic_fields = ("input_tokens" in u and ("cache_read_input_tokens" in u or "cache_creation_input_tokens" in u))
    has_pi_fields = ("input" in u and ("cacheRead" in u or "cacheWrite" in u))

    raw_in = safe_int(
        u.get("input_tokens")
        or u.get("input")
        or u.get("prompt_tokens")
        or u.get("promptTokenCount")
        or u.get("tokensIn")
        or u.get("inputOther")
    )
    out_tok = safe_int(
        u.get("output_tokens")
        or u.get("output")
        or u.get("completion_tokens")
        or u.get("candidatesTokenCount")
        or u.get("tokensOut")
    ) + safe_int(u.get("thoughts") or u.get("thoughtsTokenCount"))
    if out_tok == 0:
        out_tok = safe_int(u.get("reasoning") or u.get("reasoning_tokens"))

    cache_obj = u.get("cache") if isinstance(u.get("cache"), dict) else None
    cr = safe_int(
        u.get("cache_read_input_tokens")
        or u.get("cacheRead")
        or u.get("cache_read_tokens")
        or u.get("cached_input_tokens")
        or u.get("cached_tokens")
        or u.get("cached")
        or u.get("prompt_cache_hit_tokens")
        or u.get("inputCacheRead")
        or (cache_obj.get("read") if cache_obj else 0)
    )
    cw = safe_int(
        u.get("cache_creation_input_tokens")
        or u.get("cacheWrite")
        or u.get("cache_creation_tokens")
        or u.get("cache_write_tokens")
        or u.get("inputCacheCreation")
        or (cache_obj.get("write") if cache_obj else 0)
    )
    tot = safe_int(u.get("total_tokens") or u.get("totalTokens") or u.get("total"))

    if has_anthropic_fields or has_pi_fields or "inputOther" in u:
        fi = raw_in
    elif ("prompt_tokens" in u and cr > 0 and raw_in >= cr) or (tot > 0 and abs((raw_in + out_tok) - tot) <= 4 and cr > 0):
        fi = max(0, raw_in - cr - cw)
    else:
        fi = raw_in

    if fi + out_tok + cr + cw <= 0:
        return None

    cost = safe_float(
        u.get("cost")
        or obj.get("costUSD")
        or obj.get("cost")
        or obj.get("total_cost")
        or (msg.get("cost") if msg else None)
    )
    ts_val = (
        obj.get("timestamp")
        or obj.get("created_at")
        or obj.get("createdAt")
        or obj.get("time")
        or (msg.get("timestamp") if msg else None)
    )
    if isinstance(ts_val, dict):
        ts_val = ts_val.get("created") or ts_val.get("start")
    dt = to_local_date(ts_val, fallback_ts)
    dedup_id = str(
        obj.get("requestId")
        or (msg.get("id") if msg else "")
        or obj.get("id")
        or obj.get("uuid")
        or ""
    )
    return (mdl, dt, fi, out_tok, cr, cw, cost, dedup_id)


class MultiHarnessScanner:
    def __init__(self, home: Path):
        self.home = home
        self.pricing = PricingLookup(home)
        self.events = []  # (tool, model, date, fi, out, cr, cw, logged_cost, turns)
        self.seen_req_ids = set()
        self.claude_live = set()
        self.codex_live = set()
        self.agy_live = set()
        self.dsh_live = set()

    def add_ev(self, tool, model, date_str, fi, out_tok, cr, cw, cost=0.0, turns=1):
        if fi + out_tok + cr + cw > 0:
            self.events.append((tool, model, date_str, fi, out_tok, cr, cw, cost, turns))

    def scan_all(self):
        for fn in (
            self.scan_claude,
            self.scan_codex_live,
            self.scan_antigravity,
            self.scan_gemini_and_qwen_chats,
            self.scan_zcode,
            self.scan_kimi,
            self.scan_opencode_and_mobileclaw,
            self.scan_workbuddy_family,
            self.scan_dsh,
            self.scan_commandcode,
            self.scan_vscode_cline_family,
            self.scan_openclaw,
            self.scan_pi_and_omp,
            self.scan_qwen_jsonl,
            self.scan_factory_droid,
            self.scan_amp,
            self.scan_goose,
            self.scan_crush,
            self.scan_aider,
            self.scan_continue,
            self.scan_vibe_and_agents,
            self.scan_cursor_native,
            self.scan_zed,
            self.scan_custom_rules,
            self.scan_codex_sqlite_archive,
        ):
            try:
                fn()
            except Exception:
                pass
        rows = self.build_records()
        return merge_with_ledger_db(self.home, rows)

    def scan_jsonl_tree(self, tool_name: str, roots, default_model: str, suffix=".jsonl"):
        dedup = {}
        for r in roots:
            for fp in safe_walk_files(r, suffix=suffix):
                if fp.name.endswith(".checkpoints.jsonl"):
                    continue
                mtime = safe_mtime(fp)
                cur_m = default_model
                try:
                    with safe_open_text(fp) as f:
                        for idx, line in enumerate(f, 1):
                            if "usage" not in line and "tokens" not in line and "input" not in line and "model" not in line:
                                continue
                            try:
                                obj = json.loads(line)
                            except Exception:
                                continue
                            if not isinstance(obj, dict):
                                continue
                            if obj.get("type") in ("model_change", "turn_context", "llm.request", "session"):
                                cand = obj.get("modelId") or obj.get("model") or (obj.get("payload") or {}).get("model")
                                if cand:
                                    cur_m = normalize_model(str(cand), cur_m)
                            parsed = extract_generic_usage(obj, cur_m, mtime)
                            if not parsed:
                                continue
                            mdl, dt, fi, out_tok, cr, cw, cost, did = parsed
                            cur_m = mdl
                            key = f"{fp}:{did}" if did else f"{fp}:L{idx}"
                            prev = dedup.get(key)
                            if not prev or (fi + out_tok + cr + cw) >= (prev[3] + prev[4] + prev[5] + prev[6]):
                                dedup[key] = (tool_name, mdl, dt, fi, out_tok, cr, cw, cost)
                except Exception:
                    pass
        for item in dedup.values():
            self.add_ev(*item)

    def scan_claude(self):
        projects_root = self.home / ".claude" / "projects"
        file_items = []
        desktop_projects = set()
        for sub in ("projects", "transcripts"):
            root = self.home / ".claude" / sub
            for fp in safe_walk_files(root, suffix=".jsonl"):
                s_fp = str(fp)
                proj_group = ""
                if sub == "projects":
                    try:
                        rel_parts = fp.relative_to(projects_root).parts
                        if rel_parts:
                            proj_group = rel_parts[0]
                    except Exception:
                        pass
                ep_kind = "desktop" if ("Claude-Data" in s_fp or "claude-pzs8sxrjxfjjc" in s_fp) else "unknown"
                file_dedup = {}
                mtime = safe_mtime(fp)
                try:
                    with safe_open_text(fp) as f:
                        for idx, line in enumerate(f, 1):
                            if '"entrypoint"' in line:
                                if "claude-desktop" in line:
                                    ep_kind = "desktop"
                                elif ep_kind != "desktop" and ('"cli"' in line or '"sdk-cli"' in line):
                                    ep_kind = "cli"
                            if '"input_tokens"' not in line:
                                continue
                            try:
                                obj = json.loads(line)
                            except Exception:
                                continue
                            if not isinstance(obj, dict):
                                continue
                            ep_val = str(obj.get("entrypoint") or "").lower()
                            if "desktop" in ep_val:
                                ep_kind = "desktop"
                            elif ep_kind != "desktop" and "cli" in ep_val:
                                ep_kind = "cli"
                            msg = obj.get("message")
                            if not isinstance(msg, dict):
                                continue
                            u = msg.get("usage")
                            if not isinstance(u, dict):
                                continue
                            raw_m = msg.get("model") or obj.get("model")
                            if raw_m == "<synthetic>":
                                continue
                            mdl = normalize_model(raw_m, "claude-sonnet-4-6")
                            fi = safe_int(u.get("input_tokens"))
                            out_tok = safe_int(u.get("output_tokens"))
                            cr = safe_int(u.get("cache_read_input_tokens"))
                            cw = safe_int(u.get("cache_creation_input_tokens"))
                            if fi + out_tok + cr + cw <= 0:
                                continue
                            mid = str(msg.get("id") or "")
                            rid = str(obj.get("requestId") or "")
                            key = f"{mid}:{rid}" if (mid and rid) else (f"m:{mid}" if mid else (f"r:{rid}" if rid else f"{fp}:{idx}"))
                            dt = to_local_date(obj.get("timestamp"), mtime)
                            cost = safe_float(obj.get("costUSD"))
                            prev = file_dedup.get(key)
                            if not prev or (fi + out_tok + cr + cw) >= (prev[2] + prev[3] + prev[4] + prev[5]):
                                file_dedup[key] = (mdl, dt, fi, out_tok, cr, cw, cost)
                except Exception:
                    pass
                if ep_kind == "desktop" and proj_group:
                    desktop_projects.add(proj_group)
                file_items.append((proj_group, ep_kind, file_dedup))

        dedup = {}
        for proj_group, ep_kind, f_dedup in file_items:
            is_desktop = (ep_kind == "desktop") or (ep_kind != "cli" and proj_group and proj_group in desktop_projects)
            tool = "Claude Desktop" if is_desktop else "Claude Code"
            for key, (mdl, dt, fi, out_tok, cr, cw, cost) in f_dedup.items():
                prev = dedup.get(key)
                if not prev or (fi + out_tok + cr + cw) >= (prev[3] + prev[4] + prev[5] + prev[6]):
                    dedup[key] = (tool, mdl, dt, fi, out_tok, cr, cw, cost)
        for item in dedup.values():
            self.add_ev(*item)

    def scan_codex_live(self):
        for sub in ("sessions", "archived_sessions"):
            for fp in safe_walk_files(self.home / ".codex" / sub, suffix=".jsonl"):
                m = UUID_RE.search(fp.name)
                if m:
                    self.codex_live.add(m.group(1))
                cur_m = "gpt-5.2-codex"
                prev_tot = None
                mtime = safe_mtime(fp)
                try:
                    with safe_open_text(fp) as f:
                        for line in f:
                            if "turn_context" not in line and "token_count" not in line and "session_meta" not in line:
                                continue
                            try:
                                obj = json.loads(line)
                            except Exception:
                                continue
                            typ = obj.get("type")
                            pl = obj.get("payload") if isinstance(obj.get("payload"), dict) else {}
                            if typ == "session_meta" and pl.get("id"):
                                self.codex_live.add(str(pl["id"]))
                            elif typ == "turn_context" and pl.get("model"):
                                cur_m = normalize_model(str(pl["model"]), cur_m)
                            elif typ == "event_msg" and pl.get("type") == "token_count":
                                info = pl.get("info") if isinstance(pl.get("info"), dict) else {}
                                tot_u = info.get("total_token_usage") or {}
                                last_u = info.get("last_token_usage") or {}
                                sig = (safe_int(tot_u.get("input_tokens")), safe_int(tot_u.get("output_tokens")), safe_int(tot_u.get("cached_input_tokens")))
                                if sig == (0, 0, 0) or sig == prev_tot:
                                    continue
                                prev_tot = sig
                                raw_in = safe_int(last_u.get("input_tokens"))
                                cr = safe_int(last_u.get("cached_input_tokens"))
                                out_tok = safe_int(last_u.get("output_tokens"))
                                fi = max(0, raw_in - cr)
                                self.add_ev("OpenAI Codex", cur_m, to_local_date(obj.get("timestamp"), mtime), fi, out_tok, cr, 0)
                except Exception:
                    pass

    def scan_antigravity(self):
        brain = self.home / ".gemini" / "antigravity" / "brain"
        try:
            if not brain.exists():
                return
        except Exception:
            return
        for cdir in brain.iterdir():
            tf = cdir / ".system_generated" / "logs" / "transcript.jsonl"
            if not os.path.exists(os_path_str(tf)):
                continue
            self.agy_live.add(cdir.name)
            cur_m = "gemini-4-carbon"
            steps = {}
            mtime = safe_mtime(tf)
            try:
                with safe_open_text(tf) as f:
                    for line in f:
                        if "input_tokens" not in line and "Model Selection" not in line:
                            continue
                        try:
                            obj = json.loads(line)
                        except Exception:
                            continue
                        cnt = obj.get("content")
                        if isinstance(cnt, str) and "<USER_SETTINGS_CHANGE>" in cnt and "Model Selection" in cnt:
                            mm = AGY_MODEL_RE.search(cnt)
                            if mm:
                                cur_m = normalize_model(mm.group(1), cur_m)
                        fi = safe_int(obj.get("input_tokens"))
                        out_tok = safe_int(obj.get("output_tokens"))
                        cr = safe_int(obj.get("cache_read_tokens"))
                        cw = safe_int(obj.get("cache_creation_tokens") or obj.get("cache_write_tokens"))
                        if fi + out_tok + cr + cw > 0:
                            sidx = obj.get("step_index", len(steps))
                            steps[sidx] = ("Google Antigravity", cur_m, to_local_date(obj.get("created_at"), mtime), fi, out_tok, cr, cw)
            except Exception:
                pass
            for v in steps.values():
                self.add_ev(*v)

    def scan_gemini_and_qwen_chats(self):
        for tool_name, root, def_m in (
            ("Gemini CLI", self.home / ".gemini" / "tmp", "gemini-2.5-pro"),
            ("Qwen Code", self.home / ".qwen" / "tmp", "qwen3-coder-plus"),
        ):
            for fp in safe_walk_files(root, suffix=".json"):
                if "chats" not in fp.parts:
                    continue
                mtime = safe_mtime(fp)
                try:
                    data = json.loads(safe_read_text(fp))
                    for item in (data.get("messages") or []):
                        if not isinstance(item, dict) or not isinstance(item.get("tokens"), dict):
                            continue
                        tok = item["tokens"]
                        raw_in = safe_int(tok.get("input"))
                        out_tok = safe_int(tok.get("output")) + safe_int(tok.get("thoughts"))
                        cr = safe_int(tok.get("cached"))
                        fi = max(0, raw_in - cr) if raw_in >= cr else raw_in
                        self.add_ev(tool_name, normalize_model(item.get("model"), def_m), to_local_date(item.get("timestamp"), mtime), fi, out_tok, cr, 0)
                except Exception:
                    pass

    def scan_zcode(self):
        zdb = self.home / ".zcode" / "cli" / "db" / "db.sqlite"
        if not zdb.exists():
            return
        conn, tmp = open_sqlite_ro(zdb)
        if not conn:
            return
        try:
            for tc, ds in conn.execute("SELECT time_created, data FROM message"):
                if not ds or '"tokens"' not in ds:
                    continue
                try:
                    d = json.loads(ds)
                except Exception:
                    continue
                if d.get("role") != "assistant" or not isinstance(d.get("tokens"), dict):
                    continue
                tok = d["tokens"]
                raw_in = safe_int(tok.get("input"))
                out_tok = safe_int(tok.get("output"))
                cache = tok.get("cache") if isinstance(tok.get("cache"), dict) else {}
                cr = safe_int(cache.get("read"))
                cw = safe_int(cache.get("write"))
                tot = safe_int(tok.get("total"))
                prov = str(d.get("providerID") or "")
                if (tot > 0 and abs((raw_in + out_tok) - tot) <= 4 and cr > 0) or (raw_in >= cr > 0 and "anthropic" not in prov.lower()):
                    fi = max(0, raw_in - cr - cw)
                else:
                    fi = raw_in
                mdl = normalize_model(d.get("modelID") or d.get("model"), "glm-5.3")
                tval = (d.get("time") or {}).get("created") or tc
                self.add_ev("ZCode", mdl, to_local_date(tval, time.time()), fi, out_tok, cr, cw, safe_float(d.get("cost")))
        except Exception:
            pass
        finally:
            close_sqlite(conn, tmp)

    def scan_kimi(self):
        for wf in safe_walk_files(self.home / ".kimi-code" / "sessions", exact_name="wire.jsonl"):
            cur_m = "kimi-for-coding"
            mtime = safe_mtime(wf)
            try:
                with safe_open_text(wf) as f:
                    for line in f:
                        if '"usage.record"' not in line and '"llm.request"' not in line:
                            continue
                        try:
                            obj = json.loads(line)
                        except Exception:
                            continue
                        t = obj.get("type")
                        if t == "llm.request":
                            cur_m = normalize_model(obj.get("model") or obj.get("modelAlias"), cur_m)
                        elif t == "usage.record" and isinstance(obj.get("usage"), dict):
                            u = obj["usage"]
                            self.add_ev(
                                "Kimi Code",
                                normalize_model(obj.get("model"), cur_m),
                                to_local_date(obj.get("time"), mtime),
                                safe_int(u.get("inputOther")),
                                safe_int(u.get("output")),
                                safe_int(u.get("inputCacheRead")),
                                safe_int(u.get("inputCacheCreation")),
                            )
            except Exception:
                pass

    def _parse_opencode_dict(self, tool_name, d, fallback_ts, ts_override=None, def_model="deepseek-v4.1-flash"):
        if not isinstance(d, dict) or d.get("role") != "assistant":
            return
        tok = d.get("tokens")
        if not isinstance(tok, dict):
            return
        raw_in = safe_int(tok.get("input"))
        out_tok = safe_int(tok.get("output")) or safe_int(tok.get("reasoning"))
        cache = tok.get("cache") if isinstance(tok.get("cache"), dict) else {}
        cr = safe_int(cache.get("read"))
        cw = safe_int(cache.get("write"))
        tot = safe_int(tok.get("total"))
        fi = max(0, raw_in - cr - cw) if (tot > 0 and abs((raw_in + out_tok) - tot) <= 4 and cr > 0) else raw_in
        mdl = normalize_model(d.get("modelID") or d.get("model"), def_model)
        tval = (d.get("time") or {}).get("created") or ts_override
        self.add_ev(tool_name, mdl, to_local_date(tval, fallback_ts), fi, out_tok, cr, cw, safe_float(d.get("cost")))

    def scan_opencode_and_mobileclaw(self):
        # MobileClaw
        mc = self.home / ".local" / "share" / "mobileclaw" / "workspaces"
        seen_mc = set()
        if mc.exists():
            for ws in mc.iterdir():
                for mf in safe_walk_files(ws / "storage" / "message", suffix=".json"):
                    try:
                        d = json.loads(safe_read_text(mf))
                        mid = str(d.get("id") or mf.stem)
                        if mid in seen_mc:
                            continue
                        seen_mc.add(mid)
                        self._parse_opencode_dict("MobileClaw", d, safe_mtime(mf))
                    except Exception:
                        pass

        # OpenCode
        seen_oc = set()
        for base in (self.home / ".local" / "share" / "opencode", self.home / "AppData" / "Local" / "opencode"):
            if not base.exists():
                continue
            for db_p in base.glob("opencode*.db"):
                conn, tmp = open_sqlite_ro(db_p)
                if not conn:
                    continue
                try:
                    for mid, tc, ds in conn.execute("SELECT id, time_created, data FROM message"):
                        if not ds or mid in seen_oc:
                            continue
                        seen_oc.add(mid)
                        self._parse_opencode_dict("OpenCode", json.loads(ds), time.time(), tc, "claude-sonnet-4-6")
                except Exception:
                    pass
                finally:
                    close_sqlite(conn, tmp)
            for mf in safe_walk_files(base / "storage" / "message", suffix=".json"):
                try:
                    d = json.loads(safe_read_text(mf))
                    mid = str(d.get("id") or mf.stem)
                    if mid in seen_oc:
                        continue
                    seen_oc.add(mid)
                    self._parse_opencode_dict("OpenCode", d, safe_mtime(mf), None, "claude-sonnet-4-6")
                except Exception:
                    pass

    def scan_workbuddy_family(self):
        for tool_name, folder in (("WorkBuddy", ".workbuddy"), ("WorkBuddy", ".workbuddy-ai"), ("CodeBuddy", ".codebuddy")):
            root = self.home / folder
            if not (root / "projects").exists():
                continue
            def_m = "workbuddy-default"
            st = root / "settings.json"
            if st.exists():
                try:
                    sd = json.loads(safe_read_text(st))
                    for k in ("model", "defaultModel", "currentModel", "selectedModel"):
                        if sd.get(k):
                            def_m = normalize_model(str(sd[k]), def_m)
                            break
                except Exception:
                    pass
            self.scan_jsonl_tree(tool_name, [root / "projects"], def_m)

    def scan_dsh(self):
        for sf in safe_walk_files(self.home / ".dsh" / "storages", suffix=".json"):
            if not sf.name.startswith("session-"):
                continue
            mtime = safe_mtime(sf)
            try:
                data = json.loads(safe_read_text(sf))
                rows = ((data.get("record") or {}).get("rows")) or []
                found = False
                for item in rows:
                    u = item.get("usage") or ((item.get("meta") or {}).get("usage"))
                    if not isinstance(u, dict):
                        continue
                    raw_in = safe_int(u.get("prompt_tokens") or u.get("input_tokens"))
                    out_tok = safe_int(u.get("completion_tokens") or u.get("output_tokens"))
                    cr = safe_int(u.get("prompt_cache_hit_tokens") or u.get("cache_read_tokens") or u.get("cached_tokens"))
                    fi = (raw_in - cr) if (raw_in >= cr > 0) else raw_in
                    if fi + out_tok + cr > 0:
                        found = True
                        self.add_ev("DeepSeek (DSH)", normalize_model(item.get("model"), "deepseek-flash"), to_local_date(item.get("created_at") or item.get("timestamp"), mtime), fi, out_tok, cr, 0)
                if found:
                    self.dsh_live.add(sf.stem)
                    if sf.stem.startswith("session-"):
                        self.dsh_live.add(sf.stem[8:])
            except Exception:
                pass

    def scan_commandcode(self):
        self.scan_jsonl_tree("Command Code", [self.home / ".commandcode" / "projects"], "claude-sonnet-4-6")

    def scan_vscode_cline_family(self):
        bases = [
            self.home / "AppData" / "Roaming" / "Code" / "User" / "globalStorage",
            self.home / "AppData" / "Roaming" / "Cursor" / "User" / "globalStorage",
            self.home / "AppData" / "Roaming" / "Windsurf" / "User" / "globalStorage",
            self.home / ".config" / "Code" / "User" / "globalStorage",
            self.home / ".config" / "Cursor" / "User" / "globalStorage",
            self.home / ".vscode-server" / "data" / "User" / "globalStorage",
            self.home / ".cursor-server" / "data" / "User" / "globalStorage",
        ]
        exts = [
            ("Cline", "saoudrizwan.claude-dev"),
            ("Roo Code", "rooveterinaryinc.roo-cline"),
            ("Kilo Code", "kilocode.kilo-code"),
        ]
        for b in bases:
            if not b.exists():
                continue
            for label, ext_id in exts:
                for uf in safe_walk_files(b / ext_id / "tasks", exact_name="ui_messages.json"):
                    try:
                        msgs = json.loads(safe_read_text(uf))
                        mtime = safe_mtime(uf)
                        for m in (msgs if isinstance(msgs, list) else []):
                            if m.get("say") != "api_req_started" or not m.get("text"):
                                continue
                            info = json.loads(m["text"])
                            self.add_ev(
                                label,
                                normalize_model(info.get("model"), "claude-sonnet-4-6"),
                                to_local_date(m.get("ts"), mtime),
                                safe_int(info.get("tokensIn")),
                                safe_int(info.get("tokensOut")),
                                safe_int(info.get("cacheReads")),
                                safe_int(info.get("cacheWrites")),
                                safe_float(info.get("cost")),
                            )
                    except Exception:
                        pass

    def scan_openclaw(self):
        roots = []
        try:
            for p in self.home.glob(".openclaw*"):
                if p.is_dir():
                    roots.append(p / "agents")
                    roots.append(p / "sessions")
        except Exception:
            pass
        if roots:
            self.scan_jsonl_tree("OpenClaw", roots, "claude-sonnet-4-6")

    def scan_pi_and_omp(self):
        self.scan_jsonl_tree("Pi Agent", [self.home / ".pi" / "agent" / "sessions", self.home / ".pi" / "sessions"], "claude-sonnet-4-6")
        self.scan_jsonl_tree("Oh My Pi", [self.home / ".omp" / "agent" / "sessions", self.home / ".omp" / "sessions"], "claude-sonnet-4-6")

    def scan_qwen_jsonl(self):
        self.scan_jsonl_tree("Qwen Code", [self.home / ".qwen" / "projects"], "qwen3-coder-plus")

    def scan_factory_droid(self):
        self.scan_jsonl_tree("Factory Droid", [self.home / ".factory" / "sessions"], "claude-sonnet-4-6")
        for jf in safe_walk_files(self.home / ".factory" / "sessions", suffix=".json"):
            try:
                obj = json.loads(safe_read_text(jf))
                parsed = extract_generic_usage(obj, "claude-sonnet-4-6", safe_mtime(jf))
                if parsed:
                    self.add_ev("Factory Droid", *parsed[:7])
            except Exception:
                pass

    def scan_amp(self):
        roots = [
            self.home / ".local" / "share" / "amp" / "threads",
            self.home / ".amp" / "threads",
            self.home / "AppData" / "Local" / "amp" / "threads",
        ]
        for r in roots:
            for jf in safe_walk_files(r, suffix=".json"):
                mtime = safe_mtime(jf)
                try:
                    data = json.loads(safe_read_text(jf))
                    msgs = data.get("messages") if isinstance(data, dict) else None
                    if isinstance(msgs, list):
                        for m in msgs:
                            p = extract_generic_usage(m, "claude-sonnet-4-6", mtime)
                            if p:
                                self.add_ev("Amp", *p[:7])
                    elif isinstance(data, dict):
                        p = extract_generic_usage(data, "claude-sonnet-4-6", mtime)
                        if p:
                            self.add_ev("Amp", *p[:7])
                except Exception:
                    pass

    def scan_goose(self):
        bases = [
            self.home / ".local" / "share" / "goose" / "sessions",
            self.home / "AppData" / "Roaming" / "Block" / "goose" / "data" / "sessions",
        ]
        for b in bases:
            if not b.exists():
                continue
            self.scan_jsonl_tree("Goose", [b], "claude-sonnet-4-6")
            sdb = b / "sessions.db"
            if sdb.exists():
                conn, tmp = open_sqlite_ro(sdb)
                if conn:
                    try:
                        tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                        if "sessions" in tables:
                            cols = {r[1] for r in conn.execute("PRAGMA table_info(sessions)")}
                            if "input_tokens" in cols and "output_tokens" in cols:
                                mcol = "model" if "model" in cols else "'claude-sonnet-4-6'"
                                tcol = "created_at" if "created_at" in cols else ("updated_at" if "updated_at" in cols else "NULL")
                                for mdl, fi, out_tok, ts in conn.execute(f"SELECT {mcol}, input_tokens, output_tokens, {tcol} FROM sessions WHERE input_tokens + output_tokens > 0"):
                                    self.add_ev("Goose", normalize_model(str(mdl or ""), "claude-sonnet-4-6"), to_local_date(ts, time.time()), safe_int(fi), safe_int(out_tok), 0, 0)
                    except Exception:
                        pass
                    finally:
                        close_sqlite(conn, tmp)

    def scan_crush(self):
        for db_p in (
            self.home / ".local" / "share" / "crush" / "crush.db",
            self.home / "AppData" / "Local" / "crush" / "crush.db",
        ):
            if not db_p.exists():
                continue
            conn, tmp = open_sqlite_ro(db_p)
            if not conn:
                continue
            try:
                tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                if "sessions" in tables:
                    cols = {r[1] for r in conn.execute("PRAGMA table_info(sessions)")}
                    if "prompt_tokens" in cols and "completion_tokens" in cols:
                        c_col = "cost" if "cost" in cols else "0"
                        t_col = "created_at" if "created_at" in cols else "NULL"
                        for pt, ct, cost, ts in conn.execute(f"SELECT prompt_tokens, completion_tokens, {c_col}, {t_col} FROM sessions WHERE prompt_tokens + completion_tokens > 0"):
                            self.add_ev("Charm Crush", "claude-sonnet-4-6", to_local_date(ts, time.time()), safe_int(pt), safe_int(ct), 0, 0, safe_float(cost))
            except Exception:
                pass
            finally:
                close_sqlite(conn, tmp)

    def scan_aider(self):
        candidates = [self.home / ".aider" / "analytics.jsonl", self.home / ".aider.analytics.jsonl"]
        for fp in candidates:
            if not fp.exists():
                continue
            mtime = safe_mtime(fp)
            try:
                with safe_open_text(fp) as f:
                    for line in f:
                        if "prompt_tokens" not in line and "tokens" not in line:
                            continue
                        try:
                            obj = json.loads(line)
                        except Exception:
                            continue
                        props = obj.get("properties") if isinstance(obj.get("properties"), dict) else obj
                        fi = safe_int(props.get("prompt_tokens") or props.get("sent_tokens"))
                        out_tok = safe_int(props.get("completion_tokens") or props.get("received_tokens"))
                        if fi + out_tok > 0:
                            mdl = normalize_model(props.get("main_model") or props.get("model"), "claude-sonnet-4-6")
                            self.add_ev("Aider", mdl, to_local_date(obj.get("time") or obj.get("timestamp"), mtime), fi, out_tok, 0, 0, safe_float(props.get("cost") or props.get("message_cost")))
            except Exception:
                pass

    def scan_continue(self):
        for sf in safe_walk_files(self.home / ".continue" / "sessions", suffix=".json"):
            mtime = safe_mtime(sf)
            try:
                data = json.loads(safe_read_text(sf))
                for h in (data.get("history") if isinstance(data, dict) else []) or []:
                    if not isinstance(h, dict):
                        continue
                    for pl in (h.get("promptLogs") or []):
                        p = extract_generic_usage(pl, "claude-sonnet-4-6", mtime)
                        if p:
                            self.add_ev("Continue.dev", *p[:7])
            except Exception:
                pass

    def scan_vibe_and_agents(self):
        self.scan_jsonl_tree("Mistral Vibe", [self.home / ".vibe" / "logs" / "session"], "devstral")
        self.scan_jsonl_tree("OpenHands", [self.home / ".openhands" / "sessions"], "claude-sonnet-4-6")
        self.scan_jsonl_tree("Hermes Agent", [self.home / ".hermes" / "sessions"], "claude-sonnet-4-6")

    def scan_cursor_native(self):
        for db_p in (
            self.home / "AppData" / "Roaming" / "Cursor" / "User" / "globalStorage" / "state.vscdb",
            self.home / ".config" / "Cursor" / "User" / "globalStorage" / "state.vscdb",
            self.home / "Library" / "Application Support" / "Cursor" / "User" / "globalStorage" / "state.vscdb",
        ):
            if not db_p.exists():
                continue
            conn, tmp = open_sqlite_ro(db_p)
            if not conn:
                continue
            db_mtime = safe_mtime(db_p)
            try:
                tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                if "cursorDiskKV" in tables:
                    for (val,) in conn.execute("SELECT value FROM cursorDiskKV WHERE key LIKE 'bubbleId:%' AND value LIKE '%tokenCount%'"):
                        if not val:
                            continue
                        try:
                            d = json.loads(val)
                            tc = d.get("tokenCount")
                            if isinstance(tc, dict):
                                fi = safe_int(tc.get("inputTokens"))
                                out_tok = safe_int(tc.get("outputTokens"))
                                if fi + out_tok > 0:
                                    mdl = normalize_model((d.get("modelInfo") or {}).get("modelName") or d.get("model"), "claude-sonnet-4-6")
                                    self.add_ev("Cursor", mdl, to_local_date(d.get("createdAt"), db_mtime), fi, out_tok, 0, 0)
                        except Exception:
                            pass
            except Exception:
                pass
            finally:
                close_sqlite(conn, tmp)

    def scan_zed(self):
        for db_p in (
            self.home / ".local" / "share" / "zed" / "threads" / "threads.db",
            self.home / "AppData" / "Local" / "Zed" / "threads" / "threads.db",
        ):
            if not db_p.exists():
                continue
            conn, tmp = open_sqlite_ro(db_p)
            if not conn:
                continue
            try:
                tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                if "threads" in tables:
                    cols = {r[1] for r in conn.execute("PRAGMA table_info(threads)")}
                    if "summary" in cols and "updated_at" in cols and "data" in cols:
                        for upd, blob in conn.execute("SELECT updated_at, data FROM threads"):
                            if not blob:
                                continue
                            try:
                                text = blob.decode("utf-8", errors="ignore") if isinstance(blob, bytes) else str(blob)
                                if "token_usage" not in text and "input_tokens" not in text:
                                    continue
                                d = json.loads(text)
                                u = d.get("cumulative_token_usage") or d.get("token_usage")
                                if isinstance(u, dict):
                                    fi = safe_int(u.get("input_tokens"))
                                    out_tok = safe_int(u.get("output_tokens"))
                                    cr = safe_int(u.get("cache_read_input_tokens"))
                                    cw = safe_int(u.get("cache_creation_input_tokens"))
                                    mdl = normalize_model((d.get("model") or {}).get("name") if isinstance(d.get("model"), dict) else d.get("model"), "claude-sonnet-4-6")
                                    self.add_ev("Zed AI", mdl, to_local_date(upd, time.time()), fi, out_tok, cr, cw)
                            except Exception:
                                pass
            except Exception:
                pass
            finally:
                close_sqlite(conn, tmp)

    def scan_custom_rules(self):
        cfg = self.home / ".toklens" / "harnesses.json"
        if not cfg.exists():
            return
        try:
            rules = json.loads(cfg.read_text(encoding="utf-8"))
            for r in (rules if isinstance(rules, list) else []):
                name = str(r.get("name") or "Custom Harness")
                folder = Path(os.path.expanduser(str(r.get("dir") or "")))
                def_m = str(r.get("default_model") or "claude-sonnet-4-6")
                if folder.exists():
                    self.scan_jsonl_tree(name, [folder], def_m, suffix=str(r.get("suffix") or ".jsonl"))
        except Exception:
            pass

    def scan_codex_sqlite_archive(self):
        st_db = self.home / ".codex" / "state_5.sqlite"
        if st_db.exists():
            conn, tmp = open_sqlite_ro(st_db)
            if conn:
                try:
                    for tid, mdl, tot, cat in conn.execute("SELECT id, model, tokens_used, created_at FROM threads WHERE tokens_used > 0"):
                        if str(tid) in self.codex_live:
                            continue
                        tot = safe_int(tot)
                        if tot <= 0:
                            continue
                        cr = int(tot * 0.963)
                        out_tok = max(1, int(tot * 0.004))
                        fi = max(0, tot - cr - out_tok)
                        self.add_ev("OpenAI Codex", normalize_model(str(mdl or ""), "gpt-6-astra"), to_local_date(cat, time.time()), fi, out_tok, cr, 0)
                except Exception:
                    pass
                finally:
                    close_sqlite(conn, tmp)

    def build_records(self):
        buckets = {}
        for tool, model, dt, fi, out_tok, cr, cw, logged_cost, turns in self.events:
            inp_r, out_r, cr_r, cw_r = self.pricing.lookup(model)
            calc = (fi * inp_r + out_tok * out_r + cr * cr_r + cw * cw_r) / 1e6
            base = ((fi + cr + cw) * inp_r + out_tok * out_r) / 1e6
            actual = logged_cost if logged_cost > 0 else calc
            baseline = max(base, actual)
            rate_lbl = f"${inp_r:g} / ${out_r:g}"
            k = (dt, tool, model)
            if k not in buckets:
                buckets[k] = [dt, tool, model, 0, 0, 0, 0, 0, 0.0, 0.0, rate_lbl]
            b = buckets[k]
            b[3] += turns
            b[4] += fi
            b[5] += cr
            b[6] += cw
            b[7] += out_tok
            b[8] += actual
            b[9] += baseline
        rows = sorted(buckets.values(), key=lambda x: (x[0], x[1], x[2]))
        for r in rows:
            r[8] = round(r[8], 6)
            r[9] = round(r[9], 6)
        return rows


DAILY_ARCHIVE_DDL = (
    "CREATE TABLE IF NOT EXISTS daily_archive ("
    "rec_date TEXT NOT NULL, "
    "tool TEXT NOT NULL, "
    "model TEXT NOT NULL, "
    "turns INTEGER NOT NULL, "
    "fi INTEGER NOT NULL, "
    "cr INTEGER NOT NULL, "
    "cw INTEGER NOT NULL, "
    "out_tok INTEGER NOT NULL, "
    "cost_usd REAL NOT NULL, "
    "base_usd REAL NOT NULL, "
    "rate_label TEXT, "
    "PRIMARY KEY(rec_date, tool, model))"
)

_SNAPSHOT_HOMES = {}


def get_ledger_db_path(home: Path) -> Path:
    base = Path(home).expanduser() if home is not None else Path.home()
    return base / ".toklens" / "ledger.db"


def normalize_record_row(row):
    if not isinstance(row, (list, tuple)) or len(row) < 10:
        return None
    dt = str(row[0] or "").strip()
    tool = str(row[1] or "").strip()
    model = str(row[2] or "").strip()
    if not dt or not tool or not model:
        return None
    turns = max(0, safe_int(row[3]))
    fi = max(0, safe_int(row[4]))
    cr = max(0, safe_int(row[5]))
    cw = max(0, safe_int(row[6]))
    out_tok = max(0, safe_int(row[7]))
    cost_usd = round(max(0.0, safe_float(row[8])), 6)
    base_usd = round(max(0.0, safe_float(row[9])), 6)
    rate_lbl = str(row[10] or "") if len(row) > 10 and row[10] is not None else ""
    return [dt, tool, model, turns, fi, cr, cw, out_tok, cost_usd, base_usd, rate_lbl]


def _collapse_record_rows(rows) -> dict:
    bucket_map = {}
    for raw in (rows or []):
        r = normalize_record_row(raw)
        if not r:
            continue
        k = (r[0], r[1], r[2])
        if k in bucket_map:
            ex = bucket_map[k]
            ex[3] += r[3]
            ex[4] += r[4]
            ex[5] += r[5]
            ex[6] += r[6]
            ex[7] += r[7]
            ex[8] = round(ex[8] + r[8], 6)
            ex[9] = round(ex[9] + r[9], 6)
            if not ex[10] and r[10]:
                ex[10] = r[10]
        else:
            bucket_map[k] = list(r)
    return bucket_map


def merge_records_high_water(primary_rows, historical_rows) -> list:
    p_map = _collapse_record_rows(primary_rows)
    h_map = _collapse_record_rows(historical_rows)
    merged = []
    for k in set(p_map.keys()) | set(h_map.keys()):
        p = p_map.get(k)
        h = h_map.get(k)
        if p is not None and h is not None:
            merged.append([
                k[0],
                k[1],
                k[2],
                max(p[3], h[3]),
                max(p[4], h[4]),
                max(p[5], h[5]),
                max(p[6], h[6]),
                max(p[7], h[7]),
                round(max(p[8], h[8]), 6),
                round(max(p[9], h[9]), 6),
                p[10] if p[10] else h[10],
            ])
        elif p is not None:
            merged.append(p)
        else:
            merged.append(h)
    merged.sort(key=lambda x: (x[0], x[1], x[2]))
    return merged


def merge_with_ledger_db(home: Path, current_rows: list) -> list:
    db_path = get_ledger_db_path(home)
    conn = None
    try:
        db_path.parent.mkdir(parents=True, exist_ok=True)
        try:
            conn = sqlite3.connect(str(db_path), timeout=10.0)
        except Exception:
            conn = sqlite3.connect(os_path_str(db_path), timeout=10.0)
        conn.execute(DAILY_ARCHIVE_DDL)
        archived_rows = [
            list(r)
            for r in conn.execute(
                "SELECT rec_date, tool, model, turns, fi, cr, cw, out_tok, cost_usd, base_usd, rate_label FROM daily_archive"
            )
        ]
        merged = merge_records_high_water(current_rows, archived_rows)
        if merged:
            conn.executemany(
                "INSERT OR REPLACE INTO daily_archive "
                "(rec_date, tool, model, turns, fi, cr, cw, out_tok, cost_usd, base_usd, rate_label) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                merged,
            )
        conn.commit()
        if os.name != "nt":
            try:
                os.chmod(db_path, 0o600)
            except Exception:
                pass
        return merged
    except Exception:
        return merge_records_high_water(current_rows, [])
    finally:
        if conn is not None:
            try:
                conn.close()
            except Exception:
                pass


def scan_all(home_dir=None) -> list:
    target = Path(home_dir).expanduser() if home_dir is not None else Path.home()
    return MultiHarnessScanner(target).scan_all()


def build_snapshot(home: Path, machine_id: str, machine_name: str, machine_type: str = "vps") -> dict:
    resolved_home = Path(home).expanduser()
    scanner = MultiHarnessScanner(resolved_home)
    rows = scanner.scan_all()
    snap = {
        "schema": SCHEMA_VERSION,
        "machine_id": sanitize_machine_id(machine_id),
        "machine_name": machine_name or socket.gethostname(),
        "machine_type": machine_type,
        "os": f"{platform.system()} {platform.release()} ({platform.machine()})",
        "synced_at": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "records": rows,
    }
    if len(_SNAPSHOT_HOMES) > 64:
        _SNAPSHOT_HOMES.clear()
    _SNAPSHOT_HOMES[id(snap)] = resolved_home
    return snap


def sync_gist(snapshot: dict, gist_id: str, token: str, home: Path = None) -> dict:
    mid = sanitize_machine_id(snapshot.get("machine_id") or "vps-node")
    snapshot["machine_id"] = mid
    fname = f"toklens_node_{mid}.json"
    gid = gist_id.strip()
    tok = token.strip()
    base_headers = {
        "Authorization": f"Bearer {tok}",
        "Accept": "application/vnd.github+json",
        "User-Agent": "TokLens-Agent/2.1",
    }

    # 1. 上传前先拉取远端 Gist，若存在本节点历史快照则执行高水位合并并回填本地 ledger.db
    try:
        get_req = urllib.request.Request(f"https://api.github.com/gists/{gid}", method="GET", headers=base_headers)
        with urllib.request.urlopen(get_req, timeout=20) as g_resp:
            gist_obj = json.loads(g_resp.read().decode("utf-8", errors="ignore"))
        files_map = gist_obj.get("files") if isinstance(gist_obj, dict) else None
        if isinstance(files_map, dict):
            fmeta = None
            for k, v in files_map.items():
                if str(k).lower() == fname.lower() and isinstance(v, dict):
                    fmeta = v
                    break
            if fmeta is not None:
                raw_content = fmeta.get("content")
                if (fmeta.get("truncated") or not raw_content) and fmeta.get("raw_url"):
                    rurl = str(fmeta["raw_url"])
                    rheaders = {"User-Agent": "TokLens-Agent/2.1"} if "githubusercontent.com" in rurl.lower() else base_headers
                    raw_req = urllib.request.Request(rurl, method="GET", headers=rheaders)
                    with urllib.request.urlopen(raw_req, timeout=20) as r_resp:
                        raw_content = r_resp.read().decode("utf-8", errors="ignore")
                if raw_content:
                    remote_snap = json.loads(raw_content)
                    if isinstance(remote_snap, dict) and isinstance(remote_snap.get("records"), list):
                        merged_rows = merge_records_high_water(snapshot.get("records") or [], remote_snap["records"])
                        target_home = Path(home).expanduser() if home is not None else _SNAPSHOT_HOMES.get(id(snapshot), Path.home())
                        merged_rows = merge_with_ledger_db(target_home, merged_rows)
                        snapshot["records"] = merged_rows
                        snapshot["synced_at"] = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    except urllib.error.HTTPError as e:
        if e.code in (401, 403, 404):
            raise
    except Exception:
        pass

    # 2. 上传合并后的完整历史快照
    body = json.dumps({"files": {fname: {"content": json.dumps(snapshot, ensure_ascii=False)}}}, ensure_ascii=False).encode("utf-8")
    patch_headers = dict(base_headers)
    patch_headers["Content-Type"] = "application/json; charset=utf-8"
    req = urllib.request.Request(
        f"https://api.github.com/gists/{gid}",
        data=body,
        method="PATCH",
        headers=patch_headers,
    )
    with urllib.request.urlopen(req, timeout=20) as resp:
        if resp.status >= 300:
            raise RuntimeError(f"GitHub Gist HTTP {resp.status}")
    return snapshot


def upload_gist(snapshot: dict, gist_id: str, token: str, home: Path = None) -> dict:
    return sync_gist(snapshot, gist_id, token, home=home)


def sync_webdav(snapshot: dict, webdav_url: str, user: str, password: str, home: Path = None) -> dict:
    mid = sanitize_machine_id(snapshot.get("machine_id") or "vps-node")
    snapshot["machine_id"] = mid
    base = webdav_url.strip().rstrip("/") + "/"
    fname = f"toklens_node_{mid}.json"
    file_url = base + urllib.parse.quote(fname)
    auth = base64.b64encode(f"{user}:{password}".encode("utf-8")).decode("ascii")

    # 尝试创建目录（已存在时忽略）
    try:
        mk_req = urllib.request.Request(
            base,
            method="MKCOL",
            headers={"Authorization": f"Basic {auth}", "User-Agent": "TokLens-Agent/2.1"},
        )
        with urllib.request.urlopen(mk_req, timeout=15):
            pass
    except Exception:
        pass

    # 1. 上传前先拉取云端同名旧节点 JSON，若存在则做高水位合并并回填本地 ledger.db
    try:
        get_req = urllib.request.Request(
            file_url,
            method="GET",
            headers={"Authorization": f"Basic {auth}", "User-Agent": "TokLens-Agent/2.1"},
        )
        with urllib.request.urlopen(get_req, timeout=20) as g_resp:
            if g_resp.status < 300:
                raw_content = g_resp.read().decode("utf-8", errors="ignore")
                if raw_content:
                    remote_snap = json.loads(raw_content)
                    if isinstance(remote_snap, dict) and isinstance(remote_snap.get("records"), list):
                        merged_rows = merge_records_high_water(snapshot.get("records") or [], remote_snap["records"])
                        target_home = Path(home).expanduser() if home is not None else _SNAPSHOT_HOMES.get(id(snapshot), Path.home())
                        merged_rows = merge_with_ledger_db(target_home, merged_rows)
                        snapshot["records"] = merged_rows
                        snapshot["synced_at"] = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    except urllib.error.HTTPError as e:
        if e.code in (401, 403):
            raise
    except Exception:
        pass

    # 2. PUT 上传合并后的完整历史快照
    data = json.dumps(snapshot, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        file_url,
        data=data,
        method="PUT",
        headers={
            "Authorization": f"Basic {auth}",
            "Content-Type": "application/json; charset=utf-8",
            "User-Agent": "TokLens-Agent/2.1",
        },
    )
    with urllib.request.urlopen(req, timeout=20) as resp:
        if resp.status >= 300:
            raise RuntimeError(f"WebDAV HTTP {resp.status}")
    return snapshot


def upload_webdav(snapshot: dict, webdav_url: str, user: str, password: str, home: Path = None) -> dict:
    return sync_webdav(snapshot, webdav_url, user, password, home=home)


def install_cron(script_path: Path, interval_min: int):
    marker = "# toklens-auto-sync"
    cmd = f"*/{max(5, min(59, interval_min))} * * * * {sys.executable} {script_path.resolve()} --sync >/dev/null 2>&1 {marker}"
    try:
        cur = subprocess.run(["crontab", "-l"], capture_output=True, text=True)
        lines = [ln for ln in (cur.stdout or "").splitlines() if marker not in ln and ln.strip()]
    except Exception:
        lines = []
    lines.append(cmd)
    new_cron = "\n".join(lines) + "\n"
    proc = subprocess.run(["crontab", "-"], input=new_cron, text=True, capture_output=True)
    if proc.returncode != 0:
        raise RuntimeError(proc.stderr or "crontab 写入失败")


def main():
    ap = argparse.ArgumentParser(description="知微 · TokLens 跨平台 VPS 探针")
    ap.add_argument("--home", default=str(Path.home()), help="要扫描的用户主目录")
    ap.add_argument("--name", default=None, help="设备显示名称（如：香港主力 VPS）")
    ap.add_argument("--id", default=None, help="设备唯一 ID（默认根据主机名生成）")
    ap.add_argument("--pair", default=None, help="导入桌面端生成的 toklens1:... 配对码")
    ap.add_argument("--sync", action="store_true", help="扫描并立即上传同步到云端")
    ap.add_argument("--export", default=None, help="将节点快照导出到本地 JSON 文件")
    ap.add_argument("--install-cron", type=int, default=0, help="安装 Linux crontab 定时同步（分钟间隔，如 30）")
    args = ap.parse_args()

    home = Path(args.home).expanduser()
    cfg_dir = home / ".toklens"
    cfg_file = cfg_dir / "config.json"
    default_cfg_file = Path.home() / ".toklens" / "config.json"

    cfg = {}
    if cfg_file.exists():
        try:
            cfg = json.loads(cfg_file.read_text(encoding="utf-8"))
        except Exception:
            cfg = {}
    elif default_cfg_file.exists() and not (args.pair or args.name or args.id):
        try:
            cfg = json.loads(default_cfg_file.read_text(encoding="utf-8"))
        except Exception:
            cfg = {}

    if args.pair:
        raw_p = args.pair.strip()
        if raw_p.startswith("toklens1:"):
            raw_p = raw_p[9:]
        decoded = json.loads(base64.b64decode(raw_p.encode("ascii")).decode("utf-8"))
        cfg.update(decoded)

    if args.name:
        cfg["machine_name"] = args.name
    if args.id:
        cfg["machine_id"] = sanitize_machine_id(args.id)

    if "machine_id" not in cfg or not cfg["machine_id"]:
        cfg["machine_id"] = sanitize_machine_id("vps-" + socket.gethostname())
    if "machine_name" not in cfg or not cfg["machine_name"]:
        cfg["machine_name"] = socket.gethostname()

    if args.pair or args.name or args.id:
        cfg_dir.mkdir(parents=True, exist_ok=True)
        cfg_file.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
        try:
            os.chmod(cfg_file, 0o600)
        except Exception:
            pass

    mtype = "windows" if os.name == "nt" else "vps"
    snap = build_snapshot(home, cfg["machine_id"], cfg["machine_name"], mtype)

    if args.export and not args.sync:
        Path(args.export).write_text(json.dumps(snap, ensure_ascii=False, indent=2), encoding="utf-8")

    sync_done_msg = None
    if args.sync:
        prov = cfg.get("provider", "gist")
        if prov == "gist":
            if not cfg.get("gist_id") or not cfg.get("gist_token"):
                print("[错误] 未配置 GitHub Gist 同步凭据，请使用 --pair 导入配对码。", file=sys.stderr)
                sys.exit(2)
            snap = sync_gist(snap, cfg["gist_id"], cfg["gist_token"], home=home)
            sync_done_msg = f"[TokLens] 已成功同步至私有 GitHub Gist ({cfg['gist_id'][:8]}...)"
        elif prov == "webdav":
            if not cfg.get("webdav_url"):
                print("[错误] 未配置 WebDAV 地址，请使用 --pair 导入配对码。", file=sys.stderr)
                sys.exit(2)
            snap = sync_webdav(snap, cfg["webdav_url"], cfg.get("webdav_user", ""), cfg.get("webdav_pass", ""), home=home)
            sync_done_msg = f"[TokLens] 已成功同步至 WebDAV ({cfg['webdav_url']})"

    tot_tok = sum(r[4] + r[5] + r[6] + r[7] for r in snap["records"])
    tot_usd = sum(r[8] for r in snap["records"])
    tools = sorted({r[1] for r in snap["records"]})
    print(f"[TokLens] 节点: {snap['machine_name']} ({snap['machine_id']}) | 发现工具({len(tools)}): {', '.join(tools) or '暂无'} | 总Token: {tot_tok:,} | 等效花费: ${tot_usd:.2f}")

    if args.export:
        Path(args.export).write_text(json.dumps(snap, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"[TokLens] 已导出快照至: {args.export}")

    if sync_done_msg:
        print(sync_done_msg)

    if args.install_cron and args.install_cron > 0 and os.name != "nt":
        install_cron(Path(__file__), args.install_cron)
        print(f"[TokLens] 已写入 crontab：每 {args.install_cron} 分钟自动静默同步一次。")


if __name__ == "__main__":
    main()
