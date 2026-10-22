#!/usr/bin/env bash
# Grava as credenciais do Mercado Pago no .env.vps sem exibi-las e confere a conta pelo /users/me.
# Uso (na VPS): scripts/mp-credenciais.sh <vendedor_id> <producao: true|false>
set -euo pipefail

cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
env_file=.env.vps
vendedor=${1:-}
producao=${2:-}

if [[ ! "$vendedor" =~ ^[0-9]{1,30}$ || ! "$producao" =~ ^(true|false)$ ]]; then
    echo "Uso: $0 <vendedor_id> <true|false>" >&2
    exit 1
fi
if [[ ! -f "$env_file" ]]; then
    echo "Arquivo $env_file nao encontrado." >&2
    exit 1
fi
for cmd in curl jq; do
    command -v "$cmd" >/dev/null || { echo "Instale $cmd na VPS." >&2; exit 1; }
done

read -r -s -p "Access Token (nao aparece na tela; Enter mantem o atual): " token; echo
read -r -s -p "Chave secreta do webhook (Enter mantem a atual): " segredo; echo
[[ -n "$token" ]] || token=$(grep '^MP_ACCESS_TOKEN=' "$env_file" | cut -d= -f2- || true)

# Confere a chave contra a ultima notificacao registrada pelo log temporario do Nginx, se houver.
captura=/var/log/nginx/mp-webhook-debug.log
if [[ -n "$segredo" && -s "$captura" ]]; then
    linha=$(tail -n 1 "$captura")
    ts=$(sed -n 's/.*sig="ts=\([0-9]*\).*/\1/p' <<<"$linha")
    v1=$(sed -n 's/.*v1=\([0-9a-f]*\).*/\1/p' <<<"$linha")
    req=$(sed -n 's/.*req="\([^"]*\)".*/\1/p' <<<"$linha")
    id=$(sed -n 's/.*data\.id=\([A-Za-z0-9]*\).*/\1/p' <<<"$linha")
    confere=false
    # Orders assina com o id original; a documentacao cita o id em minusculas. Aceita os dois.
    for candidato in "$id" "${id,,}"; do
        calc=$(printf '%s' "id:${candidato};request-id:${req};ts:${ts};" | openssl dgst -sha256 -hmac "$segredo" -r | cut -d' ' -f1)
        [[ "$calc" == "$v1" ]] && confere=true
    done
    $confere && echo "Chave confere com a ultima notificacao recebida." \
        || echo "Aviso: chave NAO confere com a ultima notificacao recebida." >&2
fi
if [[ -z "$token" || "$token" =~ [[:space:]] ]]; then
    echo "Access Token vazio ou com espacos." >&2
    exit 1
fi

# Formato ajuda a distinguir Access Token de Public Key sem exibir o valor.
if [[ "$token" =~ ^(APP_USR|TEST)-[0-9]+-[0-9]{6}-[0-9a-f]{32}-[0-9]+$ ]]; then
    echo "Formato: Access Token (${BASH_REMATCH[1]}-...)."
elif [[ "$token" =~ ^(APP_USR|TEST)-[0-9a-f]{8}-[0-9a-f]{4}- ]]; then
    echo "O valor colado parece a Public Key, nao o Access Token. Nada foi gravado." >&2
    exit 1
else
    echo "Aviso: formato nao reconhecido (${#token} caracteres)." >&2
fi

# Confere dono e ambiente do token antes de gravar qualquer coisa.
resposta=""
for host in api.mercadolibre.com api.mercadopago.com; do
    corpo=$(curl --silent --max-time 15 --write-out '\n%{http_code}' \
        --header "Authorization: Bearer $token" "https://$host/users/me") || corpo=$'\n000'
    status=${corpo##*$'\n'}
    corpo=${corpo%$'\n'*}
    echo "$host/users/me -> HTTP $status $(jq -r '.message // empty' <<<"$corpo" 2>/dev/null)"
    if [[ "$status" == 200 && -z "$resposta" ]]; then resposta=$corpo; fi
done
validade=$(curl --silent --output /dev/null --max-time 15 --write-out '%{http_code}' \
    --header "Authorization: Bearer $token" https://api.mercadopago.com/v1/payment_methods) || validade=000
echo "api.mercadopago.com/v1/payment_methods -> HTTP $validade"
[[ -n "$resposta" ]] || resposta='{}'
id=$(jq -r '.id // empty' <<<"$resposta")
teste=$(jq -r 'if (.tags // []) | index("test_user") then "true" else "false" end' <<<"$resposta")
if [[ -z "$id" ]]; then
    echo "Token recusado pelo Mercado Pago em todos os enderecos. Nada foi gravado." >&2
    exit 1
fi
echo "Token pertence a conta $id (test_user=$teste)."
if [[ "$id" != "$vendedor" ]]; then
    echo "Conta do token ($id) difere do vendedor informado ($vendedor). Nada foi gravado." >&2
    exit 1
fi
if [[ "$producao" == "$teste" ]]; then
    echo "Ambiente divergente: MP_PRODUCAO=$producao com test_user=$teste. Nada foi gravado." >&2
    exit 1
fi

definir() {
    local chave=$1 valor=$2 tmp
    tmp=$(mktemp "$env_file.XXXXXX")
    chmod 600 "$tmp"
    grep -v "^${chave}=" "$env_file" >"$tmp" || true
    printf '%s=%s\n' "$chave" "$valor" >>"$tmp"
    mv "$tmp" "$env_file"
}
padrao() { grep -q "^$1=" "$env_file" || definir "$1" "$2"; }

cp -p "$env_file" "$env_file.bak-mp-$(date +%Y%m%d%H%M%S)"
definir MP_ACCESS_TOKEN "$token"
[[ -n "$segredo" ]] && definir MP_WEBHOOK_SECRET "$segredo"
definir MP_VENDEDOR_ID "$vendedor"
definir MP_PRODUCAO "$producao"
padrao MP_URL_PUBLICA https://valordafipe.com.br
padrao MP_CHAVE_PIX_CADASTRADA false
padrao MP_HABILITADO false
unset token segredo

grep -q '^MP_WEBHOOK_SECRET=.' "$env_file" || echo "Aviso: MP_WEBHOOK_SECRET ainda vazio." >&2
echo "Gravado em $env_file. Chaves MP atuais:"
grep '^MP_' "$env_file" | sed -E 's/^(MP_ACCESS_TOKEN|MP_WEBHOOK_SECRET)=.+/\1=(definido)/'

docker compose --project-name fipedados --env-file "$env_file" -f docker-compose.vps.yml up -d --wait web
echo "Container web recriado com a nova configuracao."
