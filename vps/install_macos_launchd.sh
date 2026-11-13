#!/usr/bin/env bash
# ==============================================================================
# 知微 · TokLens — macOS (Darwin) 后台静默同步守护一键安装脚本
# 专为「Windows 主力台式机 + MacBook 随身笔记本」双机/多机用户打造
#
# 核心能力：
#   1. 支持 --pair 'toklens1:...' 自动解码配对码（本地复制或云端自举拉取 toklens_agent.py）
#   2. 生成标准 ~/Library/LaunchAgents/com.toklens.agent.plist
#      - StartInterval = 900（每 15 分钟自动增量采集并同步）
#      - RunAtLoad = true（开机登录即同步一次）
#      - LowPriorityIO = true & ProcessType = Background（E-Core 能效核零感零耗电运行）
#      - 日志定向至 ~/.toklens/launchd.log
#   3. 兼容 macOS Sonoma / Sequoia 现代 launchctl bootout/bootstrap 与旧版 unload/load 回退
#   4. 支持 --status 状态巡检与 --uninstall 干净卸载
# ==============================================================================

set -euo pipefail

LABEL="com.toklens.agent"
TOKLENS_DIR="${HOME}/.toklens"
AGENT_DEST="${TOKLENS_DIR}/toklens_agent.py"
CONFIG_FILE="${TOKLENS_DIR}/config.json"
LEDGER_FILE="${TOKLENS_DIR}/ledger.db"
LOG_FILE="${TOKLENS_DIR}/launchd.log"
LAUNCH_AGENTS_DIR="${HOME}/Library/LaunchAgents"
PLIST_PATH="${LAUNCH_AGENTS_DIR}/${LABEL}.plist"
DEFAULT_INTERVAL=900

# 终端彩色输出（非交互管道自动降级为纯文本）
if [[ -t 1 ]]; then
    C_CYAN=$'\033[36m'
    C_GREEN=$'\033[32m'
    C_YELLOW=$'\033[33m'
    C_RED=$'\033[31m'
    C_BOLD=$'\033[1m'
    C_DIM=$'\033[2m'
    C_RESET=$'\033[0m'
else
    C_CYAN=""
    C_GREEN=""
    C_YELLOW=""
    C_RED=""
    C_BOLD=""
    C_DIM=""
    C_RESET=""
fi

info()  { printf "%s[TokLens]%s %s\n" "${C_CYAN}${C_BOLD}" "${C_RESET}" "$*"; }
ok()    { printf "%s[成功]%s %s\n" "${C_GREEN}${C_BOLD}" "${C_RESET}" "$*"; }
warn()  { printf "%s[提示]%s %s\n" "${C_YELLOW}${C_BOLD}" "${C_RESET}" "$*" >&2; }
err()   { printf "%s[错误]%s %s\n" "${C_RED}${C_BOLD}" "${C_RESET}" "$*" >&2; }

print_usage() {
    cat <<'EOF'
知微 · TokLens — macOS LaunchAgent 后台静默同步安装器

用法：
  bash install_macos_launchd.sh --pair 'toklens1:...' [可选参数]
  bash install_macos_launchd.sh --status
  bash install_macos_launchd.sh --uninstall [--purge]

参数说明：
  --pair <配对码>       导入桌面端「同步与设置 -> 复制配对码」生成的 toklens1:... 凭据
  --name <设备名称>     自定义当前 Mac 显示名称（默认首次安装自动读取系统 ComputerName）
  --id <设备ID>         自定义当前 Mac 唯一节点 ID（默认首次安装生成 mac-<主机名>）
  --interval <秒数>     后台同步时间间隔（默认 900 秒即 15 分钟，最小 60 秒）
  --agent-file <路径>   手动指定本地 toklens_agent.py 源文件路径
  --status              查看 LaunchAgent 运行状态、脱敏同步配置与最近同步日志
  --uninstall           停止并干净移除 LaunchAgent 后台定时任务（保留本地历史账本）
  --purge               配合 --uninstall 使用，额外彻底删除 ~/.toklens 目录及配置
  -h, --help            显示本帮助说明

典型示例：
  1) 首次配对并开启每 15 分钟无感后台同步：
     bash install_macos_launchd.sh --pair 'toklens1:eyJwcm92...' --name '我的 MacBook Pro'

  2) 查看后台守护运行状态与最近同步记录：
     bash install_macos_launchd.sh --status

  3) 干净移除 macOS 定时同步任务：
     bash install_macos_launchd.sh --uninstall
