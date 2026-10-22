"""Testes sem chamadas externas. Integração opcional usa schema descartável no PostgreSQL."""
import base64
from collections import Counter
from datetime import date
from decimal import Decimal
import io
import json
import os
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import Mock, patch
import uuid

import historico as h


def vehicle():
    return dict(id=8323, tipo=1, marca=22, modelo=1, ano="2015-5", codigo_fipe="003386-3", nome="Ford Fiesta 2015 Flex")


def quote(**changes):
    return dict(Valor="R$ 40.979,00", CodigoFipe="003386-3", MesReferencia="agosto de 2026", AnoModelo=2015, TipoVeiculo=1, **changes)


def response(status, body=None, headers=None):
    r = h.requests.Response()
    r.status_code = status
    r._content = json.dumps(body).encode()
    r.headers.update(headers or {})
    return r


def monitor():
    log = Mock()
    log.counts = Counter()
    return log


class Quotes(unittest.TestCase):
    def test_valid_quote_and_missing_are_distinct(self):
        self.assertEqual((Decimal("40979.00"), "003386-3"), h.parse_quote(quote(), vehicle(), dict(data=date(2026, 8, 1))))
        for body in [None, [], {}, {"erro": "Parâmetros inválidos", "codigo": "6"}]:
            with self.subTest(body=body), self.assertRaises(h.SourceError):
                h.parse_quote(body, vehicle(), dict(data=date(2026, 8, 1)))

    def test_wrong_month_year_type_code_and_invalid_price_never_persist(self):
        for key, value in [("MesReferencia", "julho de 2026"), ("AnoModelo", 2016), ("TipoVeiculo", 2),
                           ("CodigoFipe", "001552-0"), ("Valor", "R$ 0,00"), ("Valor", "NaN"),
                           ("Valor", "R$ -10,00"), ("Valor", "12.34,00")]:
            body = quote(); body[key] = value
            with self.subTest(key=key, value=value), self.assertRaises(h.SourceError):
                h.parse_quote(body, vehicle(), dict(data=date(2026, 8, 1)))

    def test_resume_skips_positive_and_empty_unless_requested(self):
        self.assertFalse(h.missing({1: Decimal(10)}, 1, True))
        self.assertFalse(h.missing({1: None}, 1, False))
        self.assertTrue(h.missing({1: None}, 1, True))
        self.assertTrue(h.missing({}, 1, False))

    def test_blocking_waits_retry_after_and_stops_globally(self):
        log = monitor()
        api = h.Fipe(h.arguments(["collect", "--tentativas", "2", "--pausa-bloqueio", "30"]), log)
        api.session = Mock()
        api.session.post.side_effect = [response(429, headers={"Retry-After": "600"}), response(403)]
        with patch.object(h.time, "sleep"), self.assertRaises(h.Blocked):
            api.post("test", {})
        log.wait.assert_called_once_with(600, "FIPE limitou/bloqueou a conexão (HTTP 429)")
        self.assertEqual(2, log.counts["chamadas HTTP"])

    def test_timeouts_retry_and_cap_is_actual_http_calls(self):
        log = monitor()
        api = h.Fipe(h.arguments(["collect", "--max-consultas", "1"]), log)
        api.session = Mock()
        api.session.post.side_effect = h.requests.Timeout()
        with patch.object(h.time, "sleep"), self.assertRaises(h.RequestLimit):
            api.post("test", {})
        self.assertEqual(1, api.session.post.call_count)

    def test_logs_are_jsonl_and_do_not_interpret_vehicle_markup(self):
        with TemporaryDirectory() as directory:
            log = h.Monitor(Path(directory), plain=True)
            log.event("SALVO", "Fiesta [bold] 2015", veiculo=8323, referencia=336)
            log.close()
            data = json.loads(log.path.read_text(encoding="utf8"))
            self.assertEqual(8323, data["veiculo"])
            self.assertIn("[bold]", data["mensagem"])


