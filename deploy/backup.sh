#!/usr/bin/env bash
# Backup diário do banco do Beacon (links e cliques), rodado pelo cron da VM (veja "Publicação" no README).
# Guarda os últimos 7 dias em ~/beacon/backups, comprimidos.
set -euo pipefail
# O backup tem as senhas (hash) e os links: só o dono da pasta lê
umask 077

cd "$(dirname "$0")"
mkdir -p backups
arquivo="backups/beacon-$(date +%F).sql.gz"
# Escreve num temporário e só renomeia no fim: um backup pela metade nunca passa por bom
docker compose exec -T banco pg_dump -U beacon beacon | gzip > "$arquivo.tmp"
mv "$arquivo.tmp" "$arquivo"
ls -1t backups/beacon-*.sql.gz | tail -n +8 | xargs -r rm --
