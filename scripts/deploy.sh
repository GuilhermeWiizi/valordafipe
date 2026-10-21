#!/usr/bin/env bash
set -euo pipefail

cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."

if [[ ! -f .env.vps ]]; then
    echo "Crie /srv/FipeDados/.env.vps a partir de .env.vps.example e configure a senha do banco." >&2
    exit 1
fi

compose=(docker compose --project-name fipedados --env-file .env.vps -f docker-compose.vps.yml)
if ! command -v curl >/dev/null; then
    echo "Instale curl na VPS antes de executar o deploy." >&2
    exit 1
fi
"${compose[@]}" config --quiet

# Compile antes de substituir o site que estiver em execucao.
echo "Compilando a imagem do FIPE..."
"${compose[@]}" build web

echo "Atualizando os servicos do FIPE..."
if ! "${compose[@]}" up -d --wait --wait-timeout 180; then
    "${compose[@]}" logs --tail 80 web fipedados-db >&2 || true
    exit 1
fi

# --wait verifica o processo; esta consulta confirma que o HTTP iniciou apos as migrations.
web_endpoint=$("${compose[@]}" port web 8080)
ready=false
for attempt in {1..30}; do
    if curl --fail --silent --output /dev/null --max-time 5 "http://${web_endpoint}/robots.txt"; then
        ready=true
        break
    fi
    sleep 2
done

if [[ "$ready" != true ]]; then
    echo "O FIPE nao respondeu ao teste HTTP. Confira a porta WEB_PORT e os logs abaixo." >&2
    "${compose[@]}" logs --tail 80 web >&2 || true
    exit 1
fi

"${compose[@]}" ps
echo "FIPE atualizado e respondendo em http://${web_endpoint}."