class SyncConfirmation(unittest.TestCase):
    def test_failed_remote_commit_never_acknowledges_batch(self):
        with TemporaryDirectory() as directory:
            sync = h.Sync({"host": "test"}, Path(directory) / "state.sqlite3", monitor())
            client = Mock()
            stdout = Mock(); stdout.read.return_value = b'{"confirmados": ["one"]}'
            stdout.channel.recv_exit_status.return_value = 1
            client.exec_command.return_value = (Mock(), stdout, io.BytesIO(b"ERROR: commit failed"))
            with patch.object(sync, "connect", return_value=client), self.assertRaises(ValueError):
                sync.batch([{"chave": "one"}])
            self.assertEqual(set(), sync.known)
            self.assertEqual(0, sync.state.execute("SELECT count(*) FROM enviados").fetchone()[0])
            sync.close()

    def test_successful_commit_records_only_confirmed_and_remembers_destination(self):
        with TemporaryDirectory() as directory:
            path = Path(directory) / "state.sqlite3"
            sync = h.Sync({"host": "test"}, path, monitor())
            client = Mock()
            stdout = Mock(); stdout.read.return_value = b'{"confirmados": ["one"]}'
            stdout.channel.recv_exit_status.return_value = 0
            client.exec_command.return_value = (Mock(), stdout, io.BytesIO())
            with patch.object(sync, "connect", return_value=client):
                sync.batch([{"chave": "one"}, {"chave": "unmatched"}])
            sync.close()
            reopened = h.Sync({"host": "test"}, path, monitor())
            other = h.Sync({"host": "another-vps"}, path, monitor())
            self.assertEqual({"one"}, reopened.known)
            self.assertEqual(set(), other.known)
            reopened.close(); other.close()


