#!/usr/bin/env bash
#
# Fetch tdlib/telegram-bot-api at the commit named in PIN, and prove it is that commit.
#
#   ./fetch-source.sh                 fetch (or re-point) the source tree at the pin
#   ./fetch-source.sh --verify-only   check an existing tree against the pin, fetch nothing
#   ./fetch-source.sh --check-upstream  report how far upstream has moved past the pin
#   ./fetch-source.sh --source-dir DIR  put the tree somewhere other than ./vendor/telegram-bot-api
#
# Re-runnable. Running it twice on an up-to-date tree does nothing and says so.
#
# This is a fetch script rather than a git submodule. The reasoning is in README.md
# § "The pin"; the short version is that upstream publishes no tags, so a submodule would
# record a bare SHA with nothing beside it, and it would force `git clone --recursive` and a
# few hundred megabytes of C++ onto every .NET developer on a machine that cannot build it.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_DIR="$HERE/vendor/telegram-bot-api"
MODE="fetch"

c_g='\033[0;32m'; c_b='\033[0;34m'; c_y='\033[1;33m'; c_r='\033[0;31m'; c_0='\033[0m'
log()  { printf '%b\n' "${c_b}➜${c_0} $*"; }
ok()   { printf '%b\n' "${c_g}✓${c_0} $*"; }
warn() { printf '%b\n' "${c_y}!${c_0} $*"; }
die()  { printf '%b\n' "${c_r}✗${c_0} $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --verify-only)    MODE="verify";  shift;;
    --check-upstream) MODE="upstream"; shift;;
    --source-dir)     SOURCE_DIR="${2:?--source-dir needs a path}"; shift 2;;
    -h|--help)        sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 0;;
    *) die "Unknown option: $1 (try --help)";;
  esac
done

[ -f "$HERE/PIN" ] || die "PIN is missing from $HERE. Without it there is no version to build."
# shellcheck source=PIN
. "$HERE/PIN"

for v in UPSTREAM_URL PINNED_COMMIT BOT_API_VERSION; do
  [ -n "${!v:-}" ] || die "PIN does not set $v. Refusing to guess."
done

# A full 40-character SHA, not an abbreviation. An abbreviated pin is ambiguous the day
# upstream grows a colliding prefix, and `git checkout` would take it silently.
case "$PINNED_COMMIT" in
  [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]) ;;
  *) die "PINNED_COMMIT is not a full 40-character SHA: '$PINNED_COMMIT'";;
esac

command -v git >/dev/null 2>&1 || die "git is not installed."

# ---------------------------------------------------------------------------------------
# What the checked-out source says its Bot API version is.
#
# Read from the source, not from a built binary, so that a wrong pin is caught in the second
# before the build rather than in the hour after it. Two independent places, because one grep
# that silently stops matching after an upstream file move is indistinguishable from a version
# that did not change.
# ---------------------------------------------------------------------------------------
verify_bot_api_version() {
  local dir="$1" cmake_file="$1/CMakeLists.txt" cpp_file="$1/telegram-bot-api/telegram-bot-api.cpp"
  local found=0

  [ -f "$cmake_file" ] || die "$cmake_file is missing — this is not a telegram-bot-api tree."
  [ -f "$cpp_file" ]   || die "$cpp_file is missing — this is not a telegram-bot-api tree."

  if grep -qE "^project\(TelegramBotApi VERSION ${BOT_API_VERSION//./\\.} " "$cmake_file"; then
    found=$((found + 1))
  else
    warn "CMakeLists.txt does not declare VERSION $BOT_API_VERSION. It declares:"
    grep -nE '^project\(TelegramBotApi' "$cmake_file" >&2 || echo "  (no project(TelegramBotApi …) line at all)" >&2
  fi

  if grep -qF "parameters->version_ = \"$BOT_API_VERSION\";" "$cpp_file"; then
    found=$((found + 1))
  else
    warn "telegram-bot-api.cpp does not set version_ to $BOT_API_VERSION. It sets:"
    grep -nF 'parameters->version_' "$cpp_file" >&2 || echo "  (no parameters->version_ line at all)" >&2
  fi

  [ "$found" -eq 2 ] || die "The pinned source does not implement Bot API $BOT_API_VERSION.
The spec's §2.1 numbers were read against $BOT_API_VERSION. Either PIN's BOT_API_VERSION is
wrong for PINNED_COMMIT, or upstream moved the version out from under both greps. Settle which
before building — a server that implements a different Bot API version than the spec describes
is worse than one that does not build.
Source tree: $dir"

  ok "Source declares Bot API $BOT_API_VERSION in both CMakeLists.txt and telegram-bot-api.cpp."
}

