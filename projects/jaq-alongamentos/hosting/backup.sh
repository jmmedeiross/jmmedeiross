#!/bin/sh
# Short maintenance window: stop writes before copying SQLite and session keys.
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
umask 077
mkdir -p backups
stamp=$(date -u +%Y%m%dT%H%M%SZ)
archive="backups/jaq-$stamp.tar.gz"
test ! -e "$archive"
docker compose stop app
trap 'docker compose start app' EXIT
tar --exclude='./.instance.lock' -czf "$archive" -C data .
tar -tzf "$archive" >/dev/null
printf 'Backup criado: %s\n' "$archive"
