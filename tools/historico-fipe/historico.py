#!/usr/bin/env python3
"""Coleta local da FIPE e envio incremental à VPS. Python 3.11+, sem depender do .NET."""
from __future__ import annotations

import argparse
import base64
from collections import Counter
from contextlib import contextmanager
from datetime import date, datetime, timezone
from decimal import Decimal, InvalidOperation
import email.utils
import getpass
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import sqlite3
import sys
import time
import tomllib

try:
    import paramiko
    import psycopg
    from psycopg.rows import dict_row
    import requests
    from rich.console import Console
    from rich.panel import Panel
    from rich.progress import BarColumn, Progress, TextColumn, TimeElapsedColumn, TimeRemainingColumn
    from rich.table import Table
except ImportError as exc:
    raise SystemExit("Instale as dependências: python -m pip install -r requirements.txt") from exc

HERE = Path(__file__).resolve().parent
API = "https://veiculos.fipe.org.br/api/veiculos/"
MONTHS = {m: n for n, m in enumerate(
    "janeiro fevereiro março abril maio junho julho agosto setembro outubro novembro dezembro".split(), 1)}
MONTHS["marco"] = 3


class SourceError(Exception):
    """Resposta que não é uma cotação válida; o mês permanece pendente."""


class Blocked(SourceError):
    """Bloqueio persistente: interromper a coleta inteira evita insistir na fonte."""


class CombinationUnavailable(SourceError):
    """A FIPE recusou essa combinação, sem comprovar ausência de preço."""


class RequestLimit(Exception):
    pass


def parse_month(value):
    parts = re.sub(r"\s+", "", str(value).lower().replace(" de ", "/")).split("/")
    try:
        return date(int(parts[1]), MONTHS[parts[0]], 1) if len(parts) == 2 else None
    except (ValueError, KeyError):
        return None


def parse_quote(body, vehicle, reference):
    if not isinstance(body, dict):
        raise SourceError("JSON de cotação não é um objeto")
    if body.get("Erro") or body.get("erro"):
        # 'Parâmetros inválidos' não prova ausência de preço. Não persistir lacuna.
        raise CombinationUnavailable("FIPE recusou a combinação; mês continua pendente")
    try:
        raw = str(body["Valor"]).replace("R$", "").replace("\u00a0", "").strip()
        if not re.fullmatch(r"(?:\d{1,3}(?:\.\d{3})+|\d+),\d{2}", raw):
            raise ValueError()
        value = Decimal(raw.replace(".", "").replace(",", "."))
        code = str(body["CodigoFipe"]).strip()
        if not value.is_finite() or not 0 < value <= Decimal("999999999999.99"):
            raise ValueError()
        if not re.fullmatch(r"\d{6}-\d", code):
            raise ValueError()
        if vehicle["codigo_fipe"] and code != vehicle["codigo_fipe"]:
            raise ValueError()
        if parse_month(body["MesReferencia"]) != reference["data"]:
            raise ValueError()
        if int(body["AnoModelo"]) != int(vehicle["ano"].split("-")[0]):
            raise ValueError()
        if int(body["TipoVeiculo"]) != vehicle["tipo"]:
            raise ValueError()
        return value, code
    except (KeyError, ValueError, TypeError, InvalidOperation) as exc:
        raise SourceError("Cotação inválida ou diferente do veículo/mês solicitado; nada gravado") from exc


def money(value):
    return "R$ " + f"{value:,.2f}".replace(",", "_").replace(".", ",").replace("_", ".")


def duration(seconds):
    days, remainder = divmod(round(seconds), 86400)
    hours, remainder = divmod(remainder, 3600)
    minutes, seconds = divmod(remainder, 60)
    return f"{days}d {hours:02}h {minutes:02}m {seconds:02}s"


