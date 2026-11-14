#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
知微 · TokLens v2.2 — Linux VPS / 跨平台零依赖采集与同步探针 (toklens_agent.py)
纯 Python 3 标准库实现（无需 pip 安装任何依赖，兼容 Python 3.7+ 至 3.13），
支持文件级增量字节游标、SQLite WAL 高水位归档、单实例互斥锁、26+ 种 AI Agent Harness
（含 Linux VS Code / Cursor / Windsurf / Antigravity / Trae Remote SSH 与 code-server），
生成不含任何隐私对话原文的脱敏统计快照，并通过 GitHub 私有 Gist 或 WebDAV 可靠同步。
"""

import argparse
import base64
import datetime
import fnmatch
import gzip
import json
import os
import platform
import re
import shlex
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

try:
    import fcntl
except ImportError:
    fcntl = None

AGENT_VERSION = "2.2.1"
SCHEMA_VERSION = "toklens.node.v2"
SCANNER_REV = 29
RECONCILED_REVISION = 28
CURSOR_SCHEMA_VERSION = "toklens.cursor.v2.9"
CRON_MARKER = "# toklens-auto-sync"
MAX_SQLITE_TEMP_COPY_BYTES = 128 * 1024 * 1024  # 128 MB 保护阈值，防止打满 VPS /tmp tmpfs
LOG_MAX_BYTES = 131072  # 128 KB 触发滚动
LOG_KEEP_BYTES = 65536  # 保留尾部 64 KB
PRICING_CACHE_TTL_SEC = 86400
PRICING_FAIL_COOLDOWN_SEC = 7200  # 受限网络全源失败后冷却 2 小时，避免每次 cron 阻塞

# 1. 基础公开模型百万 Token 美元单价: (input, output, cache_read, cache_write) —— 与桌面端 InitBuiltin 100% 对齐
BUILTIN_RATES = {
    # Anthropic Claude
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
    # OpenAI / Codex
    "gpt-5.4": (2.5, 15.0, 0.25, 0.0),
    "gpt-5.4-mini": (0.4, 1.6, 0.04, 0.0),
    "gpt-5.4-nano": (0.1, 0.4, 0.01, 0.0),
    "gpt-5.3-codex": (1.75, 14.0, 0.175, 0.0),
    "gpt-5.2-codex": (1.75, 14.0, 0.175, 0.0),
    "gpt-5.2": (1.75, 14.0, 0.175, 0.0),
    "gpt-5.1-codex": (1.25, 10.0, 0.125, 0.0),
    "gpt-5.1": (1.25, 10.0, 0.125, 0.0),
    "gpt-5-codex": (1.25, 10.0, 0.125, 0.0),
    "gpt-5": (1.25, 10.0, 0.125, 0.0),
    "gpt-5-mini": (0.25, 2.0, 0.025, 0.0),
    "gpt-5-nano": (0.05, 0.4, 0.005, 0.0),
    "gpt-4.1": (2.0, 8.0, 0.5, 0.0),
    "gpt-4.1-mini": (0.4, 1.6, 0.1, 0.0),
    "gpt-4.1-nano": (0.1, 0.4, 0.025, 0.0),
    "gpt-4o": (2.5, 10.0, 1.25, 0.0),
    "gpt-4o-mini": (0.15, 0.6, 0.075, 0.0),
    "codex-mini": (1.5, 6.0, 0.375, 0.0),
    "o3-pro": (20.0, 80.0, 0.0, 0.0),
    "o3": (2.0, 8.0, 0.5, 0.0),
    "o3-mini": (1.1, 4.4, 0.55, 0.0),
    "o4-mini": (1.1, 4.4, 0.275, 0.0),
    "o1": (15.0, 60.0, 7.5, 0.0),
    # Google Gemini
    "gemini-3.1-pro": (2.0, 12.0, 0.2, 0.0),
    "gemini-3-pro": (2.0, 12.0, 0.2, 0.0),
    "gemini-3-flash": (0.5, 3.0, 0.05, 0.0),
    "gemini-2.5-pro": (1.25, 10.0, 0.125, 0.0),
    "gemini-2.5-flash": (0.3, 2.5, 0.03, 0.0),
    "gemini-2.5-flash-lite": (0.1, 0.4, 0.025, 0.0),
    "gemini-2.0-flash": (0.1, 0.4, 0.025, 0.0),
    "gemini-2.0-flash-lite": (0.075, 0.3, 0.01875, 0.0),
    # Zhipu GLM / Kimi / DeepSeek / Qwen / MiniMax / Mistral / Grok
    "glm-5": (1.0, 3.2, 0.2, 0.0),
    "glm-4.7": (0.6, 2.2, 0.11, 0.0),
    "glm-4.6": (0.6, 2.2, 0.11, 0.0),
    "glm-4.5": (0.6, 2.2, 0.11, 0.0),
    "glm-4.5-air": (0.2, 1.1, 0.03, 0.0),
    "glm-4.5-flash": (0.07, 0.28, 0.014, 0.0),
    "kimi-k2.7-code": (0.95, 4.0, 0.19, 0.0),
    "kimi-k2.6": (0.95, 4.0, 0.16, 0.0),
    "kimi-k2.5": (0.6, 3.0, 0.1, 0.0),
    "kimi-k2-thinking": (0.6, 2.5, 0.15, 0.0),
    "kimi-k2-0905-preview": (0.6, 2.5, 0.15, 0.0),
    "kimi-k2-turbo-preview": (1.15, 8.0, 0.15, 0.0),
    "kimi-k2": (0.6, 2.5, 0.15, 0.0),
    "moonshot-v1-128k": (2.0, 5.0, 0.2, 0.0),
    "moonshot-v1-32k": (1.0, 3.0, 0.1, 0.0),
    "moonshot-v1-8k": (0.2, 2.0, 0.02, 0.0),
    "doubao-seed-code": (0.17, 1.1, 0.034, 0.0),
    "deepseek-v3.2": (0.28, 0.42, 0.028, 0.0),
    "deepseek-v3.1": (0.55, 1.67, 0.055, 0.0),
    "deepseek-v3": (0.27, 1.1, 0.07, 0.0),
    "deepseek-chat": (0.27, 1.1, 0.07, 0.0),
    "deepseek-r1": (0.55, 2.19, 0.14, 0.0),
    "deepseek-reasoner": (0.55, 2.19, 0.14, 0.0),
    "qwen3-coder-plus": (1.0, 5.0, 0.1, 0.0),
    "qwen3-coder": (0.3, 1.2, 0.03, 0.0),
    "qwen3-max": (1.2, 6.0, 0.12, 0.0),
    "qwen3.5-plus": (0.4, 1.2, 0.04, 0.0),
    "qwen-max": (1.6, 6.4, 0.16, 0.0),
    "qwen-plus": (0.4, 1.2, 0.04, 0.0),
    "minimax-m2.5": (0.3, 1.2, 0.03, 0.0),
    "minimax-m2.1": (0.3, 1.2, 0.03, 0.0),
    "minimax-m1": (0.4, 2.2, 0.04, 0.0),
    "codestral": (0.3, 0.9, 0.03, 0.0),
    "devstral": (0.1, 0.3, 0.01, 0.0),
    "mistral-large": (2.0, 6.0, 0.2, 0.0),
    "grok-4": (3.0, 15.0, 0.75, 0.0),
    "grok-3": (3.0, 15.0, 0.75, 0.0),
    "grok-code-fast": (0.2, 1.5, 0.02, 0.0),
}

# 2. 内部/次世代/Harness 专属模型单价覆写（在云端注册表之后加载，与桌面端 InitInternalModelOverrides 对齐）
INTERNAL_MODEL_OVERRIDES = {
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
    "gpt-6-astra": (10.0, 50.0, 1.0, 12.5),
    "gpt-6.1-sol": (2.0, 10.0, 0.1, 2.5),
    "gpt-6-sol": (2.0, 10.0, 0.2, 2.5),
    "gpt-6.1-terra": (1.0, 5.0, 0.05, 1.25),
    "gpt-6-luna": (0.1, 0.5, 0.01, 0.125),
    "gemini-4-carbon": (2.0, 10.0, 0.1, 0.0),
    "gemini-4-neon": (0.4, 2.0, 0.04, 0.0),
    "gemini-4-argon": (2.0, 10.0, 0.1, 0.0),
    "gemini-4-hydrogen": (1.5, 7.5, 0.075, 0.0),
    "gemini-pro-orchestrator": (2.0, 10.0, 0.1, 0.0),
    "orchestrator-carbon": (2.0, 10.0, 0.1, 0.0),
    "orchestrator": (2.0, 10.0, 0.1, 0.0),
    "gemini-flash-worker-agy": (0.75, 3.75, 0.075, 0.0),
    "gemini-3.8-flash": (0.75, 3.75, 0.075, 0.0),
    "gemini-3.8-flash-tiered": (0.4, 2.0, 0.04, 0.0),
    "gemini-3.8-flash-low": (0.2, 1.0, 0.02, 0.0),
    "gemini-3.5-flash-lite": (0.1, 0.4, 0.025, 0.0),
    "gemini-3.1-flash": (0.3, 1.5, 0.03, 0.0),
    "gemini-3.1-pro": (1.5, 8.0, 0.15, 0.0),
    "qwen3.5-coder": (0.4, 1.6, 0.08, 0.0),
    "qwen3.5-plus": (0.4, 1.2, 0.08, 0.0),
    "glm-5.3": (1.4, 4.4, 0.28, 0.0),
    "glm-5.3-flash": (0.1, 0.4, 0.02, 0.0),
    "glm-5.2": (1.2, 4.0, 0.24, 0.0),
    "glm-5.1": (1.0, 3.2, 0.2, 0.0),
    "kimi-for-coding": (0.95, 4.0, 0.14, 0.0),
    "k3": (1.2, 5.0, 0.18, 0.0),
    "k3-256k": (1.2, 5.0, 0.18, 0.0),
    "kimi-k3.1-pro-highspeed": (4.0, 20.0, 0.2, 4.0),
    "kimi-k3.1-pro": (2.0, 10.0, 0.2, 2.0),
    "kimi-k3": (1.2, 5.0, 0.18, 0.0),
    "deepseek-v4.1-flash": (0.1, 0.4, 0.01, 0.0),
    "deepseek-v4-pro": (0.66, 1.98, 0.022, 0.0),
    "deepseek-v4-flash": (0.1, 0.4, 0.01, 0.0),
    "deepseek-v4": (0.3, 1.2, 0.03, 0.0),
    "deepseek-flash": (0.07, 0.28, 0.007, 0.0),
    "workbuddy-default": (0.6, 2.4, 0.06, 0.0),
}

UUID_RE = re.compile(r"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})", re.I)
AGY_MODEL_RE = re.compile(r"<USER_SETTINGS_CHANGE>[\s\S]*?`Model Selection`\s+from\s+.+?\s+to\s+([^\r\n.]+)")
CLAUDE_DOT_VER_RE = re.compile(r"^(claude-(?:[a-z]+-)*\d+)\.(\d+)(?=$|[-._])")
ISO_TS_RE = re.compile(
    r"^(\d{4})-(\d{2})-(\d{2})[T\s](\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:?\d{2})?$",
    re.I,
)
NUM_STR_RE = re.compile(r"^\d+(?:\.\d+)?$")

TZ_NAME_OFFSETS = {
    "utc": 0,
    "gmt": 0,
    "etc/utc": 0,
    "asia/shanghai": 480,
    "asia/chongqing": 480,
    "asia/hong_kong": 480,
    "asia/taipei": 480,
    "asia/singapore": 480,
    "prc": 480,
    "cst-8": 480,
    "asia/tokyo": 540,
    "asia/seoul": 540,
    "europe/london": 0,
    "america/los_angeles": -480,
    "america/new_york": -300,
}


class SnapshotDict(dict):
    """纯字典子类：JSON 序列化完全透明，同时安全携带本地 home 引用避免 id(snap) 地址复用 Bug"""
    __slots__ = ("_local_home",)

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._local_home = None


def os_path_str(p: Path) -> str:
    s = str(p)
    if os.name == "nt" and len(s) >= 245 and not s.startswith("\\\\?\\"):
        s = os.path.abspath(s)
        if s.startswith("\\\\"):
            return "\\\\?\\UNC\\" + s[2:]
        return "\\\\?\\" + s
    return s


def ensure_toklens_dir(home: Path) -> Path:
    d = Path(home).expanduser() / ".toklens"
    d.mkdir(parents=True, exist_ok=True)
    if os.name != "nt":
        try:
            os.chmod(str(d), 0o700)
        except Exception:
            pass
    return d


def atomic_write_text(target: Path, content: str, mode: int = 0o600):
    parent = target.parent
    parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_name = tempfile.mkstemp(prefix=f".{target.name}.", suffix=".tmp", dir=os_path_str(parent))
    try:
        if os.name != "nt" and mode is not None:
            try:
                os.chmod(tmp_name, mode)
            except Exception:
                pass
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(content)
            f.flush()
            try:
                os.fsync(f.fileno())
            except Exception:
                pass
        os.replace(tmp_name, os_path_str(target))
        if os.name != "nt" and mode is not None:
            try:
                os.chmod(os_path_str(target), mode)
            except Exception:
                pass
    except Exception:
        try:
            if os.path.exists(tmp_name):
                os.unlink(tmp_name)
        except Exception:
            pass
        raise


def append_agent_log(home: Path, level: str, msg: str):
    try:
        cfg_dir = ensure_toklens_dir(home)
        log_fp = cfg_dir / "agent.log"
        ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        line = f"[{ts}] [{level}] {msg.strip()}\n".encode("utf-8", errors="replace")
        p_str = os_path_str(log_fp)
        if os.path.exists(p_str):
            try:
                sz = os.path.getsize(p_str)
                if sz > LOG_MAX_BYTES:
                    with open(p_str, "rb") as rf:
                        rf.seek(max(0, sz - LOG_KEEP_BYTES))
                        tail = rf.read()
                    nl = tail.find(b"\n")
                    if nl >= 0:
                        tail = tail[nl + 1:]
                    atomic_write_text(log_fp, tail.decode("utf-8", errors="replace"), mode=0o600)
            except Exception:
                pass
        flags = os.O_WRONLY | os.O_CREAT | os.O_APPEND
        fd = os.open(p_str, flags, 0o600)
        with os.fdopen(fd, "ab") as af:
            af.write(line)
    except Exception:
        pass


class SingleInstanceLock:
    """跨平台非阻塞单实例互斥锁（Linux/macOS 使用 fcntl.flock，Windows 安全放行）"""

    def __init__(self, home: Path):
        self.home = Path(home).expanduser()
        self.lock_fp = self.home / ".toklens" / "agent.lock"
        self._fh = None
        self.acquired = False

    def __enter__(self):
        if fcntl is None or os.name == "nt":
            self.acquired = True
            return self
        try:
            ensure_toklens_dir(self.home)
            fd = os.open(os_path_str(self.lock_fp), os.O_RDWR | os.O_CREAT, 0o600)
            self._fh = os.fdopen(fd, "r+", encoding="utf-8")
            fcntl.flock(self._fh.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            self._fh.seek(0)
            self._fh.truncate()
            self._fh.write(f"{os.getpid()} {int(time.time())}\n")
            self._fh.flush()
            self.acquired = True
        except (BlockingIOError, IOError, OSError):
            self.acquired = False
            if self._fh is not None:
                try:
                    self._fh.close()
                except Exception:
                    pass
                self._fh = None
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self._fh is not None:
            try:
                if fcntl is not None and self.acquired:
                    fcntl.flock(self._fh.fileno(), fcntl.LOCK_UN)
            except Exception:
                pass
            try:
                self._fh.close()
            except Exception:
                pass
            self._fh = None


def sanitize_machine_id(raw: str) -> str:
    s = re.sub(r"[^a-zA-Z0-9_-]+", "-", (raw or "").strip().lower()).strip("-")
    return s[:48] if s else "vps-node"


def normalize_model(raw, fallback="unknown") -> str:
    if not raw or not isinstance(raw, str):
        return fallback
    m = re.sub(r"[\x00-\x1f]+", "", raw).strip()
    if m in ("<synthetic>", "agent-loop", "unknown", "none", "null", ""):
        return fallback
    if "/" in m:
        m = m.rsplit("/", 1)[-1].strip()
    if not m.lower().startswith("ft:") and ":" in m:
        m = m.rsplit(":", 1)[-1].strip()
    m = m.replace("(", " ").replace(")", " ").strip()
    slug = re.sub(r"[\s_]+", "-", m.lower())
    if slug.startswith("claude-"):
        slug = CLAUDE_DOT_VER_RE.sub(r"\1-\2", slug)
        slug = re.sub(r"^(claude-3)\.(\d+)(?=-)", r"\1-\2", slug)
    return slug or fallback


def parse_tz_offset_minutes(raw) -> int:
    """解析时区偏移（分钟）：支持整数分钟(480)、小时(+8/-5)、'+08:00'、'UTC+8' 或 'Asia/Shanghai'；无效返回 None"""
    if raw is None or isinstance(raw, bool):
        return None
    if isinstance(raw, (int, float)):
        v = int(round(float(raw)))
        if -14 <= v <= 14 and v != 0 and abs(float(raw)) <= 14:
            return v * 60
        if -900 <= v <= 900:
            return v
        return None
    s = str(raw).strip()
    if not s:
        return None
    low = s.lower()
    if low in TZ_NAME_OFFSETS:
        return TZ_NAME_OFFSETS[low]
    s_clean = re.sub(r"^(?:utc|gmt)", "", low).strip()
    if not s_clean:
        return 0
    m_hm = re.match(r"^([+-]?)(\d{1,2}):(\d{2})$", s_clean)
    if m_hm:
        sign = -1 if m_hm.group(1) == "-" else 1
        mins = sign * (int(m_hm.group(2)) * 60 + int(m_hm.group(3)))
        return mins if -900 <= mins <= 900 else None
    try:
        num = float(s_clean)
        if -14.0 <= num <= 14.0 and ("." in s_clean or abs(num) <= 14):
            # 若输入如 480 则按分钟，若输入 +8 / 8 / 5.5 则按小时
            if abs(num) <= 14:
                return int(round(num * 60))
        iv = int(round(num))
        if -900 <= iv <= 900:
            return iv
    except Exception:
        pass
    return None


def _epoch_to_date_str(sec: float, tz_offset_min: int = None) -> str:
    if tz_offset_min is not None:
        tz = datetime.timezone(datetime.timedelta(minutes=int(tz_offset_min)))
        return datetime.datetime.fromtimestamp(sec, tz=tz).strftime("%Y-%m-%d")
    return datetime.datetime.fromtimestamp(sec).strftime("%Y-%m-%d")


def to_local_date(val, fallback_ts: float, tz_offset_min: int = None) -> str:
    """兼容 Python 3.7+ 纳秒级 ISO-8601、浮点秒字符串、无时区 UTC 时间戳及自定义跨端时区偏移"""
    if val is not None and not isinstance(val, bool):
        if isinstance(val, (int, float)) and val > 0:
            sec = val / 1e9 if val > 1e16 else (val / 1e6 if val > 1e14 else (val / 1000.0 if val > 1e11 else float(val)))
            try:
                return _epoch_to_date_str(sec, tz_offset_min)
            except Exception:
                pass
        elif isinstance(val, str):
            s = val.strip()
            if NUM_STR_RE.match(s):
                try:
                    fval = float(s)
                    if fval > 0:
                        return to_local_date(fval, fallback_ts, tz_offset_min)
                except Exception:
                    pass
            m_iso = ISO_TS_RE.match(s)
            if m_iso:
                try:
                    yr, mo, dy = int(m_iso.group(1)), int(m_iso.group(2)), int(m_iso.group(3))
                    hh, mm, ss = int(m_iso.group(4)), int(m_iso.group(5)), int(m_iso.group(6))
                    frac = (m_iso.group(7) or "")[:6].ljust(6, "0")
                    us = int(frac) if frac else 0
                    tz_part = m_iso.group(8)
                    if not tz_part or tz_part.upper() == "Z":
                        src_tz = datetime.timezone.utc
                    else:
                        t_clean = tz_part.replace(":", "")
                        t_sign = -1 if t_clean[0] == "-" else 1
                        t_mins = t_sign * (int(t_clean[1:3]) * 60 + int(t_clean[3:5]))
                        src_tz = datetime.timezone(datetime.timedelta(minutes=t_mins))
                    dt_utc = datetime.datetime(yr, mo, dy, hh, mm, ss, us, tzinfo=src_tz)
                    if tz_offset_min is not None:
                        dst_tz = datetime.timezone(datetime.timedelta(minutes=int(tz_offset_min)))
                        return dt_utc.astimezone(dst_tz).strftime("%Y-%m-%d")
                    return dt_utc.astimezone().strftime("%Y-%m-%d")
                except Exception:
                    pass
            if len(s) >= 10 and s[4] == "-" and s[7] == "-" and s[:4].isdigit():
                sub = s[:10]
                try:
                    yr_v = int(sub[0:4])
                    mo_v = int(sub[5:7])
                    dy_v = int(sub[8:10])
                    if 1 <= yr_v <= 9999:
                        datetime.date(yr_v, mo_v, dy_v)
                        return sub
                except Exception:
                    pass
    try:
        return _epoch_to_date_str(float(fallback_ts), tz_offset_min)
    except Exception:
        return _epoch_to_date_str(time.time(), tz_offset_min)


MAX_SAFE_TOKENS = 1_000_000_000_000_000
MAX_BUCKET_TOKENS = 2_000_000_000_000_000_000


def safe_int(v) -> int:
    if v is None or isinstance(v, bool):
        return 0
    if isinstance(v, int):
        if v <= 0:
            return 0
        return MAX_SAFE_TOKENS if v > MAX_SAFE_TOKENS else v
    if isinstance(v, float):
        if v != v or v == float("inf") or v == float("-inf") or v <= 0.0:
            return 0
        if v >= float(MAX_SAFE_TOKENS):
            return MAX_SAFE_TOKENS
        return int(v)
    try:
        s = str(v).strip()
        if not s:
            return 0
        try:
            iv = int(s)
        except ValueError:
            fv = float(s)
            if fv != fv or fv == float("inf") or fv == float("-inf") or fv <= 0.0:
                return 0
            if fv >= float(MAX_SAFE_TOKENS):
                return MAX_SAFE_TOKENS
            iv = int(fv)
        if iv <= 0:
            return 0
        return MAX_SAFE_TOKENS if iv > MAX_SAFE_TOKENS else iv
    except Exception:
        return 0


def safe_float(v) -> float:
    if v is None or isinstance(v, bool):
        return 0.0
    if isinstance(v, dict):
        return first_pos_float(v.get("total"), v.get("amount"), v.get("usd"), v.get("cost"))
    try:
        f = float(v)
        if f != f or f == float("inf") or f == float("-inf") or f <= 0.0:
            return 0.0
        return 1_000_000_000.0 if f > 1_000_000_000.0 else f
    except Exception:
        return 0.0


def first_pos_int(*vals) -> int:
    """按转换后整数 > 0 逐级回退，彻底避免 Python '0' 字符串在 or 表达式中短路"""
    for v in vals:
        iv = safe_int(v)
        if iv > 0:
            return iv
    return 0


def first_pos_float(*vals) -> float:
    """按转换后浮点数 > 0 逐级回退，彻底避免非空零值字典 {'total': 0} 或 '0' 短路吞掉真实 costUSD"""
    for v in vals:
        fv = safe_float(v)
        if fv > 0.0:
            return fv
    return 0.0


def fetch_url_bytes(req: urllib.request.Request, timeout: float = 10.0) -> bytes:
    if not req.has_header("Accept-encoding") and not req.has_header("Accept-Encoding"):
        req.add_header("Accept-Encoding", "gzip")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read()
        enc = (resp.headers.get("Content-Encoding") or "").lower()
        if "gzip" in enc or (len(raw) >= 2 and raw[0] == 0x1F and raw[1] == 0x8B):
            try:
                return gzip.decompress(raw)
            except Exception:
                return raw
        return raw


def open_sqlite_ro(db_path: Path):
    resolved = db_path.resolve()
    p_url = urllib.request.pathname2url(str(resolved))
    for query in ("mode=ro", "mode=ro&immutable=1"):
        conn = None
        try:
            uri = f"file:{p_url}?{query}"
            conn = sqlite3.connect(uri, uri=True, timeout=0.15)
            conn.execute("SELECT 1 FROM sqlite_master LIMIT 1").fetchone()
            return conn, None
        except Exception:
            if conn is not None:
                try:
                    conn.close()
                except Exception:
                    pass

    # 保护 VPS /tmp 内存盘：超大型锁死数据库跳过临时拷贝
    try:
        total_sz = os.path.getsize(os_path_str(db_path))
        wal_p = os_path_str(Path(str(db_path) + "-wal"))
        if os.path.exists(wal_p):
            total_sz += os.path.getsize(wal_p)
        if total_sz > MAX_SQLITE_TEMP_COPY_BYTES:
            return None, None
    except Exception:
        return None, None

    tmp_dir = tempfile.mkdtemp(prefix="toklens_")
    try:
        dst = Path(tmp_dir) / db_path.name
        shutil.copy2(os_path_str(db_path), os_path_str(dst))
        for suf in ("-wal", "-shm"):
            side = Path(str(db_path) + suf)
            if os.path.exists(os_path_str(side)):
                try:
                    shutil.copy2(os_path_str(side), os_path_str(Path(str(dst) + suf)))
                except Exception:
                    pass
        conn = sqlite3.connect(os_path_str(dst), timeout=2.5)
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


def safe_file_stat(p: Path):
    """返回 (exists, inode, size, mtime_ns, mtime_sec)"""
    try:
        st = os.stat(os_path_str(p))
        mtime_ns = getattr(st, "st_mtime_ns", int(st.st_mtime * 1e9))
        ino = getattr(st, "st_ino", 0) or 0
        return True, int(ino), int(st.st_size), int(mtime_ns), float(st.st_mtime)
    except Exception:
        return False, 0, 0, 0, time.time()


def safe_sqlite_combined_stat(db_path: Path):
    ok, ino, sz, m_ns, m_sec = safe_file_stat(db_path)
    if not ok:
        return False, 0, 0, 0, 0.0
    w_ok, _, w_sz, w_ns, w_sec = safe_file_stat(Path(str(db_path) + "-wal"))
    if w_ok:
        sz += w_sz
        if w_ns > m_ns:
            m_ns = w_ns
            m_sec = w_sec
    return True, ino, sz, m_ns, m_sec


def safe_mtime(p: Path) -> float:
    ok, _, _, _, m_sec = safe_file_stat(p)
    return m_sec if ok else time.time()


def safe_open_text(p: Path):
    return open(os_path_str(p), "r", encoding="utf-8", errors="ignore")


def safe_read_text(p: Path) -> str:
    with safe_open_text(p) as f:
        return f.read()


def _is_reparse_or_symlink_dir(full_dir: str) -> bool:
    try:
        if os.path.islink(full_dir):
            return True
        is_junc = getattr(os.path, "isjunction", None)
        if is_junc is not None and is_junc(full_dir):
            return True
        st = os.lstat(full_dir)
        if getattr(st, "st_file_attributes", 0) & 0x400:
            return True
    except Exception:
        return True
    return False


def safe_walk_files(root: Path, suffix: str = "", exact_name: str = "", pattern: str = ""):
    root_str = os_path_str(root)
    try:
        if not os.path.exists(root_str):
            return
    except Exception:
        return
    skip_dirs = {"node_modules", ".git", "Cache", "Code Cache", "__pycache__", "venv", ".venv", "CachedData"}
    visited_real = set()
    try:
        visited_real.add(os.path.realpath(root_str).lower() if os.name == "nt" else os.path.realpath(root_str))
    except Exception:
        pass
    root_sep_base = root_str.rstrip("/\\").count(os.sep)
    for dirpath, dirnames, filenames in os.walk(root_str, followlinks=False):
        depth = max(0, dirpath.rstrip("/\\").count(os.sep) - root_sep_base)
        if depth >= 24:
            dirnames[:] = []
        else:
            kept_dirs = []
            for d in dirnames:
                if d in skip_dirs:
                    continue
                full_d = os.path.join(dirpath, d)
                if _is_reparse_or_symlink_dir(full_d):
                    continue
                try:
                    rp = os.path.realpath(full_d)
                    rp_key = rp.lower() if os.name == "nt" else rp
                    if rp_key in visited_real:
                        continue
                    visited_real.add(rp_key)
                except Exception:
                    continue
                kept_dirs.append(d)
            dirnames[:] = kept_dirs
        clean_dir = dirpath[4:] if (os.name == "nt" and dirpath.startswith("\\\\?\\") and not dirpath.startswith("\\\\?\\UNC\\")) else dirpath
        for fn in filenames:
            if exact_name and fn != exact_name:
                continue
            if pattern:
                if not fnmatch.fnmatch(fn, pattern):
                    continue
            elif suffix and not fn.endswith(suffix):
                continue
            yield Path(clean_dir) / fn


class PricingLookup:
    """四层优先级价格引擎：Builtin -> Cloud Cache (Gzip + 负向冷却) -> Internal Overrides -> Custom Prices"""

    def __init__(self, home: Path, allow_network: bool = True):
        self.rates = dict(BUILTIN_RATES)
        self.cloud_source = "builtin"
        self.cloud_model_count = 0
        self._load_or_sync_cloud_cache(home, allow_network=allow_network)
        for k, v in INTERNAL_MODEL_OVERRIDES.items():
            self.rates[k] = v
        custom_fp = Path(home).expanduser() / ".toklens" / "custom_prices.json"
        try:
            if custom_fp.exists():
                cmap = json.loads(custom_fp.read_text(encoding="utf-8"))
                if isinstance(cmap, dict):
                    for mid, item in cmap.items():
                        if isinstance(item, dict):
                            ic = safe_float(item.get("input"))
                            oc = safe_float(item.get("output"))
                            if ic >= 0 and oc >= 0:
                                self.rates[normalize_model(str(mid), str(mid).strip().lower())] = (
                                    ic,
                                    oc,
                                    max(0.0, safe_float(item.get("cache_read"))),
                                    max(0.0, safe_float(item.get("cache_write"))),
                                )
        except Exception:
            pass

    def _load_or_sync_cloud_cache(self, home: Path, allow_network: bool = True):
        cache_fp = Path(home).expanduser() / ".toklens" / "pricing_cache.json"
        models_map = None
        cached_obj = {}
        now_ts = int(time.time())
        try:
            if cache_fp.exists():
                cached_obj = json.loads(cache_fp.read_text(encoding="utf-8"))
                if isinstance(cached_obj, dict):
                    if isinstance(cached_obj.get("models"), dict) and len(cached_obj["models"]) > 0:
                        models_map = cached_obj["models"]
                        self.cloud_source = str(cached_obj.get("source") or "cloud_cache")
                        if now_ts - int(cached_obj.get("ts_utc") or 0) < PRICING_CACHE_TTL_SEC:
                            self._merge_cloud_map(models_map)
                            return
                    failed_ts = int(cached_obj.get("failed_ts_utc") or 0)
                    if failed_ts > 0 and (now_ts - failed_ts) < PRICING_FAIL_COOLDOWN_SEC:
                        if models_map:
                            self._merge_cloud_map(models_map)
                        return
        except Exception:
            cached_obj = {}

        if not allow_network:
            if models_map:
                self._merge_cloud_map(models_map)
            return

        fresh = {}
        sources_ok = []
        ua_headers = {"User-Agent": f"TokLens-Agent/{AGENT_VERSION}", "Accept": "application/json"}

        # 1. LiteLLM CDN -> GitHub Raw
        for url in (
            "https://cdn.jsdelivr.net/gh/BerriAI/litellm@main/model_prices_and_context_window.json",
            "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json",
        ):
            try:
                req = urllib.request.Request(url, headers=dict(ua_headers))
                d = json.loads(fetch_url_bytes(req, timeout=5.0).decode("utf-8", errors="ignore"))
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
                        slug = normalize_model(str(k), "")
                        if len(slug) >= 2:
                            fresh[slug] = [round(ic, 4), round(oc, 4), round(crc, 4), round(cw, 4)]
                    sources_ok.append("LiteLLM")
                    break
            except Exception:
                pass

        # 2. OpenRouter Public Models API
        try:
            req = urllib.request.Request("https://openrouter.ai/api/v1/models", headers=dict(ua_headers))
            or_data = json.loads(fetch_url_bytes(req, timeout=5.0).decode("utf-8", errors="ignore"))
            or_list = (or_data.get("data") if isinstance(or_data, dict) else None) or []
            added_or = 0
            for m in or_list:
                if not isinstance(m, dict):
                    continue
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
                    added_or += 1
            if added_or > 0:
                sources_ok.append("OpenRouter")
        except Exception:
            pass

        # 3. models.dev Open AI Coding Model Registry (USD per 1M directly)
        try:
            req = urllib.request.Request("https://models.dev/api.json", headers=dict(ua_headers))
            md_data = json.loads(fetch_url_bytes(req, timeout=5.0).decode("utf-8", errors="ignore"))
            if isinstance(md_data, dict):
                added_md = 0
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
                            added_md += 1
                if added_md > 0:
                    sources_ok.append("models.dev")
        except Exception:
            pass

        if fresh:
            models_map = fresh
            self.cloud_source = " + ".join(sources_ok)
            try:
                ensure_toklens_dir(home)
                payload = {
                    "source": self.cloud_source,
                    "updated_at": datetime.datetime.now().strftime("%Y-%m-%d %H:%M"),
                    "ts_utc": now_ts,
                    "models": fresh,
                }
                atomic_write_text(cache_fp, json.dumps(payload, ensure_ascii=False), mode=0o600)
            except Exception:
                pass
        else:
            # 写入负向冷却时间戳（保留旧 models），防止国内受限网络每次执行都阻塞超时
            try:
                ensure_toklens_dir(home)
                neg_payload = {
                    "source": cached_obj.get("source") or "offline_cooldown",
                    "updated_at": cached_obj.get("updated_at") or "",
                    "ts_utc": int(cached_obj.get("ts_utc") or 0),
                    "failed_ts_utc": now_ts,
                    "models": models_map or {},
                }
                atomic_write_text(cache_fp, json.dumps(neg_payload, ensure_ascii=False), mode=0o600)
            except Exception:
                pass

        if models_map:
            self._merge_cloud_map(models_map)

    def _merge_cloud_map(self, models_map: dict):
        count = 0
        for raw_k, arr in models_map.items():
            if not isinstance(arr, list) or len(arr) < 4:
                continue
            ic, oc, crc, cwc = safe_float(arr[0]), safe_float(arr[1]), safe_float(arr[2]), safe_float(arr[3])
            if ic <= 0 and oc <= 0:
                continue
            k = normalize_model(str(raw_k), str(raw_k).strip().lower())
            if k in self.rates:
                ex = self.rates[k]
                # 保护内置官方直连单价免受第三方代理网关微幅手续费（如 $0.42 vs $0.40）污染
                if (
                    k in BUILTIN_RATES
                    and ex[0] > 0
                    and ex[1] > 0
                    and abs(ex[0] - ic) <= ex[0] * 0.15
                    and abs(ex[1] - oc) <= ex[1] * 0.15
                ):
                    continue
                if crc <= 0 and ex[2] > 0:
                    crc = ex[2]
                if cwc <= 0 and ex[3] > 0:
                    cwc = ex[3]
            self.rates[k] = (ic, oc, crc, cwc)
            count += 1
        self.cloud_model_count = count

    @staticmethod
    def _is_prefix_boundary_match(text: str, prefix: str) -> bool:
        if not text or not prefix or not text.startswith(prefix):
            return False
        if len(text) == len(prefix):
            return True
        # 严禁将小数点 '.' 视为前缀边界（防止 claude-opus-4 吞噬未归一化点号次版本）
        if text[len(prefix)] not in "-_":
            return False
        rest = text[len(prefix):].lower()
        for sub_tier in ("-mini", "_mini", "-nano", "_nano", "-lite", "_lite", "-flash", "_flash", "-air", "_air", "-pro", "_pro", "-highspeed", "_highspeed"):
            if sub_tier in rest:
                return False
        return True

    def lookup(self, raw_model: str):
        slug = normalize_model(raw_model, "unknown")
        if slug in self.rates:
            return self.rates[slug]
        stripped = re.sub(r"-(high|medium|low|minimal|thinking|preview|latest|fast)$", "", slug, flags=re.I)
        if stripped in self.rates:
            return self.rates[stripped]
        no_date = re.sub(r"-(?:\d{8}|\d{4}-\d{2}-\d{2})$", "", stripped)
        if no_date in self.rates:
            return self.rates[no_date]

        # Unknown rates stay explicitly unpriced; do not borrow another model.
        return (0.0, 0.0, 0.0, 0.0)


def extract_generic_usage(obj: dict, cur_model: str, fallback_ts: float, tz_offset_min: int = None):
    """通用提取器：兼容 OpenClaw / Pi / OMP / Qwen / Droid / Amp / Vibe / OpenHands / 标准 OpenAI 等结构"""
    if not isinstance(obj, dict):
        return None
    msg = obj.get("message") if isinstance(obj.get("message"), dict) else None
    meta = obj.get("_meta") if isinstance(obj.get("_meta"), dict) else None
    pdata = obj.get("providerData") if isinstance(obj.get("providerData"), dict) else None

    role = (msg.get("role") if msg else None) or obj.get("role")
    if role and str(role).lower() not in ("assistant", "model", "ai", "agent"):
        return None

    u = None
    for cand in (
        obj.get("usage"),
        msg.get("usage") if msg else None,
        obj.get("tokens"),
        msg.get("tokens") if msg else None,
        obj.get("tokenUsage"),
        msg.get("tokenUsage") if msg else None,
        obj.get("usageMetadata"),
        msg.get("usageMetadata") if msg else None,
    ):
        if isinstance(cand, dict):
            u = cand
            break
    if not u:
        return None

    raw_model = None
    for mc in (
        msg.get("model") if msg else None,
        msg.get("modelId") if msg else None,
        obj.get("model"),
        obj.get("modelId"),
        obj.get("modelID"),
        meta.get("model") if meta else None,
        meta.get("modelId") if meta else None,
        pdata.get("model") if pdata else None,
    ):
        if mc and isinstance(mc, str) and mc.strip():
            raw_model = mc.strip()
            break
    if raw_model == "<synthetic>":
        return None
    mdl = normalize_model(raw_model or cur_model, cur_model)

    has_anthropic_fields = ("input_tokens" in u and ("cache_read_input_tokens" in u or "cache_creation_input_tokens" in u))
    has_pi_fields = ("input" in u and ("cacheRead" in u or "cacheWrite" in u))

    raw_in = first_pos_int(
        u.get("input_tokens"),
        u.get("input"),
        u.get("prompt_tokens"),
        u.get("promptTokenCount"),
        u.get("tokensIn"),
        u.get("inputOther"),
    )
    base_out = first_pos_int(
        u.get("output_tokens"),
        u.get("output"),
        u.get("completion_tokens"),
        u.get("candidatesTokenCount"),
        u.get("tokensOut"),
    )
    thoughts_tok = safe_int(u.get("thoughts")) + safe_int(u.get("thoughtsTokenCount"))
    out_tok = base_out + thoughts_tok

    comp_details = u.get("completion_tokens_details") if isinstance(u.get("completion_tokens_details"), dict) else None
    if out_tok == 0:
        out_tok = (
            safe_int(u.get("reasoning"))
            + safe_int(u.get("reasoning_tokens"))
            + (safe_int(comp_details.get("reasoning_tokens")) if comp_details else 0)
        )

    cache_obj = u.get("cache") if isinstance(u.get("cache"), dict) else None
    prompt_details = u.get("prompt_tokens_details") if isinstance(u.get("prompt_tokens_details"), dict) else None
    cr = first_pos_int(
        u.get("cache_read_input_tokens"),
        u.get("cacheRead"),
        u.get("cache_read_tokens"),
        u.get("cached_input_tokens"),
        u.get("cached_tokens"),
        u.get("cached"),
        u.get("prompt_cache_hit_tokens"),
        u.get("inputCacheRead"),
        cache_obj.get("read") if cache_obj else 0,
        prompt_details.get("cached_tokens") if prompt_details else 0,
    )
    cw = first_pos_int(
        u.get("cache_creation_input_tokens"),
        u.get("cacheWrite"),
        u.get("cache_creation_tokens"),
        u.get("cache_write_tokens"),
        u.get("inputCacheCreation"),
        cache_obj.get("write") if cache_obj else 0,
        prompt_details.get("cache_write_tokens") if prompt_details else 0,
        prompt_details.get("cache_creation_tokens") if prompt_details else 0,
    )
    tot = first_pos_int(u.get("total_tokens"), u.get("totalTokens"), u.get("total"))

    if has_anthropic_fields or has_pi_fields or "inputOther" in u:
        fi = raw_in
    elif ("prompt_tokens" in u and cr > 0 and raw_in >= cr) or (tot > 0 and abs((raw_in + out_tok) - tot) <= 4 and cr > 0):
        fi = max(0, raw_in - cr - cw)
    else:
        fi = raw_in

    if fi + out_tok + cr + cw <= 0:
        return None

    cost = first_pos_float(
        u.get("cost"),
        obj.get("costUSD"),
        obj.get("cost"),
        obj.get("total_cost"),
        msg.get("cost") if msg else None,
    )
    ts_val = None
    for tc in (
        obj.get("timestamp"),
        obj.get("created_at"),
        obj.get("createdAt"),
        obj.get("time"),
        msg.get("timestamp") if msg else None,
    ):
        if tc is not None and tc != "":
            ts_val = tc
            break
    if isinstance(ts_val, dict):
        ts_val = ts_val.get("created") or ts_val.get("start")
    dt = to_local_date(ts_val, fallback_ts, tz_offset_min)

    dedup_id = ""
    for did_cand in (
        obj.get("requestId"),
        msg.get("id") if msg else None,
        obj.get("id"),
        obj.get("uuid"),
    ):
        if did_cand and str(did_cand).strip():
            dedup_id = str(did_cand).strip()
            break
    return (mdl, dt, fi, out_tok, cr, cw, cost, dedup_id)


def scan_jsonl_from_offset(fp: Path, start_offset: int, mtime_sec: float, line_callback):
    """
    字节级增量 JSONL 扫描器（与桌面端 Utf8LineScanner.ScanFromOffset 对齐）：
    - 严格按 '\\n' 消费完整行并推进 valid_end_offset；
    - 若末尾无 '\\n'：仅当文件静置 >= 2s 且以 '}' 结尾、json.loads 校验为完整 JSON 对象时才推进；
      若 AI 正在流式写半截行，valid_end_offset 停留在上一行末尾，下次唤醒时无缝重读该行。
    """
    p_str = os_path_str(fp)
    file_len = os.path.getsize(p_str)
    if start_offset < 0 or start_offset > file_len:
        start_offset = 0
    if start_offset == file_len:
        return start_offset, 0

    valid_end = start_offset
    lines_read = 0
    with open(p_str, "rb") as bf:
        if start_offset > 0:
            bf.seek(start_offset)
        while True:
            raw_line = bf.readline()
            if not raw_line:
                break
            if raw_line.endswith(b"\n"):
                valid_end += len(raw_line)
                lines_read += 1
                text = raw_line.rstrip(b"\r\n").decode("utf-8", errors="ignore")
                if len(text) > 2:
                    line_callback(text, lines_read)
            else:
                # 末尾没有换行符：检查是否为已落盘闭合的 JSON 对象
                trimmed = raw_line.strip()
                if len(trimmed) > 2 and trimmed.endswith(b"}") and (time.time() - mtime_sec) >= 2.0:
                    text = trimmed.decode("utf-8", errors="ignore")
                    try:
                        parsed_tail = json.loads(text)
                        if isinstance(parsed_tail, dict):
                            valid_end += len(raw_line)
                            lines_read += 1
                            line_callback(text, lines_read)
                    except Exception:
                        pass
                break
    return valid_end, lines_read


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

FILE_CURSORS_DDL = (
    "CREATE TABLE IF NOT EXISTS file_cursors ("
    "file_key TEXT PRIMARY KEY, "
    "home_key TEXT NOT NULL, "
    "inode INTEGER NOT NULL, "
    "file_size INTEGER NOT NULL, "
    "mtime_ns INTEGER NOT NULL, "
    "byte_offset INTEGER NOT NULL, "
    "extra_meta TEXT, "
    "events_json TEXT)"
)

KV_META_DDL = "CREATE TABLE IF NOT EXISTS kv_meta (k TEXT PRIMARY KEY, v TEXT)"


def get_ledger_db_path(home: Path) -> Path:
    base = Path(home).expanduser() if home is not None else Path.home()
    return base / ".toklens" / "ledger.db"


def _init_ledger_connection(db_path: Path):
    try:
        conn = sqlite3.connect(str(db_path), timeout=10.0)
    except Exception:
        conn = sqlite3.connect(os_path_str(db_path), timeout=10.0)
    try:
        conn.execute("PRAGMA busy_timeout=10000;")
        try:
            conn.execute("PRAGMA journal_mode=WAL;")
            conn.execute("PRAGMA synchronous=NORMAL;")
        except sqlite3.DatabaseError as db_err:
            msg_l = str(db_err).lower()
            if "not a database" in msg_l or "malformed" in msg_l or "corrupt" in msg_l:
                raise
        except Exception:
            pass
        conn.execute(DAILY_ARCHIVE_DDL)
        conn.execute(FILE_CURSORS_DDL)
        conn.execute(KV_META_DDL)
        row = conn.execute("SELECT v FROM kv_meta WHERE k='cursor_schema' LIMIT 1").fetchone()
        cur_ver = row[0] if row else ""
        if cur_ver != CURSOR_SCHEMA_VERSION:
            if cur_ver:
                conn.commit()
                backup = sqlite3.connect(str(db_path) + ".before-v2.9." + str(time.time_ns()) + ".db")
                try:
                    conn.backup(backup)
                finally:
                    backup.close()
            live_keys = [r[0] for r in conn.execute("SELECT file_key FROM file_cursors") if os.path.exists(os_path_str(Path(r[0])))]
            conn.executemany("DELETE FROM file_cursors WHERE file_key=?", [(key,) for key in live_keys])
            conn.execute("DELETE FROM file_cursors WHERE file_key LIKE '%state_5.sqlite'")
            conn.execute(
                "INSERT OR REPLACE INTO kv_meta (k, v) VALUES ('cursor_schema', ?)",
                (CURSOR_SCHEMA_VERSION,),
            )
            conn.commit()
        return conn
    except Exception:
        try:
            conn.close()
        except Exception:
            pass
        raise


def _open_ledger_rw(home: Path):
    ensure_toklens_dir(home)
    db_path = get_ledger_db_path(home)
    # An unreadable ledger is not an empty ledger. Never replace it and then
    # upload a partial scan with a newer reconciliation timestamp.
    conn = _init_ledger_connection(db_path)
    if os.name != "nt":
        try:
            os.chmod(os_path_str(db_path), 0o600)
        except Exception:
            pass
    return conn


class CursorStore:
    """文件级增量游标与归档恢复存储（对齐桌面端 LocalUsageStore）"""

    def __init__(self, ledger_home: Path):
        self.ledger_home = Path(ledger_home).expanduser()
        self.cursors = {}  # file_key -> dict
        self.dirty = []
        self._load()

    def _load(self):
        db_path = get_ledger_db_path(self.ledger_home)
        if not os.path.exists(os_path_str(db_path)):
            return
        conn = None
        try:
            conn = _open_ledger_rw(self.ledger_home)
            for fk, hk, ino, fsz, mns, boff, meta_s, ev_s in conn.execute(
                "SELECT file_key, home_key, inode, file_size, mtime_ns, byte_offset, extra_meta, events_json FROM file_cursors"
            ):
                meta = json.loads(meta_s) if meta_s else {}
                evs = json.loads(ev_s) if ev_s else []
                if not isinstance(meta, dict) or not isinstance(evs, list):
                    raise ValueError("Invalid stored request cursor")
                self.cursors[fk] = {
                    "file_key": fk,
                    "home_key": hk or "",
                    "inode": int(ino or 0),
                    "file_size": int(fsz or 0),
                    "mtime_ns": int(mns or 0),
                    "byte_offset": int(boff or 0),
                    "meta": meta if isinstance(meta, dict) else {},
                    "events": evs if isinstance(evs, list) else [],
                }
        except Exception as ex:
            self.cursors.clear()
            raise RuntimeError("请求游标读取失败，已停止扫描以保护历史账本") from ex
        finally:
            if conn is not None:
                try:
                    conn.close()
                except Exception:
                    pass

    def get(self, file_key: str):
        return self.cursors.get(file_key)

    def is_unchanged(self, file_key: str, ino: int, fsize: int, mtime_ns: int) -> bool:
        c = self.cursors.get(file_key)
        if not c:
            return False
        if c["file_size"] != fsize or c["mtime_ns"] != mtime_ns:
            return False
        if ino and c["inode"] and ino != c["inode"]:
            return False
        return True

    def can_append_seek(self, file_key: str, ino: int, fsize: int) -> bool:
        c = self.cursors.get(file_key)
        if not c or c["byte_offset"] <= 0:
            return False
        if ino and c["inode"] and ino != c["inode"]:
            return False
        return fsize > c["byte_offset"]

    def put(self, file_key: str, home_key: str, ino: int, fsize: int, mtime_ns: int, byte_offset: int, meta: dict, events: list):
        entry = {
            "file_key": file_key,
            "home_key": home_key,
            "inode": int(ino or 0),
            "file_size": int(fsize or 0),
            "mtime_ns": int(mtime_ns or 0),
            "byte_offset": int(byte_offset or 0),
            "meta": meta or {},
            "events": events or [],
        }
        self.cursors[file_key] = entry
        self.dirty.append(entry)

    def flush(self):
        if not self.dirty:
            return
        conn = None
        try:
            conn = _open_ledger_rw(self.ledger_home)
            rows = [
                (
                    e["file_key"],
                    e["home_key"],
                    e["inode"],
                    e["file_size"],
                    e["mtime_ns"],
                    e["byte_offset"],
                    json.dumps(e["meta"], ensure_ascii=False),
                    json.dumps(e["events"], ensure_ascii=False),
                )
                for e in self.dirty
            ]
            conn.executemany(
                "INSERT OR REPLACE INTO file_cursors "
                "(file_key, home_key, inode, file_size, mtime_ns, byte_offset, extra_meta, events_json) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                rows,
            )
            conn.commit()
            self.dirty.clear()
        except Exception as ex:
            raise RuntimeError("请求游标保存失败，已中止本次扫描") from ex
        finally:
            if conn is not None:
                try:
                    conn.close()
                except Exception:
                    pass


def discover_scan_homes(primary_home: Path, include_other_users: bool = False) -> list:
    p_home = Path(primary_home).expanduser()
    homes = [p_home]
    seen_real = set()
    try:
        seen_real.add(os.path.realpath(os_path_str(p_home)))
    except Exception:
        seen_real.add(str(p_home))

    if include_other_users and os.name != "nt":
        home_base = Path("/home")
        try:
            if home_base.is_dir():
                for child in sorted(home_base.iterdir(), key=lambda x: x.name):
                    try:
                        if not child.is_dir() or child.name.startswith(".") or child.name == "lost+found":
                            continue
                        rp = os.path.realpath(os_path_str(child))
                        if rp in seen_real:
                            continue
                        if os.access(rp, os.R_OK | os.X_OK):
                            seen_real.add(rp)
                            homes.append(child)
                    except Exception:
                        pass
        except Exception:
            pass
    return homes


def _fold_rotated_events(existing_buckets: list, old_events: list, exclude_dedup_keys: set = None) -> list:
    """将被轮转或截断前的无全局去重键旧事件折叠为紧凑按日桶，防止同日轮转吞掉增量且避免游标无限膨胀"""
    agg = {}
    for b in (existing_buckets or []):
        if isinstance(b, list) and len(b) >= 9:
            k = (str(b[0]), str(b[1]), str(b[2]))
            agg[k] = [k[0], k[1], k[2], safe_int(b[3]), safe_int(b[4]), safe_int(b[5]), safe_int(b[6]), safe_float(b[7]), max(1, safe_int(b[8]))]
    for ev in (old_events or []):
        if not isinstance(ev, list) or len(ev) < 9:
            continue
        dkey = str(ev[9]) if len(ev) > 9 and ev[9] else ""
        if dkey and exclude_dedup_keys and dkey in exclude_dedup_keys:
            continue
        k = (str(ev[0]), str(ev[1]), str(ev[2]))
        fi, out_t, cr, cw = safe_int(ev[3]), safe_int(ev[4]), safe_int(ev[5]), safe_int(ev[6])
        if fi + out_t + cr + cw <= 0:
            continue
        cost, turns = safe_float(ev[7]), max(1, safe_int(ev[8]))
        if k in agg:
            ex = agg[k]
            ex[3] += fi
            ex[4] += out_t
            ex[5] += cr
            ex[6] += cw
            ex[7] = round(ex[7] + cost, 8)
            ex[8] += turns
        else:
            agg[k] = [k[0], k[1], k[2], fi, out_t, cr, cw, round(cost, 8), turns]
    return list(agg.values())


def _merge_sqlite_cursor_events(cur_entry: dict, fresh_events: list) -> list:
    old_evs = (cur_entry.get("events") if cur_entry else None) or []
    if not old_evs:
        return fresh_events or []
    if not fresh_events:
        return list(old_evs)
    by_key = {}
    for oev in old_evs:
        if isinstance(oev, list) and len(oev) > 9 and oev[9]:
            by_key[str(oev[9])] = oev
    no_key_fresh = []
    for fev in fresh_events:
        if not isinstance(fev, list) or len(fev) < 9:
            continue
        dk = str(fev[9]) if len(fev) > 9 and fev[9] else ""
        if dk:
            prev = by_key.get(dk)
            if not prev or (safe_int(fev[3]) + safe_int(fev[4]) + safe_int(fev[5]) + safe_int(fev[6])) >= (
                safe_int(prev[3]) + safe_int(prev[4]) + safe_int(prev[5]) + safe_int(prev[6])
            ):
                by_key[dk] = fev
        else:
            no_key_fresh.append(fev)
    return list(by_key.values()) + no_key_fresh


def _usage_proto_fields(data):
    """Decode wire fields without searching unrelated nested bytes."""
    if not isinstance(data, (bytes, bytearray)):
        return {}
    pos, result = 0, {}

    def varint():
        nonlocal pos
        value = 0
        for shift in range(0, 64, 7):
            if pos >= len(data):
                raise ValueError("Truncated protobuf")
            byte = data[pos]
            pos += 1
            if shift == 63 and byte > 1:
                raise ValueError("Protobuf integer overflow")
            value |= (byte & 127) << shift
            if byte < 128:
                return value
        raise ValueError("Invalid protobuf varint")

    while pos < len(data):
        tag = varint()
        field, wire = tag >> 3, tag & 7
        if not field:
            raise ValueError("Invalid protobuf tag")
        if wire == 0:
            result[field] = varint()
            continue
        size = varint() if wire == 2 else (8 if wire == 1 else 4 if wire == 5 else -1)
        if size < 0 or size > len(data) - pos:
            raise ValueError("Invalid protobuf field")
        result[field] = data[pos:pos + size]
        pos += size
    return result


def _agy_native_model(backend, tier):
    actual, variant = normalize_model(backend, "unknown"), normalize_model(tier, "")
    if variant == actual or variant.startswith(actual + "-"):
        return variant
    if actual == "gemini-pro-orchestrator" and variant.startswith("orchestrator-"):
        return variant
    return actual


def _usage_epoch_ms(raw, fallback):
    try:
        stamp = datetime.datetime.fromisoformat(str(raw).replace("Z", "+00:00")).astimezone(datetime.timezone.utc)
        delta = stamp - datetime.datetime(1970, 1, 1, tzinfo=datetime.timezone.utc)
        return delta.days * 86400000 + delta.seconds * 1000 + delta.microseconds // 1000
    except (ValueError, TypeError, OverflowError):
        return int(fallback * 1000)


class CodexUsageState:
    """Exact response IDs, paired snapshots, and durable incremental state."""
    def __init__(self, tid, fallback, tz_offset_min=None, meta=None, events=None):
        self.meta = dict(meta or {})
        self.meta.setdefault("tid", tid)
        self.meta.setdefault("mdl", "unknown")
        self.fallback, self.tz_offset_min = fallback, tz_offset_min
        self.rows = {}
        for index, ev in enumerate(events or []):
            self.rows[str(ev[9]) if len(ev) > 9 and ev[9] else "legacy:" + str(index)] = list(ev)
        self.seen = list(self.meta.get("seen") or [])

    @staticmethod
    def counters(value):
        if not isinstance(value, dict):
            return None
        inp, out = safe_int(value.get("input_tokens")), safe_int(value.get("output_tokens"))
        read = min(inp, first_pos_int(value.get("cached_input_tokens"), value.get("cache_read_input_tokens"), value.get("cached_tokens")))
        write = min(inp - read, first_pos_int(value.get("cache_write_input_tokens"), value.get("cache_creation_input_tokens")))
        return [inp, out, read, write]

    def remember(self, signature):
        if signature in self.seen:
            return False
        self.seen.append(signature)
        self.seen = self.seen[-128:]
        return True

    def emit(self, usage, raw_ts, response=None):
        inp, out, read, write = usage
        if inp + out <= 0:
            return ""
        read, write = min(inp, read), min(inp - min(inp, read), write)
        ms = _usage_epoch_ms(raw_ts, self.fallback)
        key = "codex-response:" + response if response else "codex:" + str(self.meta["tid"]) + ":" + str(ms) + ":" + ",".join(map(str, usage))
        self.rows[key] = ["OpenAI Codex", self.meta["mdl"], to_local_date(raw_ts, self.fallback, self.tz_offset_min), inp - read - write, out, read, write, 0.0, 1, key, 0, self.meta["tid"], ms]
        return key

    def on_line(self, line, _index=0):
        obj = json.loads(line)
        if not isinstance(obj, dict) or not isinstance(obj.get("payload"), dict):
            return
        typ, p, ts = obj.get("type"), obj["payload"], obj.get("timestamp")
        if typ == "session_meta":
            self.meta["tid"] = str(p.get("id") or self.meta["tid"])
            self.meta["parent"] = str(p.get("forked_from_id") or "")
            self.meta["fork_ms"] = _usage_epoch_ms(ts, self.fallback)
        if typ in ("session_meta", "turn_context") or p.get("type") == "thread_settings_applied":
            if p.get("model"):
                self.meta["mdl"] = normalize_model(str(p["model"]), "unknown")
        if typ == "token_usage_record":
            usage = self.counters(p.get("usage"))
            if usage is not None:
                self.meta["pending_key"] = self.emit(usage, ts, p.get("response_id"))
                self.meta["pending"] = usage
            return
        if typ != "event_msg" or p.get("type") != "token_count" or not isinstance(p.get("info"), dict):
            return
        info = p["info"]
        model = info.get("model") or info.get("model_name") or p.get("model")
        if model:
            self.meta["mdl"] = normalize_model(str(model), "unknown")
        last, total = self.counters(info.get("last_token_usage")), self.counters(info.get("total_token_usage"))
        previous = self.meta.get("previous")
        reset = total is not None and previous is not None and sum(total[:2]) < sum(previous[:2])
        if reset and last is None:
            self.seen.clear()
        duplicate = total is not None and not self.remember(json.dumps([total, last], separators=(",", ":")))
        usage = last if last is not None else ([max(0, total[i] - (previous[i] if previous and not reset else 0)) for i in range(4)] if total is not None else None)
        if total is not None:
            self.meta["previous"] = total
        pending = self.meta.pop("pending", None)
        covered = pending is not None and (pending == usage or last is not None and last[0] + last[1] == 0)
        pending_key = self.meta.pop("pending_key", "")
        if covered and model and pending_key in self.rows:
            self.rows[pending_key][1] = self.meta["mdl"]
        if not duplicate and not covered and usage is not None:
            if total is not None or self.remember(str(ts) + "|" + json.dumps(usage)):
                self.emit(usage, ts)

    def state(self):
        self.meta["seen"] = self.seen
        self.meta["accounting_rev"] = SCANNER_REV
        return self.meta


class MultiHarnessScanner:
    def __init__(self, home: Path, tz_offset_min: int = None, include_other_users: bool = False, allow_network_pricing: bool = True):
        self.ledger_home = Path(home).expanduser()
        self.home = self.ledger_home
        self.tz_offset_min = tz_offset_min
        self.include_other_users = include_other_users
        self.scan_homes = discover_scan_homes(self.ledger_home, include_other_users=include_other_users)
        self.pricing = PricingLookup(self.ledger_home, allow_network=allow_network_pricing)
        self.cursor_store = CursorStore(self.ledger_home)

        self.events = []  # (tool, model, date, fi, out, cr, cw, logged_cost, turns)
        self.visited_files = set()
        self.active_home_keys = set()
        self.claude_seen_keys = set()
        self.opencode_seen_keys = set()
        self.mobileclaw_seen_keys = set()
        self.generic_seen_keys = set()
        self.codex_live = set()
        self.codex_archive_candidates = []
        self.archived_files_recovered = 0
        self.request_seen = set()
        self.request_events = {}
        self.reconciled_partitions = {}
        self.failed_exact_tools = set()
        self.unverified_partitions = set()

    def _norm_file_key(self, p: Path) -> str:
        s = str(p)
        return s.lower() if os.name == "nt" else s

    def _norm_home_key(self, h: Path) -> str:
        s = str(h).rstrip("/\\")
        return s.lower() if os.name == "nt" else s

    def _track_file(self, p: Path) -> str:
        fk = self._norm_file_key(p)
        self.visited_files.add(fk)
        return fk

    def add_ev(self, tool, model, date_str, fi, out_tok, cr, cw, cost=0.0, turns=1, cw1h=0):
        if fi + out_tok + cr + cw > 0:
            self.events.append((tool, model, date_str, int(fi), int(out_tok), int(cr), int(cw), float(cost), max(1, int(turns)), max(0, int(cw1h))))

    def _emit_ev_list(self, ev_list: list, dedup_set: set = None):
        for ev in (ev_list or []):
            if not isinstance(ev, (list, tuple)) or len(ev) < 9:
                continue
            dk = str(ev[9]) if len(ev) > 9 and ev[9] else ""
            if dk and dedup_set is not None:
                if dk in dedup_set:
                    continue
                dedup_set.add(dk)
            cw1h = safe_int(ev[10]) if len(ev) > 10 else 0
            self.add_ev(ev[0], ev[1], ev[2], safe_int(ev[3]), safe_int(ev[4]), safe_int(ev[5]), safe_int(ev[6]), safe_float(ev[7]), max(1, safe_int(ev[8])), cw1h=cw1h)

    def _emit_usage_events(self, file_key, rows):
        meta = (self.cursor_store.get(file_key) or {}).get("meta") or {}
        for index, row in enumerate(rows or []):
            if not isinstance(row, (list, tuple)) or len(row) < 9:
                continue
            ev = list(row)
            raw_key = str(ev[9] or "") if len(ev) > 9 else ""
            if ev[0] == "Google Antigravity":
                cid = Path(file_key).parents[2].name
                key = "agy:" + cid + ":" + (raw_key or str(index))
            else:
                key = raw_key or "legacy-codex:" + str(meta.get("tid") or file_key) + ":" + str(index)
                if not raw_key:
                    self.unverified_partitions.add(str(ev[2]) + "||OpenAI Codex")
            previous = self.request_events.get(key)
            if previous is None or sum(ev[3:7]) > sum(previous[0][3:7]) or (previous[0][1] == "unknown" and ev[1] != "unknown"):
                self.request_events[key] = (ev, str(meta.get("tid") or ""))

    def _finalize_request_events(self):
        timelines, parents = {}, {}
        for cursor in self.cursor_store.cursors.values():
            meta = cursor.get("meta") or {}
            if meta.get("tid") and meta.get("parent"):
                parents[str(meta["tid"])] = (str(meta["parent"]), safe_int(meta.get("fork_ms")))
        for ev, tid in self.request_events.values():
            if len(ev) > 12 and ev[0] == "OpenAI Codex":
                timelines.setdefault(str(ev[11] or tid), set()).add((ev[12], *ev[3:7]))
        aliases = {}
        for key, (ev, tid) in self.request_events.items():
            if len(ev) > 12 and ev[0] == "OpenAI Codex":
                tid = str(ev[11] or tid)
                signature = (ev[12], *ev[3:7])
                parent = parents.get(tid)
                if parent and ev[12] <= parent[1] and signature in timelines.get(parent[0], set()):
                    continue
                alias = (tid, signature)
                previous = aliases.get(alias)
                if previous is not None and not (key.startswith("codex-response:") and previous.startswith("codex-response:") and key != previous):
                    continue
                aliases[alias] = key
            self._emit_ev_list([ev])

    def scan_all(self, strict_ledger: bool = False):
        scan_funcs = (
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
        )
        for target_home in self.scan_homes:
            self.home = target_home
            self.active_home_keys.add(self._norm_home_key(target_home))
            for fn in scan_funcs:
                try:
                    fn()
                except Exception:
                    tools = {"scan_claude": ("Claude Code", "Claude Desktop"), "scan_codex_live": ("OpenAI Codex",), "scan_antigravity": ("Google Antigravity",), "scan_zcode": ("ZCode",)}
                    self.failed_exact_tools.update(tools.get(fn.__name__, ()))
        self.home = self.ledger_home
        self._recover_deleted_cursors_and_finalize_codex()
        self._finalize_request_events()
        self.cursor_store.flush()
        rows = self.build_records()
        now_ms = int(time.time() * 1000)
        for row in rows:
            if row[1] in ("Claude Code", "Claude Desktop", "OpenAI Codex", "Google Antigravity", "ZCode") and row[1] not in self.failed_exact_tools and row[0] + "||" + row[1] not in self.unverified_partitions:
                self.reconciled_partitions[row[0] + "||" + row[1]] = now_ms
        return merge_with_ledger_db(self.ledger_home, rows, strict=strict_ledger, reconciled_partitions=self.reconciled_partitions)

    def _recover_deleted_cursors_and_finalize_codex(self):
        for fk, cur in self.cursor_store.cursors.items():
            if fk in self.visited_files:
                continue
            hk = cur.get("home_key") or ""
            if hk and hk not in self.active_home_keys:
                continue
            # 若磁盘上仍存在（例如权限临时受限未被 walk 到）则不归为已删除，但若已删除则恢复其游标事件
            if os.path.exists(os_path_str(Path(fk))):
                continue

            evs = cur.get("events") or []
            rot = (cur.get("meta") or {}).get("rotated_buckets") or []
            if not evs and not rot:
                continue
            self.archived_files_recovered += 1

            if fk.lower().endswith("state_5.sqlite"):
                continue

            if ".codex" in fk.lower():
                tid = str((cur.get("meta") or {}).get("tid") or "")
                if not tid:
                    m = UUID_RE.search(os.path.basename(fk))
                    if m:
                        tid = m.group(1)
                if tid:
                    self.codex_live.add(tid)

            self._emit_ev_list(rot)
            if evs and all(isinstance(ev, list) and len(ev) >= 9 and ev[0] in ("OpenAI Codex", "Google Antigravity") for ev in evs):
                self._emit_usage_events(fk, evs)
                continue
            for ev in evs:
                if not isinstance(ev, list) or len(ev) < 9:
                    continue
                tool = ev[0]
                if tool in ("OpenAI Codex", "Google Antigravity"):
                    self._emit_usage_events(fk, [ev])
                    continue
                dk = str(ev[9]) if len(ev) > 9 and ev[9] else ""
                if dk:
                    if tool in ("Claude Desktop", "Claude Code"):
                        if dk in self.claude_seen_keys:
                            continue
                        self.claude_seen_keys.add(dk)
                    elif tool == "OpenCode":
                        if dk in self.opencode_seen_keys:
                            continue
                        self.opencode_seen_keys.add(dk)
                    elif tool == "MobileClaw":
                        if dk in self.mobileclaw_seen_keys:
                            continue
                        self.mobileclaw_seen_keys.add(dk)
                    elif dk in self.generic_seen_keys:
                        continue
                    else:
                        self.generic_seen_keys.add(dk)
                self.add_ev(ev[0], ev[1], ev[2], safe_int(ev[3]), safe_int(ev[4]), safe_int(ev[5]), safe_int(ev[6]), safe_float(ev[7]), max(1, safe_int(ev[8])))

        for cev in self.codex_archive_candidates:
            if not isinstance(cev, list) or len(cev) < 9:
                continue
            tid = str(cev[9]) if len(cev) > 9 and cev[9] else ""
            if tid and tid in self.codex_live:
                continue
            self.add_ev(cev[0], cev[1], cev[2], safe_int(cev[3]), safe_int(cev[4]), safe_int(cev[5]), safe_int(cev[6]), safe_float(cev[7]), max(1, safe_int(cev[8])))

    def scan_jsonl_tree(self, tool_name: str, roots, default_model: str, suffix=".jsonl", pattern=""):
        hkey = self._norm_home_key(self.home)
        seen_real_files = set()
        tree_dedup = {}  # key -> [tool, mdl, dt, fi, out, cr, cw, cost, 1, global_dk]
        rotated_all = []

        for r in roots:
            for fp in safe_walk_files(r, suffix=suffix, pattern=pattern):
                if fp.name.endswith(".checkpoints.jsonl"):
                    continue
                try:
                    rp = os.path.realpath(os_path_str(fp))
                    if rp in seen_real_files:
                        continue
                    seen_real_files.add(rp)
                except Exception:
                    pass

                ok, ino, fsize, m_ns, m_sec = safe_file_stat(fp)
                if not ok:
                    continue
                fk = self._track_file(fp)

                if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns):
                    cur = self.cursor_store.get(fk)
                    rotated_all.extend((cur.get("meta") or {}).get("rotated_buckets") or [])
                    for idx_c, ev in enumerate(cur.get("events") or []):
                        dk = str(ev[9]) if len(ev) > 9 and ev[9] else f"{fk}:c{idx_c}"
                        prev = tree_dedup.get(dk)
                        if not prev or (ev[3] + ev[4] + ev[5] + ev[6]) >= (prev[3] + prev[4] + prev[5] + prev[6]):
                            tree_dedup[dk] = ev
                    continue

                cur = self.cursor_store.get(fk)
                start_off = 0
                line_base = 0
                cur_m = default_model
                file_dedup = {}
                rot_buckets = list(((cur.get("meta") if cur else None) or {}).get("rotated_buckets") or [])

                if self.cursor_store.can_append_seek(fk, ino, fsize):
                    start_off = cur["byte_offset"]
                    meta = cur.get("meta") or {}
                    cur_m = str(meta.get("mdl") or cur_m)
                    line_base = safe_int(meta.get("ln"))
                    for idx_o, oev in enumerate(cur.get("events") or []):
                        odk = str(oev[9]) if len(oev) > 9 and oev[9] else f"{fk}:L{idx_o}"
                        file_dedup[odk] = oev
                elif cur and cur.get("events"):
                    # 文件被轮转或截断重写：带全局 ID 的保留去重，无全局 ID 的折叠进 rotated_buckets 防丢失
                    kept_global = set()
                    for oev in cur["events"]:
                        odk = str(oev[9]) if len(oev) > 9 and oev[9] else ""
                        if odk and not odk.startswith(fk + ":"):
                            file_dedup[odk] = oev
                            kept_global.add(odk)
                    rot_buckets = _fold_rotated_events(rot_buckets, cur["events"], exclude_dedup_keys=kept_global)

                def _on_line(line: str, rel_idx: int):
                    nonlocal cur_m
                    if "usage" not in line and "tokens" not in line and "input" not in line and "model" not in line:
                        return
                    try:
                        obj = json.loads(line)
                    except Exception:
                        return
                    if not isinstance(obj, dict):
                        return
                    typ = obj.get("type")
                    if typ in ("model_change", "turn_context", "llm.request", "session"):
                        pl = obj.get("payload") if isinstance(obj.get("payload"), dict) else {}
                        cand = obj.get("modelId") or obj.get("model") or pl.get("model")
                        if cand:
                            cur_m = normalize_model(str(cand), cur_m)
                    else:
                        meta_o = obj.get("_meta") if isinstance(obj.get("_meta"), dict) else {}
                        pdata_o = obj.get("providerData") if isinstance(obj.get("providerData"), dict) else {}
                        cand = obj.get("model") or meta_o.get("model") or meta_o.get("modelId") or pdata_o.get("model")
                        if cand:
                            cur_m = normalize_model(str(cand), cur_m)

                    parsed = extract_generic_usage(obj, cur_m, m_sec, self.tz_offset_min)
                    if not parsed:
                        return
                    mdl, dt, fi, out_tok, cr, cw, cost, did = parsed
                    cur_m = mdl
                    # 关键修复：有唯一请求/消息 ID 时跨文件去重（不拼文件路径），防止会话归档备份被双倍计费
                    abs_ln = line_base + rel_idx
                    global_dk = f"{tool_name}:{did}" if did else ""
                    key = global_dk if global_dk else f"{fk}:L{abs_ln}"
                    ev_row = [tool_name, mdl, dt, fi, out_tok, cr, cw, cost, 1, global_dk]
                    prev = file_dedup.get(key)
                    if not prev or (fi + out_tok + cr + cw) >= (prev[3] + prev[4] + prev[5] + prev[6]):
                        file_dedup[key] = ev_row

                valid_end = start_off
                read_cnt = 0
                try:
                    valid_end, read_cnt = scan_jsonl_from_offset(fp, start_off, m_sec, _on_line)
                except Exception:
                    pass

                file_events = list(file_dedup.values())
                new_meta = {"mdl": cur_m, "ln": line_base + read_cnt}
                if rot_buckets:
                    new_meta["rotated_buckets"] = rot_buckets
                self.cursor_store.put(fk, hkey, ino, fsize, m_ns, valid_end, new_meta, file_events)

                rotated_all.extend(rot_buckets)
                for idx_f, ev in enumerate(file_events):
                    dk = str(ev[9]) if len(ev) > 9 and ev[9] else f"{fk}:e{idx_f}"
                    prev = tree_dedup.get(dk)
                    if not prev or (ev[3] + ev[4] + ev[5] + ev[6]) >= (prev[3] + prev[4] + prev[5] + prev[6]):
                        tree_dedup[dk] = ev

        self._emit_ev_list(rotated_all)
        for dk, ev in tree_dedup.items():
            if len(ev) > 9 and ev[9]:
                if ev[9] in self.generic_seen_keys:
                    continue
                self.generic_seen_keys.add(ev[9])
            self.add_ev(ev[0], ev[1], ev[2], ev[3], ev[4], ev[5], ev[6], ev[7], ev[8])

    def scan_claude(self):
        hkey = self._norm_home_key(self.home)
        base_dirs = [
            self.home / ".claude",
            self.home / ".config" / "claude",
            self.home / "Library" / "Application Support" / "Claude",
            self.home / "AppData" / "Roaming" / "Claude",
        ]
        env_claude = os.environ.get("CLAUDE_CONFIG_DIR", "").strip()
        if env_claude and self.home == self.ledger_home:
            base_dirs.append(Path(env_claude).expanduser())

        seen_real_files = set()
        file_items = []
        desktop_projects = set()

        for cbase in base_dirs:
            projects_root = cbase / "projects"
            for sub in ("projects", "transcripts", "claude-code-sessions", "local-agent-mode-sessions"):
                root = cbase / sub
                for fp in safe_walk_files(root, suffix=".jsonl"):
                    try:
                        rp = os.path.realpath(os_path_str(fp))
                        if rp in seen_real_files:
                            continue
                        seen_real_files.add(rp)
                    except Exception:
                        pass

                    ok, ino, fsize, m_ns, m_sec = safe_file_stat(fp)
                    if not ok:
                        continue
                    fk = self._track_file(fp)
                    s_fp = str(fp)
                    norm_fp = s_fp.replace("\\", "/")

                    proj_group = ""
                    if sub == "projects":
                        try:
                            rel_parts = fp.relative_to(projects_root).parts
                            if rel_parts:
                                proj_group = rel_parts[0]
                        except Exception:
                            pass

                    path_is_desktop = (
                        "Claude-Data" in s_fp
                        or "claude-pzs8sxrjxfjjc" in s_fp
                        or "/Library/Application Support/Claude/" in norm_fp
                        or "/Roaming/Claude/" in norm_fp
                    )
                    if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns):
                        cur = self.cursor_store.get(fk)
                        c_meta = cur.get("meta") or {}
                        ep_kind = "desktop" if path_is_desktop else str(c_meta.get("ep") or "unknown")
                        if ep_kind == "desktop" and proj_group:
                            desktop_projects.add(proj_group)
                        file_items.append((fk, ino, fsize, m_ns, cur["byte_offset"], c_meta, False, proj_group, ep_kind, list(cur.get("events") or [])))
                        continue

                    cur = self.cursor_store.get(fk)
                    start_off = 0
                    line_base = 0
                    ep_kind = "desktop" if path_is_desktop else "unknown"
                    file_dedup = {}
                    rot_buckets = list(((cur.get("meta") if cur else None) or {}).get("rotated_buckets") or [])

                    if self.cursor_store.can_append_seek(fk, ino, fsize):
                        start_off = cur["byte_offset"]
                        c_meta = cur.get("meta") or {}
                        if not path_is_desktop:
                            ep_kind = str(c_meta.get("ep") or ep_kind)
                        line_base = safe_int(c_meta.get("ln"))
                        for idx_o, oev in enumerate(cur.get("events") or []):
                            odk = str(oev[9]) if len(oev) > 9 and oev[9] else f"{fk}:{idx_o}"
                            file_dedup[odk] = oev
                    elif cur and cur.get("events"):
                        c_meta = cur.get("meta") or {}
                        if not path_is_desktop:
                            ep_kind = str(c_meta.get("ep") or ep_kind)
                        kept_g = set()
                        for oev in cur["events"]:
                            odk = str(oev[9]) if len(oev) > 9 and oev[9] else ""
                            if odk and not odk.startswith(fk + ":"):
                                file_dedup[odk] = oev
                                kept_g.add(odk)
                        rot_buckets = _fold_rotated_events(rot_buckets, cur["events"], exclude_dedup_keys=kept_g)

                    def _on_claude_line(line: str, rel_idx: int):
                        nonlocal ep_kind
                        if '"entrypoint"' in line:
                            if "claude-desktop" in line:
                                ep_kind = "desktop"
                            elif ep_kind != "desktop" and ('"cli"' in line or '"sdk-cli"' in line):
                                ep_kind = "cli"
                        if '"input_tokens"' not in line:
                            return
                        try:
                            obj = json.loads(line)
                        except Exception:
                            return
                        if not isinstance(obj, dict):
                            return
                        ep_val = str(obj.get("entrypoint") or "").lower()
                        if "desktop" in ep_val:
                            ep_kind = "desktop"
                        elif ep_kind != "desktop" and "cli" in ep_val:
                            ep_kind = "cli"
                        msg = obj.get("message")
                        if not isinstance(msg, dict):
                            return
                        u = msg.get("usage")
                        if not isinstance(u, dict):
                            return
                        raw_m = msg.get("model") or obj.get("model")
                        if raw_m == "<synthetic>":
                            return
                        mdl = normalize_model(raw_m, "claude-sonnet-4-6")
                        fi = safe_int(u.get("input_tokens"))
                        out_tok = safe_int(u.get("output_tokens"))
                        cr = safe_int(u.get("cache_read_input_tokens"))
                        cw = safe_int(u.get("cache_creation_input_tokens"))
                        if fi + out_tok + cr + cw <= 0:
                            return
                        cc_obj = u.get("cache_creation")
                        cw1h = safe_int(cc_obj.get("ephemeral_1h_input_tokens")) if isinstance(cc_obj, dict) else 0
                        if cw1h < 0:
                            cw1h = 0
                        elif cw1h > cw:
                            cw1h = cw
                        mid = str(msg.get("id") or "").strip()
                        rid = str(obj.get("requestId") or "").strip()
                        abs_ln = line_base + rel_idx
                        key = f"{mid}:{rid}" if (mid and rid) else (f"m:{mid}" if mid else (f"r:{rid}" if rid else f"{fk}:{abs_ln}"))
                        dt = to_local_date(obj.get("timestamp"), m_sec, self.tz_offset_min)
                        cost = first_pos_float(obj.get("costUSD"), u.get("cost"))
                        ev_row = ["Claude Code", mdl, dt, fi, out_tok, cr, cw, cost, 1, key, cw1h]
                        prev = file_dedup.get(key)
                        if not prev or (fi + out_tok + cr + cw) >= (prev[3] + prev[4] + prev[5] + prev[6]):
                            if cw1h == 0 and prev and len(prev) > 10:
                                ev_row[10] = min(cw, safe_int(prev[10]))
                            file_dedup[key] = ev_row

                    valid_end = start_off
                    read_cnt = 0
                    try:
                        valid_end, read_cnt = scan_jsonl_from_offset(fp, start_off, m_sec, _on_claude_line)
                    except Exception:
                        pass

                    if path_is_desktop:
                        ep_kind = "desktop"
                    if ep_kind == "desktop" and proj_group:
                        desktop_projects.add(proj_group)

                    n_meta = {"ep": ep_kind, "ln": line_base + read_cnt}
                    if rot_buckets:
                        n_meta["rotated_buckets"] = rot_buckets
                    file_items.append((fk, ino, fsize, m_ns, valid_end, n_meta, True, proj_group, ep_kind, list(file_dedup.values())))

        global_dedup = {}
        for fk, ino, fsize, m_ns, valid_end, n_meta, is_dirty, proj_group, ep_kind, f_evs in file_items:
            is_desktop = (ep_kind == "desktop") or (ep_kind != "cli" and proj_group and proj_group in desktop_projects)
            final_tool = "Claude Desktop" if is_desktop else "Claude Code"
            tool_updated = False
            if is_desktop and n_meta.get("ep") == "unknown":
                n_meta["ep"] = "desktop"
                tool_updated = True

            for ev in (n_meta.get("rotated_buckets") or []):
                cw1h_r = safe_int(ev[10]) if len(ev) > 10 else 0
                self.add_ev(final_tool, ev[1], ev[2], safe_int(ev[3]), safe_int(ev[4]), safe_int(ev[5]), safe_int(ev[6]), safe_float(ev[7]), max(1, safe_int(ev[8])), cw1h=cw1h_r)

            for ev in f_evs:
                if ev[0] != final_tool:
                    ev[0] = final_tool
                    tool_updated = True
                dk = str(ev[9]) if len(ev) > 9 and ev[9] else ""
                prev = global_dedup.get(dk) if dk else None
                if not dk:
                    cw1h_i = safe_int(ev[10]) if len(ev) > 10 else 0
                    self.add_ev(final_tool, ev[1], ev[2], ev[3], ev[4], ev[5], ev[6], ev[7], ev[8], cw1h=cw1h_i)
                elif not prev or (ev[3] + ev[4] + ev[5] + ev[6]) >= (prev[3] + prev[4] + prev[5] + prev[6]):
                    global_dedup[dk] = ev

            if is_dirty or tool_updated:
                self.cursor_store.put(fk, hkey, ino, fsize, m_ns, valid_end, n_meta, f_evs)

        for dk, ev in global_dedup.items():
            if dk in self.claude_seen_keys:
                continue
            self.claude_seen_keys.add(dk)
            cw1h_g = safe_int(ev[10]) if len(ev) > 10 else 0
            self.add_ev(ev[0], ev[1], ev[2], ev[3], ev[4], ev[5], ev[6], ev[7], ev[8], cw1h=cw1h_g)

    @staticmethod
    def _scan_codex_zst_lines(fp: Path, on_line):
        path_str = os_path_str(fp)
        try:
            from compression import zstd
            with zstd.open(path_str, "rt", encoding="utf-8") as reader:
                for index, line in enumerate(reader, 1):
                    if len(line.strip()) > 2:
                        on_line(line.rstrip("\r\n"), index)
            return True
        except ImportError:
            pass
        except Exception:
            return False
        try:
            import zstandard as zstd  # type: ignore
            dctx = zstd.ZstdDecompressor()
            with open(path_str, "rb") as raw_f:
                with dctx.stream_reader(raw_f, read_size=65536) as reader:
                    buf = b""
                    idx = 0
                    while True:
                        chunk = reader.read(65536)
                        if not chunk:
                            break
                        buf += chunk
                        while True:
                            nl = buf.find(b"\n")
                            if nl < 0:
                                break
                            line_b = buf[:nl].rstrip(b"\r")
                            buf = buf[nl + 1 :]
                            if len(line_b) > 2:
                                idx += 1
                                on_line(line_b.decode("utf-8", errors="ignore"), idx)
                    tail = buf.strip()
                    if len(tail) > 2 and tail.endswith(b"}"):
                        idx += 1
                        on_line(tail.decode("utf-8", errors="ignore"), idx)
            return True
        except ImportError:
            pass
        except Exception:
            return False

        try:
            proc = subprocess.run(
                ["zstd", "-dc", path_str],
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                timeout=15,
            )
            if proc.returncode != 0 or not proc.stdout:
                return False
            idx = 0
            for raw_ln in proc.stdout.splitlines():
                line_b = raw_ln.strip()
                if len(line_b) > 2:
                    idx += 1
                    on_line(line_b.decode("utf-8", errors="ignore"), idx)
            return True
        except Exception:
            return False

    def scan_codex_live(self):
        bases = [self.home / ".codex"]
        if self.home == self.ledger_home and os.environ.get("CODEX_HOME", "").strip():
            bases.append(Path(os.environ["CODEX_HOME"]).expanduser())
        seen = set()
        for base in bases:
            for sub in ("sessions", "archived_sessions"):
                for suffix in (".jsonl", ".jsonl.zst"):
                    for fp in safe_walk_files(base / sub, suffix=suffix):
                        real = os.path.realpath(os_path_str(fp))
                        if real in seen:
                            continue
                        seen.add(real)
                        ok, ino, size, m_ns, m_sec = safe_file_stat(fp)
                        if not ok:
                            self.failed_exact_tools.add("OpenAI Codex")
                            continue
                        fk = self._track_file(fp)
                        cur = self.cursor_store.get(fk)
                        ids = UUID_RE.findall(fp.name)
                        tid = ids[-1] if ids else ""
                        if self.cursor_store.is_unchanged(fk, ino, size, m_ns):
                            self._emit_usage_events(fk, cur.get("events") or [])
                            continue
                        compressed = fp.name.endswith(".zst")
                        offset = cur["byte_offset"] if not compressed and cur and size > cur["file_size"] and self.cursor_store.can_append_seek(fk, ino, size) else 0
                        parser = CodexUsageState(tid, m_sec, self.tz_offset_min, cur.get("meta") if offset else None, cur.get("events") if cur else None)
                        failed = False

                        def on_line(line, index):
                            nonlocal failed
                            if not any(word in line for word in ("token_count", "token_usage_record", "turn_context", "thread_settings_applied", "session_meta")):
                                return
                            try:
                                parser.on_line(line, index)
                            except Exception:
                                failed = True
                                self.failed_exact_tools.add("OpenAI Codex")

                        valid_end = offset
                        try:
                            if compressed:
                                if self._scan_codex_zst_lines(fp, on_line):
                                    valid_end = size
                                else:
                                    failed = True
                            else:
                                valid_end, _ = scan_jsonl_from_offset(fp, offset, m_sec, on_line)
                        except Exception:
                            failed = True
                        if failed:
                            self.failed_exact_tools.add("OpenAI Codex")
                        events = list(parser.rows.values())
                        if not failed:
                            self.cursor_store.put(fk, self._norm_home_key(self.home), ino, size, m_ns, valid_end, parser.state(), events)
                        self._emit_usage_events(fk, events)

    @staticmethod
    def _extract_agy_db_step_models(conv_db: Path):
        if not conv_db or not os.path.exists(os_path_str(conv_db)):
            return {}, ""
        conn, tmp = open_sqlite_ro(conv_db)
        if conn is None:
            return {}, ""
        try:
            generations = {}
            for idx, data in conn.execute("SELECT idx, data FROM gen_metadata"):
                try:
                    response = _usage_proto_fields(_usage_proto_fields(data).get(1))
                    backend = response.get(19, b"").decode("utf-8")
                    if backend:
                        generations[idx] = _agy_native_model(backend, response.get(21, b"").decode("utf-8"))
                except (ValueError, TypeError, UnicodeError):
                    continue
            models = {}
            for idx, metadata in conn.execute("SELECT idx, metadata FROM steps WHERE step_type IN (15, 23) ORDER BY idx"):
                try:
                    reference = _usage_proto_fields(metadata).get(20)
                    if reference is not None:
                        generation = _usage_proto_fields(reference).get(3, 0)
                        if generation in generations:
                            models[str(idx)] = generations[generation]
                except (ValueError, TypeError):
                    continue
            return models, ""
        except sqlite3.Error:
            return {}, ""
        finally:
            close_sqlite(conn, tmp)

    def scan_antigravity(self):
        hkey = self._norm_home_key(self.home)
        agy_roots = (
            self.home / ".gemini" / "antigravity",
            self.home / ".gemini" / "antigravity-cli",
            self.home / "Library" / "Application Support" / "Antigravity",
        )
        conv_parents = {}
        conv_db_map = {}
        for root in agy_roots:
            sum_db = root / "conversation_summaries.db"
            if os.path.exists(os_path_str(sum_db)):
                sconn, stmp = open_sqlite_ro(sum_db)
                if sconn:
                    try:
                        scur = sconn.execute(
                            "SELECT conversation_id, parent_conversation_id "
                            "FROM conversation_summaries "
                            "WHERE parent_conversation_id IS NOT NULL AND parent_conversation_id != ''"
                        )
                        for cid, pid in scur.fetchall():
                            if cid and pid and cid != pid and cid not in conv_parents:
                                conv_parents[str(cid)] = str(pid)
                    except Exception:
                        pass
                    finally:
                        close_sqlite(sconn, stmp)
            cdir_dbs = root / "conversations"
            if os.path.exists(os_path_str(cdir_dbs)):
                try:
                    for dbp in cdir_dbs.iterdir():
                        if dbp.name.endswith(".db"):
                            conv_db_map.setdefault(dbp.stem, dbp)
                except Exception:
                    pass

        conv_dirs = []
        seen_conv_ids = set()
        for root in agy_roots:
            brain = root / "brain"
            try:
                if not brain.exists():
                    continue
                for cdir in brain.iterdir():
                    if cdir.name in seen_conv_ids:
                        continue
                    seen_conv_ids.add(cdir.name)
                    conv_dirs.append(cdir)
            except Exception:
                continue

        items = []
        conv_explicit_models = {}
        for cdir in conv_dirs:
            cid = cdir.name
            tf = cdir / ".system_generated" / "logs" / "transcript_full.jsonl"
            if not tf.exists():
                tf = cdir / ".system_generated" / "logs" / "transcript.jsonl"
            ok, ino, fsize, m_ns, m_sec = safe_file_stat(tf)
            if not ok:
                continue
            fk = self._track_file(tf)
            conv_db = conv_db_map.get(cid)
            db_sig = ""
            if conv_db:
                dok, _dino, dsz, dmns, _dmsec = safe_sqlite_combined_stat(conv_db)
                if dok:
                    db_sig = f"{dsz}:{dmns}"

            cur = self.cursor_store.get(fk)
            if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns) and str((cur.get("meta") or {}).get("dbs") or "") == db_sig:
                cmeta = cur.get("meta") or {}
                exp_m = str(cmeta.get("mdl") or "")
                pid = str(cmeta.get("pid") or "")
                if exp_m:
                    conv_explicit_models[cid] = exp_m
                if pid and cid not in conv_parents:
                    conv_parents[cid] = pid
                items.append((cid, fk, ino, fsize, m_ns, cur.get("byte_offset") or 0, exp_m, pid, list(cmeta.get("rotated_buckets") or []), list(cur.get("events") or []), False))
                continue

            start_off = 0
            cur_m = ""
            pid = conv_parents.get(cid, "")
            steps = {}
            rot_buckets = list(((cur.get("meta") if cur else None) or {}).get("rotated_buckets") or [])

            if self.cursor_store.can_append_seek(fk, ino, fsize):
                start_off = cur["byte_offset"]
                cur_m = str((cur.get("meta") or {}).get("mdl") or "")
                if (cur.get("meta") or {}).get("pid"):
                    pid = str(cur["meta"]["pid"])
                for idx_o, oev in enumerate(cur.get("events") or []):
                    sk = str(oev[9]) if len(oev) > 9 and oev[9] else f"s:{idx_o}"
                    steps[sk] = list(oev)
            elif cur and cur.get("events"):
                rot_buckets = _fold_rotated_events(rot_buckets, cur["events"])

            def _on_agy_line(line: str, _idx: int):
                nonlocal cur_m, pid
                if "input_tokens" not in line and "Model Selection" not in line and "sender=" not in line:
                    return
                try:
                    obj = json.loads(line)
                except Exception:
                    self.failed_exact_tools.add("Google Antigravity")
                    return
                if not isinstance(obj, dict):
                    return
                stype = str(obj.get("type") or "")
                cnt = obj.get("content")
                if isinstance(cnt, str) and stype in ("USER_INPUT", "SYSTEM_MESSAGE", ""):
                    if not pid and "sender=" in cnt:
                        sm = re.search(r"sender=([0-9a-fA-F-]{8,64})", cnt)
                        if sm and sm.group(1).lower() != cid.lower():
                            pid = sm.group(1)
                    if "<USER_SETTINGS_CHANGE>" in cnt and "Model Selection" in cnt:
                        mm = AGY_MODEL_RE.search(cnt)
                        if mm:
                            cur_m = normalize_model(mm.group(1), cur_m)
                            if cur_m == "orchestrator":
                                cur_m = "gemini-pro-orchestrator"
                if obj.get("type") not in ("PLANNER_RESPONSE", "CHECKPOINT") or obj.get("status") != "DONE" or obj.get("step_index") is None:
                    return
                fi = safe_int(obj.get("input_tokens"))
                out_tok = safe_int(obj.get("output_tokens"))
                cr = safe_int(obj.get("cache_read_tokens"))
                cw = first_pos_int(obj.get("cache_creation_tokens"), obj.get("cache_write_tokens"))
                if fi + out_tok + cr + cw > 0:
                    sidx = str(obj.get("step_index") if obj.get("step_index") is not None else f"s:{len(steps)}")
                    dt = to_local_date(obj.get("created_at"), m_sec, self.tz_offset_min)
                    steps[sidx] = ["Google Antigravity", cur_m, dt, fi, out_tok, cr, cw, 0.0, 1, sidx]

            valid_end = start_off
            if fsize > start_off:
                try:
                    valid_end, _ = scan_jsonl_from_offset(tf, start_off, m_sec, _on_agy_line)
                except Exception:
                    self.failed_exact_tools.add("Google Antigravity")

            db_step_models, latest_db_model = self._extract_agy_db_step_models(conv_db)
            evs = list(steps.values())
            for ev in evs:
                skey = str(ev[9]) if len(ev) > 9 else ""
                if skey and skey in db_step_models:
                    ev[1] = _agy_native_model(db_step_models[skey], ev[1])
                elif not ev[1]:
                    ev[1] = "unknown"
                elif ev[1] == "orchestrator":
                    ev[1] = "gemini-pro-orchestrator"

            exp_m = latest_db_model or cur_m
            if exp_m == "orchestrator":
                exp_m = "gemini-pro-orchestrator"
            if exp_m:
                conv_explicit_models[cid] = exp_m
            if pid and cid not in conv_parents:
                conv_parents[cid] = pid
            items.append((cid, fk, ino, fsize, m_ns, valid_end, exp_m, pid, rot_buckets, evs, True, db_sig))

        for it in items:
            cid, fk, ino, fsize, m_ns, valid_end, exp_m, pid, rot_buckets, evs = it[:10]
            dirty = it[10]
            db_sig = it[11] if len(it) > 11 else ""
            patched = False
            fallback_m = "unknown"
            for ev in evs:
                if not ev[1] or ev[1] == "unknown":
                    ev[1] = fallback_m
                    patched = True
                elif ev[1] == "orchestrator":
                    ev[1] = "gemini-pro-orchestrator"
                    patched = True
            for rb in rot_buckets:
                if not rb[1] or rb[1] == "unknown":
                    rb[1] = fallback_m
                elif rb[1] == "orchestrator":
                    rb[1] = "gemini-pro-orchestrator"

            if (dirty or patched) and "Google Antigravity" not in self.failed_exact_tools:
                n_meta = {"mdl": exp_m, "pid": pid, "dbs": db_sig}
                if rot_buckets:
                    n_meta["rotated_buckets"] = rot_buckets
                self.cursor_store.put(fk, hkey, ino, fsize, m_ns, valid_end, n_meta, evs)
            self._emit_usage_events(fk, rot_buckets)
            self._emit_usage_events(fk, evs)

    def scan_gemini_and_qwen_chats(self):
        hkey = self._norm_home_key(self.home)
        for tool_name, root, def_m in (
            ("Gemini CLI", self.home / ".gemini" / "tmp", "gemini-2.5-pro"),
            ("Qwen Code", self.home / ".qwen" / "tmp", "qwen3-coder-plus"),
        ):
            for fp in safe_walk_files(root, suffix=".json"):
                if "chats" not in fp.parts:
                    continue
                ok, ino, fsize, m_ns, m_sec = safe_file_stat(fp)
                if not ok:
                    continue
                fk = self._track_file(fp)
                if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns):
                    self._emit_ev_list(self.cursor_store.get(fk).get("events") or [])
                    continue
                file_evs = []
                try:
                    data = json.loads(safe_read_text(fp))
                    for item in ((data.get("messages") if isinstance(data, dict) else None) or []):
                        if not isinstance(item, dict) or not isinstance(item.get("tokens"), dict):
                            continue
                        tok = item["tokens"]
                        raw_in = safe_int(tok.get("input"))
                        out_tok = safe_int(tok.get("output")) + safe_int(tok.get("thoughts"))
                        cr = safe_int(tok.get("cached"))
                        fi = max(0, raw_in - cr) if raw_in >= cr else raw_in
                        if fi + out_tok + cr > 0:
                            mdl = normalize_model(item.get("model"), def_m)
                            dt = to_local_date(item.get("timestamp"), m_sec, self.tz_offset_min)
                            file_evs.append([tool_name, mdl, dt, fi, out_tok, cr, 0, 0.0, 1, ""])
                except Exception:
                    cur_old = self.cursor_store.get(fk)
                    if cur_old and cur_old.get("events"):
                        file_evs = list(cur_old["events"])
                self.cursor_store.put(fk, hkey, ino, fsize, m_ns, fsize, {}, file_evs)
                self._emit_ev_list(file_evs)

    def scan_zcode(self):
        hkey = self._norm_home_key(self.home)
        zdb = self.home / ".zcode" / "cli" / "db" / "db.sqlite"
        ok, ino, csize, m_ns, m_sec = safe_sqlite_combined_stat(zdb)
        if not ok:
            return
        fk = self._track_file(zdb)
        if self.cursor_store.is_unchanged(fk, ino, csize, m_ns):
            self._emit_ev_list(self.cursor_store.get(fk).get("events") or [])
            return

        cur = self.cursor_store.get(fk)
        conn, tmp = open_sqlite_ro(zdb)
        if not conn:
            self.failed_exact_tools.add("ZCode")
            if cur and cur.get("events"):
                self._emit_ev_list(cur["events"])
            return

        fresh_evs = []
        try:
            cols = {r[1] for r in conn.execute("PRAGMA table_info(message)")}
            id_col = "id" if "id" in cols else "rowid"
            for mid, tc, ds in conn.execute(f"SELECT {id_col}, time_created, data FROM message WHERE data LIKE '%\"tokens\"%'"):
                if not ds:
                    continue
                try:
                    d = json.loads(ds)
                except Exception:
                    continue
                if not isinstance(d, dict) or d.get("role") != "assistant" or not isinstance(d.get("tokens"), dict):
                    continue
                tok = d["tokens"]
                raw_in = safe_int(tok.get("input"))
                out_tok = safe_int(tok.get("output"))
                cache = tok.get("cache") if isinstance(tok.get("cache"), dict) else {}
                cr = safe_int(cache.get("read"))
                cw = safe_int(cache.get("write"))
                tot = safe_int(tok.get("total"))
                prov = str(d.get("providerId") or d.get("providerID") or "")
                if (tot > 0 and abs((raw_in + out_tok) - tot) <= 4 and cr > 0) or (raw_in >= cr > 0 and "anthropic" not in prov.lower()):
                    fi = max(0, raw_in - cr - cw)
                else:
                    fi = raw_in
                if fi + out_tok + cr + cw <= 0:
                    continue
                mdl = normalize_model(d.get("modelId") or d.get("modelID") or d.get("model"), "unknown")
                tval = (d.get("time") or {}).get("created") if isinstance(d.get("time"), dict) else None
                dt = to_local_date(tval or tc, m_sec, self.tz_offset_min)
                dk = f"z:{mid}"
                fresh_evs.append(["ZCode", mdl, dt, fi, out_tok, cr, cw, safe_float(d.get("cost")), 1, dk])
        except Exception:
            self.failed_exact_tools.add("ZCode")
        finally:
            close_sqlite(conn, tmp)

        merged_evs = _merge_sqlite_cursor_events(cur, fresh_evs)
        if "ZCode" not in self.failed_exact_tools:
            self.cursor_store.put(fk, hkey, ino, csize, m_ns, csize, {}, merged_evs)
        self._emit_ev_list(merged_evs)

    def scan_kimi(self):
        hkey = self._norm_home_key(self.home)
        for wf in safe_walk_files(self.home / ".kimi-code" / "sessions", exact_name="wire.jsonl"):
            ok, ino, fsize, m_ns, m_sec = safe_file_stat(wf)
            if not ok:
                continue
            fk = self._track_file(wf)
            if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns):
                cur = self.cursor_store.get(fk)
                self._emit_ev_list((cur.get("meta") or {}).get("rotated_buckets") or [])
                self._emit_ev_list(cur.get("events") or [])
                continue

            cur = self.cursor_store.get(fk)
            start_off = 0
            cur_m = "kimi-for-coding"
            file_evs = []
            rot_buckets = list(((cur.get("meta") if cur else None) or {}).get("rotated_buckets") or [])

            if self.cursor_store.can_append_seek(fk, ino, fsize):
                start_off = cur["byte_offset"]
                cur_m = str((cur.get("meta") or {}).get("mdl") or cur_m)
                file_evs = list(cur.get("events") or [])
            elif cur and cur.get("events"):
                rot_buckets = _fold_rotated_events(rot_buckets, cur["events"])

            def _on_kimi_line(line: str, _idx: int):
                nonlocal cur_m
                if '"usage.record"' not in line and '"llm.request"' not in line:
                    return
                try:
                    obj = json.loads(line)
                except Exception:
                    return
                if not isinstance(obj, dict):
                    return
                t = obj.get("type")
                if t == "llm.request":
                    cur_m = normalize_model(obj.get("model") or obj.get("modelAlias"), cur_m)
                elif t == "usage.record" and isinstance(obj.get("usage"), dict):
                    u = obj["usage"]
                    fi = safe_int(u.get("inputOther"))
                    out_tok = safe_int(u.get("output"))
                    cr = safe_int(u.get("inputCacheRead"))
                    cw = safe_int(u.get("inputCacheCreation"))
                    if fi + out_tok + cr + cw > 0:
                        mdl = normalize_model(obj.get("model"), cur_m)
                        dt = to_local_date(obj.get("time"), m_sec, self.tz_offset_min)
                        file_evs.append(["Kimi Code", mdl, dt, fi, out_tok, cr, cw, 0.0, 1, ""])

            valid_end = start_off
            try:
                valid_end, _ = scan_jsonl_from_offset(wf, start_off, m_sec, _on_kimi_line)
            except Exception:
                pass

            n_meta = {"mdl": cur_m}
            if rot_buckets:
                n_meta["rotated_buckets"] = rot_buckets
            self.cursor_store.put(fk, hkey, ino, fsize, m_ns, valid_end, n_meta, file_evs)
            self._emit_ev_list(rot_buckets)
            self._emit_ev_list(file_evs)

    def _build_opencode_ev(self, tool_name, d, fallback_ts, ts_override=None, def_model="deepseek-v4.1-flash", dedup_id=""):
        if not isinstance(d, dict) or d.get("role") != "assistant":
            return None
        tok = d.get("tokens")
        if not isinstance(tok, dict):
            return None
        raw_in = safe_int(tok.get("input"))
        out_tok = first_pos_int(tok.get("output"), tok.get("reasoning"))
        cache = tok.get("cache") if isinstance(tok.get("cache"), dict) else {}
        cr = safe_int(cache.get("read"))
        cw = safe_int(cache.get("write"))
        tot = safe_int(tok.get("total"))
        fi = max(0, raw_in - cr - cw) if (tot > 0 and abs((raw_in + out_tok) - tot) <= 4 and cr > 0) else raw_in
        if fi + out_tok + cr + cw <= 0:
            return None
        mdl = normalize_model(d.get("modelID") or d.get("model"), def_model)
        tval = (d.get("time") or {}).get("created") if isinstance(d.get("time"), dict) else None
        dt = to_local_date(tval or ts_override, fallback_ts, self.tz_offset_min)
        return [tool_name, mdl, dt, fi, out_tok, cr, cw, safe_float(d.get("cost")), 1, dedup_id]

    def scan_opencode_and_mobileclaw(self):
        hkey = self._norm_home_key(self.home)
        xdg_data = Path(os.environ.get("XDG_DATA_HOME", "").strip()).expanduser() if (os.environ.get("XDG_DATA_HOME", "").strip() and self.home == self.ledger_home) else None

        # 1. MobileClaw
        mc_bases = [self.home / ".local" / "share" / "mobileclaw" / "workspaces"]
        if xdg_data:
            mc_bases.append(xdg_data / "mobileclaw" / "workspaces")
        seen_mc_real = set()
        for mc in mc_bases:
            try:
                if not mc.exists():
                    continue
                ws_list = list(mc.iterdir())
            except Exception:
                continue
            for ws in ws_list:
                for mf in safe_walk_files(ws / "storage" / "message", suffix=".json"):
                    try:
                        rp = os.path.realpath(os_path_str(mf))
                        if rp in seen_mc_real:
                            continue
                        seen_mc_real.add(rp)
                    except Exception:
                        pass
                    ok, ino, fsize, m_ns, m_sec = safe_file_stat(mf)
                    if not ok:
                        continue
                    fk = self._track_file(mf)
                    if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns):
                        self._emit_ev_list(self.cursor_store.get(fk).get("events") or [], dedup_set=self.mobileclaw_seen_keys)
                        continue
                    fevs = []
                    try:
                        d = json.loads(safe_read_text(mf))
                        if isinstance(d, dict):
                            mid = str(d.get("id") or mf.stem)
                            ev = self._build_opencode_ev("MobileClaw", d, m_sec, None, "deepseek-v4.1-flash", mid)
                            if ev:
                                fevs.append(ev)
                    except Exception:
                        pass
                    self.cursor_store.put(fk, hkey, ino, fsize, m_ns, fsize, {}, fevs)
                    self._emit_ev_list(fevs, dedup_set=self.mobileclaw_seen_keys)

        # 2. OpenCode (修复单行坏 JSON 中断整表扫描 Bug + SQL 预过滤)
        oc_bases = [
            self.home / ".local" / "share" / "opencode",
            self.home / "AppData" / "Local" / "opencode",
            self.home / "Library" / "Application Support" / "opencode",
        ]
        if xdg_data:
            oc_bases.append(xdg_data / "opencode")
        seen_oc_real = set()
        for base in oc_bases:
            try:
                if not base.exists():
                    continue
                rp_b = os.path.realpath(os_path_str(base))
                if rp_b in seen_oc_real:
                    continue
                seen_oc_real.add(rp_b)
            except Exception:
                continue

            for db_p in base.glob("opencode*.db"):
                ok, ino, csize, m_ns, m_sec = safe_sqlite_combined_stat(db_p)
                if not ok:
                    continue
                fk = self._track_file(db_p)
                if self.cursor_store.is_unchanged(fk, ino, csize, m_ns):
                    self._emit_ev_list(self.cursor_store.get(fk).get("events") or [], dedup_set=self.opencode_seen_keys)
                    continue
                cur = self.cursor_store.get(fk)
                conn, tmp = open_sqlite_ro(db_p)
                if not conn:
                    if cur and cur.get("events"):
                        self._emit_ev_list(cur["events"], dedup_set=self.opencode_seen_keys)
                    continue
                db_evs = []
                try:
                    for mid, tc, ds in conn.execute("SELECT id, time_created, data FROM message WHERE data LIKE '%\"tokens\"%'"):
                        if not ds:
                            continue
                        try:
                            d = json.loads(ds)
                            ev = self._build_opencode_ev("OpenCode", d, m_sec, tc, "claude-sonnet-4-6", str(mid or ""))
                            if ev:
                                db_evs.append(ev)
                        except Exception:
                            continue
                except Exception:
                    pass
                finally:
                    close_sqlite(conn, tmp)

                merged_db_evs = _merge_sqlite_cursor_events(cur, db_evs)
                self.cursor_store.put(fk, hkey, ino, csize, m_ns, csize, {}, merged_db_evs)
                self._emit_ev_list(merged_db_evs, dedup_set=self.opencode_seen_keys)

            for mf in safe_walk_files(base / "storage" / "message", suffix=".json"):
                ok, ino, fsize, m_ns, m_sec = safe_file_stat(mf)
                if not ok:
                    continue
                fk = self._track_file(mf)
                if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns):
                    self._emit_ev_list(self.cursor_store.get(fk).get("events") or [], dedup_set=self.opencode_seen_keys)
                    continue
                fevs = []
                try:
                    d = json.loads(safe_read_text(mf))
                    if isinstance(d, dict):
                        mid = str(d.get("id") or mf.stem)
                        ev = self._build_opencode_ev("OpenCode", d, m_sec, None, "claude-sonnet-4-6", mid)
                        if ev:
                            fevs.append(ev)
                except Exception:
                    pass
                self.cursor_store.put(fk, hkey, ino, fsize, m_ns, fsize, {}, fevs)
                self._emit_ev_list(fevs, dedup_set=self.opencode_seen_keys)

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
                    if isinstance(sd, dict):
                        for k in ("model", "defaultModel", "currentModel", "selectedModel"):
                            if sd.get(k):
                                def_m = normalize_model(str(sd[k]), def_m)
                                break
                except Exception:
                    pass
            self.scan_jsonl_tree(tool_name, [root / "projects"], def_m)

    def scan_dsh(self):
        hkey = self._norm_home_key(self.home)
        for sf in safe_walk_files(self.home / ".dsh" / "storages", suffix=".json"):
            if not sf.name.startswith("session-"):
                continue
            ok, ino, fsize, m_ns, m_sec = safe_file_stat(sf)
            if not ok:
                continue
            fk = self._track_file(sf)
            if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns):
                self._emit_ev_list(self.cursor_store.get(fk).get("events") or [])
                continue
            fevs = []
            try:
                data = json.loads(safe_read_text(sf))
                rows = ((data.get("record") or {}).get("rows")) if isinstance(data, dict) else None
                for item in (rows or []):
                    if not isinstance(item, dict):
                        continue
                    u = item.get("usage") or ((item.get("meta") or {}).get("usage") if isinstance(item.get("meta"), dict) else None)
                    if not isinstance(u, dict):
                        continue
                    raw_in = first_pos_int(u.get("prompt_tokens"), u.get("input_tokens"))
                    out_tok = first_pos_int(u.get("completion_tokens"), u.get("output_tokens"))
                    cr = first_pos_int(u.get("prompt_cache_hit_tokens"), u.get("cache_read_tokens"), u.get("cached_tokens"))
                    fi = (raw_in - cr) if (raw_in >= cr > 0) else raw_in
                    if fi + out_tok + cr > 0:
                        mdl = normalize_model(item.get("model"), "deepseek-flash")
                        dt = to_local_date(item.get("created_at") or item.get("timestamp"), m_sec, self.tz_offset_min)
                        fevs.append(["DeepSeek (DSH)", mdl, dt, fi, out_tok, cr, 0, 0.0, 1, ""])
            except Exception:
                cur_old = self.cursor_store.get(fk)
                if cur_old and cur_old.get("events"):
                    fevs = list(cur_old["events"])
            self.cursor_store.put(fk, hkey, ino, fsize, m_ns, fsize, {}, fevs)
            self._emit_ev_list(fevs)

    def scan_commandcode(self):
        self.scan_jsonl_tree("Command Code", [self.home / ".commandcode" / "projects"], "claude-sonnet-4-6")

    def scan_vscode_cline_family(self):
        """覆盖桌面 IDE 与 Linux VPS Remote SSH / code-server / Windsurf / Antigravity / Trae 全家族插件"""
        hkey = self._norm_home_key(self.home)
        host_Storage_candidates = [
            ("Code", self.home / "AppData" / "Roaming" / "Code" / "User" / "globalStorage"),
            ("Cursor", self.home / "AppData" / "Roaming" / "Cursor" / "User" / "globalStorage"),
            ("Windsurf", self.home / "AppData" / "Roaming" / "Windsurf" / "User" / "globalStorage"),
            ("Antigravity", self.home / "AppData" / "Roaming" / "Antigravity" / "User" / "globalStorage"),
            ("Trae", self.home / "AppData" / "Roaming" / "Trae" / "User" / "globalStorage"),
            ("Code", self.home / ".config" / "Code" / "User" / "globalStorage"),
            ("Code", self.home / ".config" / "Code - Insiders" / "User" / "globalStorage"),
            ("Code", self.home / ".config" / "VSCodium" / "User" / "globalStorage"),
            ("Cursor", self.home / ".config" / "Cursor" / "User" / "globalStorage"),
            ("Windsurf", self.home / ".config" / "Windsurf" / "User" / "globalStorage"),
            ("Antigravity", self.home / ".config" / "Antigravity" / "User" / "globalStorage"),
            ("Trae", self.home / ".config" / "Trae" / "User" / "globalStorage"),
            ("Code", self.home / "Library" / "Application Support" / "Code" / "User" / "globalStorage"),
            ("Code", self.home / "Library" / "Application Support" / "Code - Insiders" / "User" / "globalStorage"),
            ("Code", self.home / "Library" / "Application Support" / "VSCodium" / "User" / "globalStorage"),
            ("Cursor", self.home / "Library" / "Application Support" / "Cursor" / "User" / "globalStorage"),
            ("Windsurf", self.home / "Library" / "Application Support" / "Windsurf" / "User" / "globalStorage"),
            ("Windsurf", self.home / "Library" / "Application Support" / "Windsurf - Next" / "User" / "globalStorage"),
            ("Antigravity", self.home / "Library" / "Application Support" / "Antigravity" / "User" / "globalStorage"),
            ("Trae", self.home / "Library" / "Application Support" / "Trae" / "User" / "globalStorage"),
            # Linux VPS Remote SSH & Web IDE Servers
            ("Code", self.home / ".vscode-server" / "data" / "User" / "globalStorage"),
            ("Code", self.home / ".vscode-server-insiders" / "data" / "User" / "globalStorage"),
            ("Code", self.home / ".vscode-remote" / "data" / "User" / "globalStorage"),
            ("Code", self.home / ".vscodium-server" / "data" / "User" / "globalStorage"),
            ("Code", self.home / ".openvscode-server" / "data" / "User" / "globalStorage"),
            ("Code", self.home / ".local" / "share" / "code-server" / "User" / "globalStorage"),
            ("Cursor", self.home / ".cursor-server" / "data" / "User" / "globalStorage"),
            ("Windsurf", self.home / ".windsurf-server" / "data" / "User" / "globalStorage"),
            ("Windsurf", self.home / ".codeium" / "windsurf-server" / "data" / "User" / "globalStorage"),
            ("Antigravity", self.home / ".antigravity-server" / "data" / "User" / "globalStorage"),
            ("Trae", self.home / ".trae-server" / "data" / "User" / "globalStorage"),
        ]
        exts = [
            ("Cline", "saoudrizwan.claude-dev"),
            ("Roo Code", "rooveterinaryinc.roo-cline"),
            ("Kilo Code", "kilocode.kilo-code"),
        ]
        seen_real_bases = set()
        for ide_label, b in host_Storage_candidates:
            try:
                if not b.exists():
                    continue
                rb = os.path.realpath(os_path_str(b))
                if rb in seen_real_bases:
                    continue
                seen_real_bases.add(rb)
            except Exception:
                continue

            for base_tool, ext_id in exts:
                full_label = f"{base_tool} ({ide_label})"
                for uf in safe_walk_files(b / ext_id / "tasks", exact_name="ui_messages.json"):
                    ok, ino, fsize, m_ns, m_sec = safe_file_stat(uf)
                    if not ok:
                        continue
                    fk = self._track_file(uf)
                    if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns):
                        self._emit_ev_list(self.cursor_store.get(fk).get("events") or [])
                        continue

                    fevs = []
                    try:
                        msgs = json.loads(safe_read_text(uf))
                        for m in (msgs if isinstance(msgs, list) else []):
                            if not isinstance(m, dict) or m.get("say") != "api_req_started" or not m.get("text"):
                                continue
                            try:
                                info = json.loads(m["text"])
                            except Exception:
                                continue
                            if not isinstance(info, dict):
                                continue
                            fi = safe_int(info.get("tokensIn"))
                            out_tok = safe_int(info.get("tokensOut"))
                            cr = safe_int(info.get("cacheReads"))
                            cw = safe_int(info.get("cacheWrites"))
                            if fi + out_tok + cr + cw > 0:
                                mdl = normalize_model(info.get("model"), "claude-sonnet-4-6")
                                dt = to_local_date(m.get("ts"), m_sec, self.tz_offset_min)
                                fevs.append([full_label, mdl, dt, fi, out_tok, cr, cw, safe_float(info.get("cost")), 1, ""])
                    except Exception:
                        cur_old = self.cursor_store.get(fk)
                        if cur_old and cur_old.get("events"):
                            fevs = list(cur_old["events"])
                    self.cursor_store.put(fk, hkey, ino, fsize, m_ns, fsize, {}, fevs)
                    self._emit_ev_list(fevs)

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
        hkey = self._norm_home_key(self.home)
        fdir = self.home / ".factory" / "sessions"
        self.scan_jsonl_tree("Factory Droid", [fdir], "claude-sonnet-4-6")
        for jf in safe_walk_files(fdir, suffix=".json"):
            ok, ino, fsize, m_ns, m_sec = safe_file_stat(jf)
            if not ok:
                continue
            fk = self._track_file(jf)
            if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns):
                self._emit_ev_list(self.cursor_store.get(fk).get("events") or [])
                continue
            fevs = []
            try:
                obj = json.loads(safe_read_text(jf))
                parsed = extract_generic_usage(obj, "claude-sonnet-4-6", m_sec, self.tz_offset_min)
                if parsed:
                    mdl, dt, fi, out_tok, cr, cw, cost, _ = parsed
                    fevs.append(["Factory Droid", mdl, dt, fi, out_tok, cr, cw, cost, 1, ""])
            except Exception:
                pass
            self.cursor_store.put(fk, hkey, ino, fsize, m_ns, fsize, {}, fevs)
            self._emit_ev_list(fevs)

    def scan_amp(self):
        hkey = self._norm_home_key(self.home)
        roots = [
            self.home / ".local" / "share" / "amp" / "threads",
            self.home / ".amp" / "threads",
            self.home / "AppData" / "Local" / "amp" / "threads",
            self.home / "Library" / "Application Support" / "amp" / "threads",
        ]
        seen_real = set()
        for r in roots:
            for jf in safe_walk_files(r, suffix=".json"):
                try:
                    rp = os.path.realpath(os_path_str(jf))
                    if rp in seen_real:
                        continue
                    seen_real.add(rp)
                except Exception:
                    pass
                ok, ino, fsize, m_ns, m_sec = safe_file_stat(jf)
                if not ok:
                    continue
                fk = self._track_file(jf)
                if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns):
                    self._emit_ev_list(self.cursor_store.get(fk).get("events") or [])
                    continue
                fevs = []
                try:
                    data = json.loads(safe_read_text(jf))
                    msgs = data.get("messages") if isinstance(data, dict) else None
                    if isinstance(msgs, list):
                        for m in msgs:
                            p = extract_generic_usage(m, "claude-sonnet-4-6", m_sec, self.tz_offset_min)
                            if p:
                                fevs.append(["Amp", p[0], p[1], p[2], p[3], p[4], p[5], p[6], 1, ""])
                    elif isinstance(data, dict):
                        p = extract_generic_usage(data, "claude-sonnet-4-6", m_sec, self.tz_offset_min)
                        if p:
                            fevs.append(["Amp", p[0], p[1], p[2], p[3], p[4], p[5], p[6], 1, ""])
                except Exception:
                    pass
                self.cursor_store.put(fk, hkey, ino, fsize, m_ns, fsize, {}, fevs)
                self._emit_ev_list(fevs)

    def scan_goose(self):
        hkey = self._norm_home_key(self.home)
        bases = [
            self.home / ".local" / "share" / "goose" / "sessions",
            self.home / "AppData" / "Roaming" / "Block" / "goose" / "data" / "sessions",
            self.home / "Library" / "Application Support" / "Block" / "goose" / "data" / "sessions",
            self.home / "Library" / "Application Support" / "Goose" / "sessions",
        ]
        for b in bases:
            if not b.exists():
                continue
            self.scan_jsonl_tree("Goose", [b], "claude-sonnet-4-6")
            sdb = b / "sessions.db"
            ok, ino, csize, m_ns, m_sec = safe_sqlite_combined_stat(sdb)
            if not ok:
                continue
            fk = self._track_file(sdb)
            if self.cursor_store.is_unchanged(fk, ino, csize, m_ns):
                self._emit_ev_list(self.cursor_store.get(fk).get("events") or [])
                continue
            cur = self.cursor_store.get(fk)
            conn, tmp = open_sqlite_ro(sdb)
            if not conn:
                if cur and cur.get("events"):
                    self._emit_ev_list(cur["events"])
                continue
            db_evs = []
            try:
                tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                if "sessions" in tables:
                    cols = {r[1] for r in conn.execute("PRAGMA table_info(sessions)")}
                    if "input_tokens" in cols and "output_tokens" in cols:
                        id_col = "id" if "id" in cols else "rowid"
                        mcol = "model" if "model" in cols else "'claude-sonnet-4-6'"
                        tcol = "created_at" if "created_at" in cols else ("updated_at" if "updated_at" in cols else "NULL")
                        for sid, mdl, fi, out_tok, ts in conn.execute(
                            f"SELECT {id_col}, {mcol}, input_tokens, output_tokens, {tcol} FROM sessions WHERE input_tokens + output_tokens > 0"
                        ):
                            dt = to_local_date(ts, m_sec, self.tz_offset_min)
                            db_evs.append(["Goose", normalize_model(str(mdl or ""), "claude-sonnet-4-6"), dt, safe_int(fi), safe_int(out_tok), 0, 0, 0.0, 1, f"goose:{sid}"])
            except Exception:
                pass
            finally:
                close_sqlite(conn, tmp)
            merged_evs = _merge_sqlite_cursor_events(cur, db_evs)
            self.cursor_store.put(fk, hkey, ino, csize, m_ns, csize, {}, merged_evs)
            self._emit_ev_list(merged_evs)

    def scan_crush(self):
        hkey = self._norm_home_key(self.home)
        for db_p in (
            self.home / ".local" / "share" / "crush" / "crush.db",
            self.home / "AppData" / "Local" / "crush" / "crush.db",
            self.home / "Library" / "Application Support" / "crush" / "crush.db",
        ):
            ok, ino, csize, m_ns, m_sec = safe_sqlite_combined_stat(db_p)
            if not ok:
                continue
            fk = self._track_file(db_p)
            if self.cursor_store.is_unchanged(fk, ino, csize, m_ns):
                self._emit_ev_list(self.cursor_store.get(fk).get("events") or [])
                continue
            cur = self.cursor_store.get(fk)
            conn, tmp = open_sqlite_ro(db_p)
            if not conn:
                if cur and cur.get("events"):
                    self._emit_ev_list(cur["events"])
                continue
            db_evs = []
            try:
                tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                if "sessions" in tables:
                    cols = {r[1] for r in conn.execute("PRAGMA table_info(sessions)")}
                    if "prompt_tokens" in cols and "completion_tokens" in cols:
                        id_col = "id" if "id" in cols else "rowid"
                        c_col = "cost" if "cost" in cols else "0"
                        t_col = "created_at" if "created_at" in cols else "NULL"
                        for sid, pt, ct, cost, ts in conn.execute(
                            f"SELECT {id_col}, prompt_tokens, completion_tokens, {c_col}, {t_col} FROM sessions WHERE prompt_tokens + completion_tokens > 0"
                        ):
                            dt = to_local_date(ts, m_sec, self.tz_offset_min)
                            db_evs.append(["Charm Crush", "claude-sonnet-4-6", dt, safe_int(pt), safe_int(ct), 0, 0, safe_float(cost), 1, f"crush:{sid}"])
            except Exception:
                pass
            finally:
                close_sqlite(conn, tmp)
            merged_evs = _merge_sqlite_cursor_events(cur, db_evs)
            self.cursor_store.put(fk, hkey, ino, csize, m_ns, csize, {}, merged_evs)
            self._emit_ev_list(merged_evs)

    def scan_aider(self):
        hkey = self._norm_home_key(self.home)
        for fp in (self.home / ".aider" / "analytics.jsonl", self.home / ".aider.analytics.jsonl"):
            ok, ino, fsize, m_ns, m_sec = safe_file_stat(fp)
            if not ok:
                continue
            fk = self._track_file(fp)
            if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns):
                cur = self.cursor_store.get(fk)
                self._emit_ev_list((cur.get("meta") or {}).get("rotated_buckets") or [])
                self._emit_ev_list(cur.get("events") or [])
                continue

            cur = self.cursor_store.get(fk)
            start_off = 0
            fevs = []
            rot_buckets = list(((cur.get("meta") if cur else None) or {}).get("rotated_buckets") or [])
            if self.cursor_store.can_append_seek(fk, ino, fsize):
                start_off = cur["byte_offset"]
                fevs = list(cur.get("events") or [])
            elif cur and cur.get("events"):
                rot_buckets = _fold_rotated_events(rot_buckets, cur["events"])

            def _on_aider_line(line: str, _idx: int):
                if "tokens" not in line:
                    return
                try:
                    obj = json.loads(line)
                except Exception:
                    return
                if not isinstance(obj, dict):
                    return
                props = obj.get("properties") if isinstance(obj.get("properties"), dict) else obj
                fi = first_pos_int(props.get("prompt_tokens"), props.get("sent_tokens"))
                out_tok = first_pos_int(props.get("completion_tokens"), props.get("received_tokens"))
                if fi + out_tok > 0:
                    mdl = normalize_model(props.get("main_model") or props.get("model"), "claude-sonnet-4-6")
                    dt = to_local_date(obj.get("time") or obj.get("timestamp"), m_sec, self.tz_offset_min)
                    cost = first_pos_float(props.get("cost"), props.get("message_cost"))
                    fevs.append(["Aider", mdl, dt, fi, out_tok, 0, 0, cost, 1, ""])

            valid_end = start_off
            try:
                valid_end, _ = scan_jsonl_from_offset(fp, start_off, m_sec, _on_aider_line)
            except Exception:
                pass
            n_meta = {"rotated_buckets": rot_buckets} if rot_buckets else {}
            self.cursor_store.put(fk, hkey, ino, fsize, m_ns, valid_end, n_meta, fevs)
            self._emit_ev_list(rot_buckets)
            self._emit_ev_list(fevs)

    def scan_continue(self):
        hkey = self._norm_home_key(self.home)
        for sf in safe_walk_files(self.home / ".continue" / "sessions", suffix=".json"):
            ok, ino, fsize, m_ns, m_sec = safe_file_stat(sf)
            if not ok:
                continue
            fk = self._track_file(sf)
            if self.cursor_store.is_unchanged(fk, ino, fsize, m_ns):
                self._emit_ev_list(self.cursor_store.get(fk).get("events") or [])
                continue
            fevs = []
            try:
                data = json.loads(safe_read_text(sf))
                for h in ((data.get("history") if isinstance(data, dict) else None) or []):
                    if not isinstance(h, dict):
                        continue
                    for pl in (h.get("promptLogs") or []):
                        p = extract_generic_usage(pl, "claude-sonnet-4-6", m_sec, self.tz_offset_min)
                        if p:
                            fevs.append(["Continue.dev", p[0], p[1], p[2], p[3], p[4], p[5], p[6], 1, ""])
            except Exception:
                pass
            self.cursor_store.put(fk, hkey, ino, fsize, m_ns, fsize, {}, fevs)
            self._emit_ev_list(fevs)

    def scan_vibe_and_agents(self):
        self.scan_jsonl_tree("Mistral Vibe", [self.home / ".vibe" / "logs" / "session"], "devstral")
        self.scan_jsonl_tree("OpenHands", [self.home / ".openhands" / "sessions"], "claude-sonnet-4-6")
        self.scan_jsonl_tree("Hermes Agent", [self.home / ".hermes" / "sessions"], "claude-sonnet-4-6")

    def scan_cursor_native(self):
        """覆盖 Cursor 桌面端与 Linux ~/.cursor-server state.vscdb，并锁定 bubbleId 首次观测日期防跨天翻倍"""
        hkey = self._norm_home_key(self.home)
        for db_p in (
            self.home / "AppData" / "Roaming" / "Cursor" / "User" / "globalStorage" / "state.vscdb",
            self.home / ".config" / "Cursor" / "User" / "globalStorage" / "state.vscdb",
            self.home / "Library" / "Application Support" / "Cursor" / "User" / "globalStorage" / "state.vscdb",
            self.home / "Library" / "Application Support" / "Cursor Nightly" / "User" / "globalStorage" / "state.vscdb",
            self.home / ".cursor-server" / "data" / "User" / "globalStorage" / "state.vscdb",
        ):
            ok, ino, csize, m_ns, m_sec = safe_sqlite_combined_stat(db_p)
            if not ok:
                continue
            fk = self._track_file(db_p)
            if self.cursor_store.is_unchanged(fk, ino, csize, m_ns):
                self._emit_ev_list(self.cursor_store.get(fk).get("events") or [])
                continue

            cur = self.cursor_store.get(fk)
            old_dates = {}
            if cur and cur.get("events"):
                for oev in cur["events"]:
                    if isinstance(oev, list) and len(oev) > 9 and oev[9] and oev[2]:
                        old_dates[str(oev[9])] = str(oev[2])

            conn, tmp = open_sqlite_ro(db_p)
            if not conn:
                if cur and cur.get("events"):
                    self._emit_ev_list(cur["events"])
                continue

            db_evs = []
            try:
                tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                if "cursorDiskKV" in tables:
                    for b_key, val in conn.execute(
                        "SELECT key, value FROM cursorDiskKV WHERE key LIKE 'bubbleId:%' AND value LIKE '%tokenCount%'"
                    ):
                        if not val:
                            continue
                        try:
                            d = json.loads(val)
                            tc = d.get("tokenCount") if isinstance(d, dict) else None
                            if isinstance(tc, dict):
                                fi = safe_int(tc.get("inputTokens"))
                                out_tok = safe_int(tc.get("outputTokens"))
                                if fi + out_tok > 0:
                                    minfo = d.get("modelInfo") if isinstance(d.get("modelInfo"), dict) else {}
                                    mdl = normalize_model(minfo.get("modelName") or d.get("model"), "claude-sonnet-4-6")
                                    b_key_str = str(b_key or "")
                                    c_at = d.get("createdAt")
                                    if c_at is not None and str(c_at).strip() != "":
                                        dt = to_local_date(c_at, m_sec, self.tz_offset_min)
                                    elif b_key_str and b_key_str in old_dates:
                                        dt = old_dates[b_key_str]
                                    else:
                                        dt = to_local_date(None, m_sec, self.tz_offset_min)
                                    db_evs.append(["Cursor", mdl, dt, fi, out_tok, 0, 0, 0.0, 1, b_key_str])
                        except Exception:
                            continue
            except Exception:
                pass
            finally:
                close_sqlite(conn, tmp)

            merged_evs = _merge_sqlite_cursor_events(cur, db_evs)
            self.cursor_store.put(fk, hkey, ino, csize, m_ns, csize, {}, merged_evs)
            self._emit_ev_list(merged_evs)

    def scan_zed(self):
        """修复 Zed cumulative_token_usage 跨天 updated_at 漂移导致的按日账单翻倍 Bug（按 thread 追踪增量 delta）"""
        hkey = self._norm_home_key(self.home)
        for db_p in (
            self.home / ".local" / "share" / "zed" / "threads" / "threads.db",
            self.home / "AppData" / "Local" / "Zed" / "threads" / "threads.db",
            self.home / "Library" / "Application Support" / "Zed" / "threads" / "threads.db",
        ):
            ok, ino, csize, m_ns, m_sec = safe_sqlite_combined_stat(db_p)
            if not ok:
                continue
            fk = self._track_file(db_p)
            if self.cursor_store.is_unchanged(fk, ino, csize, m_ns):
                self._emit_ev_list(self.cursor_store.get(fk).get("events") or [])
                continue

            cur = self.cursor_store.get(fk)
            conn, tmp = open_sqlite_ro(db_p)
            if not conn:
                if cur and cur.get("events"):
                    self._emit_ev_list(cur["events"])
                continue

            prev_threads = dict(((cur.get("meta") if cur else None) or {}).get("zed_threads") or {})
            history_evs = list((cur.get("events") if cur else None) or [])
            new_threads = dict(prev_threads)

            try:
                tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                if "threads" in tables:
                    cols = {r[1] for r in conn.execute("PRAGMA table_info(threads)")}
                    if "updated_at" in cols and "data" in cols:
                        id_col = "id" if "id" in cols else "rowid"
                        for row_id, upd, blob in conn.execute(f"SELECT {id_col}, updated_at, data FROM threads"):
                            if not blob:
                                continue
                            try:
                                text = blob.decode("utf-8", errors="ignore") if isinstance(blob, bytes) else str(blob)
                                if "token_usage" not in text and "input_tokens" not in text:
                                    continue
                                d = json.loads(text)
                                if not isinstance(d, dict):
                                    continue
                                u = d.get("cumulative_token_usage") or d.get("token_usage")
                                if not isinstance(u, dict):
                                    continue
                                fi = safe_int(u.get("input_tokens"))
                                out_tok = safe_int(u.get("output_tokens"))
                                cr = safe_int(u.get("cache_read_input_tokens"))
                                cw = safe_int(u.get("cache_creation_input_tokens"))
                                if fi + out_tok + cr + cw <= 0:
                                    continue
                                m_obj = d.get("model")
                                mdl = normalize_model(
                                    (m_obj.get("model") or m_obj.get("name")) if isinstance(m_obj, dict) else m_obj,
                                    "claude-sonnet-4-6",
                                )
                                tid = str(row_id if row_id is not None else (d.get("id") or d.get("thread_id") or ""))
                                dt = to_local_date(upd, m_sec, self.tz_offset_min)

                                prev_vec = prev_threads.get(tid)
                                if isinstance(prev_vec, list) and len(prev_vec) == 4:
                                    # 若线程被重置则按新全量，否则只记录较上次增长的增量 delta 到更新当日
                                    if fi < prev_vec[0] and out_tok < prev_vec[1]:
                                        d_fi, d_out, d_cr, d_cw = fi, out_tok, cr, cw
                                    else:
                                        d_fi = max(0, fi - safe_int(prev_vec[0]))
                                        d_out = max(0, out_tok - safe_int(prev_vec[1]))
                                        d_cr = max(0, cr - safe_int(prev_vec[2]))
                                        d_cw = max(0, cw - safe_int(prev_vec[3]))
                                else:
                                    d_fi, d_out, d_cr, d_cw = fi, out_tok, cr, cw

                                new_threads[tid] = [fi, out_tok, cr, cw]
                                if d_fi + d_out + d_cr + d_cw > 0:
                                    history_evs.append(["Zed AI", mdl, dt, d_fi, d_out, d_cr, d_cw, 0.0, 1, ""])
                            except Exception:
                                continue
            except Exception:
                pass
            finally:
                close_sqlite(conn, tmp)

            # 折叠同一 (tool, model, dt) 以保持 Zed 历史事件列表紧凑
            collapsed_zed = _fold_rotated_events([], history_evs)
            compact_evs = [b + [""] for b in collapsed_zed]
            self.cursor_store.put(fk, hkey, ino, csize, m_ns, csize, {"zed_threads": new_threads}, compact_evs)
            self._emit_ev_list(compact_evs)

    def scan_custom_rules(self):
        cfg = self.home / ".toklens" / "harnesses.json"
        if not cfg.exists():
            return
        try:
            rules = json.loads(cfg.read_text(encoding="utf-8"))
            for r in (rules if isinstance(rules, list) else []):
                if not isinstance(r, dict):
                    continue
                name = str(r.get("name") or "Custom Harness")
                raw_dir = str(r.get("dir") or "").strip()
                if not raw_dir:
                    continue
                if raw_dir.startswith("~"):
                    folder = self.home / raw_dir[1:].lstrip("/\\")
                else:
                    folder = Path(raw_dir)
                def_m = str(r.get("default_model") or "claude-sonnet-4-6")
                pat = str(r.get("pattern") or "").strip()
                suf = str(r.get("suffix") or "").strip()
                if folder.exists():
                    if pat:
                        self.scan_jsonl_tree(name, [folder], def_m, suffix="", pattern=pat)
                    else:
                        self.scan_jsonl_tree(name, [folder], def_m, suffix=(suf or ".jsonl"))
        except Exception:
            pass

    def scan_codex_sqlite_archive(self):
        # A single tokens_used total cannot establish a cache/output split.
        # Retain its backup, but never manufacture a precise bill from ratios.
        return

    def build_records(self):
        buckets = {}
        for ev in self.events:
            tool, model, dt, fi, out_tok, cr, cw, logged_cost, turns = ev[:9]
            cw1h = safe_int(ev[9]) if len(ev) > 9 else 0
            fi = max(0, min(MAX_SAFE_TOKENS, safe_int(fi)))
            out_tok = max(0, min(MAX_SAFE_TOKENS, safe_int(out_tok)))
            cr = max(0, min(MAX_SAFE_TOKENS, safe_int(cr)))
            cw = max(0, min(MAX_SAFE_TOKENS, safe_int(cw)))
            cw1h_c = min(cw, max(0, cw1h))
            cw5m = max(0, cw - cw1h_c)
            turns = max(1, min(1_000_000_000, safe_int(turns)))
            inp_r, out_r, cr_r, cw_r = self.pricing.lookup(model)
            cw1h_r = max(cw_r, inp_r * 2.0)
            calc = min(1e12, max(0.0, (fi * inp_r + out_tok * out_r + cr * cr_r + cw5m * cw_r + cw1h_c * cw1h_r) / 1e6))
            base = min(1e12, max(0.0, ((fi + cr + cw) * inp_r + out_tok * out_r) / 1e6))
            safe_logged = safe_float(logged_cost)
            actual = safe_logged if safe_logged > 0 else calc
            baseline = max(base, actual)
            rate_lbl = f"${inp_r:g} / ${out_r:g}" if any((inp_r, out_r, cr_r, cw_r)) else "未定价"
            k = (dt, tool, model)
            if k not in buckets:
                buckets[k] = [dt, tool, model, 0, 0, 0, 0, 0, 0.0, 0.0, rate_lbl]
            b = buckets[k]
            b[3] = min(1_000_000_000, b[3] + turns)
            b[4] = min(MAX_BUCKET_TOKENS, b[4] + fi)
            b[5] = min(MAX_BUCKET_TOKENS, b[5] + cr)
            b[6] = min(MAX_BUCKET_TOKENS, b[6] + cw)
            b[7] = min(MAX_BUCKET_TOKENS, b[7] + out_tok)
            b[8] = min(1e12, b[8] + actual)
            b[9] = min(1e12, b[9] + baseline)
        rows = sorted(buckets.values(), key=lambda x: (x[0], x[1], x[2]))
        for r in rows:
            r[8] = round(r[8], 6)
            r[9] = round(r[9], 6)
        return rows


def normalize_record_row(row):
    """校验并归一化 11 列统计快照行: [date, tool, model, turns, fi, cr, cw, out, cost_usd, base_usd, rate_label]"""
    if not isinstance(row, (list, tuple)) or len(row) < 10:
        return None
    dt = str(row[0] or "").strip()[:10]
    tool = str(row[1] or "").strip()
    model = str(row[2] or "").strip()
    if len(dt) != 10 or not tool or not model:
        return None
    turns = max(0, safe_int(row[3]))
    fi = max(0, min(MAX_BUCKET_TOKENS, int(row[4]) if isinstance(row[4], int) and not isinstance(row[4], bool) else safe_int(row[4])))
    cr = max(0, min(MAX_BUCKET_TOKENS, int(row[5]) if isinstance(row[5], int) and not isinstance(row[5], bool) else safe_int(row[5])))
    cw = max(0, min(MAX_BUCKET_TOKENS, int(row[6]) if isinstance(row[6], int) and not isinstance(row[6], bool) else safe_int(row[6])))
    out_tok = max(0, min(MAX_BUCKET_TOKENS, int(row[7]) if isinstance(row[7], int) and not isinstance(row[7], bool) else safe_int(row[7])))
    if fi + cr + cw + out_tok <= 0 and turns <= 0:
        return None
    cost_usd = max(0.0, safe_float(row[8]))
    base_usd = max(cost_usd, safe_float(row[9]))
    rate_lbl = str(row[10] if len(row) > 10 and row[10] is not None else "")
    return [dt, tool, model, max(1, turns), fi, cr, cw, out_tok, round(cost_usd, 6), round(base_usd, 6), rate_lbl]


def _collapse_record_rows(rows) -> dict:
    """将同一来源内的记录按 (date, tool, model) 去重折叠"""
    out = {}
    for raw in (rows or []):
        r = normalize_record_row(raw)
        if not r:
            continue
        k = (r[0], r[1], r[2])
        if k not in out:
            out[k] = r
        else:
            ex = out[k]
            ex[3] = min(1_000_000_000, ex[3] + r[3])
            ex[4] = min(MAX_BUCKET_TOKENS, ex[4] + r[4])
            ex[5] = min(MAX_BUCKET_TOKENS, ex[5] + r[5])
            ex[6] = min(MAX_BUCKET_TOKENS, ex[6] + r[6])
            ex[7] = min(MAX_BUCKET_TOKENS, ex[7] + r[7])
            ex[8] = round(min(1e12, ex[8] + r[8]), 6)
            ex[9] = round(min(1e12, ex[9] + r[9]), 6)
            if r[10]:
                ex[10] = r[10]
    return out


def merge_records_high_water(primary_rows, historical_rows, historical_scanner_rev=None, primary_partitions=None, historical_partitions=None):
    """Replace proved day/tool partitions; retain coherent whole legacy rows."""
    p_map, h_map = _collapse_record_rows(primary_rows), _collapse_record_rows(historical_rows)
    primary_partitions, historical_partitions = primary_partitions or {}, historical_partitions or {}
    owners = {}
    for key in set(primary_partitions) | set(historical_partitions):
        p_time, h_time = safe_int(primary_partitions.get(key)), safe_int(historical_partitions.get(key))
        if p_time > 0 or h_time > 0:
            owners[key] = p_time >= h_time
    merged = []
    for key in set(p_map) | set(h_map):
        current, previous = p_map.get(key), h_map.get(key)
        partition = key[0] + "||" + key[1]
        if partition in owners:
            chosen = current if owners[partition] else previous
        elif current and previous:
            chosen = max((current, previous), key=lambda r: (r[3], sum(r[4:8])))
        else:
            chosen = current or previous
        if chosen is not None:
            merged.append(list(chosen))
    return sorted(merged, key=lambda r: (r[0], r[1], r[2]))


def merge_with_ledger_db(home: Path, current_rows: list, strict: bool = False, reconciled_partitions=None) -> list:
    """
    使用本地 ~/.toklens/ledger.db (SQLite WAL) 进行持久化高水位归档：
    - 仅增量写入新增、发生变化或需淘汰旧错分类的行，降低 VPS 磁盘写放大；
    - 当 strict=True（如云同步阶段）且本地已有 ledger.db 读取失败/锁死时，严禁返回未归档残缺快照冲刷云端，直接抛出异常中止覆盖。
    """
    db_path = get_ledger_db_path(home)
    conn = None
    try:
        conn = _open_ledger_rw(home)
        archived_rows = []
        archived_map = {}
        cur = conn.execute(
            "SELECT rec_date, tool, model, turns, fi, cr, cw, out_tok, cost_usd, base_usd, rate_label "
            "FROM daily_archive"
        )
        for db_row in cur.fetchall():
            lst = list(db_row)
            archived_rows.append(lst)
            archived_map[(str(lst[0]), str(lst[1]), str(lst[2]))] = (
                safe_int(lst[3]),
                safe_int(lst[4]),
                safe_int(lst[5]),
                safe_int(lst[6]),
                safe_int(lst[7]),
                round(safe_float(lst[8]), 6),
                round(safe_float(lst[9]), 6),
                str(lst[10] or ""),
            )

        proof_row = conn.execute("SELECT v FROM kv_meta WHERE k='accounting_partitions'").fetchone()
        old_proofs = json.loads(proof_row[0]) if proof_row else {}
        merged = merge_records_high_water(current_rows, archived_rows, primary_partitions=reconciled_partitions, historical_partitions=old_proofs)
        proofs = dict(old_proofs)
        for key, value in (reconciled_partitions or {}).items():
            proofs[key] = max(safe_int(value), safe_int(proofs.get(key)))
        conn.execute("INSERT OR REPLACE INTO kv_meta(k,v) VALUES ('accounting_partitions',?)", (json.dumps(proofs, separators=(",", ":")),))
        if reconciled_partitions is not None:
            reconciled_partitions.clear()
            reconciled_partitions.update(proofs)
        merged_keys = set((r[0], r[1], r[2]) for r in merged)
        obsolete_keys = [k for k in archived_map.keys() if k not in merged_keys]

        diff_params = []
        for r in merged:
            k = (r[0], r[1], r[2])
            val_tup = (r[3], r[4], r[5], r[6], r[7], round(r[8], 6), round(r[9], 6), r[10])
            if archived_map.get(k) != val_tup:
                diff_params.append((r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8], r[9], r[10]))

        if obsolete_keys or diff_params:
            if obsolete_keys:
                conn.executemany(
                    "DELETE FROM daily_archive WHERE rec_date = ? AND tool = ? AND model = ?",
                    obsolete_keys,
                )
            if diff_params:
                conn.executemany(
                    "INSERT OR REPLACE INTO daily_archive "
                    "(rec_date, tool, model, turns, fi, cr, cw, out_tok, cost_usd, base_usd, rate_label) "
                    "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                    diff_params,
                )
        conn.commit()
        return merged
    except Exception as ex:
        append_agent_log(home, "ERROR", f"ledger.db 读写异常: {ex}")
        raise RuntimeError("本地归档账本 ledger.db 访问失败，已中止本次扫描及同步以保护历史数据") from ex
    finally:
        if conn is not None:
            try:
                conn.close()
            except Exception:
                pass


def scan_all(home_dir=None, tz_offset_min: int = None, include_other_users: bool = False):
    """对外扫描入口：扫描指定 HOME 目录并返回高水位归档后的记录列表"""
    target_home = Path(home_dir).expanduser() if home_dir else Path.home()
    return MultiHarnessScanner(
        target_home,
        tz_offset_min=tz_offset_min,
        include_other_users=include_other_users,
    ).scan_all()


def build_snapshot(
    home: Path,
    machine_id: str,
    machine_name: str,
    machine_type: str = "vps",
    tz_offset_min: int = None,
    include_other_users: bool = False,
    strict_ledger: bool = False,
) -> dict:
    resolved_home = Path(home).expanduser() if home else Path.home()
    scanner = MultiHarnessScanner(
        resolved_home,
        tz_offset_min=tz_offset_min,
        include_other_users=include_other_users,
    )
    records = scanner.scan_all(strict_ledger=strict_ledger)
    snap = SnapshotDict(
        {
            "schema": SCHEMA_VERSION,
            "agent_version": AGENT_VERSION,
            "scanner_rev": SCANNER_REV,
            "accounting_updated_ms": int(time.time() * 1000),
            "reconciled_partitions": scanner.reconciled_partitions,
            "machine_id": sanitize_machine_id(machine_id),
            "machine_name": machine_name.strip() or socket.gethostname(),
            "machine_type": machine_type,
            "os": f"{platform.system()} {platform.release()} ({platform.machine()})",
            "synced_at": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "records": records,
        }
    )
    if tz_offset_min is not None:
        snap["tz_offset_min"] = int(tz_offset_min)
    snap._local_home = resolved_home
    return snap


def _http_request_with_retry(make_req_fn, timeout: float = 20.0, retries: int = 3):
    """
    带指数退避重试与 Gzip 解压的 HTTP 执行器：
    - 对 408 / 429 / 500 / 502 / 503 / 504 及网络抖动超时执行最多 3 次退避重试（1.0s, 2.5s）；
    - 对 401 / 403 / 404 / 405 等确定性状态码立即向上抛出 HTTPError，绝不无效死等。
    """
    backoffs = (1.0, 2.5, 5.0)
    last_err = None
    for attempt in range(max(1, retries)):
        try:
            req = make_req_fn()
            if not req.has_header("Accept-encoding") and not req.has_header("Accept-Encoding"):
                req.add_header("Accept-Encoding", "gzip")
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                status = getattr(resp, "status", 200) or 200
                raw = resp.read()
                enc = (resp.headers.get("Content-Encoding") or "").lower()
                if "gzip" in enc or (len(raw) >= 2 and raw[0] == 0x1F and raw[1] == 0x8B):
                    try:
                        raw = gzip.decompress(raw)
                    except Exception:
                        pass
                return status, raw
        except urllib.error.HTTPError as he:
            last_err = he
            if he.code in (408, 429, 500, 502, 503, 504) and attempt + 1 < retries:
                time.sleep(backoffs[min(attempt, len(backoffs) - 1)])
                continue
            raise
        except (urllib.error.URLError, socket.timeout, TimeoutError, ConnectionError, OSError) as ne:
            last_err = ne
            if attempt + 1 < retries:
                time.sleep(backoffs[min(attempt, len(backoffs) - 1)])
                continue
            raise
    if last_err is not None:
        raise last_err
    raise RuntimeError("HTTP 请求未知失败")


def sync_gist(snapshot: dict, gist_id: str, token: str, home: Path = None) -> dict:
    """
    通过 GitHub Gist API 双向安全同步：
    1. Pre-GET 读取云端该节点的既有快照（带重试）；若 Pre-GET 发生 401/403/404/5xx/超时，立即抛错中止，严禁盲目覆盖远端历史！
    2. 仅当 200 OK 且远端已存在同名节点文件时执行高水位合并并回写本地 ledger.db；若远端尚无该节点文件（首次接入），直接进入上传；
    3. PATCH 上传最新合并快照（带重试）。
    """
    gid = gist_id.strip()
    if "/" in gid:
        gid = gid.rstrip("/").rsplit("/", 1)[-1]
    fname = f"toklens_node_{snapshot['machine_id']}.json"
    api_url = f"https://api.github.com/gists/{gid}"
    headers = {
        "Authorization": f"Bearer {token.strip()}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": f"TokLens-Agent/{AGENT_VERSION}",
    }
    target_home = home or getattr(snapshot, "_local_home", None) or Path.home()

    # 1. Pre-GET 合并云端历史（严禁静默吞掉网络/5xx/鉴权异常！）
    _, raw_meta = _http_request_with_retry(
        lambda: urllib.request.Request(api_url, headers=dict(headers), method="GET"),
        timeout=20.0,
        retries=3,
    )
    gist_meta = json.loads(raw_meta.decode("utf-8"))
    files_map = gist_meta.get("files") or {}
    f_obj = files_map.get(fname)
    if isinstance(f_obj, dict):
        remote_text = f_obj.get("content") or ""
        if f_obj.get("truncated") and f_obj.get("raw_url"):
            raw_u = str(f_obj["raw_url"]).strip()
            parsed_raw = urllib.parse.urlparse(raw_u)
            raw_host = (parsed_raw.hostname or "").lower()
            if parsed_raw.scheme != "https" or raw_host not in (
                "gist.githubusercontent.com",
                "raw.githubusercontent.com",
                "api.github.com",
            ):
                raise ValueError(f"拒绝访问非授信 Gist raw_url 主机: {raw_host}")
            raw_headers = dict(headers) if raw_host == "api.github.com" else {"User-Agent": headers["User-Agent"]}
            _, raw_body = _http_request_with_retry(
                lambda: urllib.request.Request(raw_u, headers=dict(raw_headers), method="GET"),
                timeout=25.0,
                retries=3,
            )
            remote_text = raw_body.decode("utf-8")
        if remote_text.strip():
            remote_snap = json.loads(remote_text)
            if isinstance(remote_snap, dict) and isinstance(remote_snap.get("records"), list):
                hist_rev = safe_int(remote_snap.get("scanner_rev"))
                p_proofs = dict(snapshot.get("reconciled_partitions") or {})
                h_proofs = remote_snap.get("reconciled_partitions") or {} if hist_rev >= RECONCILED_REVISION else {}
                merged_rows = merge_records_high_water(snapshot.get("records", []), remote_snap["records"], hist_rev, p_proofs, h_proofs)
                for key, value in h_proofs.items():
                    p_proofs[key] = max(safe_int(value), safe_int(p_proofs.get(key)))
                snapshot["records"] = merge_with_ledger_db(target_home, merged_rows, strict=True, reconciled_partitions=p_proofs)
                snapshot["reconciled_partitions"] = p_proofs

    # 2. PATCH 上传合并后的最新快照
    payload = {"files": {fname: {"content": json.dumps(snapshot, ensure_ascii=False, separators=(",", ":"))}}}
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    patch_headers = dict(headers)
    patch_headers["Content-Type"] = "application/json; charset=utf-8"
    _, patch_resp = _http_request_with_retry(
        lambda: urllib.request.Request(api_url, data=body, headers=dict(patch_headers), method="PATCH"),
        timeout=30.0,
        retries=3,
    )
    return json.loads(patch_resp.decode("utf-8"))


def upload_gist(snapshot: dict, gist_id: str, token: str, home: Path = None) -> dict:
    return sync_gist(snapshot, gist_id, token, home=home)


def sync_webdav(snapshot: dict, webdav_url: str, user: str, password: str, home: Path = None) -> dict:
    """
    通过 WebDAV 双向安全同步：
    1. 尝试 MKCOL 确保目录存在；
    2. Pre-GET 读取远端既有同名节点文件：仅在 HTTP 404 时确认为新节点跳过合并；
       若发生 401/403/429/5xx 或网络超时，立即抛错中止 PUT，严禁盲目覆盖远端历史！
    3. PUT 上传合并后快照。
    """
    base = webdav_url.strip().rstrip("/")
    if not base.lower().endswith("/toklens"):
        base += "/TokLens"
    fname = f"toklens_node_{snapshot['machine_id']}.json"
    auth = base64.b64encode(f"{user}:{password}".encode("utf-8")).decode("ascii")
    base_headers = {
        "Authorization": f"Basic {auth}",
        "User-Agent": f"TokLens-Agent/{AGENT_VERSION}",
    }
    target_home = home or getattr(snapshot, "_local_home", None) or Path.home()

    # 1. 确保目录存在 (MKCOL)
    try:
        mk_req = urllib.request.Request(base + "/", headers=dict(base_headers), method="MKCOL")
        with urllib.request.urlopen(mk_req, timeout=10.0) as _:
            pass
    except Exception:
        pass

    file_url = f"{base}/{fname}"

    # 2. Pre-GET 远端快照（仅 404 允许跳过合并；其余错误一律中断保护云端）
    try:
        _, raw_remote = _http_request_with_retry(
            lambda: urllib.request.Request(file_url, headers=dict(base_headers), method="GET"),
            timeout=20.0,
            retries=3,
        )
        remote_text = raw_remote.decode("utf-8")
        if remote_text.strip():
            remote_snap = json.loads(remote_text)
            if isinstance(remote_snap, dict) and isinstance(remote_snap.get("records"), list):
                hist_rev = safe_int(remote_snap.get("scanner_rev"))
                p_proofs = dict(snapshot.get("reconciled_partitions") or {})
                h_proofs = remote_snap.get("reconciled_partitions") or {} if hist_rev >= RECONCILED_REVISION else {}
                merged_rows = merge_records_high_water(snapshot.get("records", []), remote_snap["records"], hist_rev, p_proofs, h_proofs)
                for key, value in h_proofs.items():
                    p_proofs[key] = max(safe_int(value), safe_int(p_proofs.get(key)))
                snapshot["records"] = merge_with_ledger_db(target_home, merged_rows, strict=True, reconciled_partitions=p_proofs)
                snapshot["reconciled_partitions"] = p_proofs
    except urllib.error.HTTPError as he:
        if he.code != 404:
            raise
    # 非 404 的 URLError / TimeoutError 直接向上抛出，绝不盲目 PUT

    # 3. PUT 上传合并后的最新快照
    body = json.dumps(snapshot, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    put_headers = dict(base_headers)
    put_headers["Content-Type"] = "application/json; charset=utf-8"
    status, _ = _http_request_with_retry(
        lambda: urllib.request.Request(file_url, data=body, headers=dict(put_headers), method="PUT"),
        timeout=30.0,
        retries=3,
    )
    return {"status": status, "url": file_url}


def upload_webdav(snapshot: dict, webdav_url: str, user: str, password: str, home: Path = None) -> dict:
    return sync_webdav(snapshot, webdav_url, user, password, home=home)


def get_installed_cron_lines() -> list:
    if os.name == "nt":
        return []
    try:
        res = subprocess.run(["crontab", "-l"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=5)
        if res.returncode != 0:
            return []
        return [
            ln.strip()
            for ln in (res.stdout or "").splitlines()
            if CRON_MARKER in ln or "toklens_agent.py" in ln
        ]
    except Exception:
        return []


def install_cron(
    script_path: Path,
    interval_min: int,
    home: Path = None,
    explicit_home: str = None,
    tz_offset_min: int = None,
    no_multi_user: bool = False,
):
    if os.name == "nt":
        raise RuntimeError("crontab 仅适用于 Linux / macOS 环境；Windows 环境请使用任务计划程序或桌面客户端。")
    mins = max(1, min(59, int(interval_min)))
    py = shlex.quote(sys.executable or "python3")
    sp = shlex.quote(str(script_path.resolve()))
    cmd_parts = [py, sp]
    if explicit_home:
        cmd_parts.extend(["--home", shlex.quote(str(Path(explicit_home).expanduser().resolve()))])
    if tz_offset_min is not None:
        cmd_parts.extend(["--tz", shlex.quote(str(int(tz_offset_min)))])
    if no_multi_user:
        cmd_parts.append("--no-multi-user")
    cmd_parts.extend(["--sync", "--quiet"])

    target_home = Path(home).expanduser().resolve() if home else Path.home().resolve()
    ensure_toklens_dir(target_home)
    err_log = shlex.quote(str(target_home / ".toklens" / "agent.log"))
    cron_cmd = f"{' '.join(cmd_parts)} >>{err_log} 2>&1".replace("%", "\\%")
    cron_line = f"*/{mins} * * * * {cron_cmd} {CRON_MARKER}"

    res = subprocess.run(["crontab", "-l"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    existing = res.stdout.splitlines() if res.returncode == 0 else []
    filtered = [ln for ln in existing if CRON_MARKER not in ln and "toklens_agent.py" not in ln]
    filtered.append(cron_line)
    new_cron = "\n".join(filtered).strip() + "\n"
    proc = subprocess.run(["crontab", "-"], input=new_cron, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if proc.returncode != 0:
        raise RuntimeError(f"写入 crontab 失败: {proc.stderr.strip()}")
    append_agent_log(target_home, "INFO", f"已安装定时任务 (每 {mins} 分钟): {cron_line}")
    return cron_line


def uninstall_cron(home: Path = None) -> int:
    if os.name == "nt":
        return 0
    res = subprocess.run(["crontab", "-l"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if res.returncode != 0 or not (res.stdout or "").strip():
        return 0
    existing = res.stdout.splitlines()
    filtered = [ln for ln in existing if CRON_MARKER not in ln and "toklens_agent.py" not in ln]
    removed = len(existing) - len(filtered)
    if removed <= 0:
        return 0
    if filtered:
        new_cron = "\n".join(filtered).strip() + "\n"
        proc = subprocess.run(["crontab", "-"], input=new_cron, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if proc.returncode != 0:
            raise RuntimeError(f"更新 crontab 失败: {proc.stderr.strip()}")
    else:
        subprocess.run(["crontab", "-r"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if home is not None:
        append_agent_log(home, "INFO", f"已卸载 {removed} 条 TokLens crontab 定时任务")
    return removed


def _format_tz_label(tz_offset_min: int = None) -> str:
    if tz_offset_min is not None:
        sign = "+" if tz_offset_min >= 0 else "-"
        absm = abs(int(tz_offset_min))
        return f"固定对齐 UTC{sign}{absm // 60:02d}:{absm % 60:02d} ({tz_offset_min} min)"
    now_local = datetime.datetime.now().astimezone()
    off = now_local.utcoffset()
    mins = int(off.total_seconds() // 60) if off is not None else 0
    sign = "+" if mins >= 0 else "-"
    absm = abs(mins)
    tz_name = now_local.tzname() or "System"
    return f"系统本地时区 {tz_name} (UTC{sign}{absm // 60:02d}:{absm % 60:02d})"


def _mask_secret(s: str, head: int = 4, tail: int = 3) -> str:
    val = (s or "").strip()
    if not val:
        return "(未配置)"
    if len(val) <= head + tail + 2:
        return val[:2] + "***"
    return f"{val[:head]}...{val[-tail:]}"


def _detect_harness_presence(scan_homes: list) -> list:
    """探测各扫描主目录下已存在的 AI Harness 与 Remote SSH 数据源"""
    probes = [
        ("Claude Code", [".claude/projects", ".claude/transcripts", ".config/claude/projects", ".config/claude/transcripts"]),
        ("OpenAI Codex", [".codex/sessions", ".codex/archived_sessions", ".codex/state_5.sqlite"]),
        ("Gemini CLI", [".gemini/tmp", ".gemini/antigravity/conversations"]),
        ("Antigravity Remote/CLI", [".antigravity-server/data/User/globalStorage", ".config/Antigravity/User/globalStorage"]),
        ("VS Code Remote SSH", [".vscode-server/data/User/globalStorage", ".vscode-server-insiders/data/User/globalStorage", ".vscode-remote/data/User/globalStorage"]),
        ("Cursor Remote/CLI", [".cursor-server/data/User/globalStorage", ".config/Cursor/User/globalStorage", ".cursor/chats"]),
        ("Windsurf Remote", [".windsurf-server/data/User/globalStorage", ".codeium/windsurf-server/data/User/globalStorage"]),
        ("Trae Remote", [".trae-server/data/User/globalStorage"]),
        ("code-server / OpenVSCode", [".local/share/code-server/User/globalStorage", ".openvscode-server/data/User/globalStorage", ".vscodium-server/data/User/globalStorage"]),
        ("OpenCode", [".local/share/opencode/opencode.db", ".local/share/opencode/storage/message"]),
        ("OpenClaw", [".openclaw/agents"]),
        ("Kimi CLI", [".kimi/sessions"]),
        ("Qwen Code", [".qwen/projects", ".qwen/tmp"]),
        ("Aider", [".aider.chat.history.md", ".aider/analytics.jsonl"]),
        ("Continue", [".continue/sessions"]),
        ("Zed", [".local/share/zed/threads/threads.db"]),
        ("Goose / Crush / Pi / Amp", [".local/share/goose/sessions", ".crush/logs", ".pi/agent/sessions", ".local/share/amp/threads"]),
    ]
    found = []
    for label, rels in probes:
        hit_paths = []
        for h in scan_homes:
            for r in rels:
                cand = h / r
                if os.path.exists(os_path_str(cand)):
                    hit_paths.append(str(cand))
        if hit_paths:
            found.append((label, hit_paths))
    return found


def run_status(home: Path, cfg: dict, tz_offset_min: int, include_other_users: bool):
    cfg_dir = home / ".toklens"
    cfg_file = cfg_dir / "config.json"
    db_path = get_ledger_db_path(home)
    log_fp = cfg_dir / "agent.log"
    scan_homes = discover_scan_homes(home, include_other_users=include_other_users)

    m_name = cfg.get("machine_name") or socket.gethostname()
    m_id = sanitize_machine_id(cfg.get("machine_id") or m_name)
    provider = (cfg.get("provider") or "").strip().lower()

    print(f"=== 知微 · TokLens Linux VPS 探针状态 (v{AGENT_VERSION}) ===")
    print(f"  节点名称 / ID  : {m_name} ({m_id})")
    print(f"  主配置目录     : {cfg_dir} ({'已就绪' if cfg_file.exists() else '未配对 config.json'})")
    print(f"  时区切分模式   : {_format_tz_label(tz_offset_min)}")
    print(f"  扫描用户目录   : {', '.join(str(p) for p in scan_homes)}")

    if provider == "gist":
        print(f"  云同步通道     : GitHub Gist (ID: {_mask_secret(cfg.get('gist_id', ''), 6, 4)}, Token: {_mask_secret(cfg.get('gist_token', ''), 4, 4)})")
    elif provider == "webdav":
        print(f"  云同步通道     : WebDAV ({cfg.get('webdav_url', '')}, User: {cfg.get('webdav_user', '')})")
    else:
        print("  云同步通道     : 未配置 (可使用 --pair <配对码> 绑定)")

    cron_lines = get_installed_cron_lines()
    if cron_lines:
        print(f"  Crontab 定时   : 已启用 ({cron_lines[0]})")
    elif os.name == "nt":
        print("  Crontab 定时   : N/A (当前为 Windows 环境)")
    else:
        print("  Crontab 定时   : 未安装 (可使用 --install-cron 15 开启)")

    if os.path.exists(os_path_str(db_path)):
        sz_kb = os.path.getsize(os_path_str(db_path)) / 1024.0
        conn, tmp = open_sqlite_ro(db_path)
        if conn:
            try:
                r = conn.execute(
                    "SELECT COUNT(DISTINCT rec_date), COUNT(*), COALESCE(SUM(turns),0), "
                    "COALESCE(SUM(fi+cr+cw+out_tok),0), COALESCE(SUM(cost_usd),0.0) FROM daily_archive"
                ).fetchone()
                cur_cnt = 0
                try:
                    c_row = conn.execute("SELECT COUNT(*) FROM file_cursors").fetchone()
                    cur_cnt = safe_int(c_row[0]) if c_row else 0
                except Exception:
                    pass
                days, buckets, turns, tot_tok, tot_usd = r if r else (0, 0, 0, 0, 0.0)
                print(
                    f"  本地归档账本   : {sz_kb:.1f} KB | {days} 个自然日 | {buckets} 条聚合桶 | "
                    f"增量游标 {cur_cnt} 个"
                )
                print(f"  归档累计用量   : {turns:,} 轮对话 | {tot_tok:,} Tokens | 估算成本 ${tot_usd:,.4f}")
            finally:
                close_sqlite(conn, tmp)
        else:
            print(f"  本地归档账本   : {sz_kb:.1f} KB (暂无法只读打开)")
    else:
        print("  本地归档账本   : 尚未生成 (首次扫描或同步后自动创建)")

    if os.path.exists(os_path_str(log_fp)):
        try:
            lines = safe_read_text(log_fp).splitlines()[-5:]
            if lines:
                print("\n--- 最近运行日志 (~/.toklens/agent.log) ---")
                for ln in lines:
                    print(f"  {ln}")
        except Exception:
            pass


def run_doctor(home: Path, cfg: dict, tz_offset_min: int, include_other_users: bool) -> int:
    rows = []  # (status, item, detail)
    cfg_dir = home / ".toklens"
    cfg_file = cfg_dir / "config.json"
    db_path = get_ledger_db_path(home)

    # 1. Python & SQLite 环境检查
    py_ver = platform.python_version()
    sq_ver = sqlite3.sqlite_version
    rows.append(("OK", "Python / SQLite", f"Python {py_ver}, SQLite {sq_ver} (零第三方依赖)"))

    # 2. 单实例锁检测
    with SingleInstanceLock(home) as lk:
        if lk.acquired:
            rows.append(("OK", "单实例互斥锁", "agent.lock 空闲可用 (无并发卡死进程)"))
        else:
            rows.append(("WARN", "单实例互斥锁", f"{cfg_dir / 'agent.lock'} 正被另一探针进程持有"))

    # 3. 时区对齐诊断
    now_off = datetime.datetime.now().astimezone().utcoffset()
    sys_mins = int(now_off.total_seconds() // 60) if now_off is not None else 0
    if tz_offset_min is not None:
        rows.append(("OK", "跨端自然日时区", _format_tz_label(tz_offset_min)))
    elif sys_mins == 0:
        rows.append(
            (
                "WARN",
                "跨端自然日时区",
                "当前系统为 UTC+00:00 且未固定 tz_offset_min；若桌面端在国内，建议加 --tz +8 对齐账单日期",
            )
        )
    else:
        rows.append(("OK", "跨端自然日时区", _format_tz_label(None)))

    # 4. 目录权限与 ledger.db 完整性检查
    if os.path.exists(os_path_str(cfg_dir)):
        if os.name != "nt":
            d_mode = os.stat(os_path_str(cfg_dir)).st_mode & 0o777
            if d_mode & 0o077:
                rows.append(("WARN", "安全目录权限", f"{cfg_dir} 权限为 {oct(d_mode)}，建议收敛为 0o700"))
            else:
                rows.append(("OK", "安全目录权限", f"{cfg_dir} 权限 0o700 隔离正常"))
        if os.path.exists(os_path_str(cfg_file)) and os.name != "nt":
            f_mode = os.stat(os_path_str(cfg_file)).st_mode & 0o777
            if f_mode & 0o077:
                rows.append(("WARN", "凭据文件权限", f"config.json 权限为 {oct(f_mode)}，建议 0o600"))
            else:
                rows.append(("OK", "凭据文件权限", "config.json 权限 0o600 安全"))
    else:
        rows.append(("OK", "安全目录权限", "~/.toklens 将在首次写入时以 0o700 创建"))

    if os.path.exists(os_path_str(db_path)):
        conn, tmp = open_sqlite_ro(db_path)
        if conn:
            try:
                chk = conn.execute("PRAGMA integrity_check").fetchone()
                jmode = conn.execute("PRAGMA journal_mode").fetchone()
                chk_val = str(chk[0]) if chk else "unknown"
                jm_val = str(jmode[0]) if jmode else "unknown"
                if chk_val.lower() == "ok":
                    rows.append(("OK", "SQLite 账本健康", f"integrity_check=ok, journal_mode={jm_val}"))
                else:
                    rows.append(("FAIL", "SQLite 账本健康", f"integrity_check 异常: {chk_val}"))
            except Exception as ex:
                rows.append(("FAIL", "SQLite 账本健康", f"检查异常: {ex}"))
            finally:
                close_sqlite(conn, tmp)
        else:
            rows.append(("FAIL", "SQLite 账本健康", "无法打开 ledger.db"))

    # 5. 多用户与 AI 工具发现
    scan_homes = discover_scan_homes(home, include_other_users=include_other_users)
    found_harnesses = _detect_harness_presence(scan_homes)
    if found_harnesses:
        summary = ", ".join(f"{name}({len(paths)})" for name, paths in found_harnesses)
        rows.append(("OK", "AI 工具数据源", f"扫描 {len(scan_homes)} 个主目录，发现 {len(found_harnesses)} 类工具: {summary}"))
    else:
        rows.append(("WARN", "AI 工具数据源", f"在 {', '.join(str(p) for p in scan_homes)} 下暂未检测到已知 AI 会话目录"))

    # 6. 云端价格引擎检查
    pl = PricingLookup(home, allow_network=True)
    rows.append(("OK", "计费单价引擎", f"已加载 {len(pl.rates)} 个模型单价规则 (来源状态: {pl.cloud_source})"))

    # 7. Gist / WebDAV 连通性与鉴权探测
    provider = (cfg.get("provider") or "").strip().lower()
    if provider == "gist":
        gid = (cfg.get("gist_id") or "").strip()
        tok = (cfg.get("gist_token") or "").strip()
        if "/" in gid:
            gid = gid.rstrip("/").rsplit("/", 1)[-1]
        if not gid or not tok:
            rows.append(("FAIL", "Gist 连通性", "config.json 缺少 gist_id 或 gist_token"))
        else:
            api_url = f"https://api.github.com/gists/{gid}"
            headers = {
                "Authorization": f"Bearer {tok}",
                "Accept": "application/vnd.github+json",
                "User-Agent": f"TokLens-Agent/{AGENT_VERSION}",
            }
            try:
                t0 = time.time()
                _, raw = _http_request_with_retry(
                    lambda: urllib.request.Request(api_url, headers=dict(headers), method="GET"),
                    timeout=10.0,
                    retries=2,
                )
                ms = int((time.time() - t0) * 1000)
                meta = json.loads(raw.decode("utf-8"))
                f_cnt = len(meta.get("files") or {})
                rows.append(("OK", "Gist 连通性", f"鉴权通过 ({ms} ms)，远端 Gist 含 {f_cnt} 个节点快照文件"))
            except Exception as ex:
                rows.append(("FAIL", "Gist 连通性", f"连接或鉴权失败: {ex}"))
    elif provider == "webdav":
        w_url = (cfg.get("webdav_url") or "").strip().rstrip("/")
        w_usr = cfg.get("webdav_user") or ""
        w_pwd = cfg.get("webdav_pass") or ""
        if not w_url:
            rows.append(("FAIL", "WebDAV 连通性", "config.json 缺少 webdav_url"))
        else:
            auth = base64.b64encode(f"{w_usr}:{w_pwd}".encode("utf-8")).decode("ascii")
            headers = {
                "Authorization": f"Basic {auth}",
                "Depth": "0",
                "User-Agent": f"TokLens-Agent/{AGENT_VERSION}",
            }
            try:
                t0 = time.time()
                req = urllib.request.Request(w_url + "/", headers=headers, method="PROPFIND")
                with urllib.request.urlopen(req, timeout=10.0) as resp:
                    code = getattr(resp, "status", 200)
                ms = int((time.time() - t0) * 1000)
                rows.append(("OK", "WebDAV 连通性", f"PROPFIND 响应 HTTP {code} ({ms} ms)"))
            except urllib.error.HTTPError as he:
                if he.code in (207, 405):
                    rows.append(("OK", "WebDAV 连通性", f"服务端可达 (HTTP {he.code})"))
                else:
                    rows.append(("FAIL", "WebDAV 连通性", f"HTTP {he.code}: {he.reason}"))
            except Exception as ex:
                rows.append(("FAIL", "WebDAV 连通性", f"连接失败: {ex}"))
    else:
        rows.append(("WARN", "云端同步配置", "尚未绑定 Gist 或 WebDAV 配对码 (仅本地采集模式)"))

    print(f"=== 知微 · TokLens 自检诊断报告 (v{AGENT_VERSION}) ===")
    print(f"{'状态':<8} {'检查项':<18} {'诊断详情'}")
    print("-" * 78)
    fail_cnt = 0
    for st, item, detail in rows:
        tag = f"[{st}]"
        if st == "FAIL":
            fail_cnt += 1
        print(f"{tag:<8} {item:<18} {detail}")
    print("-" * 78)
    if found_harnesses:
        print("已发现的数据目录明细:")
        for name, paths in found_harnesses:
            for p in paths[:3]:
                print(f"  - {name}: {p}")
    return 1 if fail_cnt > 0 else 0


def main():
    parser = argparse.ArgumentParser(description=f"知微 · TokLens v{AGENT_VERSION} — Linux VPS / 跨平台零依赖采集与同步探针")
    parser.add_argument("--home", default=None, help="指定要扫描的用户 HOME 目录（默认当前用户 ~）")
    parser.add_argument("--name", default=None, help="自定义机器显示名称（例如：HK-Dev-VPS）")
    parser.add_argument("--id", default=None, help="自定义机器唯一标识 ID（默认基于主机名生成）")
    parser.add_argument("--pair", default=None, help="导入从 Windows 客户端复制的 Base64 同步配对码并保存到 ~/.toklens/config.json")
    parser.add_argument("--tz", default=None, help="跨端自然日对齐时区偏移（例如 +8、+08:00、480 或 Asia/Shanghai）")
    parser.add_argument("--no-multi-user", action="store_true", help="以 root 运行时仅扫描 /root，不自动合并 /home/* 普通用户目录")
    parser.add_argument("--sync", action="store_true", help="扫描并使用 ~/.toklens/config.json 中的凭据上传同步")
    parser.add_argument("--export", default=None, help="将脱敏统计快照导出为本地 JSON 文件路径")
    parser.add_argument("--install-cron", type=int, default=0, metavar="MINUTES", help="安装/更新 Linux crontab 定时同步任务（推荐 15 分钟）")
    parser.add_argument("--uninstall-cron", action="store_true", help="移除已安装的 TokLens crontab 定时同步任务")
    parser.add_argument("--status", action="store_true", help="查看探针绑定状态、定时任务、归档统计与最近日志")
    parser.add_argument("--doctor", action="store_true", help="执行环境权限、SQLite 健康、AI 工具发现与云端连通性自检")
    parser.add_argument("--quiet", action="store_true", help="静默模式（供 crontab 后台调用，仅输出错误并记录 agent.log）")
    parser.add_argument("--version", action="version", version=f"TokLens Agent v{AGENT_VERSION} ({SCHEMA_VERSION})")
    args = parser.parse_args()

    home = Path(args.home).expanduser().resolve() if args.home else Path.home().resolve()
    cfg_dir = home / ".toklens"
    cfg_file = cfg_dir / "config.json"

    cfg = {}
    if os.path.exists(os_path_str(cfg_file)):
        try:
            loaded = json.loads(safe_read_text(cfg_file))
            if isinstance(loaded, dict):
                cfg = loaded
        except Exception as ex:
            print(f"[TokLens] 警告：读取配置文件 {cfg_file} 失败: {ex}", file=sys.stderr)

    config_dirty = False
    if args.pair:
        raw_b64 = args.pair.strip()
        raw_b64 += "=" * ((4 - len(raw_b64) % 4) % 4)
        try:
            decoded_bytes = base64.b64decode(raw_b64.encode("ascii"))
            pair_data = json.loads(decoded_bytes.decode("utf-8"))
            if not isinstance(pair_data, dict):
                raise ValueError("配对码内容不是有效的 JSON 对象")
        except Exception as ex:
            print(f"[TokLens] 错误：无效的 Base64 同步配对码，请从桌面端重新复制完整配对码 ({ex})", file=sys.stderr)
            sys.exit(2)

        cfg.update(pair_data)
        if "tz_offset_min" in pair_data:
            parsed_pair_tz = parse_tz_offset_minutes(pair_data.get("tz_offset_min"))
            if parsed_pair_tz is not None:
                cfg["tz_offset_min"] = parsed_pair_tz
        config_dirty = True

    cli_tz = None
    if args.tz is not None:
        cli_tz = parse_tz_offset_minutes(args.tz)
        if cli_tz is None:
            print(f"[TokLens] 错误：无法解析 --tz 时区参数 '{args.tz}'（示例: +8, +08:00, 480, Asia/Shanghai）", file=sys.stderr)
            sys.exit(2)
        if cfg.get("tz_offset_min") != cli_tz and (config_dirty or os.path.exists(os_path_str(cfg_file)) or args.install_cron > 0):
            cfg["tz_offset_min"] = cli_tz
            config_dirty = True

    tz_offset_min = cli_tz if cli_tz is not None else parse_tz_offset_minutes(cfg.get("tz_offset_min"))

    if args.name:
        cfg["machine_name"] = args.name.strip()
        if args.pair or os.path.exists(os_path_str(cfg_file)):
            config_dirty = True
    if args.id:
        cfg["machine_id"] = sanitize_machine_id(args.id)
        if args.pair or os.path.exists(os_path_str(cfg_file)):
            config_dirty = True

    if config_dirty:
        ensure_toklens_dir(home)
        if not cfg.get("machine_name"):
            cfg["machine_name"] = socket.gethostname()
        if not cfg.get("machine_id"):
            cfg["machine_id"] = sanitize_machine_id(cfg["machine_name"])
        atomic_write_text(cfg_file, json.dumps(cfg, indent=2, ensure_ascii=False), mode=0o600)
        if args.pair and not args.quiet:
            tz_hint = f", 时区偏移={tz_offset_min}min" if tz_offset_min is not None else ""
            print(f"[TokLens] 配对配置已安全保存至 {cfg_file} (权限 0600{tz_hint})")
        append_agent_log(home, "INFO", f"已更新配置文件 config.json (provider={cfg.get('provider', 'none')})")

    if args.uninstall_cron:
        try:
            removed = uninstall_cron(home=home)
            if removed > 0:
                print(f"[TokLens] 已成功移除 {removed} 条 crontab 自动同步任务。")
            else:
                print("[TokLens] 当前 crontab 中未发现已安装的 TokLens 定时任务。")
        except Exception as ex:
            print(f"[TokLens] 卸载 crontab 失败: {ex}", file=sys.stderr)
            sys.exit(1)
        if not (args.sync or args.export or args.status or args.doctor):
            return

    is_root_default_home = False
    if os.name != "nt" and hasattr(os, "geteuid"):
        try:
            is_root_default_home = (
                os.geteuid() == 0
                and not args.home
                and os.path.realpath(os_path_str(home)) == os.path.realpath("/root")
            )
        except Exception:
            is_root_default_home = False
    include_other_users = bool(is_root_default_home and not args.no_multi_user)

    if args.status:
        run_status(home, cfg, tz_offset_min, include_other_users)
        return

    if args.doctor:
        code = run_doctor(home, cfg, tz_offset_min, include_other_users)
        sys.exit(code)

    m_name = cfg.get("machine_name") or socket.gethostname()
    m_id = sanitize_machine_id(cfg.get("machine_id") or m_name)
    m_type = cfg.get("machine_type") or ("vps" if platform.system().lower() == "linux" else "desktop")

    with SingleInstanceLock(home) as lock:
        if not lock.acquired:
            msg = "检测到另一探针实例正在运行 (agent.lock 已锁定)，本次自动跳过以防并发冲突。"
            append_agent_log(home, "WARN", msg)
            if not args.quiet:
                print(f"[TokLens] {msg}")
            return

        try:
            snap = build_snapshot(
                home,
                m_id,
                m_name,
                m_type,
                tz_offset_min=tz_offset_min,
                include_other_users=include_other_users,
                strict_ledger=bool(args.sync),
            )
        except Exception as ex:
            append_agent_log(home, "ERROR", f"构建快照失败: {ex}")
            print(f"[TokLens] 扫描构建快照失败: {ex}", file=sys.stderr)
            sys.exit(1)

        if args.sync or args.pair:
            provider = (cfg.get("provider") or "").lower()
            try:
                if provider == "gist":
                    sync_gist(snap, cfg["gist_id"], cfg["gist_token"], home=home)
                    tot_tok = sum(r[4] + r[5] + r[6] + r[7] for r in snap["records"])
                    tot_cost = sum(r[8] for r in snap["records"])
                    summary = f"Gist 同步成功 ({m_name}): {len(snap['records'])} 条记录, {tot_tok:,} Tokens, ${tot_cost:,.4f}"
                    append_agent_log(home, "INFO", summary)
                    if not args.quiet:
                        print(f"[TokLens] {summary}")
                elif provider == "webdav":
                    res = sync_webdav(
                        snap,
                        cfg["webdav_url"],
                        cfg.get("webdav_user", ""),
                        cfg.get("webdav_pass", ""),
                        home=home,
                    )
                    tot_tok = sum(r[4] + r[5] + r[6] + r[7] for r in snap["records"])
                    tot_cost = sum(r[8] for r in snap["records"])
                    summary = f"WebDAV 同步成功 ({res['url']}): {len(snap['records'])} 条记录, {tot_tok:,} Tokens, ${tot_cost:,.4f}"
                    append_agent_log(home, "INFO", summary)
                    if not args.quiet:
                        print(f"[TokLens] {summary}")
                elif args.sync:
                    err_m = f"未找到有效的同步通道配置，请先运行 --pair 绑定配对码 ({cfg_file})"
                    append_agent_log(home, "ERROR", err_m)
                    print(f"[TokLens] {err_m}", file=sys.stderr)
                    sys.exit(1)
            except Exception as ex:
                append_agent_log(home, "ERROR", f"云端同步失败 ({provider}): {ex}")
                print(f"[TokLens] 云端同步失败: {ex}", file=sys.stderr)
                sys.exit(1)

        tot_tok = sum(r[4] + r[5] + r[6] + r[7] for r in snap["records"])
        tot_cost = sum(r[8] for r in snap["records"])
        if not args.quiet:
            print(
                f"[TokLens v{AGENT_VERSION}] 节点: {snap['machine_name']} ({snap['machine_id']}) | "
                f"聚合记录: {len(snap['records'])} 条 | 总计 Token: {tot_tok:,} | 估算成本: ${tot_cost:,.4f}"
            )

        if args.export:
            out_p = Path(args.export).expanduser().resolve()
            exp_mode = 0o600 if ".toklens" in str(out_p) else 0o644
            atomic_write_text(out_p, json.dumps(snap, indent=2, ensure_ascii=False), mode=exp_mode)
            if not args.quiet:
                print(f"[TokLens] 快照已导出至: {out_p}")

    if args.install_cron > 0:
        try:
            line = install_cron(
                Path(__file__),
                args.install_cron,
                home=home,
                explicit_home=args.home,
                tz_offset_min=tz_offset_min,
                no_multi_user=args.no_multi_user,
            )
            if not args.quiet:
                print(f"[TokLens] 已配置 crontab 自动同步任务:\n  {line}")
        except Exception as ex:
            append_agent_log(home, "ERROR", f"配置 crontab 失败: {ex}")
            print(f"[TokLens] 配置 crontab 失败: {ex}", file=sys.stderr)
            sys.exit(1)


if __name__ == "__main__":
    main()
