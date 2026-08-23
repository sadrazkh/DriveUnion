#!/usr/bin/env bash
#
# Install the Drive Union Bot API server: user, directories, environment file, systemd unit,
# sweep timer, and the checks that turn a wrong configuration into a refusal instead of a
# quietly-public server.
#
#   ./install.sh                          fetch, build if needed, install, check, do not start
#   ./install.sh --start                  …and start it once every check has passed
#   ./install.sh --skip-build             the binary is already installed; just do the rest
#   ./install.sh --panel-user drive-union add the panel's user to the group that can delete
#                                         the server's files (spec §2.4.2 — do this)
#   ./install.sh --verify                 change nothing; run every check against what is there
#
# Run as root. Re-runnable: it creates what is missing and leaves what is there, and it never
# overwrites an existing environment file.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

SERVICE_USER="dubotapi"
SERVICE_GROUP="dubotapi"
STATE_ROOT="/var/lib/drive-union-bot-api"
CONFIG_DIR="/etc/drive-union-bot-api"
ENV_FILE="$CONFIG_DIR/bot-api.env"
UNIT_DIR="/etc/systemd/system"
SERVER_UNIT="drive-union-bot-api.service"
SWEEP_UNIT="drive-union-bot-api-sweep.service"
SWEEP_TIMER="drive-union-bot-api-sweep.timer"
PREFIX="/usr/local"
BINARY="$PREFIX/bin/telegram-bot-api"

DO_START=0
SKIP_BUILD=0
VERIFY_ONLY=0
PANEL_USER=""

c_g='\033[0;32m'; c_b='\033[0;34m'; c_y='\033[1;33m'; c_r='\033[0;31m'; c_0='\033[0m'
log()  { printf '%b\n' "${c_b}➜${c_0} $*"; }
ok()   { printf '%b\n' "${c_g}✓${c_0} $*"; }
warn() { printf '%b\n' "${c_y}!${c_0} $*"; }
die()  { printf '%b\n' "${c_r}✗${c_0} $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --start)      DO_START=1; shift;;
    --skip-build) SKIP_BUILD=1; shift;;
    --verify)     VERIFY_ONLY=1; shift;;
    --panel-user) PANEL_USER="${2:?--panel-user needs a username}"; shift 2;;
    --prefix)     PREFIX="${2:?--prefix needs a path}"; BINARY="$PREFIX/bin/telegram-bot-api"; shift 2;;
    -h|--help)    sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 0;;
    *) die "Unknown option: $1 (try --help)";;
  esac
done

[ "$(id -u)" -eq 0 ] || die "Run as root: it creates a system user and writes into /etc and /var/lib."
[ "$(uname -s)" = "Linux" ] || die "Linux only."
command -v systemctl >/dev/null 2>&1 || die "systemd is required; this installs systemd units."

[ -f "$HERE/PIN" ] || die "PIN is missing from $HERE."
# shellcheck source=PIN
. "$HERE/PIN"

# ---------------------------------------------------------------------------------------
# systemd version. SocketBindDeny/SocketBindAllow in the unit need 249+. Checked before the
# unit is installed rather than after it fails to start.
# ---------------------------------------------------------------------------------------
SYSTEMD_VER="$(systemctl --version | head -1 | awk '{print $2}' | tr -cd '0-9')"
if [ -n "$SYSTEMD_VER" ] && [ "$SYSTEMD_VER" -lt 249 ]; then
  warn "systemd $SYSTEMD_VER is older than 249. The unit's SocketBindDeny= / SocketBindAllow=
      lines will not be understood. Remove those two lines from $SERVER_UNIT before starting it.
      Losing them costs one of three independent locks on the listener; --http-ip-address and
      the firewall rule are the other two and both still apply."
else
  ok "systemd $SYSTEMD_VER supports SocketBind*."
fi

# ---------------------------------------------------------------------------------------
# The binary.
# ---------------------------------------------------------------------------------------
build_if_needed() {
  if [ -x "$BINARY" ]; then
    local reported
    reported="$("$BINARY" --version 2>&1 | head -1)"
    if printf '%s' "$reported" | grep -qF "$BOT_API_VERSION"; then
      ok "telegram-bot-api present and reports $BOT_API_VERSION, matching PIN."
      return 0
    fi
    warn "$BINARY reports '$reported' but PIN names Bot API $BOT_API_VERSION. Rebuilding."
  fi

  [ "$SKIP_BUILD" -eq 0 ] || die "$BINARY is missing or is the wrong version, and --skip-build was given."

  log "Fetching the pinned source…"
  "$HERE/fetch-source.sh"
  log "Building. This takes about an hour and is memory-hungry; build.sh checks first."
  "$HERE/build.sh" --prefix "$PREFIX"
}