EOF
}

ensure_macos_and_user() {
    local os_name
    os_name="$(uname -s 2>/dev/null || echo "")"
    if [[ "${os_name}" != "Darwin" ]]; then
        err "当前系统为 ${os_name:-未知}，本脚本仅适用于 macOS (Darwin) LaunchAgent 环境。"
        warn "若在 Linux VPS 上部署，请运行：python3 toklens_agent.py --pair 'toklens1:...' --sync --install-cron 15"
        exit 1
    fi
    if [[ "$(id -u)" -eq 0 ]]; then
        err "请勿使用 sudo 或 root 身份运行本脚本！"
        warn "LaunchAgent 必须安装在当前普通登录用户下，才能读取您个人目录中的 Cursor / Claude / VS Code 等日志。"
        exit 1
    fi
}

# 探测可用的 Python 3（规避未安装 Xcode CLT 时 /usr/bin/python3 触发系统弹窗阻塞）
resolve_python3() {
    local cand
    local -a candidates=()

    if command -v python3 >/dev/null 2>&1; then
        cand="$(command -v python3)"
        if [[ "${cand}" != "/usr/bin/python3" ]]; then
            candidates+=("${cand}")
        fi
    fi

    candidates+=(
        "/opt/homebrew/bin/python3"
        "/usr/local/bin/python3"
        "/Library/Frameworks/Python.framework/Versions/Current/bin/python3"
    )

    if xcode-select -p >/dev/null 2>&1; then
        candidates+=("/usr/bin/python3")
    fi

    for cand in ${candidates[@]+"${candidates[@]}"}; do
        if [[ -x "${cand}" ]]; then
            if "${cand}" -c 'import sys, json, sqlite3, ssl, plistlib, urllib.request; sys.exit(0 if sys.version_info >= (3, 7) else 1)' >/dev/null 2>&1; then
                printf "%s" "${cand}"
                return 0
            fi
        fi
    done

    err "未检测到可用的 Python 3.7+ 运行环境（需包含 sqlite3 / ssl 标准库）。"
    warn "请先在终端执行以下命令之一安装 Python 3 后重试："
    warn "  • 安装苹果命令行工具：xcode-select --install"
    warn "  • 或使用 Homebrew 安装：brew install python"
    exit 1
}

# 校验配对码格式合法性（通过 sys.argv[1] 传参，彻底杜绝任何命令/代码注入）
validate_pair_code() {
    local py_bin="$1"
    local pair_str="$2"
    "${py_bin}" -c '
import base64, json, sys

raw = sys.argv[1].strip()
if raw.lower().startswith("toklens1:"):
    raw = raw[9:].strip()
if not raw:
    sys.exit(2)
pad = raw + ("=" * (-len(raw) % 4))
try:
    cfg = json.loads(base64.b64decode(pad.encode("ascii")).decode("utf-8"))
except Exception:
    sys.exit(3)
if not isinstance(cfg, dict):
    sys.exit(3)
prov = str(cfg.get("provider") or "gist").lower()
if prov == "webdav":
    if not str(cfg.get("webdav_url") or "").strip():
        sys.exit(4)
else:
    if not str(cfg.get("gist_id") or "").strip() or not str(cfg.get("gist_token") or "").strip():
        sys.exit(5)
' "${pair_str}" || {
        local rc=$?
        case "${rc}" in
            4) err "配对码中缺少有效的 WebDAV 地址，请在桌面端重新复制配对码。" ;;
            5) err "配对码中缺少私有 Gist ID 或 Token（请先在 Windows 桌面端点击一次「保存并立即同步」后再复制配对码）。" ;;
            *) err "配对码解析失败！请确认完整粘贴了以 toklens1: 开头的配对码字符串。" ;;
        esac
        exit 1
    }
}