class Monitor:
    def __init__(self, directory, plain=False):
        for stream in (sys.stdout, sys.stderr):
            if hasattr(stream, "reconfigure"):
                stream.reconfigure(encoding="utf-8", errors="replace")
        directory.mkdir(parents=True, exist_ok=True)
        self.path = directory / (datetime.now().strftime("historico-%Y%m%d-%H%M%S-") + str(os.getpid()) + ".jsonl")
        self.file = self.path.open("a", encoding="utf-8", buffering=1)
        self.console = Console(highlight=False)
        self.counts = Counter()
        self.progress = Progress(TextColumn("{task.description}"), BarColumn(),
                                 TextColumn("{task.completed:,.0f}/{task.total:,.0f}"),
                                 TimeElapsedColumn(), TimeRemainingColumn(), console=self.console,
                                 disable=plain or not self.console.is_terminal)
        self.progress.start()

    def event(self, event, message, **data):
        stamp = datetime.now().astimezone().isoformat(timespec="seconds")
        self.file.write(json.dumps({"horario": stamp, "evento": event, "mensagem": message, **data}, ensure_ascii=False, default=str) + "\n")
        color = {"SALVO": "green", "SYNC": "cyan", "ERRO": "red", "PAUSA": "yellow", "PENDENTE": "yellow"}.get(event, "white")
        self.console.print(f"{stamp[11:19]} [{event}] {message}", style=color, markup=False)

    def wait(self, seconds, reason):
        self.event("PAUSA", f"{reason}. Espera de {seconds:.0f}s; Ctrl+C interrompe com segurança.")
        task = self.progress.add_task("Pausa / nova tentativa", total=seconds)
        end = time.monotonic() + seconds
        last_heartbeat = time.monotonic()
        try:
            while (remaining := end - time.monotonic()) > 0:
                time.sleep(min(1, remaining))
                self.progress.update(task, completed=seconds - max(0, end - time.monotonic()))
                if time.monotonic() - last_heartbeat >= 30:
                    self.event("AGUARDANDO", f"{reason}; faltam aproximadamente {max(0, end - time.monotonic()):.0f}s")
                    last_heartbeat = time.monotonic()
        finally:
            self.progress.remove_task(task)

    def close(self):
        self.progress.stop()
        self.console.print(Panel(" | ".join(f"{k}: {v:,}" for k, v in self.counts.items()) or "Nenhuma alteração", title="Resumo da execução"))
        self.console.print(f"Log completo: {self.path}", markup=False)
        self.file.close()


