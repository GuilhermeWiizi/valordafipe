// Eventos do GA4 que a medição otimizada não captura. Só é carregado quando a tag está ativa
// (produção, fora do painel administrativo); o gtag vem do <head>.

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
        transaction_id: id, value: valor, currency: "BRL",
        items: [{ item_id: status.dataset.gaPlano, item_name: status.dataset.gaPlanoNome, price: valor, quantity: 1 }]
    });
}

function eventosDaPagina() { eventoPendente(); compraAprovada(); }
eventosDaPagina();
if (typeof Blazor !== "undefined") Blazor.addEventListener("enhancedload", eventosDaPagina);