# 部署 toklens_agent.py 至 ~/.toklens/toklens_agent.py
deploy_agent_script() {
    local py_bin="$1"
    local custom_src="$2"
    local pair_str="$3"
    local script_dir=""
    local self_src="${BASH_SOURCE[0]:-}"
    if [[ -n "${self_src}" ]] && [[ -f "${self_src}" ]]; then
        script_dir="$(cd "$(dirname "${self_src}")" && pwd)"
    fi

    mkdir -p "${TOKLENS_DIR}"
    chmod 700 "${TOKLENS_DIR}"

    local chosen_src=""
    if [[ -n "${custom_src}" ]]; then
        if [[ ! -f "${custom_src}" ]]; then
            err "指定的探针文件不存在：${custom_src}"
            exit 1
        fi
        chosen_src="${custom_src}"
    else
        local -a local_candidates=()
        if [[ -n "${script_dir}" ]]; then
            local_candidates+=(
                "${script_dir}/toklens_agent.py"
                "${script_dir}/vps/toklens_agent.py"
            )
        fi
        local_candidates+=(
            "./toklens_agent.py"
            "./vps/toklens_agent.py"
        )
        local item
        for item in "${local_candidates[@]}"; do
            if [[ -f "${item}" ]]; then
                chosen_src="${item}"
                break
            fi
        done
    fi

    # 1. 优先从本地同级/指定路径原子拷贝
    if [[ -n "${chosen_src}" ]]; then
        local src_abs dst_abs
        src_abs="$(cd "$(dirname "${chosen_src}")" && pwd)/$(basename "${chosen_src}")"
        dst_abs="${AGENT_DEST}"
        if [[ "${src_abs}" != "${dst_abs}" ]]; then
            local tmp_agent="${AGENT_DEST}.tmp.$$"
            cp -f "${chosen_src}" "${tmp_agent}"
            chmod 700 "${tmp_agent}"
            mv -f "${tmp_agent}" "${AGENT_DEST}"
            info "已将本地探针部署至：${AGENT_DEST}"
        else
            chmod 700 "${AGENT_DEST}"
            info "已确认就地探针脚本：${AGENT_DEST}"
        fi
        return 0
    fi

    # 2. 若本地无附带脚本，尝试通过配对码或既有配置从私有 Gist / WebDAV 云端自举拉取
    if [[ -n "${pair_str}" ]] || [[ -f "${CONFIG_FILE}" ]]; then
        info "本地未附带 toklens_agent.py，正在从您的私有云端同步库安全拉取最新探针..."
        local tmp_dl="${AGENT_DEST}.dl.$$"
        if "${py_bin}" -c '
import base64, json, pathlib, sys, urllib.request

pair_raw = sys.argv[1].strip()
cfg_path = pathlib.Path(sys.argv[2])
out_path = pathlib.Path(sys.argv[3])

cfg = {}
if pair_raw:
    s = pair_raw[9:].strip() if pair_raw.lower().startswith("toklens1:") else pair_raw
    s += "=" * (-len(s) % 4)
    cfg = json.loads(base64.b64decode(s.encode("ascii")).decode("utf-8"))
elif cfg_path.exists():
    cfg = json.loads(cfg_path.read_text(encoding="utf-8"))

dav = str(cfg.get("provider") or "").lower() == "webdav"
ua = {"User-Agent": "TokLens-macOS-Bootstrap/2.2"}
if dav:
    wurl = str(cfg.get("webdav_url") or "").strip().rstrip("/")
    if not wurl:
        sys.exit(2)
    auth = base64.b64encode((str(cfg.get("webdav_user") or "") + ":" + str(cfg.get("webdav_pass") or "")).encode("utf-8")).decode("ascii")
    headers = {"Authorization": "Basic " + auth, "User-Agent": "TokLens-macOS-Bootstrap/2.2"}
    req = urllib.request.Request(wurl + "/toklens_agent.py", headers=headers)
    txt = urllib.request.urlopen(req, timeout=20).read().decode("utf-8")
else:
    gid = str(cfg.get("gist_id") or "").strip()
    tok = str(cfg.get("gist_token") or "").strip()
    if not gid or not tok:
        sys.exit(2)
    headers = {"Authorization": "Bearer " + tok, "Accept": "application/vnd.github+json", "User-Agent": "TokLens-macOS-Bootstrap/2.2"}
    raw = urllib.request.urlopen(urllib.request.Request("https://api.github.com/gists/" + gid, headers=headers), timeout=20).read().decode("utf-8")
    fmeta = (json.loads(raw).get("files") or {}).get("toklens_agent.py")
    if not isinstance(fmeta, dict):
        sys.exit(3)
    if not fmeta.get("truncated") and fmeta.get("content"):
        txt = fmeta["content"]
    else:
        rurl = str(fmeta.get("raw_url") or "")
        rheaders = ua if "githubusercontent.com" in rurl.lower() else headers
        txt = urllib.request.urlopen(urllib.request.Request(rurl, headers=rheaders), timeout=20).read().decode("utf-8")

if "MultiHarnessScanner" not in txt:
    sys.exit(4)
out_path.write_text(txt, encoding="utf-8")
' "${pair_str}" "${CONFIG_FILE}" "${tmp_dl}" >/dev/null 2>&1; then
            chmod 700 "${tmp_dl}"
            mv -f "${tmp_dl}" "${AGENT_DEST}"
            ok "已从云端拉取并部署探针至：${AGENT_DEST}"
            return 0
        else
            rm -f "${tmp_dl}"
            if [[ -f "${AGENT_DEST}" ]]; then
                warn "云端未找到或暂无法拉取 toklens_agent.py，将继续使用已部署的 ${AGENT_DEST}。"
                chmod 700 "${AGENT_DEST}"
                return 0
            fi
        fi
    fi

    if [[ -f "${AGENT_DEST}" ]]; then
        chmod 700 "${AGENT_DEST}"
        info "使用已存在的探针脚本：${AGENT_DEST}"
        return 0
    fi

    err "未能找到 toklens_agent.py！"
    warn "请将 toklens_agent.py 与本脚本放在同一目录下运行，或附带有效的 --pair 'toklens1:...' 参数自动拉取。"
    exit 1
}

