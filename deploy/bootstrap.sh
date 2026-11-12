#!/usr/bin/env bash
# ShaadiLens — one-time server setup for Ubuntu 24.04 (ARM64 or x86_64). Safe to run again.
#
#   curl -fsSL https://raw.githubusercontent.com/surajingle22/shaadilens/main/deploy/bootstrap.sh -o bootstrap.sh
#   sudo bash bootstrap.sh                      # first run
#   sudo bash bootstrap.sh --deploy-key "ssh-ed25519 AAAA... github-actions"   # later: add the CI key
#
# What it does (each part checks first, so re-running changes nothing that is already right):
#   1. base packages + unattended security upgrades
#   2. Docker Engine + compose plugin (official Docker apt repo), log rotation
#   3. firewall: opens 80/443 (Oracle images REJECT everything except SSH by default)
#   4. swap file
#   5. "deploy" user (runs containers; GitHub Actions logs in as this user — no sudo)
#   6. /opt/shaadilens: git checkout of the repo (config only), .env with generated secrets, data folders
#   7. DuckDNS IP refresh (cron, every 5 minutes)
# Secrets are written to /opt/shaadilens/.env only and are never printed.

set -euo pipefail

# ----------------------------------------------------------------------------- settings
DEPLOY_USER="${DEPLOY_USER:-deploy}"
APP_ROOT="${APP_ROOT:-/opt/shaadilens}"
REPO_URL="${REPO_URL:-https://github.com/surajingle22/shaadilens.git}"
REPO_BRANCH="${REPO_BRANCH:-main}"
SWAP_SIZE_GB="${SWAP_SIZE_GB:-4}"
TIMEZONE="${TIMEZONE:-Asia/Kolkata}"
DEPLOY_KEY=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --deploy-key) DEPLOY_KEY="${2:-}"; shift 2 ;;
    --branch)     REPO_BRANCH="${2:-}"; shift 2 ;;
    -h|--help)    sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

# ----------------------------------------------------------------------------- helpers
step() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
ok()   { printf '    \033[32m✓\033[0m %s\n' "$*"; }
warn() { printf '    \033[33m!\033[0m %s\n' "$*"; }
die()  { printf '\033[31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Run with sudo:  sudo bash $0"
# shellcheck disable=SC1091
. /etc/os-release
[[ "${ID:-}" == "ubuntu" ]] || die "This script supports Ubuntu only (found: ${PRETTY_NAME:-unknown})."
[[ "${VERSION_ID:-}" == "24.04" ]] || warn "Tested on Ubuntu 24.04; this is ${PRETTY_NAME}. Continuing."
export DEBIAN_FRONTEND=noninteractive
ARCH="$(dpkg --print-architecture)"   # arm64 | amd64

# ============================================================================ 1. base packages
step "1/7 Base packages and automatic security updates"
# iptables-persistent asks two questions on install; answer them in advance.
echo "iptables-persistent iptables-persistent/autosave_v4 boolean false" | debconf-set-selections
echo "iptables-persistent iptables-persistent/autosave_v6 boolean false" | debconf-set-selections
apt-get update -qq
apt-get install -y -qq ca-certificates curl gnupg git openssl cron \
  unattended-upgrades iptables-persistent >/dev/null
ok "packages installed ($ARCH)"

# Security updates install daily by themselves. No automatic reboot: a reboot in the middle of a wedding
# would take the gallery offline for a minute — reboot yourself when /var/run/reboot-required exists.
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
EOF
systemctl enable --now unattended-upgrades >/dev/null 2>&1 || true
ok "unattended security upgrades on (no auto-reboot)"

if [[ "$(timedatectl show -p Timezone --value 2>/dev/null)" != "$TIMEZONE" ]]; then
  timedatectl set-timezone "$TIMEZONE" && ok "timezone set to $TIMEZONE (cron times are local)"
else
  ok "timezone already $TIMEZONE"
fi

# ============================================================================ 2. Docker
step "2/7 Docker Engine + compose plugin"
if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1; then
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$ARCH signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update -qq
  apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin >/dev/null
  ok "Docker installed"
else
  ok "Docker already installed"
fi

# Log rotation for every container: at most 3 × 10 MB of logs each, so logs can never fill the disk.
DAEMON_JSON='{
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "3" }
}'
if [[ ! -f /etc/docker/daemon.json ]] || [[ "$(cat /etc/docker/daemon.json)" != "$DAEMON_JSON" ]]; then
  [[ -f /etc/docker/daemon.json ]] && cp /etc/docker/daemon.json "/etc/docker/daemon.json.bak.$(date +%s)"
  printf '%s\n' "$DAEMON_JSON" > /etc/docker/daemon.json
  systemctl restart docker
  ok "log rotation configured (10 MB × 3 per container)"
