#!/bin/sh
# NetworkFolderManager Server - installation on Linux (systemd)
#   sudo sh install-linux.sh
set -e

if [ "$(id -u)" -ne 0 ]; then
    echo "Please run with sudo: sudo sh install-linux.sh"
    exit 1
fi

DIR="$(cd "$(dirname "$0")" && pwd)"
DATA=/var/lib/nfm-server

echo "== Installing NetworkFolderManager Server =="

# Dedicated system account without login
if ! id nfm >/dev/null 2>&1; then
    useradd --system --home-dir "$DATA" --shell /usr/sbin/nologin nfm 2>/dev/null \
        || adduser --system --home "$DATA" --shell /usr/sbin/nologin --group nfm
fi

install -d -o nfm -g nfm -m 700 "$DATA"
install -d -m 755 /opt/nfm-server
systemctl stop nfm-server 2>/dev/null || true
install -m 755 "$DIR/nfm-server" /opt/nfm-server/nfm-server

# "nfm-server" command for administration (always runs as the nfm account)
cat > /usr/local/bin/nfm-server <<'EOF'
#!/bin/sh
if [ "$(id -un)" = "nfm" ]; then
    exec env NFM_SERVER_DATA=/var/lib/nfm-server /opt/nfm-server/nfm-server "$@"
fi
exec sudo -u nfm -H env NFM_SERVER_DATA=/var/lib/nfm-server DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/nfm-server/.net /opt/nfm-server/nfm-server "$@"
EOF
chmod 755 /usr/local/bin/nfm-server

install -m 644 "$DIR/nfm-server.service" /etc/systemd/system/nfm-server.service
systemctl daemon-reload
systemctl enable --now nfm-server

# Firewall (only if active)
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw allow 45830/tcp comment "NFM Server HTTPS"
    ufw allow 45823/udp comment "NFM Server discovery"
fi
if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --permanent --add-port=45830/tcp --add-port=45823/udp
    firewall-cmd --reload
fi

sleep 2
echo
/usr/local/bin/nfm-server info
echo
echo "Done. The server is running as a service (systemctl status nfm-server)."
echo "Create an admin account:   sudo nfm-server admin add <name>"
echo "Or open https://<ip-address>:45830 in a browser and register with the setup code above."