# 生成标准 LaunchAgent plist 并用 plutil 校验语法
write_launchd_plist() {
    local py_bin="$1"
    local interval_sec="$2"

    mkdir -p "${LAUNCH_AGENTS_DIR}"
    chmod 755 "${LAUNCH_AGENTS_DIR}"
    mkdir -p "${TOKLENS_DIR}"
    chmod 700 "${TOKLENS_DIR}"
    touch "${LOG_FILE}"
    chmod 600 "${LOG_FILE}"

    local tmp_plist="${PLIST_PATH}.tmp.$$"
    "${py_bin}" -c '
import pathlib, plistlib, sys

out_fp = pathlib.Path(sys.argv[1])
label = sys.argv[2]
py_exec = sys.argv[3]
agent_fp = sys.argv[4]
interval_sec = int(sys.argv[5])
work_dir = sys.argv[6]
log_fp = sys.argv[7]
home_dir = sys.argv[8]

payload = {
    "Label": label,
    "ProgramArguments": [py_exec, agent_fp, "--sync"],
    "StartInterval": interval_sec,
    "RunAtLoad": True,
    "LowPriorityIO": True,
    "LowPriorityBackgroundIO": True,
    "ProcessType": "Background",
    "Nice": 10,
    "WorkingDirectory": work_dir,
    "StandardOutPath": log_fp,
    "StandardErrorPath": log_fp,
    "EnvironmentVariables": {
        "HOME": home_dir,
        "PATH": "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin",
        "LC_CTYPE": "UTF-8",
        "PYTHONUTF8": "1",
        "PYTHONIOENCODING": "utf-8",
    },
}

with out_fp.open("wb") as f:
    plistlib.dump(payload, f, fmt=plistlib.FMT_XML, sort_keys=False)
' "${tmp_plist}" "${LABEL}" "${py_bin}" "${AGENT_DEST}" "${interval_sec}" "${TOKLENS_DIR}" "${LOG_FILE}" "${HOME}"

    chmod 644 "${tmp_plist}"
    if command -v plutil >/dev/null 2>&1; then
        if ! plutil -lint "${tmp_plist}" >/dev/null 2>&1; then
            rm -f "${tmp_plist}"
            err "生成的 LaunchAgent plist 未通过 macOS plutil 语法校验！"
            exit 1
        fi
    fi
    mv -f "${tmp_plist}" "${PLIST_PATH}"
}

