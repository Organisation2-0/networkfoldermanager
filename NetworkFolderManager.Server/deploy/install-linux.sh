#!/bin/sh
# NetworkFolderManager Server – Installation unter Linux (systemd)
#   sudo sh install-linux.sh
set -e

if [ "$(id -u)" -ne 0 ]; then
    echo "Bitte mit sudo ausfuehren: sudo sh install-linux.sh"
    exit 1
fi

DIR="$(cd "$(dirname "$0")" && pwd)"
DATA=/var/lib/nfm-server

echo "== NetworkFolderManager Server wird installiert =="

# Eigenes Systemkonto ohne Login
if ! id nfm >/dev/null 2>&1; then
    useradd --system --home-dir "$DATA" --shell /usr/sbin/nologin nfm 2>/dev/null \
        || adduser --system --home "$DATA" --shell /usr/sbin/nologin --group nfm
fi

install -d -o nfm -g nfm -m 700 "$DATA"
install -d -m 755 /opt/nfm-server
systemctl stop nfm-server 2>/dev/null || true
install -m 755 "$DIR/nfm-server" /opt/nfm-server/nfm-server

# Befehl "nfm-server" fuer die Verwaltung (laeuft automatisch als Konto nfm)
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

# Firewall (nur wenn aktiv)
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw allow 45830/tcp comment "NFM-Server HTTPS"
    ufw allow 45823/udp comment "NFM-Server Erkennung"
fi
if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --permanent --add-port=45830/tcp --add-port=45823/udp
    firewall-cmd --reload
fi

sleep 2
echo
/usr/local/bin/nfm-server info
echo
echo "Fertig. Der Server laeuft als Dienst (systemctl status nfm-server)."
echo "Admin-Konto anlegen:   sudo nfm-server admin add <Name>"
echo "Oder im Browser https://<IP-Adresse>:45830 oeffnen und mit dem Setup-Code oben registrieren."