# ---------------------------------------------------------------------------------------
# User, group, directories.
#
# The group is the mechanism by which the panel deletes files the server wrote — the instant a
# send completes, per spec §2.4.2. setgid on the directories so new files inherit the group,
# 2770 so the group can write, and UMask=0002 in the unit so the server does not strip the
# group-write bit off each file as it creates it. Miss any one of the three and the panel's
# delete-on-success fails with EACCES on every file, which the sweeper then masks by cleaning
# up thirty minutes late.
# ---------------------------------------------------------------------------------------
ensure_identity() {
  if ! getent group "$SERVICE_GROUP" >/dev/null; then
    log "Creating group $SERVICE_GROUP…"
    groupadd --system "$SERVICE_GROUP"
  fi

  if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    log "Creating system user $SERVICE_USER…"
    useradd --system --gid "$SERVICE_GROUP" --home-dir "$STATE_ROOT" \
            --no-create-home --shell /usr/sbin/nologin \
            --comment "Drive Union Telegram Bot API server" "$SERVICE_USER"
  fi
  ok "Service identity $SERVICE_USER:$SERVICE_GROUP."

  install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 2770 "$STATE_ROOT"
  install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 2770 "$STATE_ROOT/work"
  install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 2770 "$STATE_ROOT/tmp"
  # install -d does not always keep the setgid bit; set it again rather than assume.
  chmod 2770 "$STATE_ROOT" "$STATE_ROOT/work" "$STATE_ROOT/tmp"
  ok "$STATE_ROOT/{work,tmp} exist, mode 2770, $SERVICE_USER:$SERVICE_GROUP."

  install -d -o root -g root -m 0700 "$CONFIG_DIR"

  if [ -n "$PANEL_USER" ]; then
    id -u "$PANEL_USER" >/dev/null 2>&1 || die "--panel-user $PANEL_USER does not exist."
    usermod -aG "$SERVICE_GROUP" "$PANEL_USER"
    ok "$PANEL_USER added to $SERVICE_GROUP. It must restart before the new group takes effect."
  else
    warn "No --panel-user given. Until the panel's user is in the $SERVICE_GROUP group it cannot
      delete the files the server writes, and delete-on-success (spec §2.4.2 rule 1) fails on
      every file. Re-run with --panel-user <name>, or: usermod -aG $SERVICE_GROUP <name>"
  fi
}

# ---------------------------------------------------------------------------------------
# The environment file, and the checks that make it safe to start.
# ---------------------------------------------------------------------------------------
ensure_env_file() {
  if [ ! -f "$ENV_FILE" ]; then
    log "Creating $ENV_FILE from the example…"
    install -o root -g root -m 0600 "$HERE/drive-union-bot-api.env.example" "$ENV_FILE"
    warn "Every value in $ENV_FILE is empty. Fill it in before starting the service."
  fi
  chown root:root "$ENV_FILE"
  chmod 0600 "$ENV_FILE"
  ok "$ENV_FILE present, root:root, mode 0600."
}