# 卸载已加载的 LaunchAgent（兼容现代 bootout 与旧版 unload）
stop_launchd_agent() {
    local uid
    uid="$(id -u)"
    local gui_domain="gui/${uid}"
    local user_domain="user/${uid}"

    launchctl bootout "${gui_domain}/${LABEL}" >/dev/null 2>&1 || true
    if [[ -f "${PLIST_PATH}" ]]; then
        launchctl bootout "${gui_domain}" "${PLIST_PATH}" >/dev/null 2>&1 || true
    fi
    launchctl bootout "${user_domain}/${LABEL}" >/dev/null 2>&1 || true
    if [[ -f "${PLIST_PATH}" ]]; then
        launchctl unload -w "${PLIST_PATH}" >/dev/null 2>&1 || true
    fi
}

# 注册并启动 LaunchAgent（优先 Sonoma/Sequoia bootstrap，自动回退旧版 load）
start_launchd_agent() {
    local uid
    uid="$(id -u)"
    local gui_domain="gui/${uid}"
    local user_domain="user/${uid}"

    stop_launchd_agent
    sleep 0.3

    launchctl enable "${gui_domain}/${LABEL}" >/dev/null 2>&1 || true
    if launchctl bootstrap "${gui_domain}" "${PLIST_PATH}" >/dev/null 2>&1; then
        printf "%s" "bootstrap (${gui_domain})"
        return 0
    fi

    # 处理 macOS Sonoma / Sequoia 快速重装时的瞬态 I/O 竞争
    sleep 0.5
    if launchctl bootstrap "${gui_domain}" "${PLIST_PATH}" >/dev/null 2>&1; then
        printf "%s" "bootstrap (${gui_domain})"
        return 0
    fi

    # 兼容无桌面 GUI 会话的纯 SSH 登录场景
    launchctl enable "${user_domain}/${LABEL}" >/dev/null 2>&1 || true
    if launchctl bootstrap "${user_domain}" "${PLIST_PATH}" >/dev/null 2>&1; then
        printf "%s" "bootstrap (${user_domain})"
        return 0
    fi

    # 旧版 macOS 兼容回退
    if launchctl load -w "${PLIST_PATH}" >/dev/null 2>&1; then
        printf "%s" "load -w (兼容模式)"
        return 0
    fi

    return 1
}

