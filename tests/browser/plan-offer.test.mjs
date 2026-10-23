import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = await readFile(new URL("../../src/FipeDados.Web/wwwroot/js/plan-offer.js", import.meta.url), "utf8");
const { registerConsultation: register, storageKey, registerAndShow, dispose, consumeSearch } = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
const now = 1_800_000_000_000;
function storage(initial = null) {
    let value = initial;
    return { getItem: () => value, setItem: (key, next) => { assert.equal(key, storageKey); value = next; } };
}

test("pesquisa SSR só é consumida no destino correto e uma única vez", () => {
    let raw = JSON.stringify({ caminho: "/veiculo", evento: "consulta-1" });
    const tab = { getItem: () => raw, removeItem: () => { raw = null; } };
    assert.equal(consumeSearch("/veiculo", tab), "consulta-1");
    assert.equal(consumeSearch("/veiculo", tab), null);
    raw = JSON.stringify({ caminho: "/outro", evento: "consulta-2" });
    assert.equal(consumeSearch("/veiculo", tab), null);
    raw = "{";
    assert.equal(consumeSearch("/veiculo", tab), null);
});

test("pesquisa e comparação compartilham a primeira oferta na terceira consulta", () => {
    const tab = storage();
    assert.equal(register(tab, "pesquisa:1", now), false);
    assert.equal(register(tab, "comparacao:1", now), false);
    assert.equal(register(tab, "pesquisa:2", now), true);
    assert.equal(register(tab, "pesquisa:2", now), false);
});

test("exige três novas consultas e dez minutos; dispara na próxima elegível", () => {
    const tab = storage();
    for (let i = 1; i <= 3; i++) register(tab, `${i}`, now);
    for (let i = 4; i <= 6; i++) assert.equal(register(tab, `${i}`, now + 599_999), false);
    assert.equal(register(tab, "7", now + 600_000), true);
    assert.equal(register(tab, "8", now + 1_200_000), false);
    assert.equal(register(tab, "9", now + 1_200_000), false);
    assert.equal(register(tab, "10", now + 1_200_000), true);
});

test("recarregar preserva o estado; uma aba independente começa do zero", () => {
    const tab = storage();
    register(tab, "1", now);
    register(tab, "2", now);
    const reloaded = storage(tab.getItem(storageKey));
    assert.equal(register(reloaded, "3", now), true);
    assert.equal(register(storage(), "3", now), false);
});

test("evento duplicado não conta e estado inválido reinicia sem abrir oferta", () => {
    const tab = storage();
    register(tab, "1", now);
    assert.equal(register(tab, "1", now), false);
    assert.equal(JSON.parse(tab.getItem(storageKey)).count, 1);
    for (const invalid of ["{", "null", '{"count":99}', '{"count":3,"lastShown":"ontem","lastEvent":"1"}'])
        assert.equal(register(storage(invalid), "2", now), false);
});

test("falha ao ler ou escrever impede a oferta sem propagar erro", () => {
    assert.equal(register({ getItem() { throw Error("blocked"); } }, "1", now), null);
    assert.equal(register({ getItem: () => null, setItem() { throw Error("quota"); } }, "1", now), null);
});

test("mostra só após pintar resultado e restaura foco ao fechar", async () => {
    const tab = storage();
    register(tab, "1", now);
    register(tab, "2", now);
    const listeners = new Map();
    let frames = 0, focused = false;
    globalThis.requestAnimationFrame = callback => { frames++; callback(); };
    globalThis.window = { sessionStorage: tab };
    const previous = { isConnected: true, tagName: "BUTTON", hasAttribute: () => false,
        getClientRects: () => [1], focus: () => { focused = true; } };
    globalThis.document = { activeElement: previous, body: {} };
    const dialog = {
        isConnected: true, open: false,
        addEventListener: (event, fn) => listeners.set(event, fn),
        removeEventListener: event => listeners.delete(event),
        showModal() { assert.equal(frames, 2); this.open = true; },
        close() { this.open = false; listeners.get("close")?.(); }
    };
    await registerAndShow(dialog, "3");
    assert.equal(dialog.open, true);
    dialog.close();
    assert.equal(focused, true);
    dispose(dialog);
    assert.equal(listeners.size, 0);
    const count = tab.getItem(storageKey);
    dialog.isConnected = false;
    await registerAndShow(dialog, "4");
    assert.equal(tab.getItem(storageKey), count);
});
