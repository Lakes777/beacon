#!/usr/bin/env bash
# Publica uma versão nova na VM: monta as imagens aqui (para ARM, a arquitetura da VM), manda pela
# conexão SSH e reinicia. A VM não precisa do código nem do .NET, só do Docker. Uso: deploy/publicar.sh
set -euo pipefail

VM="${VM:-ubuntu@IP_DA_VM}"
CHAVE_SSH="${CHAVE_SSH:-$HOME/.ssh/oracle_bot}"
PASTA="beacon"
cd "$(dirname "$0")/.."

# O .env (segredos) é criado uma vez na VM e nunca sai de lá; veja "Publicação" no README
if ! ssh -i "$CHAVE_SSH" "$VM" "test -f $PASTA/.env"; then
	echo "Falta o arquivo $PASTA/.env na VM (segredos). Veja a seção Publicação do README." >&2
	exit 1
fi

# Só publica o que está no Git: senão iria para o ar um código que não existe em nenhum commit
if [ -n "$(git status --porcelain)" ]; then
	echo "Há mudanças sem commit. Faça o commit antes de publicar (git status)." >&2
	exit 1
fi
VERSAO="$(git rev-parse --short HEAD)"

echo "Montando as imagens (ARM) da versão $VERSAO..."
for alvo in api estatisticas; do
	# "latest" é a que o compose usa; o número do commit permite voltar a uma versão anterior
	docker buildx build -q --platform linux/arm64 --target "$alvo" \
		-t "beacon-$alvo:latest" -t "beacon-$alvo:$VERSAO" --load . >/dev/null
done

echo "Enviando para a VM (comprimidas)..."
docker save "beacon-api:$VERSAO" beacon-api:latest "beacon-estatisticas:$VERSAO" beacon-estatisticas:latest \
	| gzip | ssh -i "$CHAVE_SSH" "$VM" "gunzip | docker load -q"
scp -q -i "$CHAVE_SSH" deploy/compose.yaml deploy/Caddyfile deploy/backup.sh "$VM:$PASTA/"

# O "up" espera a API ficar saudável (o Caddy e o serviço de estatísticas dependem disso)
echo "Reiniciando..."
if ! ssh -i "$CHAVE_SSH" "$VM" "cd $PASTA && docker compose up -d --remove-orphans"; then
	echo "A API não ficou saudável. Veja: ssh -i $CHAVE_SSH $VM 'cd $PASTA && docker compose logs api --tail 50'" >&2
	echo "Para voltar à versão anterior: veja \"Voltar uma versão\" no README." >&2
	exit 1
fi

# Guarda as 3 versões mais novas de cada imagem (para poder voltar) e apaga as outras
ssh -i "$CHAVE_SSH" "$VM" 'for img in beacon-api beacon-estatisticas; do
	docker images "$img" --format "{{.Tag}}" | grep -v "^latest$" | tail -n +4 | xargs -r -I{} docker rmi -f "$img:{}" >/dev/null
done; docker image prune -f >/dev/null'
echo "No ar: versão $VERSAO."

