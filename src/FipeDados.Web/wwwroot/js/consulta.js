// A consulta usa HTTP apenas para modelos/anos. Tipo, marcas, foco e abertura são locais.
export class ConsultaState {
    constructor(brands, request = async (url, signal) => {
        const response = await fetch(url, { signal });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return response.json();
    }, changed = () => {}) {
        this.brands = brands; this.request = request; this.changed = changed;
        this.tipo = 1; this.marca = 0; this.modelo = null; this.ano = null;
        this.modelos = []; this.anos = []; this.termo = ""; this.temMais = false;
        this.buscando = false; this.carregandoAnos = false; this.erro = null;
        this.versaoBusca = 0; this.versaoAnos = 0;
    }
    get marcas() { return [{ id: 0, nome: "Todas as marcas" }, ...(this.brands[this.tipo] ?? [])]; }
    cancelarBusca() {
        this.versaoBusca++; this.busca?.abort(); this.buscando = false;
    }
    limparModelo() {
        this.cancelarBusca(); this.versaoAnos++; this.cargaAnos?.abort();
        this.modelo = this.ano = null; this.modelos = []; this.anos = [];
        this.carregandoAnos = false; this.erro = null; this.termo = "";
    }
    trocarTipo(tipo) {
        if (this.tipo === tipo) return;
        this.tipo = tipo; this.marca = 0; this.limparModelo(); this.changed();
    }
    trocarMarca(item) {
        this.marca = item.id; this.limparModelo(); this.changed();
    }
    async buscar(termo) {
        this.cancelarBusca(); this.termo = termo.trim(); this.modelos = []; this.temMais = false; this.erro = null;
        if (this.termo.length < 2 && !(this.marca > 0 && this.termo === "")) { this.changed(); return; }
        const version = this.versaoBusca, controller = this.busca = new AbortController();
        const query = new URLSearchParams({ tipo: this.tipo, termo: this.termo });
        if (this.marca > 0) query.set("marca", this.marca);
        this.buscando = true; this.changed();
        try {
            const result = await this.request(`/api/consulta/modelos?${query}`, controller.signal);
            if (controller.signal.aborted || version !== this.versaoBusca) return;
            this.modelos = result.itens; this.temMais = result.temMais;
        } catch (error) {
            if (!controller.signal.aborted && version === this.versaoBusca)
                this.erro = { etapa: "modelos", mensagem: "Não foi possível pesquisar os modelos." };
        } finally {
            if (version === this.versaoBusca) { this.buscando = false; this.changed(); }
        }
    }
    async selecionarModelo(item) {
        this.limparModelo(); this.modelo = item; this.marca = item.marcaId;
        if (!this.marcas.some(m => m.id === item.marcaId)) this.brands[this.tipo].push({ id: item.marcaId, nome: item.marcaNome });
        await this.carregarAnos();
    }
    async carregarAnos() {
        this.versaoAnos++; this.cargaAnos?.abort();
        const version = this.versaoAnos, controller = this.cargaAnos = new AbortController(), id = this.modelo.id;
        this.ano = null; this.anos = []; this.carregandoAnos = true; this.erro = null; this.changed();
        try {
            const result = await this.request(`/api/consulta/modelos/${id}/anos`, controller.signal);
            if (controller.signal.aborted || version !== this.versaoAnos) return;
            this.anos = result;
            if (!result.length) this.erro = { etapa: "anos", mensagem: "Nenhum ano disponível para este modelo no momento." };
        } catch (error) {
            if (!controller.signal.aborted && version === this.versaoAnos)
                this.erro = { etapa: "anos", mensagem: "Não foi possível carregar os anos." };
        } finally {
            if (version === this.versaoAnos) { this.carregandoAnos = false; this.changed(); }
        }
    }
    selecionarAno(item) { this.ano = item; this.changed(); }
    repetir() { return this.erro?.etapa === "anos" ? this.carregarAnos() : this.buscar(this.termo); }
    dispose() { this.cancelarBusca(); this.versaoAnos++; this.cargaAnos?.abort(); }
}

const normalize = text => text.normalize("NFD").replace(/\p{Diacritic}/gu, "").toLocaleLowerCase("pt-BR");