@unittest.skipUnless(os.environ.get("FIPE_PY_TEST_DSN"), "Configure FIPE_PY_TEST_DSN para integração PostgreSQL")
class Postgres(unittest.TestCase):
    def setUp(self):
        self.schema = "python_historico_" + uuid.uuid4().hex
        self.db = h.psycopg.connect(os.environ["FIPE_PY_TEST_DSN"], autocommit=True, row_factory=h.dict_row)
        self.db.execute(h.psycopg.sql.SQL("CREATE SCHEMA {} ; SET search_path TO {}").format(h.psycopg.sql.Identifier(self.schema), h.psycopg.sql.Identifier(self.schema)))
        self.db.execute('''CREATE TABLE "Marcas" ("Id" integer PRIMARY KEY, "Tipo" integer, "CodigoFipe" integer, "Nome" text);
            CREATE TABLE "Modelos" ("Id" integer PRIMARY KEY,"MarcaId" integer REFERENCES "Marcas","CodigoFipe" integer,"Nome" text);
            CREATE TABLE "Veiculos" ("Id" integer PRIMARY KEY,"ModeloId" integer REFERENCES "Modelos","AnoModeloCodigo" text,"CodigoFipe" text,"AnoLabel" text);
            CREATE TABLE "TabelasReferencia" ("Codigo" integer PRIMARY KEY,"Mes" text,"Data" date);
            CREATE TABLE "Precos" ("Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,"VeiculoId" integer REFERENCES "Veiculos","TabelaReferenciaCodigo" integer REFERENCES "TabelasReferencia","Valor" numeric(14,2),"ColetadoEm" timestamptz,
              UNIQUE("VeiculoId","TabelaReferenciaCodigo"));
            INSERT INTO "Marcas" VALUES(999,1,22,'Ford');
            INSERT INTO "Modelos" VALUES(777,999,1,'Fiesta');
            INSERT INTO "Veiculos" VALUES(444,777,'2015-5','003386-3','2015 Flex');
            INSERT INTO "TabelasReferencia" VALUES(338,'outubro/2026','2026-10-01'),(337,'setembro/2026','2026-09-01');
            INSERT INTO "Precos" ("VeiculoId","TabelaReferenciaCodigo","Valor","ColetadoEm") VALUES(444,338,39870,now()),(444,337,NULL,now());''')

    def tearDown(self):
        self.db.execute("ROLLBACK")
        self.db.execute(h.psycopg.sql.SQL("DROP SCHEMA {} CASCADE").format(h.psycopg.sql.Identifier(self.schema)))
        self.db.close()

    def merge(self, rows):
        payload = base64.b64encode(json.dumps(rows).encode()).decode()
        script = (h.HERE / "merge_precos.sql").read_text(encoding="utf8").replace("__PAYLOAD_BASE64__", payload)
        cursor = self.db.execute(script, prepare=False)
        result = None
        while True:
            if cursor.description:
                result = json.loads(next(iter(cursor.fetchone().values()))) if cursor.description[0].type_code == 25 else next(iter(cursor.fetchone().values()))
            if not cursor.nextset():
                break
        return result

    def row(self, code, month, value="40979.00", **changes):
        return dict(chave=str(code),tipo=1,marca=22,modelo=1,ano="2015-5",codigo_fipe="003386-3",
                    referencia=code,mes=month,data=h.parse_month(month).isoformat(),valor=value,coletado_em="2026-10-22T12:00:00+00:00",**changes)

    def test_merge_different_ids_preserves_positive_fills_null_and_is_idempotent(self):
        rows = [self.row(338,"outubro/2026", "1.00"),self.row(337,"setembro/2026"),self.row(336,"agosto/2026")]
        result = self.merge(rows)
        self.assertEqual(2, result["gravados"])
        self.assertEqual(1, result["preservados"])
        self.assertEqual(Decimal("39870"), self.db.execute('SELECT "Valor" FROM "Precos" WHERE "TabelaReferenciaCodigo"=338').fetchone()["Valor"])
        self.assertEqual({444}, {r["VeiculoId"] for r in self.db.execute('SELECT "VeiculoId" FROM "Precos"')})
        self.assertEqual(0, self.merge(rows)["gravados"])
        self.assertEqual(3, self.db.execute('SELECT count(*) AS n FROM "Precos"').fetchone()["n"])

    def test_unmatched_and_incompatible_data_are_not_confirmed(self):
        missing = self.row(336,"agosto/2026"); missing["modelo"] = 200
        wrong = self.row(335,"julho/2026"); wrong["codigo_fipe"] = "001552-0"
        wrong_ref = self.row(338,"agosto/2026")
        result = self.merge([missing, wrong, wrong_ref])
        self.assertEqual(1, result["nao_mapeados"])
        self.assertEqual(2, result["incompativeis"])
        self.assertEqual([], result["confirmados"])
        self.assertEqual(0, result["gravados"])
        self.assertEqual(3, len(result["pendencias"]))

    def test_server_cursor_sends_without_using_local_ids_and_can_resume(self):
        with TemporaryDirectory() as directory:
            sync = h.Sync({"host": "test"}, Path(directory) / "sync.sqlite3", monitor())
            args = h.arguments(["sync", "--veiculo", "444"])
            def batch(rows):
                self.assertNotIn("local_id", rows[0])
                sync.known.update(r["chave"] for r in rows)
                return dict(recebidos=len(rows),gravados=len(rows),preservados=0,nao_mapeados=0,incompativeis=0)
            with patch.object(sync,"batch",side_effect=batch) as call:
                sync.send(self.db,args)
                sync.send(self.db,args)
                self.assertEqual(1, call.call_count)
            sync.close()

    def test_local_fill_preserves_existing_price_and_resume_counts(self):
        v = vehicle(); v["id"] = 444
        self.assertFalse(h.save_quote(self.db,v,dict(codigo=338),Decimal(1),"003386-3"))
        self.assertTrue(h.save_quote(self.db,v,dict(codigo=337),Decimal(40000),"003386-3"))
        _, _, pending = h.plan(self.db,h.arguments(["plan", "--veiculo", "444"]),monitor())
        self.assertEqual(0, pending)


if __name__ == "__main__":
    unittest.main(verbosity=2)
