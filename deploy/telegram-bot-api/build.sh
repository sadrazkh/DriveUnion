#!/usr/bin/env bash
#
# Build telegram-bot-api at the pinned commit, on the Linux box it will run on.
#
#   ./build.sh                     preflight, configure, build, install to /usr/local
#   ./build.sh --jobs 1            override the computed parallelism (see the RAM note below)
#   ./build.sh --prefix /opt/du    install somewhere else
#   ./build.sh --preflight-only    run every check and stop before compiling anything
#
# Re-runnable: the build directory is reused, so a second run after a failed one resumes
# rather than starting over. Pass --clean to throw it away.
#
# ---------------------------------------------------------------------------------------
# READ THIS BEFORE STARTING IT. The single most likely way this goes wrong is an OOM kill at
# 90% on a small VPS, after most of an hour of CPU. That is why every check below runs first
# and why this script would rather refuse than start.
#
#   Time    On the order of an hour. TDLib is nearly all of it. This figure comes from the
#           Telegram spec §2.4.1 and has NOT been measured on this box by anyone.
#   RAM     TDLib's own README, verbatim: "clang 6.0 with libc++ required less than 500 MB of
#           RAM per file and GCC 4.9/6.3 used less than 1 GB of RAM per file". That is PER
#           PARALLEL COMPILER PROCESS. `make -j$(nproc)` on a 4-core / 4 GB box therefore asks
#           for ~4 GB of compiler alone, and the kernel kills it. The preflight below computes
#           a job count that fits in MemAvailable + SwapFree and refuses if even one job does
#           not. Swap counts: a build that runs into swap is slow and finishes, which beats
#           fast and killed.
#   Disk    Several GB of objects in the build tree, on a box whose owner has said there is no
#           room. The default requirement below is an ESTIMATE, not a measurement.
# ---------------------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_DIR="$HERE/vendor/telegram-bot-api"
BUILD_DIR=""
PREFIX="/usr/local"
JOBS=""
CLEAN=0
PREFLIGHT_ONLY=0

# Nobody has built this here, so this is a stated estimate rather than an observed figure.
# If the build runs out of space, raise it and write the real number into this line.
MIN_FREE_GIB="${DUBOTAPI_BUILD_MIN_FREE_GIB:-5}"

# How much memory to leave for everything that is not a compiler: the kernel, sshd, and — on
# this box — Postgres and the panel, which are serving customers while this runs.
RESERVE_MB=512

c_g='\033[0;32m'; c_b='\033[0;34m'; c_y='\033[1;33m'; c_r='\033[0;31m'; c_0='\033[0m'
log()  { printf '%b\n' "${c_b}➜${c_0} $*"; }
ok()   { printf '%b\n' "${c_g}✓${c_0} $*"; }
warn() { printf '%b\n' "${c_y}!${c_0} $*"; }
die()  { printf '%b\n' "${c_r}✗${c_0} $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --jobs)           JOBS="${2:?--jobs needs a number}"; shift 2;;
    --prefix)         PREFIX="${2:?--prefix needs a path}"; shift 2;;
    --source-dir)     SOURCE_DIR="${2:?--source-dir needs a path}"; shift 2;;
    --build-dir)      BUILD_DIR="${2:?--build-dir needs a path}"; shift 2;;
    --clean)          CLEAN=1; shift;;
    --preflight-only) PREFLIGHT_ONLY=1; shift;;
    -h|--help)        sed -n '2,34p' "$0" | sed 's/^# \{0,1\}//'; exit 0;;
    *) die "Unknown option: $1 (try --help)";;
  esac
done

[ -n "$BUILD_DIR" ] || BUILD_DIR="$SOURCE_DIR/build"

[ -f "$HERE/PIN" ] || die "PIN is missing from $HERE. Without it there is no version to build."
# shellcheck source=PIN
. "$HERE/PIN"

# ---------------------------------------------------------------------------------------
# Preflight. Everything that can be known before an hour of CPU is spent is known here.
# ---------------------------------------------------------------------------------------

[ "$(uname -s)" = "Linux" ] || die "This builds a Linux service. On Windows or macOS there is
nothing here that can run; the panel's development machine is not the build machine (spec §2.4.5)."

log "Checking the toolchain…"
MISSING=()
for tool in git cmake gperf make; do
  command -v "$tool" >/dev/null 2>&1 || MISSING+=("$tool")
done