do_status() {
    ensure_macos_and_user
    local uid
    uid="$(id -u)"
    local py_bin=""
    py_bin="$(resolve_python3 2>/dev/null || true)"

    printf "\n%s=== 知微 · TokLens macOS 后台守护状态 ===%s\n" "${C_BOLD}${C_CYAN}" "${C_RESET}"

    # 1. LaunchAgent 服务状态
    local svc_info=""
    local active_domain=""
    if svc_info="$(launchctl print "gui/${uid}/${LABEL}" 2>/dev/null)"; then
        active_domain="gui/${uid}/${LABEL}"
    elif svc_info="$(launchctl print "user/${uid}/${LABEL}" 2>/dev/null)"; then
        active_domain="user/${uid}/${LABEL}"
    fi

    if [[ -n "${active_domain}" ]]; then
        local state_val runs_val exit_val
        state_val="$(printf "%s\n" "${svc_info}" | sed -n 's/^[[:space:]]*state = \(.*\)$/\1/p' | head -n 1)"
        runs_val="$(printf "%s\n" "${svc_info}" | sed -n 's/^[[:space:]]*runs = \(.*\)$/\1/p' | head -n 1)"
        exit_val="$(printf "%s\n" "${svc_info}" | sed -n 's/^[[:space:]]*last exit code = \(.*\)$/\1/p' | head -n 1)"
        printf "  • 守护状态  : %s已加载就绪%s (%s | 状态: %s | 累计触发: %s 次 | 最近退出码: %s)\n" \
            "${C_GREEN}${C_BOLD}" "${C_RESET}" \
            "${active_domain}" "${state_val:-waiting}" "${runs_val:-0}" "${exit_val:-0}"
    elif launchctl list "${LABEL}" >/dev/null 2>&1; then
        printf "  • 守护状态  : %s已注册运行中%s (legacy launchctl)\n" "${C_GREEN}${C_BOLD}" "${C_RESET}"
    else
        printf "  • 守护状态  : %s未加载 / 未运行%s\n" "${C_YELLOW}${C_BOLD}" "${C_RESET}"
    fi

    # 2. Plist 与探针文件检查
    if [[ -f "${PLIST_PATH}" ]]; then
        printf "  • Plist 路径 : %s (存在)\n" "${PLIST_PATH}"
    else
        printf "  • Plist 路径 : %s (未生成)\n" "${PLIST_PATH}"
    fi

    if [[ -f "${AGENT_DEST}" ]]; then
        printf "  • 探针脚本  : %s (存在)\n" "${AGENT_DEST}"
    else
        printf "  • 探针脚本  : %s (未部署)\n" "${AGENT_DEST}"
    fi

    # 3. 脱敏配置与本地高水位归档统计
    if [[ -n "${py_bin}" ]] && [[ -f "${CONFIG_FILE}" ]]; then
        "${py_bin}" -c '
import json, pathlib, sqlite3, sys, urllib.request

cfg_fp = pathlib.Path(sys.argv[1])
db_fp = pathlib.Path(sys.argv[2])

try:
    cfg = json.loads(cfg_fp.read_text(encoding="utf-8"))
    mname = cfg.get("machine_name") or "-"
    mid = cfg.get("machine_id") or "-"
    prov = str(cfg.get("provider") or "gist").lower()
    if prov == "webdav":
        url = str(cfg.get("webdav_url") or "")
        target = f"WebDAV ({url})" if url else "WebDAV (未配置地址)"
    else:
        gid = str(cfg.get("gist_id") or "")
        masked = (gid[:8] + "...") if len(gid) > 8 else (gid or "未配置")
        has_tok = "已配置令牌" if cfg.get("gist_token") else "缺少令牌"
        target = f"GitHub 私有 Gist ({masked}, {has_tok})"
    print(f"  • 节点标识  : {mname} (ID: {mid})")
    print(f"  • 同步通道  : {target}")
except Exception as e:
    print(f"  • 配置文件  : 读取异常 ({e})")

if db_fp.exists():
    try:
        uri = f"file:{urllib.request.pathname2url(str(db_fp.resolve()))}?mode=ro"
        conn = sqlite3.connect(uri, uri=True, timeout=2.0)
        row = conn.execute(
            "SELECT COUNT(*), COUNT(DISTINCT tool), COALESCE(SUM(fi+cr+cw+out_tok),0), COALESCE(SUM(cost_usd),0), COALESCE(MAX(rec_date),\x27-\x27) FROM daily_archive"
        ).fetchone()
        conn.close()
        if row:
            print(f"  • 本地账本  : 已归档 {row[0]} 条日结记录 | 覆盖 {row[1]} 款工具 | 累计 {int(row[2]):,} Tokens (${float(row[3]):.2f}) | 最新日期 {row[4]}")
    except Exception:
        pass
' "${CONFIG_FILE}" "${LEDGER_FILE}"
    else
        printf "  • 同步配置  : 尚未生成 %s\n" "${CONFIG_FILE}"
    fi

    # 4. 最近日志输出
    local log_mtime=""
    if [[ -f "${LOG_FILE}" ]] && [[ -x "/usr/bin/stat" ]]; then
        log_mtime="$(/usr/bin/stat -f "%Sm" -t "%Y-%m-%d %H:%M:%S" "${LOG_FILE}" 2>/dev/null || true)"
    fi
    if [[ -n "${log_mtime}" ]]; then
        printf "\n%s--- 最近同步日志 (%s | 更新于 %s) ---%s\n" "${C_BOLD}" "${LOG_FILE}" "${log_mtime}" "${C_RESET}"
    else
        printf "\n%s--- 最近同步日志 (%s) ---%s\n" "${C_BOLD}" "${LOG_FILE}" "${C_RESET}"
    fi
    if [[ -s "${LOG_FILE}" ]]; then
        tail -n 12 "${LOG_FILE}"
    else
        printf "  %s(暂无日志记录，任务将在加载时及每 15 分钟写入同步结果)%s\n" "${C_DIM}" "${C_RESET}"
    fi
    printf "\n"
}