else
  ok "log rotation already configured"
fi
systemctl enable --now docker >/dev/null 2>&1
ok "$(docker --version | cut -d, -f1), $(docker compose version --short | sed 's/^/compose /')"

# ============================================================================ 3. firewall
step "3/7 Firewall: open ports 80 and 443"
# Oracle's Ubuntu images ship iptables rules (saved in /etc/iptables/rules.v4) that REJECT every incoming
# connection except SSH. We insert ACCEPT rules for 80/443 just before that REJECT — both live and in the
# saved file, so they survive reboots. We edit the file instead of "netfilter-persistent save" because
# saving would also freeze Docker's own rules into it.
RULES=/etc/iptables/rules.v4
open_port_live() {
  local port="$1" pos
  if iptables -C INPUT -p tcp -m state --state NEW -m tcp --dport "$port" -j ACCEPT 2>/dev/null; then
    return 0
  fi
  pos="$(iptables -L INPUT --line-numbers -n | awk '$2=="REJECT"{print $1; exit}')"
  if [[ -n "$pos" ]]; then
    iptables -I INPUT "$pos" -p tcp -m state --state NEW -m tcp --dport "$port" -j ACCEPT
  else
    iptables -A INPUT -p tcp -m state --state NEW -m tcp --dport "$port" -j ACCEPT
  fi
}
if [[ -f "$RULES" ]] && grep -q -- '-A INPUT -j REJECT' "$RULES"; then
  for port in 80 443; do
    open_port_live "$port"
    if ! grep -q -- "--dport $port -j ACCEPT" "$RULES"; then
      sed -i "0,/^-A INPUT -j REJECT/s//-A INPUT -p tcp -m state --state NEW -m tcp --dport $port -j ACCEPT\n&/" "$RULES"
    fi
  done
  ok "Oracle iptables rules: 80/443 accepted (live + $RULES)"
elif command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
  ufw allow OpenSSH >/dev/null; ufw allow 80/tcp >/dev/null; ufw allow 443/tcp >/dev/null
  ok "ufw: OpenSSH, 80, 443 allowed"
else
  ok "no host firewall blocking 80/443 (cloud firewall still applies)"
fi
warn "Also open TCP 80 and 443 in your cloud provider's firewall (Oracle: VCN security list)."

# ============================================================================ 4. swap
step "4/7 Swap file (${SWAP_SIZE_GB} GB)"
# Swap = disk space used as overflow RAM, so a memory spike (big upload burst + reel render) slows down
# instead of the kernel killing a container.
if swapon --show=NAME --noheadings | grep -q .; then
  ok "swap already active: $(swapon --show=NAME,SIZE --noheadings | tr -s ' ' | paste -sd ',')"
else
  if [[ ! -f /swapfile ]]; then
    fallocate -l "${SWAP_SIZE_GB}G" /swapfile
    chmod 600 /swapfile
    mkswap /swapfile >/dev/null
  fi
  swapon /swapfile
  grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
  ok "swap on: /swapfile ${SWAP_SIZE_GB} GB"
fi
echo 'vm.swappiness=10' > /etc/sysctl.d/90-shaadilens-swap.conf
sysctl -q -p /etc/sysctl.d/90-shaadilens-swap.conf
ok "vm.swappiness=10 (use swap only under pressure)"

# ============================================================================ 5. deploy user
step "5/7 User '$DEPLOY_USER'"
# Membership in the docker group lets this user run containers. That is effectively root on this machine,
# so only the GitHub Actions deploy key logs in as it, and it gets no password and no sudo.
if ! id "$DEPLOY_USER" >/dev/null 2>&1; then
  useradd --create-home --shell /bin/bash "$DEPLOY_USER"
  passwd -l "$DEPLOY_USER" >/dev/null
  ok "user created (password login disabled)"
else
  ok "user exists"