CXX_BIN="${CXX:-}"
if [ -z "$CXX_BIN" ]; then
  if   command -v g++     >/dev/null 2>&1; then CXX_BIN="g++"
  elif command -v clang++ >/dev/null 2>&1; then CXX_BIN="clang++"
  else MISSING+=("g++ or clang++")
  fi
fi

if [ ${#MISSING[@]} -gt 0 ]; then
  echo
  die "Missing: ${MISSING[*]}

Upstream's own dependency list, verbatim: OpenSSL, zlib, C++17 compatible compiler
(Clang 5.0+, GCC 7.0+), gperf (build only), CMake (3.10+, build only).

  Debian / Ubuntu   apt-get install -y build-essential cmake gperf libssl-dev zlib1g-dev git
  RHEL / Alma       dnf install -y gcc-c++ make cmake gperf openssl-devel zlib-devel git
  Alpine            apk add build-base cmake gperf openssl-dev zlib-dev git linux-headers"
fi

CMAKE_VER="$(cmake --version | head -1 | awk '{print $3}')"
CMAKE_MAJOR="${CMAKE_VER%%.*}"
CMAKE_REST="${CMAKE_VER#*.}"
CMAKE_MINOR="${CMAKE_REST%%.*}"
if [ "$CMAKE_MAJOR" -lt 3 ] || { [ "$CMAKE_MAJOR" -eq 3 ] && [ "$CMAKE_MINOR" -lt 10 ]; }; then
  die "CMake $CMAKE_VER is too old; upstream requires 3.10+."
fi
ok "cmake $CMAKE_VER, $CXX_BIN $("$CXX_BIN" -dumpversion 2>/dev/null || echo '?'), gperf present."

# OpenSSL and zlib headers. cmake would find this eventually, but "eventually" is after the
# TDLib subdirectory has been configured, and the message it produces is about a CMake module
# rather than about a package somebody has to install.
log "Probing for the OpenSSL and zlib headers…"
PROBE="$(mktemp -d)"
trap 'rm -rf "$PROBE"' EXIT
cat > "$PROBE/probe.cpp" <<'CPP'
#include <openssl/ssl.h>
#include <zlib.h>
int main() { return 0; }
CPP
if ! "$CXX_BIN" -std=c++17 -c "$PROBE/probe.cpp" -o "$PROBE/probe.o" 2>"$PROBE/probe.log"; then
  echo "--- compiler output ---" >&2
  cat "$PROBE/probe.log" >&2
  die "The OpenSSL and/or zlib development headers are not installed.
  Debian / Ubuntu   apt-get install -y libssl-dev zlib1g-dev
  RHEL / Alma       dnf install -y openssl-devel zlib-devel"
fi
ok "OpenSSL and zlib headers found."

# The install destination, checked NOW rather than after the build. Discovering that
# /usr/local/bin is not writable at the end of an hour is the definition of half-succeeding.
if [ ! -d "$PREFIX/bin" ]; then
  [ -w "$(dirname "$PREFIX")" ] || [ "$(id -u)" -eq 0 ] \
    || die "$PREFIX/bin does not exist and cannot be created as $(id -un). Re-run as root, or --prefix somewhere writable."
elif [ ! -w "$PREFIX/bin" ] && [ "$(id -u)" -ne 0 ]; then
  die "$PREFIX/bin is not writable by $(id -un), and this is not root.
The build would take about an hour and then fail on the last step. Re-run as root:
  sudo $0 $*"
fi
ok "Install destination $PREFIX/bin is reachable."

# --- Memory, which is the one that kills builds ------------------------------------------
mem_kb() { awk -v k="$1" '$1 == k":" {print $2; found=1} END {if (!found) print 0}' /proc/meminfo; }
MEM_AVAIL_MB=$(( $(mem_kb MemAvailable) / 1024 ))
MEM_TOTAL_MB=$(( $(mem_kb MemTotal) / 1024 ))
SWAP_FREE_MB=$(( $(mem_kb SwapFree) / 1024 ))
NPROC="$(nproc 2>/dev/null || echo 1)"

# TDLib's own figures, verbatim from its README: "clang 6.0 with libc++ required less than
# 500 MB of RAM per file and GCC 4.9/6.3 used less than 1 GB of RAM per file". GCC is the
# default on every distro this is likely to run on, so the conservative number is the default.
if "$CXX_BIN" --version 2>/dev/null | head -1 | grep -qi clang; then
  PER_JOB_MB=512; COMPILER_KIND="clang"
else
  PER_JOB_MB=1024; COMPILER_KIND="gcc"
fi

BUDGET_MB=$(( MEM_AVAIL_MB + SWAP_FREE_MB - RESERVE_MB ))
MAX_JOBS=$(( BUDGET_MB / PER_JOB_MB ))

echo
echo "  memory      ${MEM_TOTAL_MB} MB total, ${MEM_AVAIL_MB} MB available, ${SWAP_FREE_MB} MB swap free"
echo "  reserve     ${RESERVE_MB} MB left for the rest of the box"
echo "  per job     ${PER_JOB_MB} MB (${COMPILER_KIND}, TDLib's own figure)"
echo "  budget      ${BUDGET_MB} MB → at most ${MAX_JOBS} parallel compiler processes"
echo "  cpus        ${NPROC}"
echo

if [ -n "$JOBS" ]; then
  if [ "$JOBS" -gt "$MAX_JOBS" ]; then
    warn "--jobs $JOBS is above the ${MAX_JOBS} this box's memory supports. If the build dies
      with 'Killed' or 'signal 9' and no other message, this is why. Nothing here can stop you."
  fi
elif [ "$MAX_JOBS" -lt 1 ]; then
  die "There is not enough memory to run even one compiler process, let alone link TDLib.

Add swap before building — it is slower and it finishes, which is the trade you want:

  fallocate -l 4G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile
  echo '/swapfile none swap sw 0 0' >> /etc/fstab

Then re-run. If swap is not an option on this box, the answer is to build the binary somewhere
else and copy it over — the spec §2.4.1 names a Linux CI job as the better long-term answer."
else
  JOBS=$(( MAX_JOBS < NPROC ? MAX_JOBS : NPROC ))
fi
ok "Building with -j$JOBS."

# --- Disk --------------------------------------------------------------------------------
mkdir -p "$BUILD_DIR"
FREE_KB="$(df -Pk "$BUILD_DIR" | awk 'NR==2 {print $4}')"
FREE_GIB=$(( FREE_KB / 1024 / 1024 ))
if [ "$FREE_GIB" -lt "$MIN_FREE_GIB" ]; then
  die "Only ${FREE_GIB} GiB free on the volume holding $BUILD_DIR; this build wants at least
${MIN_FREE_GIB} GiB of object files. That figure is an estimate — nobody has measured this build.

If you believe it fits, override it:  DUBOTAPI_BUILD_MIN_FREE_GIB=3 $0

Note that this is the same volume the Bot API working directory and M3's spool live on. Filling
it during a build takes the panel down with it."
fi
ok "${FREE_GIB} GiB free where the build tree goes."

# --- The pin -----------------------------------------------------------------------------
log "Verifying the source against PIN…"
"$HERE/fetch-source.sh" --verify-only --source-dir "$SOURCE_DIR"

if [ "$PREFLIGHT_ONLY" -eq 1 ]; then
  echo
  ok "Preflight passed. Nothing was compiled (--preflight-only)."
  exit 0
fi

# ---------------------------------------------------------------------------------------
# Build.
# ---------------------------------------------------------------------------------------
if [ "$CLEAN" -eq 1 ]; then
  log "Removing $BUILD_DIR…"
  rm -rf "$BUILD_DIR"
  mkdir -p "$BUILD_DIR"
fi

log "Configuring…"
cmake -S "$SOURCE_DIR" -B "$BUILD_DIR" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX="$PREFIX"

echo
log "Compiling. This is the hour. Watch it with: journalctl -f  (nothing here) — or just wait."
log "If it stops with 'Killed' and nothing else, that was the OOM killer; re-run with --jobs 1."
echo

# `cmake --build … -- -j N` rather than `--parallel N`, because --parallel is CMake 3.12+ and
# upstream's floor is 3.10. The -j goes through to make or ninja either way.
cmake --build "$BUILD_DIR" -- -j "$JOBS"

log "Installing to $PREFIX…"
cmake --build "$BUILD_DIR" --target install

BINARY="$PREFIX/bin/telegram-bot-api"
[ -x "$BINARY" ] || die "The install step reported success but $BINARY is not there. Nothing to run."

echo
ok "Built and installed."
echo
echo "  binary   $BINARY"
echo "  reports  $("$BINARY" --version 2>&1 | head -1)"
echo "  pinned   Bot API $BOT_API_VERSION at ${PINNED_COMMIT:0:12}"
echo
echo "Compare those last two lines. They must agree; if they do not, the binary on this box is"
echo "not the one PIN names and the spec's §2.1 figures do not describe it."
echo
echo "Next: ./install.sh   (creates the user, the directories, the unit and the sweep timer)"
