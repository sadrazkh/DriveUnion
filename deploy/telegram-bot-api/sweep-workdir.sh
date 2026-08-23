#!/usr/bin/env bash
#
# Delete stale files out of the Bot API server's working directory.
#
#   ./sweep-workdir.sh                    DRY RUN — report what would go, delete nothing
#   ./sweep-workdir.sh --delete           actually delete
#   ./sweep-workdir.sh --age 5 --delete   override the age from the environment file
#   ./sweep-workdir.sh --dir /path        sweep one specific directory (repeatable)
#
# Dry run is the default and there is no way to make deletion the default. A sweeper that
# deletes by accident on a box holding customer file contents is worse than one that never runs.
#
# ---------------------------------------------------------------------------------------
# WHY THIS EXISTS. The owner's constraint, in their own words:
#
#     «من جا نداره سرورم جا داشتم که از تلگرام و گوگل درایو استفاده نمیکردم»
#     There is no room on the server; if there were, neither Telegram nor Drive would be here.
#
# The Bot API server writes every file it handles into its working directory and never removes
# it. That is not an oversight to work around — reading the pinned source's complete option
# list, there is no option for automatic deletion, expiry, cleanup or retention. Nothing but
# this deletes anything. With a 2000 MB ceiling in both directions, a directory nobody sweeps
# is a full volume, and a full volume takes Postgres and M3's transfer spool down with it.
#
# WHAT THIS IS NOT. The Telegram spec §2.4.2 puts the sweep in the panel as a tested
# BackgroundService, on the grounds that a shell one-liner has no test. That is right, and this
# does not replace it. This runs for the two cases the in-app sweeper structurally cannot:
#
#   1. The app is not running. The most likely moment for this directory to hold gigabytes is
#      immediately after the panel crashed mid-transfer — which is exactly when its
#      BackgroundService is not running either.
#   2. The app does not exist yet. This server gets built, started and rehearsed against a
#      throwaway bot (spec §2.4.4) before any Telegram C# ships. Files land on the disk during
#      that rehearsal.
#
# And it is not a one-liner: --dry-run prints, per file, what it would remove and why, which is
# a test a person can run in one command against the live directory.
#
# It also deliberately does NOT implement the spec's free-space watermark rule — "below
# WorkDirMinFreeBytes, delete oldest-first regardless of age". Deleting a five-minute-old file
# is destructive; it may be an in-flight transfer. That decision needs to know what is in
# flight, the panel knows and a timer does not, and an unattended `find` making it at 3am is the
# wrong place for it.
# ---------------------------------------------------------------------------------------
set -euo pipefail

ENV_FILE="${DUBOTAPI_ENV_FILE:-/etc/drive-union-bot-api/bot-api.env}"

# Every directory swept must be strictly inside this. Not a convention — a check, applied to
# each target after symlink resolution. It is the difference between a bug that deletes stale
# files from the wrong directory and one that deletes /var.
STATE_ROOT="${DUBOTAPI_STATE_ROOT:-/var/lib/drive-union-bot-api}"

DELETE=0
AGE=""
DIRS=()

c_g='\033[0;32m'; c_y='\033[1;33m'; c_r='\033[0;31m'; c_0='\033[0m'
ok()   { printf '%b\n' "${c_g}✓${c_0} $*"; }
warn() { printf '%b\n' "${c_y}!${c_0} $*"; }
die()  { printf '%b\n' "${c_r}✗${c_0} $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --delete)   DELETE=1; shift;;
    --dry-run)  DELETE=0; shift;;
    --age)      AGE="${2:?--age needs a number of minutes}"; shift 2;;
    --dir)      DIRS+=("${2:?--dir needs a path}"); shift 2;;
    --env-file) ENV_FILE="${2:?--env-file needs a path}"; shift 2;;
    -h|--help)  sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 0;;
    *) die "Unknown option: $1 (try --help)";;
  esac
done

