// Contingência para quando a Fipe não responde ao servidor: o navegador do visitante consulta a
// API da Fipe (que libera CORS para qualquer origem) e entrega cada mês ao componente .NET.
// Nada é gravado no banco; cada visitante gasta só a cota do próprio IP.
const api = "https://veiculos.fipe.org.br/api/veiculos/";
const intervalo = 1000; // Em rajada, a Fipe bloqueia o IP depois de ~10 consultas; a 1/s, depois de ~90.
const chavePausa = "fipe-navegador-pausa";
const pausaAposFalha = 5 * 60_000;
const nomesMeses = ["janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro"];

class FalhaFipe extends Error { }

const esperar = ms => new Promise(r => setTimeout(r, ms));

function emPausa() {
    try { return Number(localStorage.getItem(chavePausa)) > Date.now(); } catch { return false; }
}

function pausar() {
    try { localStorage.setItem(chavePausa, String(Date.now() + pausaAposFalha)); } catch { }
}

// POST form-urlencoded com os parâmetros na URL é uma requisição "simples": dispensa o preflight
// de CORS. O 429 do Cloudflare vem sem o cabeçalho de CORS e aparece como falha de rede.
async function consultarFipe(endpoint, parametros) {
    try {
        const resposta = await fetch(api + endpoint + "?" + new URLSearchParams(parametros), {
            method: "POST",
            headers: { "content-type": "application/x-www-form-urlencoded" },
            signal: AbortSignal.timeout(15_000)
        });
        if (!resposta.ok) throw new Error(`HTTP ${resposta.status}`);
        return await resposta.json();
    } catch (erro) {
        throw new FalhaFipe(`Fipe ${endpoint}: ${erro.message}`);
    }
}

// "outubro/2026 " → "2026-10"
function dataDoMes(rotulo) {
    const [nome, ano] = String(rotulo).trim().toLowerCase().split("/");
    const mes = nomesMeses.indexOf(nome) + 1;
    return mes > 0 && /^\d{4}$/.test(ano ?? "") ? `${ano}-${String(mes).padStart(2, "0")}` : null;
}

// "R$ 48.093,00" → 48093; "nadaencontrado" e afins → null
function valorDe(resposta) {
    if (typeof resposta?.Valor !== "string") return null;
    const valor = Number(resposta.Valor.replace(/[^\d,]/g, "").replace(",", "."));
    return valor > 0 ? valor : null;
}

function parametrosValor(plano, codigoTabela, porCodigo) {
    return porCodigo
        ? {
            codigoTabelaReferencia: codigoTabela, codigoTipoVeiculo: plano.tipo, anoModelo: plano.anoModelo,
            modeloCodigoExterno: plano.codigoFipe, codigoTipoCombustivel: plano.codigoTipoCombustivel, tipoConsulta: "codigo"
        }
        : {
            codigoTabelaReferencia: codigoTabela, codigoTipoVeiculo: plano.tipo, codigoMarca: plano.codigoMarca,
            codigoModelo: plano.codigoModelo, ano: plano.ano, anoModelo: plano.anoModelo,
            codigoTipoCombustivel: plano.codigoTipoCombustivel, tipoConsulta: "tradicional", modeloCodigoExterno: ""
        };
}

/**
 * Consulta na Fipe os meses que o servidor não tem, do mais recente ao mais antigo, e entrega
 * cada um ao componente por `MesConsultado(codigo, data, valor)`. Para na primeira falha e,
 * se a falha veio da Fipe, suspende novas consultas deste navegador por 5 minutos.
 */
export async function consultar(plano, componente) {
    if (emPausa()) return { consultados: 0, completo: false };
    let consultados = 0, primeira = true;
    const fipe = async (endpoint, parametros) => {
        if (!primeira) await esperar(intervalo);
        primeira = false;
        return consultarFipe(endpoint, parametros);
    };
    try {
        let referencias = plano.referencias;
        if (referencias.length < plano.meses) {
            // O servidor ainda não tem todas as tabelas de referência: a lista vem da Fipe.
            const lista = await fipe("ConsultarTabelaDeReferencia", {});
            referencias = lista.slice(0, plano.meses)
                .map(t => ({ codigo: t.Codigo, data: dataDoMes(t.Mes) }))
                .filter(t => t.data);
        }
        const conhecidas = new Set(plano.conhecidas);
        const faltantes = referencias
            .filter(r => !conhecidas.has(r.codigo))
            .sort((a, b) => b.data.localeCompare(a.data));
        for (const referencia of faltantes) {
            // Um modelo do ano X só entra nas tabelas a partir do ano X-1: antes disso não gasta consulta.
            if (plano.anoModelo < 32000 && Number(referencia.data.slice(0, 4)) < plano.anoModelo - 1) continue;
            let valor = valorDe(await fipe("ConsultarValorComTodosParametros", parametrosValor(plano, referencia.codigo, !!plano.codigoFipe)));
            // Algumas versões não são encontradas pelo código em todos os meses.
            if (valor == null && plano.codigoFipe)
                valor = valorDe(await fipe("ConsultarValorComTodosParametros", parametrosValor(plano, referencia.codigo, false)));
            consultados++;
            await componente.invokeMethodAsync("MesConsultado", referencia.codigo, referencia.data, valor);
        }
        return { consultados, completo: true };
    } catch (erro) {
        if (erro instanceof FalhaFipe) {
            pausar();
            console.warn("Consulta à Fipe pelo navegador interrompida.", erro.message);
        }
        return { consultados, completo: false };
    }
}

/**
 * Página SSR do veículo: troca a série do histórico pelos dados mesclados. A tabela é refeita
 * com as linhas já formatadas no servidor; o gráfico e o acumulado são do <fipe-historico>.
 */
export function exibir(serie, linhas, estatisticas) {
    const grafico = document.querySelector("fipe-historico");
    const corpo = grafico?.closest(".vehicle-history")?.querySelector(".history-table tbody");
    if (!grafico || !corpo) return;
    corpo.replaceChildren(...linhas.map(l => {
        const tr = document.createElement("tr");
        tr.dataset.indice = l.indice;
        const th = document.createElement("th");
        th.scope = "row";
        th.textContent = l.mes;
        tr.append(th, celula(l.valor), celula(l.variacaoPct, l.classePct), celula(l.variacaoValor, l.classeValor),
            celula(l.acumulada, l.classeAcumulada, true));
        return tr;
    }));
    grafico.dataset.serie = JSON.stringify(serie);

    for (const [chave, texto] of Object.entries(estatisticas ?? {})) {
        const alvo = document.querySelector(`[data-estatistica="${chave}"]`);
        if (alvo) alvo.textContent = texto;
    }
}

function celula(texto, classe, acumulada) {
    const td = document.createElement("td");
    td.textContent = texto;
    if (classe) td.className = classe;
    if (acumulada) td.dataset.acumulada = "";
    return td;
}
