// Gráfico e filtros do histórico do veículo, no navegador: a página é SSR estático e não
// mantém circuito no servidor. Web Component para funcionar também na navegação aprimorada,
// que insere o HTML novo sem reexecutar scripts.
const apexUrl = "/_content/Blazor-ApexCharts/js/apexcharts.esm.js";
let apex;
const carregarApex = () => apex ??= import(apexUrl).then(m => m.default);

const decimal = new Intl.NumberFormat("pt-BR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const curto = new Intl.NumberFormat("pt-BR", { maximumFractionDigits: 1 });
const moeda = new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" });
const pct = v => v == null ? "—" : (v > 0 ? "+" : "") + decimal.format(v) + "%";
const numeroCsv = v => v == null ? "" : v.toFixed(2).replace(".", ",");
const padrao = () => ({ periodo: null, medida: "preco" });

class FipeHistorico extends HTMLElement {
    static observedAttributes = ["data-serie"];
    #estado = padrao();
    #grafico = null;
    #secao = null;
    #versao = 0;
    #aoClicar = e => this.#clique(e);

    connectedCallback() {
        this.#secao = this.closest(".vehicle-history");
        this.#secao?.addEventListener("click", this.#aoClicar);
        this.#aplicar();
    }

    disconnectedCallback() {
        this.#secao?.removeEventListener("click", this.#aoClicar);
        this.#versao++;
        this.#grafico?.destroy();
        this.#grafico = null;
    }

    attributeChangedCallback(_nome, antes, depois) {
        // HTML novo do servidor (ex.: depois de completar o histórico) chega no estado padrão.
        if (!this.isConnected || antes === null || antes === depois) return;
        this.#estado = padrao();
        this.#aplicar();
    }

    #clique(e) {
        const botao = e.target.closest("button[data-periodo], button[data-medida]");
        if (!botao || !this.#secao?.contains(botao)) return;
        if (botao.dataset.periodo !== undefined) this.#estado.periodo = botao.dataset.periodo ? Number(botao.dataset.periodo) : null;
        else this.#estado.medida = botao.dataset.medida;
        this.#aplicar();
    }

    #serie() {
        try { return JSON.parse(this.dataset.serie || "[]"); } catch { return []; }
    }

    async #aplicar() {
        const secao = this.#secao;
        if (!secao) return;
        const todas = this.#serie();
        const { periodo, medida } = this.#estado;
        const percentual = medida === "variacao";
        const inicio = periodo ? Math.max(0, todas.length - periodo) : 0;
        const linhas = todas.slice(inicio);
        const base = linhas.find(l => l.v > 0)?.v;
        const acumulada = l => base > 0 && l.v != null ? (l.v / base - 1) * 100 : null;

        for (const b of secao.querySelectorAll("button[data-periodo]")) this.#marcar(b, b.dataset.periodo === String(periodo ?? ""));
        for (const b of secao.querySelectorAll("button[data-medida]")) this.#marcar(b, b.dataset.medida === medida);
        this.#texto("[data-historico-resumo]", linhas.length === 0 ? "Aguardando preços disponíveis."
            : `${linhas[0].m} a ${linhas.at(-1).m} · ${linhas.filter(l => l.v != null).length} meses com preço`);
        this.#texto("[data-historico-medida]", percentual ? "Variação desde a primeira cotação do período" : "Preço de referência em reais");
        this.#texto("[data-historico-desde]", linhas.find(l => l.v > 0)?.m ?? "—");

        for (const tr of secao.querySelectorAll("tr[data-indice]")) {
            const i = Number(tr.dataset.indice);
            tr.hidden = i < inicio;
            const celula = tr.querySelector("[data-acumulada]");
            if (tr.hidden || !celula || !todas[i]) continue;
            const valor = acumulada(todas[i]);
            celula.textContent = pct(valor);
            celula.classList.toggle("history-up", valor > 0);
            celula.classList.toggle("history-down", valor < 0);
        }

        const csv = secao.querySelector("[data-historico-csv]");
        if (csv) csv.href = "data:text/csv;charset=utf-8," + encodeURIComponent("﻿Referência;Preço FIPE;Variação mensal (%);Variação mensal (R$);Variação acumulada (%)\r\n"
            + linhas.slice().reverse().map(l => [l.d, numeroCsv(l.v), numeroCsv(l.pm), numeroCsv(l.vm), numeroCsv(acumulada(l))].join(";")).join("\r\n"));

        const versao = ++this.#versao;
        let ApexCharts;
        try { ApexCharts = await carregarApex(); } catch { return; } // Sem gráfico, a tabela continua completa.
        if (versao !== this.#versao || !this.isConnected) return;
        const opcoes = {
            chart: { type: "line", height: 320, toolbar: { show: false }, fontFamily: "inherit", animations: { enabled: false } },
            colors: ["#197653"],
            stroke: { curve: "straight", width: 3 },
            markers: { size: 3 },
            dataLabels: { enabled: false },
            series: [{ name: percentual ? "Variação acumulada" : "Preço Fipe", data: linhas.map(l => percentual ? acumulada(l) : l.v) }],
            xaxis: { categories: linhas.map(l => l.r) },
            yaxis: { labels: { formatter: v => v == null ? "—" : percentual ? curto.format(v) + "%" : "R$ " + curto.format(v / 1000) + " mil" } },
            tooltip: { y: { formatter: v => v == null ? "Sem preço" : percentual ? decimal.format(v) + "%" : moeda.format(v) } }
        };
        if (this.#grafico) { await this.#grafico.updateOptions(opcoes, true, false); return; }
        this.replaceChildren();
        this.#grafico = new ApexCharts(this, opcoes);
        await this.#grafico.render();
    }

    #marcar(botao, selecionado) {
        botao.classList.toggle("selected", selecionado);
        botao.setAttribute("aria-pressed", String(selecionado));
    }

    #texto(seletor, valor) {
        const alvo = this.#secao?.querySelector(seletor);
        if (alvo) alvo.textContent = valor;
    }
}

if (!customElements.get("fipe-historico")) customElements.define("fipe-historico", FipeHistorico);