# GNU find. -printf and -xdev are not in BusyBox find, and the way BusyBox fails here is by
# treating -printf as a path, finding nothing, and reporting a clean sweep.
find --version 2>/dev/null | head -1 | grep -qi 'GNU findutils' \
  || die "This needs GNU find. -printf and -xdev are what make the report accurate, and a find
without them reports an empty sweep rather than an error."

if [ ${#DIRS[@]} -eq 0 ] || [ -z "$AGE" ]; then
  [ -r "$ENV_FILE" ] || die "Cannot read $ENV_FILE, and neither --dir nor --age fully replaced it.
Either run as a user that can read the environment file, or pass --dir and --age explicitly."
  # shellcheck disable=SC1090
  . "$ENV_FILE"
  [ ${#DIRS[@]} -eq 0 ] && DIRS=("${DUBOTAPI_WORK_DIR:?DUBOTAPI_WORK_DIR is not set in $ENV_FILE}" \
                                 "${DUBOTAPI_TEMP_DIR:?DUBOTAPI_TEMP_DIR is not set in $ENV_FILE}")
  [ -z "$AGE" ] && AGE="${DUBOTAPI_SWEEP_MAX_AGE_MINUTES:?DUBOTAPI_SWEEP_MAX_AGE_MINUTES is not set in $ENV_FILE}"
fi

case "$AGE" in
  ''|*[!0-9]*) die "--age must be a whole number of minutes, got '$AGE'.";;
esac
[ "$AGE" -ge 1 ] || die "--age of $AGE minutes would delete files the server is writing right now."

command -v realpath >/dev/null 2>&1 || die "realpath is not installed; every safety check here depends on it."
ROOT_REAL="$(realpath -e "$STATE_ROOT" 2>/dev/null)" \
  || die "$STATE_ROOT does not exist. Nothing has been installed here, or DUBOTAPI_STATE_ROOT is wrong."

# Decimal units, matching how the spec states every size in this feature (2000 MB is 2e9 bytes,
# not 2 GiB — see §2.1 on why the decimal reading is the one that is enforced).
human() { awk -v b="$1" 'BEGIN {
  split("B KB MB GB TB", u, " "); i = 1;
  while (b >= 1000 && i < 5) { b /= 1000; i++ }
  fmt = (i == 1) ? "%d %s" : "%.1f %s";
  printf fmt, b, u[i]
}'; }

# ---------------------------------------------------------------------------------------
# The refusal list. Each target has to survive all of it before a single file is looked at.
# ---------------------------------------------------------------------------------------
vet() {
  local raw="$1" real

  [ -n "$raw" ] || die "An empty path was passed as a sweep target. That is how a script deletes \$HOME."

  real="$(realpath -e "$raw" 2>/dev/null)" || die "Sweep target does not exist: $raw"
  [ -d "$real" ] || die "Sweep target is not a directory: $raw → $real"

  # Symlinks: the target itself is resolved above, and `find -P` below never follows one during
  # traversal. Between them there is no path by which a symlink planted in the working directory
  # can point this at something outside it.
  case "$real" in
    "$ROOT_REAL"|"$ROOT_REAL"/*) ;;
    *) die "Refusing to sweep $real — it is outside $ROOT_REAL.
Every deletion this script performs is inside that tree by construction. If the working
directory genuinely moved, move DUBOTAPI_STATE_ROOT and the unit's ReadWritePaths with it.";;
  esac

  # A second, independent lock. If STATE_ROOT itself were ever set to something catastrophic,
  # the containment check above would happily pass.
  case "$real" in
    /|/bin|/boot|/dev|/etc|/home|/lib|/lib64|/opt|/proc|/root|/run|/sbin|/srv|/sys|/tmp|/usr|/var|/var/lib|/var/log)
      die "Refusing to sweep $real. That is a system directory.";;
  esac

  # Depth: /var/lib/drive-union-bot-api/work is four components. Anything shallower than three
  # is not a service's working directory whatever it is called.
  local depth; depth=$(( $(printf '%s' "${real#/}" | tr -cd '/' | wc -c) ))
  [ "$depth" -ge 2 ] || die "Refusing to sweep $real — only $((depth + 1)) path components deep."

  printf '%s' "$real"
}

# ---------------------------------------------------------------------------------------
# Sweep.
# ---------------------------------------------------------------------------------------
MODE="dry-run"; [ "$DELETE" -eq 1 ] && MODE="delete"

total_files=0
total_bytes=0
failed=0

for target in "${DIRS[@]}"; do
  dir="$(vet "$target")"

  # -P   never follow a symlink, not even one named on the command line (default, said aloud)
  # -xdev  never cross onto another filesystem — a mount appearing under the working directory
  #        is not this service's data and is not ours to delete
  # -mindepth 1  never the directory itself
  # -type f  only regular files. Never a directory, never a symlink, never a socket. Empty
  #        per-bot subdirectories are left alone deliberately: the server has them open.
  # -mmin +N  modified more than N minutes ago
  while IFS= read -r -d '' record; do
    size="${record%%$'\t'*}"
    path="${record#*$'\t'}"

    if [ "$DELETE" -eq 1 ]; then
      if rm -f -- "$path" 2>/dev/null; then
        total_files=$((total_files + 1))
        total_bytes=$((total_bytes + size))
        printf '  removed  %10s  %s\n' "$(human "$size")" "$path"
      else
        failed=$((failed + 1))
        warn "could not remove $path"
      fi
    else
      total_files=$((total_files + 1))
      total_bytes=$((total_bytes + size))
      printf '  would remove  %10s  %s\n' "$(human "$size")" "$path"
    fi
  done < <(find -P "$dir" -xdev -mindepth 1 -type f -mmin "+$AGE" -printf '%s\t%p\0')
done

# ---------------------------------------------------------------------------------------
# The report, on one line, always — including when nothing was removed.
#
# Two numbers, and in production they mean opposite things. `removed` is the crash-path count
# and zero is the GOOD state, because the panel is supposed to delete each file the instant its
# send completes; an alarm on zero deletions would fire every minute of a healthy year.
# `remaining` is the real health signal: it should sit at or near zero, and a remaining size
# that stays above zero across several minutes means delete-on-success has stopped running.
#
# Both are printed unconditionally so that a sweeper which deleted nothing does not look
# identical to one that never ran — the log line's absence is the failure, not its contents.
# ---------------------------------------------------------------------------------------
rem_files=0
rem_bytes=0
for target in "${DIRS[@]}"; do
  dir="$(vet "$target")"
  while IFS= read -r -d '' size; do
    rem_files=$((rem_files + 1))
    rem_bytes=$((rem_bytes + size))
  done < <(find -P "$dir" -xdev -mindepth 1 -type f -printf '%s\0')
done

# printf "%d", not print: awk's default output format is %.6g, which turns a hundred gigabytes
# into "1.024e+11" and every arithmetic use of it downstream into nonsense.
free_bytes="$(df -Pk "$ROOT_REAL" | awk 'NR==2 {printf "%d", $4 * 1024}')"

printf 'sweep mode=%s age_min=%s dirs=%s removed_files=%d removed_bytes=%d (%s) remaining_files=%d remaining_bytes=%d (%s) free_bytes=%s (%s) failed=%d\n' \
  "$MODE" "$AGE" "${#DIRS[@]}" \
  "$total_files" "$total_bytes" "$(human "$total_bytes")" \
  "$rem_files" "$rem_bytes" "$(human "$rem_bytes")" \
  "$free_bytes" "$(human "$free_bytes")" \
  "$failed"

if [ "$failed" -gt 0 ]; then
  die "$failed file(s) could not be removed. Almost always this is ownership: the files belong
to the Bot API server's user and this process is not in its group. See README.md § 'Permissions'."
fi

if [ "$DELETE" -eq 0 ] && [ "$total_files" -gt 0 ]; then
  warn "Dry run. Nothing was deleted. Add --delete."
else
  ok "Sweep complete."
fi
