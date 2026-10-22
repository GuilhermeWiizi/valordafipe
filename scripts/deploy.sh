#!/usr/bin/env bash
set -euo pipefail

cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."

if [[ ! -f .env.vps ]]; then
    echo "Crie /srv/FipeDados/.env.vps a partir de .env.vps.example e configure a senha do banco." >&2
    exit 1
fi

revision=$(git rev-parse HEAD)
expected_revision=${1:-$revision}
if [[ "$revision" != "$expected_revision" ]]; then
    echo "A revisao da VPS ($revision) difere da solicitada ($expected_revision). Deploy cancelado." >&2
    exit 1
fi
if ! git diff --quiet HEAD --; then
    echo "Ha alteracoes locais na VPS. Commit ou revise essas alteracoes antes do deploy." >&2
    exit 1
fi
export BUILD_REVISION="$revision"

compose=(docker compose --project-name fipedados --env-file .env.vps -f docker-compose.vps.yml)
if ! command -v curl >/dev/null; then
    echo "Instale curl na VPS antes de executar o deploy." >&2
    exit 1
fi
"${compose[@]}" config --quiet

# Compile antes de substituir o site que estiver em execucao.
echo "Compilando a imagem do FIPE na revisao $BUILD_REVISION..."
"${compose[@]}" build web

echo "Atualizando os servicos do FIPE..."
if ! "${compose[@]}" up -d --wait --wait-timeout 180; then
    "${compose[@]}" logs --tail 80 web fipedados-db >&2 || true
    exit 1
fi

# HTTP 200 sozinho nao comprova que a imagem nova foi publicada.
web_endpoint=$("${compose[@]}" port web 8080)
check_version() {
    local url=$1 attempts=$2 actual attempt
    for ((attempt = 1; attempt <= attempts; attempt++)); do
        actual=$(curl --fail --silent --max-time 10 \
            --header 'Cache-Control: no-cache' "${url}/health/version?revision=${BUILD_REVISION}") || actual=""
        if [[ "$actual" == "$BUILD_REVISION" ]]; then return 0; fi
        if ((attempt < attempts)); then sleep 2; fi
    done
    echo "A revisao $BUILD_REVISION nao foi confirmada em $url. Confira a imagem, o Nginx, o dominio e o cache." >&2
    return 1
}

if ! check_version "http://${web_endpoint}" 30; then
    "${compose[@]}" logs --tail 80 web >&2 || true
    exit 1
fi

# HTML e versao podem responder mesmo quando o JavaScript que ativa os combos esta ausente.
curl --fail --silent --show-error --max-time 10 \
    "http://${web_endpoint}/_framework/blazor.web.js" --output /dev/null

public_url=$("${compose[@]}" exec -T web printenv Seo__SiteUrl)
public_url=${public_url%$'\r'}
if [[ "$public_url" != https://* ]]; then
    echo "Configure SEO_SITE_URL com o dominio HTTPS publicado antes de concluir o deploy." >&2
    exit 1
fi
check_version "${public_url%/}" 3
curl --fail --silent --show-error --max-time 10 \
    "${public_url%/}/_framework/blazor.web.js" --output /dev/null

"${compose[@]}" ps
echo "FIPE atualizado: $BUILD_REVISION confirmado no container e em $public_url."
