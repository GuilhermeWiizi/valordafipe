export const storageKey = "valordafipe:oferta-plano:v1";
const interval = 10 * 60 * 1000;
const handlers = new WeakMap();

// Uma única leitura/escrita síncrona compartilha a frequência entre pesquisa e comparação.
// Retorna null quando o armazenamento não permite manter essa regra.
export function registerConsultation(storage, eventId, now = Date.now()) {
    try {
        const raw = storage.getItem(storageKey);
        let state;
        try { state = JSON.parse(raw); } catch { state = null; }
        if (!state || !Number.isSafeInteger(state.count) || state.count < 0 || state.count > 3
            || !(state.lastShown === null || (Number.isFinite(state.lastShown) && state.lastShown >= 0))
            || typeof state.lastEvent !== "string") {
            state = { count: 0, lastShown: null, lastEvent: "" };
        }
        if (!eventId || state.lastEvent === eventId) return false;
        state.count = Math.min(3, state.count + 1);
        state.lastEvent = eventId;
        const show = state.count >= 3 && (state.lastShown === null || now - state.lastShown >= interval);
        if (show) { state.count = 0; state.lastShown = now; }
        storage.setItem(storageKey, JSON.stringify(state));
        return show;
    } catch { return null; }
}

export async function registerAndShow(dialog, eventId) {
    // Espera a pintura do resultado, sem esperar chamadas de atualização do histórico.
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    if (!dialog?.isConnected || dialog.open) return;
    let show;
    try { show = registerConsultation(window.sessionStorage, eventId); } catch { return; }
    if (!show) return;

    const previous = document.activeElement;
    const restoreFocus = () => {
        const target = previous?.isConnected && previous !== document.body && previous.getClientRects().length
            ? previous : document.querySelector("main h1");
        if (!target) return;
        if (!target.hasAttribute("tabindex") && target.tagName === "H1") target.setAttribute("tabindex", "-1");
        target.focus({ preventScroll: true });
    };
    if (handlers.has(dialog)) dialog.removeEventListener("close", handlers.get(dialog));
    handlers.set(dialog, restoreFocus);
    dialog.addEventListener("close", restoreFocus);
    dialog.showModal(); // Dialog nativo: Escape, foco inicial e contenção da navegação por Tab.
}

export function dispose(dialog) {
    if (!dialog) return;
    if (handlers.has(dialog)) dialog.removeEventListener("close", handlers.get(dialog));
    handlers.delete(dialog);
    if (dialog.open) dialog.close();
}
