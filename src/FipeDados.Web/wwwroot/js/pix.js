document.addEventListener("click", async event => {
    const button = event.target.closest?.("[data-pix-copy]");
    if (!button) return;
    const field = document.getElementById(button.dataset.pixCopy);
    const status = document.getElementById("pix-copy-status");
    if (!field || !status) return;
    try {
        await navigator.clipboard.writeText(field.value);
        status.textContent = "Código Pix copiado.";
    } catch {
        field.focus();
        field.select();
        status.textContent = "Selecione e copie o código acima para colar no aplicativo do banco.";
    }
});

// Consulta apenas o estado local. O servidor confirma o pagamento diretamente no provedor.
function acompanharPix() {
    const panel = document.querySelector("[data-pix-status]");
    if (!panel || panel.dataset.pixWatching) return;
    panel.dataset.pixWatching = "true";
    if (![0, 4].includes(Number(panel.dataset.pixState))) return;
    let running = false;
    const timer = setInterval(async () => {
        if (!panel.isConnected) { clearInterval(timer); return; }
        if (document.visibilityState !== "visible" || running) return;
        running = true;
        try {
            const response = await fetch(panel.dataset.pixStatus, { cache: "no-store", credentials: "same-origin", redirect: "error" });
            if (response.status === 401 || response.status === 404) { clearInterval(timer); return; }
            if (!response.ok) return;
            const state = await response.json();
            if (state.status !== Number(panel.dataset.pixState) || state.temCodigo !== (panel.dataset.pixCode === "true") || state.revisao) {
                clearInterval(timer);
                location.reload();
            }
        } catch { /* Uma falha transitória não altera o pedido nem comprova um pagamento. */ }
        finally { running = false; }
    }, 5000);
}
acompanharPix();
document.addEventListener("DOMContentLoaded", acompanharPix);
if (typeof Blazor !== "undefined") Blazor.addEventListener("enhancedload", acompanharPix);
