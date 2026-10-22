// Eventos do GA4 que a medição otimizada não captura. Só é carregado quando a tag está ativa
// (produção, fora do painel administrativo); o gtag vem do <head>.

const chaveOrigemPlano = "vf_ga_origem_plano";
let ultimaOrigemPlano = "direto";
const checkoutsMedidos = new WeakSet();

function origemPlano() {
    try { return sessionStorage.getItem(chaveOrigemPlano) || ultimaOrigemPlano; }
    catch { return ultimaOrigemPlano; }
}

// Mantém a origem da oferta durante a escolha do plano, login e cadastro na mesma aba.
document.addEventListener("click", event => {
    const link = event.target.closest?.("a[data-plan-source]");
    if (!link) return;
    const origem = link.dataset.planSource;
    if (origem !== "planos") {
        ultimaOrigemPlano = origem;
        try { sessionStorage.setItem(chaveOrigemPlano, origem); } catch { /* A medição continua sem storage. */ }
    }
    gtag("event", "plan_offer_click", { placement: origem, offer_source: origemPlano(), plan_code: link.dataset.planCode });
});

// "Baixar CSV" é um link data:, que a medição de downloads ignora.
document.addEventListener("click", event => {
    const link = event.target.closest?.("[data-historico-csv]");
    if (!link) return;
    gtag("event", "file_download", { file_extension: "csv", file_name: link.getAttribute("download"), link_url: location.pathname });
});

// Evento agendado pelo servidor antes de um redirecionamento (cadastro concluído).
function eventoPendente() {
    const evento = document.cookie.split("; ").find(c => c.startsWith("vf_ga_evento="))?.split("=")[1];
    if (!evento) return;
    document.cookie = "vf_ga_evento=; Max-Age=0; Path=/";
    if (evento === "sign_up") gtag("event", "sign_up", { method: "email" });
}

// Pix aprovado: uma compra por pedido, mesmo se a página for aberta de novo.
function compraAprovada() {
    const status = document.querySelector("[data-ga-compra]");
    if (!status) return;
    const id = status.dataset.gaCompra, chave = "vf_ga_compra_" + id;
    try { if (localStorage.getItem(chave)) return; localStorage.setItem(chave, "1"); } catch { /* Sem storage, o transaction_id ainda deduplica no GA. */ }
    const valor = Number(status.dataset.gaValor);
    gtag("event", "purchase", {
        transaction_id: id, value: valor, currency: "BRL", offer_source: origemPlano(),
        items: [{ item_id: status.dataset.gaPlano, item_name: status.dataset.gaPlanoNome, price: valor, quantity: 1 }]
    });
}

function inicioCheckout() {
    const plano = document.querySelector("[data-ga-checkout]");
    if (!plano || checkoutsMedidos.has(plano)) return;
    checkoutsMedidos.add(plano);
    const valor = Number(plano.dataset.gaValor);
    gtag("event", "begin_checkout", {
        value: valor, currency: "BRL", offer_source: origemPlano(),
        items: [{ item_id: plano.dataset.gaCheckout, item_name: plano.dataset.gaPlanoNome, price: valor, quantity: 1 }]
    });
}

function eventosDaPagina() { eventoPendente(); inicioCheckout(); compraAprovada(); }
eventosDaPagina();
if (typeof Blazor !== "undefined") Blazor.addEventListener("enhancedload", eventosDaPagina);