verify_checkout() {
  local dir="$1" head
  [ -d "$dir/.git" ] || die "$dir is not a git checkout. Run without --verify-only to create it."
  head="$(git -C "$dir" rev-parse HEAD)"
  [ "$head" = "$PINNED_COMMIT" ] || die "The source tree is not at the pin.
  pinned : $PINNED_COMMIT  ($PINNED_COMMIT_SUBJECT)
  checked: $head
Re-run this script without --verify-only to move it, or fix PIN if the tree is right."
  ok "HEAD is the pinned commit ${PINNED_COMMIT:0:12} ($PINNED_COMMIT_SUBJECT)."

  # TDLib is a submodule of telegram-bot-api (.gitmodules: path td → https://github.com/tdlib/td.git).
  # Its commit is pinned transitively by the gitlink inside the pinned commit, so there is nothing
  # to record here — but there IS something to check, because a non-recursive clone leaves td/
  # empty and cmake fails several minutes in with a message about a missing subdirectory.
  [ -f "$dir/td/CMakeLists.txt" ] || die "$dir/td is empty — the TDLib submodule was not initialised.
Fix: git -C '$dir' submodule update --init --recursive"
  ok "TDLib submodule present at $dir/td ($(git -C "$dir" rev-parse --short HEAD:td 2>/dev/null || echo 'gitlink unreadable'))."

  if [ -n "$(git -C "$dir" status --porcelain 2>/dev/null)" ]; then
    warn "The source tree has local modifications. You are not building the pin."
    git -C "$dir" status --short >&2
  fi
}

case "$MODE" in
  verify)
    verify_checkout "$SOURCE_DIR"
    verify_bot_api_version "$SOURCE_DIR"
    ;;

  upstream)
    # Answers "is the pin still current?" without touching the working tree. It cannot answer
    # "did the Bot API version change" on its own — for that, read the commit subjects it prints
    # and https://core.telegram.org/bots/api-changelog. Upstream marks a version bump with a
    # commit literally titled "Update version to X.Y."
    log "Asking $UPSTREAM_URL what its default branch head is…"
    remote_head="$(git ls-remote "$UPSTREAM_URL" HEAD | awk '{print $1}')"
    [ -n "$remote_head" ] || die "git ls-remote returned nothing. Network, or the URL moved."

    if [ "$remote_head" = "$PINNED_COMMIT" ]; then
      ok "The pin IS upstream head. Nothing to bump."
    else
      warn "Upstream has moved."
      echo "  pinned : $PINNED_COMMIT  ($PINNED_COMMIT_DATE — $PINNED_COMMIT_SUBJECT)"
      echo "  head   : $remote_head"
      echo
      echo "To see what changed, and whether the Bot API version moved:"
      echo "  git -C '$SOURCE_DIR' fetch origin"
      echo "  git -C '$SOURCE_DIR' log --oneline $PINNED_COMMIT..$remote_head"
      echo
      echo "A commit titled 'Update version to X.Y.' in that range means the Bot API version"
      echo "changed, which means the Telegram spec's §2.1 figures need re-reading before this"
      echo "pin moves. A range with no such commit is a maintenance bump and is cheap."
    fi
    ;;

  fetch)
    if [ -d "$SOURCE_DIR/.git" ]; then
      if [ "$(git -C "$SOURCE_DIR" rev-parse HEAD)" = "$PINNED_COMMIT" ]; then
        ok "Source already at the pin; fetching nothing."
      else
        log "Moving the existing tree to the pin…"
        git -C "$SOURCE_DIR" fetch --depth 1 origin "$PINNED_COMMIT" 2>/dev/null \
          || git -C "$SOURCE_DIR" fetch origin
        git -C "$SOURCE_DIR" checkout --quiet --detach "$PINNED_COMMIT"
      fi
    else
      [ -e "$SOURCE_DIR" ] && die "$SOURCE_DIR exists but is not a git checkout. Move it aside."
      log "Cloning $UPSTREAM_URL into $SOURCE_DIR (this pulls TDLib too; expect a few hundred MB)…"
      mkdir -p "$(dirname "$SOURCE_DIR")"
      git clone "$UPSTREAM_URL" "$SOURCE_DIR"
      git -C "$SOURCE_DIR" checkout --quiet --detach "$PINNED_COMMIT"
    fi

    log "Initialising the TDLib submodule…"
    git -C "$SOURCE_DIR" submodule update --init --recursive

    verify_checkout "$SOURCE_DIR"
    verify_bot_api_version "$SOURCE_DIR"
    ok "Source ready at $SOURCE_DIR. Next: ./build.sh"
    ;;
esac
