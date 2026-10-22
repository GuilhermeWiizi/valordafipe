#!/usr/bin/env bash
# Dump diario do PostgreSQL do FIPE (contas, pedidos e pagamentos) em formato custom do pg_dump.
# Rode como root na instalacao e por cron, por exemplo em /etc/cron.d/valordafipe-backup:
#   30 3 * * * root /srv/FipeDados/deploy/backup/backup-fipedados.sh >/dev/null
# Restaurar: docker exec -i fipedados-fipedados-db-1 pg_restore -U fipe -d fipedados --clean --if-exists < ARQUIVO
# FIPE_BACKUP_DIR, FIPE_BACKUP_DIAS e FIPE_DB_CONTAINER mudam destino, retencao e container.
set -euo pipefail
# O cron roda com PATH=/usr/bin:/bin.
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

DESTINO="${FIPE_BACKUP_DIR:-/root/backups/fipedados/db}"
DIAS="${FIPE_BACKUP_DIAS:-30}"
CONTAINER="${FIPE_DB_CONTAINER:-fipedados-fipedados-db-1}"
LOG=/var/log/backup-fipedados.log

log() { echo "[$(date +'%Y-%m-%d %H:%M:%S')] $*" | tee -a "$LOG"; }
trap 'log "ERRO: backup falhou na linha $LINENO"' ERR

umask 077
mkdir -p "$DESTINO"
usuario=$(docker exec "$CONTAINER" printenv POSTGRES_USER)
banco=$(docker exec "$CONTAINER" printenv POSTGRES_DB)
arquivo="$DESTINO/fipedados_$(date +%Y%m%d_%H%M%S).dump"
parcial="$arquivo.parcial"

docker exec "$CONTAINER" pg_dump -U "$usuario" -d "$banco" -Fc >"$parcial"
# O indice do arquivo so e legivel se o dump terminou inteiro.
docker exec -i "$CONTAINER" pg_restore --list >/dev/null <"$parcial"
mv "$parcial" "$arquivo"
log "Dump gerado: $(basename "$arquivo") ($(du -h "$arquivo" | cut -f1))"

find "$DESTINO" -name 'fipedados_*.dump' -mtime +"$DIAS" -delete
find "$DESTINO" -name '*.parcial' -mmin +60 -delete
log "Backup finalizado; retencao de $DIAS dias."