class Fipe:
    def __init__(self, args, monitor):
        self.args, self.log = args, monitor
        self.session = requests.Session()
        self.session.headers.update({"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
                                     "Origin": "https://veiculos.fipe.org.br", "Referer": "https://veiculos.fipe.org.br/", "Accept": "application/json"})
        self.last = 0.0

    def post(self, endpoint, body):
        for attempt in range(1, self.args.tentativas + 1):
            if self.args.max_consultas and self.log.counts["chamadas HTTP"] >= self.args.max_consultas:
                raise RequestLimit()
            time.sleep(max(0, self.args.intervalo - (time.monotonic() - self.last)))
            self.last = time.monotonic()
            self.log.counts["chamadas HTTP"] += 1
            started = time.monotonic()
            self.log.event("HTTP", f"POST {endpoint} · tentativa {attempt}/{self.args.tentativas}", parametros=body)
            try:
                response = self.session.post(API + endpoint, json=body, timeout=(10, self.args.timeout), allow_redirects=False)
            except requests.RequestException:
                self.log.event("ERRO", "Falha de rede/timeout; nenhuma cotação foi gravada")
                if attempt == self.args.tentativas:
                    raise SourceError("Falha de rede após todas as tentativas") from None
                self.log.wait(min(300, 15 * 2 ** (attempt - 1)), "Falha de rede")
                continue
            self.log.event("HTTP", f"HTTP {response.status_code} · {time.monotonic() - started:.1f}s")
            if response.status_code in (403, 429):
                if attempt == self.args.tentativas:
                    raise Blocked(f"HTTP {response.status_code} persistente; coleta inteira interrompida")
                delay = self.args.pausa_bloqueio * 2 ** (attempt - 1)
                retry = response.headers.get("Retry-After", "")
                if retry.isdigit():
                    delay = max(delay, int(retry))
                elif retry:
                    try:
                        delay = max(delay, (email.utils.parsedate_to_datetime(retry) - datetime.now(timezone.utc)).total_seconds())
                    except (ValueError, TypeError):
                        pass
                self.log.wait(delay, f"FIPE limitou/bloqueou a conexão (HTTP {response.status_code})")
                continue
            if response.status_code >= 500 and attempt < self.args.tentativas:
                self.log.wait(min(300, 15 * 2 ** (attempt - 1)), "Instabilidade na FIPE")
                continue
            if response.status_code != 200:
                raise SourceError(f"HTTP {response.status_code}; mês continua pendente")
            try:
                return response.json()
            except ValueError:
                raise SourceError("A FIPE retornou conteúdo inválido em vez de JSON") from None
        raise SourceError("Consulta sem resposta válida")

    def quote(self, vehicle, reference):
        body = {"codigoTabelaReferencia": reference["codigo"], "codigoTipoVeiculo": vehicle["tipo"],
                "codigoMarca": vehicle["marca"], "codigoModelo": vehicle["modelo"], "ano": vehicle["ano"],
                "anoModelo": int(vehicle["ano"].split("-")[0]), "codigoTipoCombustivel": int(vehicle["ano"].split("-")[1]),
                "tipoConsulta": "codigo" if vehicle["codigo_fipe"] else "tradicional", "modeloCodigoExterno": vehicle["codigo_fipe"] or ""}
        response = self.post("ConsultarValorComTodosParametros", body)
        if vehicle["codigo_fipe"] and isinstance(response, dict) and (response.get("Erro") or response.get("erro")):
            self.log.event("PENDENTE", "Consulta por código recusada; tentando marca/modelo/ano")
            body.update(tipoConsulta="tradicional", modeloCodigoExterno="")
            response = self.post("ConsultarValorComTodosParametros", body)
        return parse_quote(response, vehicle, reference)


VEHICLES = '''SELECT v."Id" AS id, ma."Tipo" AS tipo, ma."CodigoFipe" AS marca,
mo."CodigoFipe" AS modelo, v."AnoModeloCodigo" AS ano, v."CodigoFipe" AS codigo_fipe,
ma."Nome" || ' ' || mo."Nome" || ' ' || v."AnoLabel" AS nome
FROM "Veiculos" v JOIN "Modelos" mo ON mo."Id"=v."ModeloId"
JOIN "Marcas" ma ON ma."Id"=mo."MarcaId"
WHERE (%s::integer[] IS NULL OR v."Id"=ANY(%s)) AND (%s=0 OR ma."Tipo"=%s)
ORDER BY v."Id" LIMIT %s'''


def vehicles(db, args):
    return db.execute(VEHICLES, (args.veiculo, args.veiculo, args.tipo, args.tipo, args.limite_veiculos)).fetchall()


def references(db, months):
    return db.execute('SELECT "Codigo" AS codigo, "Mes" AS mes, "Data" AS data FROM "TabelasReferencia" ORDER BY "Data" DESC LIMIT %s', (months,)).fetchall()


def refresh_references(db, api, months):
    raw = api.post("ConsultarTabelaDeReferencia", {})
    if not isinstance(raw, list) or not raw:
        raise SourceError("Lista de referências inválida")
    parsed = []
    for item in raw:
        try:
            code, month = int(item["Codigo"]), parse_month(item["Mes"])
            if code <= 0 or month is None:
                raise ValueError()
            parsed.append((code, item["Mes"], month))
        except (KeyError, ValueError, TypeError):
            raise SourceError("Lista de referências contém mês inválido") from None
    with db.transaction():
        for code, label, month in sorted(parsed, key=lambda r: r[2], reverse=True)[:months]:
            row = db.execute('SELECT "Data" FROM "TabelasReferencia" WHERE "Codigo"=%s', (code,)).fetchone()
            if row and row["Data"] != month:
                raise SourceError("Referência local diverge da FIPE; atualização cancelada")
            db.execute('INSERT INTO "TabelasReferencia" ("Codigo","Mes","Data") VALUES (%s,%s,%s) ON CONFLICT DO NOTHING', (code, label, month))


