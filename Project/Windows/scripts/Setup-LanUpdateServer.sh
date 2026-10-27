#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Run this script on the Mac mini." >&2
  exit 2
fi

ROOT=/usr/local/var/www/feige-updates
CHANNEL_ROOT="$ROOT/windows/stable"
CONFIG=/usr/local/etc/nginx/servers/macmini.conf
CONFIG_CHANGED=0
if ! grep -q 'location /updates/' "$CONFIG"; then
  BACKUP="/usr/local/etc/nginx/macmini.conf.feige-backup-$(date +%Y%m%d%H%M%S)"
  cp -p "$CONFIG" "$BACKUP"
else
  BACKUP=""
fi
# Backups live outside the included servers/* directory; do not remove the first backup on reruns.

mkdir -p "$CHANNEL_ROOT/versions"
chmod 755 /usr/local/var/www "$ROOT" "$ROOT/windows" "$CHANNEL_ROOT" "$CHANNEL_ROOT/versions"
chown -R "$(id -un)":staff "$ROOT"

if ! grep -q 'location /updates/' "$CONFIG"; then
  CONFIG_CHANGED=1
  python3 - "$CONFIG" <<'PY'
from pathlib import Path
import sys
p = Path(sys.argv[1])
text = p.read_text()
if text.count('    error_page  500 502 503 504 /50x.html;') != 1 or text.count('    error_page  404 /404.html;') != 1:
    raise SystemExit('Expected one server-level insertion point; refusing to guess config structure.')
text = text.replace('    error_page  404 /404.html;', '    error_page 404 = @feige_update_not_found;\n    location @feige_update_not_found { return 404; }', 1)
marker = '    error_page  500 502 503 504 /50x.html;'
block = '''

    # Feige Windows updater: anonymous LAN-only static content. Signed manifest and
    # package hashes are verified by clients; no uploads are exposed over HTTP.
    location /updates/ {
        alias /usr/local/var/www/feige-updates/;
        allow 127.0.0.1;
        # Private LAN ranges: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16.
        allow 10.0.0.0/8;
        allow 172.16.0.0/12;
        allow 192.168.0.0/16;
        deny all;
        autoindex off;
        # Directory listings/pages are intentionally not served; only concrete files resolve.
        add_header X-Content-Type-Options nosniff always;
        add_header Cache-Control "no-cache, no-store, must-revalidate" always;
        add_header Access-Control-Allow-Origin "*" always;
        limit_except GET HEAD { deny all; }
        try_files $uri =404;
    }
'''
p.write_text(text.replace(marker, marker + block, 1))
PY
fi

if ! nginx -t; then
  if [[ "$CONFIG_CHANGED" == "1" && -f "$BACKUP" ]]; then cp -p "$BACKUP" "$CONFIG"; fi
  echo "Nginx config validation failed; original config restored." >&2
  exit 1
fi
if ! nginx -s reload; then
  if [[ "$CONFIG_CHANGED" == "1" && -f "$BACKUP" ]]; then
    cp -p "$BACKUP" "$CONFIG"
    nginx -t && nginx -s reload || true
  fi
  echo "Nginx reload failed; original config restored." >&2
  exit 1
fi
if curl --fail --silent --show-error http://127.0.0.1:8080/updates/windows/stable/feige-windows-update.json >/dev/null 2>&1; then
  echo "An update pointer already exists and was left unchanged."
fi
printf 'Feige update server ready on http://192.168.50.229:8080/updates/windows/stable/\n'
printf 'Channel root: %s\n' "$CHANNEL_ROOT"
if [[ -n "$BACKUP" ]]; then printf 'Config backup: %s\n' "$BACKUP"; fi