do_uninstall() {
    local purge_data="$1"
    ensure_macos_and_user

    info "正在停止并移除 TokLens macOS 后台同步守护 (${LABEL})..."
    stop_launchd_agent

    if [[ -f "${PLIST_PATH}" ]]; then
        rm -f "${PLIST_PATH}"
        ok "已移除 LaunchAgent 配置文件：${PLIST_PATH}"
    else
        info "未发现 ${PLIST_PATH}，无需删除。"
    fi

    if [[ "${purge_data}" == "1" ]]; then
        if [[ -d "${TOKLENS_DIR}" ]]; then
            rm -rf "${TOKLENS_DIR}"
            ok "已彻底清理本地探针、配置与日志目录：${TOKLENS_DIR}"
        fi
    else
        info "已保留本地历史归档账本与配对配置：${TOKLENS_DIR}"
        printf "  %s(如需连同本地缓存与配对信息彻底清除，可附加 --purge 参数或手动执行 rm -rf ~/.toklens)%s\n" "${C_DIM}" "${C_RESET}"
    fi

    ok "知微 · TokLens 定时同步任务已干净卸载！"
}

main() {
    local action="install"
    local pair_code=""
    local custom_name=""
    local custom_id=""
    local custom_agent_file=""
    local interval_sec="${DEFAULT_INTERVAL}"
    local purge_flag="0"

    while [[ $# -gt 0 ]]; do
        case "$1" in
            --pair)
                if [[ $# -lt 2 ]]; then err "--pair 参数缺少配对码值！"; exit 1; fi
                pair_code="$2"
                shift 2
                ;;
            --pair=*)
                pair_code="${1#*=}"
                shift
                ;;
            --name)
                if [[ $# -lt 2 ]]; then err "--name 参数缺少设备显示名称！"; exit 1; fi
                custom_name="$2"
                shift 2
                ;;
            --name=*)
                custom_name="${1#*=}"
                shift
                ;;
            --id)
                if [[ $# -lt 2 ]]; then err "--id 参数缺少设备 ID！"; exit 1; fi
                custom_id="$2"
                shift 2
                ;;
            --id=*)
                custom_id="${1#*=}"
                shift
                ;;
            --interval)
                if [[ $# -lt 2 ]]; then err "--interval 参数缺少秒数值！"; exit 1; fi
                interval_sec="$2"
                shift 2
                ;;
            --interval=*)
                interval_sec="${1#*=}"
                shift
                ;;
            --agent-file)
                if [[ $# -lt 2 ]]; then err "--agent-file 参数缺少文件路径！"; exit 1; fi
                custom_agent_file="$2"
                shift 2
                ;;
            --agent-file=*)
                custom_agent_file="${1#*=}"
                shift
                ;;
            --status)
                action="status"
                shift
                ;;
            --uninstall)
                action="uninstall"
                shift
                ;;
            --purge)
                purge_flag="1"
                shift
                ;;
            -h|--help)
                print_usage
                exit 0
                ;;
            *)
                err "未知参数：$1"
                print_usage
                exit 1
                ;;
        esac
    done

    if [[ "${action}" == "status" ]]; then
        do_status
        exit 0
    fi

    if [[ "${action}" == "uninstall" ]]; then
        do_uninstall "${purge_flag}"
        exit 0
    fi

    # 校验同步间隔整数
    if [[ ! "${interval_sec}" =~ ^[0-9]+$ ]] || [[ "${interval_sec}" -lt 60 ]]; then
        err "--interval 必须为不小于 60 的整数秒（默认 900 秒即 15 分钟）。"
        exit 1
    fi

    ensure_macos_and_user
    local py_bin
    py_bin="$(resolve_python3)"
    info "检测到可用 Python 3 环境：${py_bin}"

    if [[ -n "${pair_code}" ]]; then
        validate_pair_code "${py_bin}" "${pair_code}"
    elif [[ ! -f "${CONFIG_FILE}" ]]; then
        err "首次安装需提供桌面端配对码（未检测到已存在的 ${CONFIG_FILE}）！"
        warn "请在 Windows 电脑打开 TokLens.exe ->「同步与设置」-> 点击顶栏「复制配对码」，然后在 Mac 终端执行："
        warn "  bash install_macos_launchd.sh --pair 'toklens1:...'"
        exit 1
    fi

    deploy_agent_script "${py_bin}" "${custom_agent_file}" "${pair_code}"

    # 首次安装未传 --name / --id 时，自动读取 macOS 友好的本机名与 mac- 前缀 ID（已有配置绝不覆盖，防止节点分裂）
    local effective_name="${custom_name}"
    local effective_id="${custom_id}"
    if [[ ! -f "${CONFIG_FILE}" ]]; then
        if [[ -z "${effective_name}" ]]; then
            effective_name="$(scutil --get ComputerName 2>/dev/null || scutil --get LocalHostName 2>/dev/null || hostname -s 2>/dev/null || echo "MacBook")"
        fi
        if [[ -z "${effective_id}" ]]; then
            local raw_host
            raw_host="$(scutil --get LocalHostName 2>/dev/null || hostname -s 2>/dev/null || echo "macbook")"
            effective_id="mac-${raw_host}"
        fi
    fi

    local -a agent_args=()
    if [[ -n "${pair_code}" ]]; then
        agent_args+=("--pair" "${pair_code}")
    fi
    if [[ -n "${effective_name}" ]]; then
        agent_args+=("--name" "${effective_name}")
    fi
    if [[ -n "${effective_id}" ]]; then
        agent_args+=("--id" "${effective_id}")
    fi
    agent_args+=("--sync")

    info "正在执行首次全量 AI 日志扫描与云端对账同步..."
    if ! "${py_bin}" "${AGENT_DEST}" "${agent_args[@]}"; then
        err "首次云端同步校验未通过，请检查网络连接或重新从桌面端生成配对码后重试。"
        exit 1
    fi

    info "正在生成低功耗 LaunchAgent 守护配置 (${PLIST_PATH})..."
    write_launchd_plist "${py_bin}" "${interval_sec}"

    info "正在向 macOS launchd 注册后台定时服务..."
    local loaded_via=""
    if ! loaded_via="$(start_launchd_agent)"; then
        err "launchctl 加载服务失败！"
        warn "请打开 macOS「系统设置 -> 通用 -> 登录项与扩展 -> 允许在后台运行」，确认未禁用 Python / TokLens 后重试。"
        exit 1
    fi

    local mins_str="$(( interval_sec / 60 )) 分钟"
    if (( interval_sec % 60 != 0 )); then
        mins_str="${interval_sec} 秒"
    fi

    local self_cmd="${BASH_SOURCE[0]:-install_macos_launchd.sh}"
    printf "\n%s============================================================%s\n" "${C_GREEN}${C_BOLD}" "${C_RESET}"
    ok "知微 · TokLens macOS 后台静默同步守护已就绪！"
    printf "%s============================================================%s\n" "${C_GREEN}${C_BOLD}" "${C_RESET}"
    printf "  • 服务标签  : %s (%s)\n" "${LABEL}" "${loaded_via}"
    printf "  • 同步策略  : 每 %s (%s 秒) 自动同步 + 登录自启 + 低优静默 I/O\n" "${mins_str}" "${interval_sec}"
    printf "  • 探针位置  : %s\n" "${AGENT_DEST}"
    printf "  • Plist配置 : %s\n" "${PLIST_PATH}"
    printf "  • 运行日志  : %s\n" "${LOG_FILE}"
    printf "  • 状态巡检  : bash %s --status\n" "${self_cmd}"
    printf "  • 卸载任务  : bash %s --uninstall\n\n" "${self_cmd}"
}

main "$@"