def known_prices(db, vehicle):
    return {r["referencia"]: r["valor"] for r in db.execute('SELECT "TabelaReferenciaCodigo" AS referencia, "Valor" AS valor FROM "Precos" WHERE "VeiculoId"=%s', (vehicle,))}


def missing(known, reference, retry_empty):
    return reference not in known or retry_empty and not (known[reference] is not None and known[reference] > 0)


def save_quote(db, vehicle, reference, value, code):
    with db.transaction():
        row = db.execute('''UPDATE "Veiculos" SET "CodigoFipe"=%s WHERE "Id"=%s
            AND ("CodigoFipe" IS NULL OR "CodigoFipe"='' OR "CodigoFipe"=%s) RETURNING "Id"''', (code, vehicle["id"], code)).fetchone()
        if not row:
            raise SourceError("Código FIPE local mudou durante a consulta; gravação cancelada")
        row = db.execute('''INSERT INTO "Precos" ("VeiculoId","TabelaReferenciaCodigo","Valor","ColetadoEm")
            VALUES (%s,%s,%s,now()) ON CONFLICT ("VeiculoId","TabelaReferenciaCodigo") DO UPDATE
            SET "Valor"=EXCLUDED."Valor", "ColetadoEm"=EXCLUDED."ColetadoEm"
            WHERE "Precos"."Valor" IS NULL OR "Precos"."Valor"<=0 RETURNING "Id"''', (vehicle["id"], reference["codigo"], value)).fetchone()
    vehicle["codigo_fipe"] = code
    return bool(row)


def plan(db, args, log):
    selected, refs = vehicles(db, args), references(db, args.meses)
    if not refs:
        raise ValueError("Banco sem referências; execute collect/run para atualizá-las")
    log.event("PLANO", "Calculando cobertura local e tamanho da fila")
    pending = db.execute('''SELECT count(*) AS total FROM unnest(%s::integer[]) v(id)
        CROSS JOIN unnest(%s::integer[]) r(codigo)
        LEFT JOIN "Precos" p ON p."VeiculoId"=v.id AND p."TabelaReferenciaCodigo"=r.codigo
        WHERE p."Id" IS NULL OR (%s AND (p."Valor" IS NULL OR p."Valor"<=0))''',
        ([v["id"] for v in selected], [r["codigo"] for r in refs], args.reconsultar_vazios)).fetchone()["total"]
    table = Table(title="Plano de coleta local")
    table.add_column("Item"); table.add_column("Quantidade / período")
    for name, value in [("Veículos", f"{len(selected):,}"), ("Referências", f"{len(refs)}: {refs[-1]['mes']} a {refs[0]['mes']}"),
                        ("Combinações já consultadas", f"{len(selected) * len(refs) - pending:,}"), ("Combinações pendentes", f"{pending:,}"),
                        ("Tempo mínimo estimado", f"{duration(pending * args.intervalo)}; pausas e retentativas aumentam esse tempo")]:
        table.add_row(name, value)
    log.console.print(table)
    log.event("PLANO", f"{len(selected):,} veículos; {pending:,} combinações pendentes", pendentes=pending, meses=len(refs))
    return selected, refs, pending