export class FipeConsulta extends HTMLElement {
    connectedCallback() {
        if (this.life) return;
        this.life = new AbortController();
        const options = { signal: this.life.signal };
        this.fields = new Map([...this.querySelectorAll("[data-campo]")].map(root => [root.dataset.campo, {
            root, details: root.querySelector("details"), trigger: root.querySelector("summary"),
            input: root.querySelector("input"), list: root.querySelector("[role=listbox]"),
            status: root.querySelector("[role=status]"), index: 0, items: []
        }]));
        this.state = new ConsultaState({ 1: JSON.parse(this.dataset.carros), 2: JSON.parse(this.dataset.motos) }, undefined, () => this.render());
        // Uma escolha feita no radio nativo antes do download do módulo é preservada.
        this.state.tipo = Number(this.querySelector("[name=tipo-consulta]:checked").value);
        this.addEventListener("change", event => {
            if (event.target.name !== "tipo-consulta") return;
            this.closeAll(); this.state.trocarTipo(Number(event.target.value));
        }, options);
        for (const [name, field] of this.fields) {
            field.details.addEventListener("toggle", () => {
                const open = field.details.open;
                field.root.classList.toggle("is-open", open); field.trigger.setAttribute("aria-expanded", String(open));
                if (!open) { if (name === "modelo") { clearTimeout(this.timer); this.state.cancelarBusca(); } return; }
                this.closeAll(name); field.input.value = ""; field.index = 0;
                this.renderField(name); field.input.focus({ preventScroll: true });
                if (name === "modelo") this.state.buscar("");
            }, options);
            field.trigger.addEventListener("click", event => {
                if (field.trigger.getAttribute("aria-disabled") === "true") event.preventDefault();
            }, options);
            field.input.addEventListener("input", () => {
                field.index = 0;
                if (name !== "modelo") { this.renderField(name); return; }
                clearTimeout(this.timer); this.state.cancelarBusca(); this.state.modelos = []; this.state.termo = field.input.value.trim(); this.renderField(name);
                this.timer = setTimeout(() => this.state.buscar(field.input.value), 300);
            }, options);
            field.root.addEventListener("keydown", event => this.key(name, event), options);
            field.list.addEventListener("click", event => {
                const option = event.target.closest("[data-option]");
                if (option) this.select(name, field.items.find(item => item.id === Number(option.dataset.option)));
            }, options);
        }
        this.querySelector("[data-repetir]").addEventListener("click", () => this.state.repetir(), options);
        document.addEventListener("click", event => {
            for (const field of this.fields.values()) if (!field.root.contains(event.target)) field.details.open = false;
        }, options);
        this.querySelector("[data-consultar]").addEventListener("click", event => {
            if (!this.state.ano) { event.preventDefault(); return; }
            // A oferta de plano só conta pesquisas concluídas, inclusive após navegação SSR.
            try { sessionStorage.setItem("valordafipe:pesquisa", JSON.stringify({ caminho: this.state.ano.caminho, evento: crypto.randomUUID() })); } catch { }
        }, options);
        this.render();
    }
    disconnectedCallback() {
        clearTimeout(this.timer); this.life?.abort(); this.state?.dispose(); this.life = null;
    }
    closeAll(except) {
        clearTimeout(this.timer);
        for (const [name, field] of this.fields) if (name !== except) field.details.open = false;
    }
    select(name, item) {
        if (!item) return;
        this.closeAll();
        if (name === "marca") this.state.trocarMarca(item);
        else if (name === "modelo") this.state.selecionarModelo(item);
        else this.state.selecionarAno(item);
        this.fields.get(name).trigger.focus({ preventScroll: true });
    }
    key(name, event) {
        const field = this.fields.get(name);
        if (!field.details.open) return;
        if (event.key === "Escape" || event.key === "Tab") {
            field.details.open = false;
            if (event.key === "Escape") { event.preventDefault(); field.trigger.focus({ preventScroll: true }); }
            return;
        }
        if (event.target !== field.input || !field.items.length) return;
        if (event.key === "Enter") { event.preventDefault(); this.select(name, field.items[field.index]); return; }
        if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
        event.preventDefault();
        field.index = event.key === "Home" ? 0 : event.key === "End" ? field.items.length - 1
            : (field.index + (event.key === "ArrowDown" ? 1 : -1) + field.items.length) % field.items.length;
        this.renderField(name);
        field.list.querySelector(".highlighted")?.scrollIntoView({ block: "nearest" });
    }
    render() {
        const s = this.state;
        for (const [name, field] of this.fields) {
            const selected = name === "marca" ? s.marcas.find(m => m.id === s.marca) : name === "modelo" ? s.modelo : s.ano;
            const disabled = name === "ano" && (!s.modelo || s.carregandoAnos || !s.anos.length);
            field.trigger.setAttribute("aria-disabled", String(disabled));
            field.root.classList.toggle("is-disabled", disabled);
            field.root.classList.toggle("has-value", (selected?.id ?? 0) > 0);
            field.root.querySelector("[data-valor]").textContent = selected?.nome ?? (name === "modelo" ? "Escolha ou pesquise o modelo" : s.carregandoAnos ? "Carregando anos…" : "Escolha o ano");
            if (name === "modelo") field.root.querySelector(".field-hint").textContent = s.marca > 0 ? "Pesquise os modelos desta marca" : "Pesquise o modelo ou selecione uma marca";
            if (name === "ano") field.root.querySelector(".field-hint").textContent = s.modelo ? "Escolha o ano e a motorização" : "Selecione um modelo primeiro";
            if (disabled) field.details.open = false;
            this.renderField(name);
        }
        const error = this.querySelector("[data-erro]"); error.hidden = !s.erro;
        error.querySelector("span").textContent = s.erro?.mensagem ?? "";
        this.querySelector(".progress-track > span").style.width = `${s.ano ? 100 : s.modelo ? 66 : s.marca ? 33 : 0}%`;
        this.querySelector("[data-progresso]").textContent = s.ano ? "Tudo pronto para consultar" : "Selecione marca, modelo e ano";
        const link = this.querySelector("[data-consultar]");
        link.setAttribute("aria-disabled", String(!s.ano));
        if (s.ano) { link.href = s.ano.caminho; link.removeAttribute("tabindex"); }
        else { link.removeAttribute("href"); link.setAttribute("tabindex", "-1"); }
    }
    renderField(name) {
        const f = this.fields.get(name), s = this.state;
        if (!f.details.open) return;
        const source = name === "marca" ? s.marcas : name === "modelo" ? s.modelos : s.anos;
        const matches = name === "modelo" ? source : source.filter(item => normalize(item.nome).includes(normalize(f.input.value)));
        f.items = matches.slice(0, 80); f.index = Math.min(f.index, Math.max(0, f.items.length - 1));
        const fragment = document.createDocumentFragment();
        for (const [index, item] of f.items.entries()) {
            const button = document.createElement("button");
            button.type = "button"; button.tabIndex = -1; button.setAttribute("role", "option");
            button.id = `consulta-${name}-option-${item.id}`; button.dataset.option = item.id;
            button.className = `select-option${index === f.index ? " highlighted" : ""}`;
            button.setAttribute("aria-selected", String(item.id === (name === "marca" ? s.marca : name === "modelo" ? s.modelo?.id : s.ano?.id)));
            button.textContent = name === "modelo" && !s.marca ? `${item.marcaNome} — ${item.nome}` : item.nome;
            fragment.append(button);
        }
        f.list.replaceChildren(fragment); f.list.setAttribute("aria-busy", String(name === "modelo" && s.buscando));
        if (f.items.length) f.input.setAttribute("aria-activedescendant", `consulta-${name}-option-${f.items[f.index].id}`);
        else f.input.removeAttribute("aria-activedescendant");
        f.status.hidden = f.items.length > 0 && !(name === "modelo" && s.buscando);
        f.status.textContent = name === "modelo" && s.buscando ? "Pesquisando modelos…"
            : name === "modelo" && !s.marca && f.input.value.trim().length < 2 ? "Digite pelo menos 2 caracteres para pesquisar."
            : "Nenhuma opção encontrada.";
        const more = matches.length > 80 || (name === "modelo" && s.temMais);
        f.root.querySelector("[data-total]").textContent = more ? `${f.items.length}+ opções · refine a busca para ver mais` : `${matches.length} opções disponíveis`;
    }
}

if (!customElements.get("fipe-consulta")) customElements.define("fipe-consulta", FipeConsulta);