check_env_file() {
  # shellcheck disable=SC1090
  . "$ENV_FILE"

  local missing=()
  for key in TELEGRAM_API_ID TELEGRAM_API_HASH DUBOTAPI_WORK_DIR DUBOTAPI_TEMP_DIR \
             DUBOTAPI_HTTP_IP DUBOTAPI_HTTP_PORT DUBOTAPI_MAX_CONNECTIONS \
             DUBOTAPI_VERBOSITY DUBOTAPI_SWEEP_MAX_AGE_MINUTES; do
    [ -n "${!key:-}" ] || missing+=("$key")
  done

  if [ ${#missing[@]} -gt 0 ]; then
    die "These keys in $ENV_FILE have no value: ${missing[*]}

Nothing is started until they do. An empty DUBOTAPI_HTTP_IP in particular does not mean 'the
default' — the argument disappears and the server falls back to accepting connections on ANY
local IPv4 address, which is this box's public one. See README.md § 'The front door' for what
that costs. DUBOTAPI_EXTRA_ARGS is the one key that may legitimately stay empty."
  fi

  # The bind address. There is no legitimate reason for this to be anything but loopback while
  # the panel and the server share a box, and this is the check that catches a typo before the
  # firewall has to.
  case "$DUBOTAPI_HTTP_IP" in
    127.0.0.1|::1|127.*) ok "Bind address $DUBOTAPI_HTTP_IP is loopback.";;
    *) die "DUBOTAPI_HTTP_IP is '$DUBOTAPI_HTTP_IP', which is not loopback.

An unauthenticated Bot API server on a routable address is a total compromise of every bot on
it AND an arbitrary-file-read primitive on this host — the bot token is the only credential and
it is in the URL path, and --local mode implements file:// uploads. Nothing in Drive Union needs
this server to be reachable from anywhere: the panel calls it over loopback and it calls the
panel's webhook back over loopback. If you are certain, edit this check out deliberately rather
than by passing a flag.";;
  esac

  # DUBOTAPI_WORK_DIR / TEMP_DIR against the unit's ReadWritePaths. systemd does not expand
  # variables in ReadWritePaths, so these two can drift apart, and when they do the server
  # starts, listens, looks healthy, and gets EACCES on the first file that arrives.
  local rw; rw="$(awk -F= '/^ReadWritePaths=/ {print $2}' "$HERE/$SERVER_UNIT" | head -1 | tr -d ' ')"
  for d in "$DUBOTAPI_WORK_DIR" "$DUBOTAPI_TEMP_DIR"; do
    case "$d" in
      "$rw"|"$rw"/*) ;;
      *) die "$d is outside the unit's ReadWritePaths ($rw).
ProtectSystem=strict makes everything else read-only, so the server would see that directory and
be unable to write to it. Either move the directory back under $rw, or change ReadWritePaths in
$SERVER_UNIT — and change DUBOTAPI_STATE_ROOT for the sweeper at the same time.";;
    esac
  done
  ok "Working and temp directories are inside the unit's ReadWritePaths."

  # The port against SocketBindAllow. Same class of drift, different symptom: the unit starts
  # and every connection is refused.
  local allowed; allowed="$(awk -F= '/^SocketBindAllow=/ {print $2}' "$HERE/$SERVER_UNIT" | head -1 | tr -d ' ')"
  if [ -n "$allowed" ] && [ "$allowed" != "$DUBOTAPI_HTTP_PORT" ]; then
    die "DUBOTAPI_HTTP_PORT is $DUBOTAPI_HTTP_PORT but the unit's SocketBindAllow= is $allowed.
The server would start and be unable to bind. Change one to match the other."
  fi
  ok "Port $DUBOTAPI_HTTP_PORT matches the unit's SocketBindAllow."
}

# ---------------------------------------------------------------------------------------
# Units. The sweep unit's ExecStart and ReadOnlyPaths name this checkout, so they are rewritten
# to wherever it actually is rather than assuming a path.
# ---------------------------------------------------------------------------------------
install_units() {
  install -o root -g root -m 0644 "$HERE/$SERVER_UNIT" "$UNIT_DIR/$SERVER_UNIT"

  sed -e "s#^ExecStart=.*sweep-workdir.sh#ExecStart=$HERE/sweep-workdir.sh#" \
      -e "s#^ReadOnlyPaths=.*telegram-bot-api\$#ReadOnlyPaths=$HERE#" \
      "$HERE/$SWEEP_UNIT" > "$UNIT_DIR/$SWEEP_UNIT"
  chown root:root "$UNIT_DIR/$SWEEP_UNIT"; chmod 0644 "$UNIT_DIR/$SWEEP_UNIT"

  install -o root -g root -m 0644 "$HERE/$SWEEP_TIMER" "$UNIT_DIR/$SWEEP_TIMER"

  chmod 0755 "$HERE/sweep-workdir.sh"

  grep -q "^ExecStart=$HERE/sweep-workdir.sh --delete\$" "$UNIT_DIR/$SWEEP_UNIT" \
    || die "The sweep unit's ExecStart was not rewritten to this checkout's path.
Installed unit says: $(grep '^ExecStart=' "$UNIT_DIR/$SWEEP_UNIT")
A sweep timer pointing at a script that is not there fires every minute and does nothing, which
is the failure this whole directory exists to prevent."

  systemctl daemon-reload
  ok "Units installed: $SERVER_UNIT, $SWEEP_UNIT, $SWEEP_TIMER."
}

# ---------------------------------------------------------------------------------------
# After it is running: is it actually only on loopback?
# ---------------------------------------------------------------------------------------
verify_listener() {
  command -v ss >/dev/null 2>&1 || { warn "ss is not installed; cannot verify the listener from here."; return 0; }
  local line
  line="$(ss -ltnH "sport = :$DUBOTAPI_HTTP_PORT" 2>/dev/null || true)"
  if [ -z "$line" ]; then
    warn "Nothing is listening on port $DUBOTAPI_HTTP_PORT yet."
    return 0
  fi
  echo "$line" | sed 's/^/    /'
  if echo "$line" | awk '{print $4}' | grep -qE '^(0\.0\.0\.0|\*|\[::\]):'; then
    die "The server is listening on a WILDCARD address, not loopback. Stop it now:
  systemctl stop $SERVER_UNIT
Then fix DUBOTAPI_HTTP_IP in $ENV_FILE. Until it is stopped, every bot on this server is
compromised by anyone who can reach port $DUBOTAPI_HTTP_PORT."
  fi
  ok "Listening on loopback only."

  # From another host, this must time out or be refused. That check cannot be run from here and
  # is the one that actually proves it — see README.md § "Verifying it is serving".
}

# ---------------------------------------------------------------------------------------
main() {
  if [ "$VERIFY_ONLY" -eq 1 ]; then
    [ -x "$BINARY" ] && ok "$BINARY — $("$BINARY" --version 2>&1 | head -1)" || warn "$BINARY is not installed."
    [ -f "$ENV_FILE" ] || die "$ENV_FILE does not exist."
    check_env_file
    verify_listener
    systemctl is-active --quiet "$SERVER_UNIT" && ok "$SERVER_UNIT is active." || warn "$SERVER_UNIT is not active."
    systemctl is-active --quiet "$SWEEP_TIMER" && ok "$SWEEP_TIMER is active." || warn "$SWEEP_TIMER is not active."
    exit 0
  fi

  # Configuration is validated BEFORE the build, not after. The build is an hour of CPU; the
  # environment file is the thing most likely to be wrong on a first run. Discovering an empty
  # DUBOTAPI_HTTP_IP after the hour rather than before it is the same mistake build.sh's
  # preflight exists to avoid, made one level up.
  ensure_identity
  ensure_env_file
  check_env_file
  build_if_needed
  install_units

  if [ "$DO_START" -eq 1 ]; then
    log "Starting…"
    systemctl enable --now "$SERVER_UNIT"
    systemctl enable --now "$SWEEP_TIMER"
    sleep 3
    if ! systemctl is-active --quiet "$SERVER_UNIT"; then
      journalctl -u "$SERVER_UNIT" -n 30 --no-pager >&2 || true
      die "$SERVER_UNIT did not stay up. The journal is above."
    fi
    ok "$SERVER_UNIT is running."
    verify_listener
  else
    warn "Not started. When the environment file is filled in:
      systemctl enable --now $SERVER_UNIT
      systemctl enable --now $SWEEP_TIMER
      $0 --verify"
  fi

  cat <<EOF

  Installed.

    binary         $BINARY  ($("$BINARY" --version 2>&1 | head -1))
    pinned         Bot API $BOT_API_VERSION at ${PINNED_COMMIT:0:12}  ($PINNED_COMMIT_SUBJECT)
    service user   $SERVICE_USER:$SERVICE_GROUP
    working dir    $STATE_ROOT/work
    temp dir       $STATE_ROOT/tmp
    environment    $ENV_FILE  (root:root 0600 — never in the repository)
    units          $UNIT_DIR/$SERVER_UNIT
                   $UNIT_DIR/$SWEEP_UNIT
                   $UNIT_DIR/$SWEEP_TIMER

  ---------------------------------------------------------------------------------------
  TWO THINGS THIS SCRIPT DELIBERATELY DID NOT DO.

  1. It did not touch the firewall. Changing a box's packet filter from an installer is how a
     remote session ends. Deny 8081 inbound yourself — it is the second, independent lock on
     the listener and it fails separately from the bind address:

       ufw deny 8081/tcp
       # or:  firewall-cmd --permanent --remove-port=8081/tcp && firewall-cmd --reload

  2. It did not write anything into /etc/nginx, and nothing here ever will. The correct nginx
     configuration for this server is none at all: every leg is loopback and Telegram never
     connects inbound. nginx-if-it-ever-moves.conf.template says why, at length.

  ---------------------------------------------------------------------------------------
  ADD THESE TO src/DriveUnion.Web/appsettings.Production.json. Nothing here edits them.

    "Telegram": {
      "ApiBaseUrl": "http://$DUBOTAPI_HTTP_IP:$DUBOTAPI_HTTP_PORT/",
      "LocalBotServer": true,
      "MaxSendBytes": 2000000000,
      "MaxReceiveBytes": 2000000000,
      "WorkDirPath": "$STATE_ROOT/work",
      "WorkDirMaxAgeMinutes": $DUBOTAPI_SWEEP_MAX_AGE_MINUTES,
      "PinnedBotApiVersion": "$BOT_API_VERSION",
      "PinnedCommit": "$PINNED_COMMIT"
    }

  Three more keys have no defaults anybody can honestly supply from here —
  WorkDirHeadroomBytes, WorkDirMinFreeBytes and MaxConcurrentTransfers — because all three
  are read off this box's real free space. README.md § "Configuration keys" has the arithmetic.

EOF
}

main "$@"
