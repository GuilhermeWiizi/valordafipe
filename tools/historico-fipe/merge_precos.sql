-- Executado na VPS em uma única transação por lote. O payload é JSON em base64.
-- Os IDs locais não são usados para relacionar os veículos.
BEGIN;
SET LOCAL statement_timeout = '45s';
SET LOCAL lock_timeout = '5s';
CREATE TEMP TABLE entrada ON COMMIT DROP AS
SELECT * FROM jsonb_to_recordset(
    convert_from(decode('__PAYLOAD_BASE64__', 'base64'), 'UTF8')::jsonb
) AS x(chave text, tipo integer, marca integer, modelo integer, ano text,
       codigo_fipe text, referencia integer, mes text, data date,
       valor numeric(14,2), coletado_em timestamptz);

-- Cada linha só é elegível quando o catálogo e a referência são compatíveis.
CREATE TEMP TABLE mapeados ON COMMIT DROP AS
SELECT e.*, v."Id" AS veiculo_id,
       (v."Id" IS NOT NULL AND (v."CodigoFipe" IS NULL OR v."CodigoFipe" = ''
        OR e.codigo_fipe IS NULL OR v."CodigoFipe" = e.codigo_fipe)
        AND (t."Codigo" IS NULL OR t."Data" = e.data)) AS compativel
FROM entrada e
LEFT JOIN "Marcas" ma ON ma."Tipo" = e.tipo AND ma."CodigoFipe" = e.marca
LEFT JOIN "Modelos" mo ON mo."MarcaId" = ma."Id" AND mo."CodigoFipe" = e.modelo
LEFT JOIN "Veiculos" v ON v."ModeloId" = mo."Id" AND v."AnoModeloCodigo" = e.ano
LEFT JOIN "TabelasReferencia" t ON t."Codigo" = e.referencia;

INSERT INTO "TabelasReferencia" ("Codigo", "Mes", "Data")
SELECT DISTINCT referencia, mes, data FROM mapeados WHERE compativel
ON CONFLICT ("Codigo") DO NOTHING;

-- Preenche também lacunas nulas/zero, preservando toda cotação positiva existente.
CREATE TEMP TABLE alterados ON COMMIT DROP AS
WITH gravados AS (
    INSERT INTO "Precos" ("VeiculoId", "TabelaReferenciaCodigo", "Valor", "ColetadoEm")
    SELECT veiculo_id, referencia, valor, coletado_em FROM mapeados
    WHERE compativel AND valor > 0
    ON CONFLICT ("VeiculoId", "TabelaReferenciaCodigo") DO UPDATE
    SET "Valor" = EXCLUDED."Valor", "ColetadoEm" = EXCLUDED."ColetadoEm"
    WHERE "Precos"."Valor" IS NULL OR "Precos"."Valor" <= 0
    RETURNING "VeiculoId", "TabelaReferenciaCodigo"
) SELECT * FROM gravados;

-- Só completa código FIPE desconhecido; nunca substitui outro código.
UPDATE "Veiculos" v SET "CodigoFipe" = m.codigo_fipe
FROM mapeados m
WHERE m.compativel AND m.veiculo_id = v."Id" AND m.codigo_fipe IS NOT NULL
AND (v."CodigoFipe" IS NULL OR v."CodigoFipe" = '');

SELECT json_build_object(
    'recebidos', (SELECT count(*) FROM entrada),
    'gravados', (SELECT count(*) FROM alterados),
    'preservados', (SELECT count(*) FROM mapeados WHERE compativel) - (SELECT count(*) FROM alterados),
    'nao_mapeados', (SELECT count(*) FROM mapeados WHERE veiculo_id IS NULL),
    'incompativeis', (SELECT count(*) FROM mapeados WHERE veiculo_id IS NOT NULL AND NOT compativel),
    'pendencias', (SELECT coalesce(json_agg(json_build_object(
        'tipo', tipo, 'marca', marca, 'modelo', modelo, 'ano', ano,
        'codigo_fipe', codigo_fipe, 'referencia', referencia,
        'motivo', CASE WHEN veiculo_id IS NULL THEN 'veículo não encontrado na VPS'
                      ELSE 'código FIPE ou data da referência incompatível' END
    )), '[]'::json) FROM mapeados WHERE NOT compativel),
    'confirmados', (SELECT coalesce(json_agg(chave), '[]'::json) FROM mapeados WHERE compativel)
);
COMMIT;