class Sync:
    def __init__(self, config, state_path, log):
        self.config, self.log, self.client = config, log, None
        self.pending = set()
        self.password = None
        state_path.parent.mkdir(parents=True, exist_ok=True)
        self.state = sqlite3.connect(state_path)
        self.state.execute("CREATE TABLE IF NOT EXISTS enviados (destino TEXT, chave TEXT, PRIMARY KEY(destino,chave))")
        self.destination = hashlib.sha256(json.dumps(config, sort_keys=True).encode()).hexdigest()
        self.known = {r[0] for r in self.state.execute("SELECT chave FROM enviados WHERE destino=?", (self.destination,))}

    def connect(self):
        if self.client and self.client.get_transport() and self.client.get_transport().is_active():
            return self.client
        cfg = self.config
        client = paramiko.SSHClient()
        client.load_system_host_keys()
        client.set_missing_host_key_policy(paramiko.RejectPolicy())
        common = dict(hostname=cfg["host"], port=cfg.get("port", 22), username=cfg.get("user", "root"),
                      timeout=15, auth_timeout=20, banner_timeout=20)
        if cfg.get("key_file"):
            common["key_filename"] = str(Path(cfg["key_file"]).expanduser())
        self.password = self.password or os.environ.get(cfg.get("password_env", "FIPE_VPS_SSH_PASSWORD"))
        try:
            client.connect(**common, password=self.password)
        except paramiko.AuthenticationException:
            if self.password:
                client.close()
                raise ValueError("Senha SSH recusada; confira a credencial") from None
            if not sys.stdin.isatty():
                client.close()
                raise ValueError("Sem autenticação SSH. Configure chave ou FIPE_VPS_SSH_PASSWORD") from None
            self.password = getpass.getpass(f"Senha SSH de {common['username']}@{common['hostname']}: ")
            client.connect(**common, password=self.password, allow_agent=False, look_for_keys=False)
        transport = client.get_transport()
        transport.set_keepalive(30)
        self.client = client
        self.log.event("SSH", f"Conexão autenticada em {cfg['host']}; banco acessado dentro do Docker")
        return client

    def batch(self, rows):
        payload = base64.b64encode(json.dumps(rows, default=str, ensure_ascii=False).encode()).decode()
        sql = (HERE / "merge_precos.sql").read_text(encoding="utf-8").replace("__PAYLOAD_BASE64__", payload)
        cfg = self.config
        command = shlex.join(["docker", "exec", "-i", cfg.get("container", "fipedados-fipedados-db-1"), "psql", "-X", "-qAt", "-v", "ON_ERROR_STOP=1", "-v", "VERBOSITY=sqlstate", "-U", cfg.get("db_user", "fipe"), "-d", cfg.get("database", "fipedados")])
        stdin, stdout, stderr = self.connect().exec_command(command, timeout=120)
        stdin.write(sql)
        stdin.flush()
        stdin.channel.shutdown_write()
        output = stdout.read().decode()
        errors = stderr.read().decode(errors="replace")
        if stdout.channel.recv_exit_status() != 0:
            # Mensagens PostgreSQL podem conter dados da linha; não registrar o payload.
            sqlstate = re.search(r"ERROR:\s+([0-9A-Z]{5})\b", errors)
            suffix = f" (SQLSTATE {sqlstate[1]})" if sqlstate else ""
            raise ValueError("VPS recusou o lote; transação não confirmada. Verifique catálogo/schema/permissões" + suffix)
        try:
            result = json.loads(output.strip())
            confirmed = set(result["confirmados"])
            if not confirmed.issubset({r["chave"] for r in rows}):
                raise ValueError()
        except (ValueError, KeyError, TypeError):
            raise ValueError("Resposta da VPS inválida; lote permanece pendente e pode ser reenviado") from None
        # Só confirmar após COMMIT remoto e saída zero do psql. Reenvio é idempotente.
        self.state.executemany("INSERT OR IGNORE INTO enviados VALUES (?,?)", [(self.destination, key) for key in confirmed])
        self.state.commit()
        self.known.update(confirmed)
        self.pending.difference_update(confirmed)
        self.pending.update({r["chave"] for r in rows} - confirmed)
        return result

    def send(self, db, args):
        self.log.event("SYNC", "Verificando preços locais ainda não confirmados na VPS")
        selected = vehicles(db, args)
        ids = [v["id"] for v in selected]
        sent = 0
        batch = []
        with db.cursor(name="precos_para_vps", row_factory=dict_row, withhold=True) as cursor:
            # Cursor no servidor evita carregar todo o histórico na memória.
            with db.transaction():
                cursor.execute('''SELECT p."VeiculoId" AS local_id, ma."Tipo" AS tipo, ma."CodigoFipe" AS marca,
                    mo."CodigoFipe" AS modelo,v."AnoModeloCodigo" AS ano,v."CodigoFipe" AS codigo_fipe,
                    t."Codigo" AS referencia,t."Mes" AS mes,t."Data" AS data,p."Valor" AS valor,p."ColetadoEm" AS coletado_em
                    FROM "Precos" p JOIN "Veiculos" v ON v."Id"=p."VeiculoId" JOIN "Modelos" mo ON mo."Id"=v."ModeloId"
                    JOIN "Marcas" ma ON ma."Id"=mo."MarcaId" JOIN "TabelasReferencia" t ON t."Codigo"=p."TabelaReferenciaCodigo"
                    WHERE p."Valor">0 AND v."Id"=ANY(%s) AND t."Codigo"=ANY(%s)
                    ORDER BY p."Id"''', (ids, [r["codigo"] for r in references(db, args.meses)]))
            for row in cursor:
                row.pop("local_id")
                row["chave"] = hashlib.sha256(json.dumps(row, default=str, sort_keys=True).encode()).hexdigest()
                if row["chave"] in self.known:
                    continue
                batch.append(row)
                if len(batch) >= args.lote_sync:
                    self.report(self.batch(batch)); sent += len(batch); batch = []
            if batch:
                self.report(self.batch(batch)); sent += len(batch)
        self.log.event("SYNC", f"Sincronização concluída: {sent:,} preços enviados nesta passagem")

    def report(self, result):
        self.log.counts["gravados VPS"] += result["gravados"]
        self.log.counts["preservados VPS"] += result["preservados"]
        self.log.counts["pendentes VPS"] = len(self.pending)
        self.log.event("SYNC", f"Lote de {result['recebidos']} · gravados/preenchidos: {result['gravados']} · já existentes: {result['preservados']} · sem veículo: {result['nao_mapeados']} · incompatíveis: {result['incompativeis']}", **{k: v for k, v in result.items() if k not in ("confirmados", "pendencias")})
        for item in result.get("pendencias", []):
            self.log.event("PENDENTE", f"Merge: tipo {item['tipo']} / marca {item['marca']} / modelo {item['modelo']} / ano {item['ano']} / referência {item['referencia']} · {item['motivo']}", **item)
        if result["nao_mapeados"] or result["incompativeis"]:
            self.log.event("PENDENTE", "Alguns preços ficaram fora do merge; catálogo/referências precisam ser conciliados. Nenhuma cotação existente foi sobrescrita")

    def close(self):
        if self.client:
            self.client.close()
        self.state.close()


