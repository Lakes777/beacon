#!/usr/bin/env bash
# Traz os backups diários da VM para o PC (uma cópia fora da VM). Roda pelo Agendador de
# Tarefas do Windows (tarefa "Beacon - trazer backup"), via WSL. Guarda os últimos 30 dias.
set -euo pipefail

VM="${VM:-ubuntu@IP_DA_VM}"
CHAVE_SSH="${CHAVE_SSH:-$HOME/.ssh/oracle_bot}"
DESTINO="${DESTINO:-/mnt/d/Backups/Beacon}"
mkdir -p "$DESTINO"
exec >> "$DESTINO/trazer.log" 2>&1
echo "$(date '+%F %T') começando"

SSH=(ssh -i "$CHAVE_SSH" -o BatchMode=yes -o ConnectTimeout=20)
# Lista o que há na VM e traz só o que ainda não está aqui
for arquivo in $("${SSH[@]}" "$VM" 'ls beacon/backups/ | grep -E "^beacon-[0-9-]+\.sql\.gz$"'); do
	if [ ! -s "$DESTINO/$arquivo" ]; then
		"${SSH[@]}" "$VM" "cat beacon/backups/$arquivo" > "$DESTINO/$arquivo.tmp"
		# Confere se o arquivo chegou inteiro antes de dar como bom
		gzip -t "$DESTINO/$arquivo.tmp"
		mv "$DESTINO/$arquivo.tmp" "$DESTINO/$arquivo"
		echo "  trouxe $arquivo ($(du -h "$DESTINO/$arquivo" | cut -f1))"
	fi
done
ls -1t "$DESTINO"/beacon-*.sql.gz | tail -n +31 | xargs -r rm --
echo "$(date '+%F %T') ok: $(ls "$DESTINO"/beacon-*.sql.gz | wc -l) backup(s) no PC"