fi
usermod -aG docker "$DEPLOY_USER"
DEPLOY_HOME="$(getent passwd "$DEPLOY_USER" | cut -d: -f6)"
install -d -m 700 -o "$DEPLOY_USER" -g "$DEPLOY_USER" "$DEPLOY_HOME/.ssh"
AUTH_KEYS="$DEPLOY_HOME/.ssh/authorized_keys"
touch "$AUTH_KEYS"; chown "$DEPLOY_USER:$DEPLOY_USER" "$AUTH_KEYS"; chmod 600 "$AUTH_KEYS"
if [[ -n "$DEPLOY_KEY" ]]; then
  [[ "$DEPLOY_KEY" =~ ^ssh-(ed25519|rsa)\ [A-Za-z0-9+/=]+ ]] || die "--deploy-key must be a PUBLIC key line (ssh-ed25519 AAAA...)"
  if grep -qxF "$DEPLOY_KEY" "$AUTH_KEYS"; then
    ok "deploy key already present"
  else
    echo "$DEPLOY_KEY" >> "$AUTH_KEYS"
    ok "deploy key added"
  fi
fi
ok "$(grep -c . "$AUTH_KEYS" || true) key(s) can log in as $DEPLOY_USER"

# ============================================================================ 6. app folder
step "6/7 $APP_ROOT"
# Layout:
#   app/        git checkout — only compose files, Caddyfile and scripts are used (images come from GHCR)
#   .env        production settings and secrets (chmod 600)
#   music/      royalty-free tracks for reels (copy yours here)
#   backups/    database dumps + photo snapshots
#   state/      which version is deployed (written by deploy.sh)
#   logs/       cron job logs
install -d -m 750 -o "$DEPLOY_USER" -g "$DEPLOY_USER" "$APP_ROOT"
for d in music backups backups/db backups/photos state logs; do
  install -d -m 750 -o "$DEPLOY_USER" -g "$DEPLOY_USER" "$APP_ROOT/$d"
done
chmod 755 "$APP_ROOT/music"   # containers read it as a non-root user

if [[ -d "$APP_ROOT/app/.git" ]]; then
  ok "repo already checked out (deploy.sh updates it)"
else
  sudo -u "$DEPLOY_USER" git clone --quiet --branch "$REPO_BRANCH" "$REPO_URL" "$APP_ROOT/app"
  ok "repo cloned ($REPO_BRANCH)"
fi

ENV_FILE="$APP_ROOT/.env"
if [[ -f "$ENV_FILE" ]]; then
  ok ".env exists — left unchanged"
else
  TEMPLATE="$APP_ROOT/app/deploy/.env.production.example"
  [[ -f "$TEMPLATE" ]] || die "template missing: $TEMPLATE (is --branch right?)"
  umask 077
  cp "$TEMPLATE" "$ENV_FILE"
  for key in POSTGRES_PASSWORD JWT_SECRET MEDIA_SIGNING_KEY; do
    # 64 hex characters; hex never needs escaping in sed or in a connection string
    sed -i "s|^${key}=GENERATED\$|${key}=$(openssl rand -hex 32)|" "$ENV_FILE"
  done
  umask 022
  ok ".env created with generated secrets"
fi
chown "$DEPLOY_USER:$DEPLOY_USER" "$ENV_FILE"
chmod 600 "$ENV_FILE"

# ============================================================================ 7. DuckDNS
step "7/7 DuckDNS IP refresh"
CRON_FILE=/etc/cron.d/shaadilens-duckdns
cat > "$CRON_FILE" <<EOF
# Keeps the DuckDNS name pointing at this server (no-op until DUCKDNS_* are set in $ENV_FILE).
*/5 * * * * $DEPLOY_USER $APP_ROOT/app/deploy/duckdns-update.sh >> $APP_ROOT/logs/duckdns.log 2>&1
EOF
chmod 644 "$CRON_FILE"
ok "cron every 5 min → $APP_ROOT/logs/duckdns.log"

# ============================================================================ summary
step "Summary"
TODO_KEYS="$(grep -E '^[A-Z_]+=.*CHANGE_ME' "$ENV_FILE" | cut -d= -f1 | paste -sd ' ' || true)"
echo "    Architecture : $ARCH ($(nproc) CPUs, $(free -g | awk '/Mem:/{print $2}') GB RAM)"
echo "    Disk (/)     : $(df -h / | awk 'NR==2{print $4 " free of " $2}')"
echo "    Swap         : $(swapon --show=SIZE --noheadings | paste -sd '+')"
echo "    Ports 80/443 : $(iptables -S INPUT | grep -cE -- '--dport (80|443) -j ACCEPT') ACCEPT rule(s) in iptables"
if [[ -n "$TODO_KEYS" ]]; then
  echo
  warn "Edit these lines in $ENV_FILE before the first deploy:"
  echo "        $TODO_KEYS"
  echo "      sudo -u $DEPLOY_USER nano $ENV_FILE"
fi
[[ -f /var/run/reboot-required ]] && warn "A reboot is pending from updates:  sudo reboot"
echo
ok "Bootstrap finished."