@contextmanager
def collector_lock(db):
    if not db.execute("SELECT pg_try_advisory_lock(726354891) AS ok").fetchone()["ok"]:
        raise ValueError("Outro coletor Python já está rodando neste banco")
    try:
        yield
    finally:
        db.execute("SELECT pg_advisory_unlock(726354891)")


def collect(db, args, log, sync=None):
    api = Fipe(args, log)
    try:
        if not args.sem_atualizar_referencias:
            refresh_references(db, api, args.meses)
        selected, refs, pending = plan(db, args, log)
        task = log.progress.add_task("Coleta: veículo / mês", total=pending)
        last_sync, errors = time.monotonic(), 0
        for index, vehicle in enumerate(selected, 1):
            known = known_prices(db, vehicle["id"])
            outstanding = [r for r in refs if missing(known, r["codigo"], args.reconsultar_vazios)]
            if not outstanding:
                continue
            log.event("VEICULO", f"[{index:,}/{len(selected):,}] ID local {vehicle['id']} · {vehicle['nome']} · {len(outstanding)} meses pendentes", veiculo=vehicle["id"])
            for reference in outstanding:
                log.progress.update(task, description=f"ID {vehicle['id']} · {reference['mes']}")
                log.event("CONSULTA", f"{vehicle['nome']} · {reference['mes']} · referência {reference['codigo']}", veiculo=vehicle["id"], referencia=reference["codigo"])
                started = time.monotonic()
                try:
                    value, code = api.quote(vehicle, reference)
                    written = save_quote(db, vehicle, reference, value, code)
                    log.counts["salvos local"] += int(written)
                    log.counts["preservados local"] += int(not written)
                    errors = 0
                    log.event("SALVO", f"{vehicle['nome']} · {reference['mes']} · {money(value)} · {'gravado localmente' if written else 'preço existente preservado'} · {time.monotonic() - started:.1f}s", veiculo=vehicle["id"], referencia=reference["codigo"], valor=str(value))
                except (Blocked, RequestLimit):
                    raise
                except SourceError as exc:
                    errors = 0 if isinstance(exc, CombinationUnavailable) else errors + 1
                    log.counts["meses pendentes por falha"] += 1
                    log.event("PENDENTE", str(exc), veiculo=vehicle["id"], referencia=reference["codigo"])
                    if errors >= 10:
                        raise Blocked("Dez combinações seguidas falharam; interrompido para não insistir em uma fonte indisponível") from None
                log.progress.advance(task)
                if sync and time.monotonic() - last_sync >= args.sincronizar_cada:
                    safe_sync(sync, db, args, log)
                    last_sync = time.monotonic()
        log.progress.update(task, description="Coleta finalizada (confira pendências)")
    finally:
        api.session.close()


