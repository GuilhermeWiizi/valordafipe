#!/usr/bin/env bash
# Baixa as faixas de IP do Googlebot e do Bingbot e grava o arquivo que o Nginx usa para
# isentá-los do limite por IP (geo $valordafipe_isento em valordafipe.conf).
# Rode como root na instalação e por cron, por exemplo em /etc/cron.d/valordafipe-bots:
#   17 4 * * * root /srv/FipeDados/deploy/nginx/atualizar-bots-verificados.sh >/dev/null
# NGINX_BOTS_ARQUIVO muda o destino e NGINX_RECARREGAR=0 pula o teste/reload (uso local).
set -euo pipefail

DESTINO="${NGINX_BOTS_ARQUIVO:-/etc/nginx/valordafipe-bots-verificados.conf}"
FONTES=(
  https://developers.google.com/static/crawling/ipranges/common-crawlers.json
  https://www.bing.com/toolbox/bingbot.json
)

novo="$(mktemp)"
trap 'rm -f "$novo"' EXIT

echo "# Gerado por atualizar-bots-verificados.sh em $(date -u +%Y-%m-%dT%H:%M:%SZ). Não edite." > "$novo"
for fonte in "${FONTES[@]}"; do
  echo "# $fonte" >> "$novo"
  # Só aceita CIDR de verdade: o conteúdo baixado vira configuração do Nginx.
  curl -fsSL --max-time 30 "$fonte" \
    | grep -oE '"ipv[46]Prefix"[[:space:]]*:[[:space:]]*"[0-9a-fA-F:.]+/[0-9]{1,3}"' \
    | sed -E 's/.*"([0-9a-fA-F:.]+\/[0-9]{1,3})"$/\1 1;/' >> "$novo"
done

faixas="$(grep -c ' 1;$' "$novo" || true)"
if [ "$faixas" -lt 10 ]; then
  echo "Só $faixas faixas baixadas; mantido o arquivo anterior." >&2
  exit 1
fi

anterior=""
if [ -f "$DESTINO" ]; then
  anterior="$(mktemp)"
  cp "$DESTINO" "$anterior"
fi
install -m 644 "$novo" "$DESTINO"

if [ "${NGINX_RECARREGAR:-1}" = "1" ]; then
  if ! nginx -t -q; then
    if [ -n "$anterior" ]; then cp "$anterior" "$DESTINO"; else rm -f "$DESTINO"; fi
    echo "nginx -t falhou; restaurado o arquivo anterior." >&2
    exit 1
  fi
  systemctl reload nginx
fi
[ -z "$anterior" ] || rm -f "$anterior"
echo "$faixas faixas de robôs verificados em $DESTINO"
