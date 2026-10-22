// Pede um anúncio para cada bloco novo do AdSense. Observa o DOM porque a navegação aprimorada
// e as páginas interativas do Blazor inserem os blocos sem reexecutar scripts inline.
function carregarAnuncios() {
    // Só os blocos do site (com data-ad-slot): o AdSense cria seus próprios <ins> ocultos.
    for (const bloco of document.querySelectorAll("ins.adsbygoogle[data-ad-slot]:not([data-vf-anuncio])")) {
        bloco.dataset.vfAnuncio = "1";
        try { (window.adsbygoogle = window.adsbygoogle || []).push({}); } catch { /* Bloco já preenchido ou sem espaço: o AdSense ignora. */ }
    }
}
carregarAnuncios();
// O observer já agrupa as mutações; a busca só encontra blocos ainda não marcados.
new MutationObserver(carregarAnuncios).observe(document.body, { childList: true, subtree: true });