def safe_sync(sync, db, args, log):
    try:
        sync.send(db, args)
        return True
    except paramiko.BadHostKeyException:
        log.counts["falhas de sincronização"] += 1
        log.event("ERRO", "A identidade SSH da VPS difere de known_hosts; confira o servidor antes de continuar")
        return False
    except ValueError as exc:
        log.counts["falhas de sincronização"] += 1
        log.event("ERRO", f"{exc}. Preços locais preservados; lote pode ser reenviado")
        return False
    except (paramiko.SSHException, OSError) as exc:
        log.counts["falhas de sincronização"] += 1
        reason = "Chave do servidor ausente em known_hosts; confira a identidade via ssh antes de continuar" if isinstance(exc, paramiko.SSHException) and "not found in known_hosts" in str(exc) else f"Falha SSH/rede ({type(exc).__name__}); confira host, chave e credencial"
        log.event("ERRO", reason + ". Preços permanecem locais; próxima passagem tentará de novo")
        return False


def arguments(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    p.add_argument("comando", choices=["plan", "collect", "sync", "run"], help="prévia, coleta local, envio à VPS ou coleta com envio periódico")
    p.add_argument("--config", type=Path, default=HERE / "historico.local.toml")
    p.add_argument("--meses", type=int, default=24)
    p.add_argument("--veiculo", type=int, action="append", help="ID local; pode repetir")
    p.add_argument("--tipo", type=int, choices=[0, 1, 2, 3], default=0, help="0=todos, 1=carros, 2=motos, 3=caminhões")
    p.add_argument("--limite-veiculos", type=int)
    p.add_argument("--max-consultas", type=int, default=0, help="limite real de chamadas HTTP (0=sem limite), incluindo referências/retentativas")
    p.add_argument("--intervalo", type=float, default=1.8, help="intervalo mínimo entre requisições, em segundos")
    p.add_argument("--tentativas", type=int, default=4)
    p.add_argument("--timeout", type=float, default=30)
    p.add_argument("--pausa-bloqueio", type=float, default=300, help="espera inicial para HTTP 403/429; dobra a cada tentativa")
    p.add_argument("--sincronizar-cada", type=float, default=300, help="intervalo de envio em run, em segundos; também envia ao terminar")
    p.add_argument("--lote-sync", type=int, default=500)
    p.add_argument("--reconsultar-vazios", action="store_true", help="reconsultar também linhas locais sem preço; positivos são preservados")
    p.add_argument("--sem-atualizar-referencias", action="store_true")
    p.add_argument("--sem-tela", action="store_true", help="desabilitar barra dinâmica; mantém todos os eventos")
    p.add_argument("--logs", type=Path, default=HERE / "logs")
    p.add_argument("--estado", type=Path, default=HERE / "logs" / "sync.sqlite3")
    args = p.parse_args(argv)
    if not 1 <= args.meses <= 240 or not 1 <= args.tentativas <= 10 or args.intervalo < 1.67 or args.timeout <= 0 or args.pausa_bloqueio <= 0 or args.sincronizar_cada <= 0 or not 1 <= args.lote_sync <= 5000 or args.max_consultas < 0 or args.limite_veiculos is not None and args.limite_veiculos <= 0:
        p.error("Limites inválidos; intervalo deve ser >=1.67s, meses 1..240 e lote 1..5000")
    return args


def main(argv=None):
    args = arguments(argv)
    log = Monitor(args.logs, args.sem_tela)
    sync = None
    code = 0
    try:
        config = tomllib.loads(args.config.read_text(encoding="utf-8-sig")) if args.config.exists() else {}
        dsn = os.environ.get("FIPE_LOCAL_DSN") or config.get("local_dsn")
        if not dsn:
            raise ValueError("Configure local_dsn no historico.local.toml ou FIPE_LOCAL_DSN")
        log.console.print(Panel("FIPE direta na máquina local → PostgreSQL local → merge incremental na VPS\nRetomada pelos preços gravados · Ctrl+C encerra · credenciais não entram no log", title="Histórico FIPE · Python"))
        log.event("INICIO", f"Modo {args.comando}; janela de {args.meses} meses; intervalo HTTP {args.intervalo}s")
        with psycopg.connect(dsn, autocommit=True, row_factory=dict_row, connect_timeout=10) as db:
            if args.comando == "plan":
                plan(db, args, log)
            else:
                with collector_lock(db):
                    if args.comando in ("sync", "run"):
                        if not config.get("vps", {}).get("host"):
                            raise ValueError("Configure [vps] no historico.local.toml")
                        sync = Sync(config["vps"], args.estado, log)
                        log.event("SSH", "Autenticação por chave ou variável de ambiente; sem elas a senha será solicitada no primeiro envio")
                    if args.comando in ("collect", "run"):
                        try:
                            collect(db, args, log, sync)
                        except RequestLimit:
                            log.event("LIMITE", "Limite de chamadas atingido; dados gravados permitem retomar depois")
                        except Blocked as exc:
                            log.event("ERRO", str(exc)); code = 2
                    if sync and not safe_sync(sync, db, args, log):
                        code = 2
        if log.counts["meses pendentes por falha"] or log.counts["falhas de sincronização"] or log.counts["pendentes VPS"]:
            code = 2
        log.event("FIM", "Execução concluída" if code == 0 else "Execução com pendências; consulte o log e retome", codigo_saida=code)
    except KeyboardInterrupt:
        log.event("PARADO", "Interrompido pelo usuário. Preços já confirmados estão preservados; rode sync para enviar os pendentes")
        code = 130
    except Exception as exc:
        # Erros de conexão podem conter DSN/senha. Não serializar exceções externas.
        message = str(exc) if type(exc) in (ValueError, SourceError, Blocked, CombinationUnavailable) else f"Falha em {type(exc).__name__}; confira conexão/configuração. Credenciais omitidas"
        log.event("ERRO", message); code = 2
    finally:
        if sync:
            sync.close()
        log.close()
    return code


if __name__ == "__main__":
    sys.exit(main())
