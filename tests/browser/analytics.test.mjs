import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import test from "node:test";

const source = await readFile(new URL("../../src/FipeDados.Web/wwwroot/js/analytics.js", import.meta.url), "utf8");

function browser({ session = new Map(), blockedStorage = false } = {}) {
    const events = [], clicks = [], nodes = new Map(), local = new Map();
    let enhancedLoad;
    const storage = map => ({
        getItem(key) { if (blockedStorage) throw new Error("storage blocked"); return map.get(key) ?? null; },
        setItem(key, value) { if (blockedStorage) throw new Error("storage blocked"); map.set(key, value); }
    });
    const context = vm.createContext({
        document: { cookie: "", querySelector: selector => nodes.get(selector), addEventListener: (name, callback) => clicks.push(callback) },
        sessionStorage: storage(session), localStorage: storage(local), location: { pathname: "/" },
        Blazor: { addEventListener: (name, callback) => { enhancedLoad = callback; } },
        gtag: (...args) => events.push(args)
    });
    vm.runInContext(source, context);
    return {
        events, nodes, load: () => enhancedLoad(),
        click(origin, code) {
            const link = { dataset: { planSource: origin, planCode: code } };
            for (const listener of clicks) listener({ target: { closest: selector => selector === "a[data-plan-source]" ? link : null } });
        }
    };
}

test("origem da oferta atravessa escolha do plano e recarga; checkout e compra mantêm valor e plano", () => {
    const session = new Map(), landing = browser({ session });
    landing.click("home");
    landing.click("planos", "anual");
    assert.equal(landing.events[1][2].offer_source, "home");
    const checkout = browser({ session });
    checkout.nodes.set("[data-ga-checkout]", { dataset: { gaCheckout: "anual", gaPlanoNome: "Anual", gaValor: "89.90" } });
    checkout.load();
    checkout.load();
    assert.equal(checkout.events.length, 1);
    const [, name, data] = checkout.events[0];
    assert.equal(name, "begin_checkout");
    assert.equal(data.offer_source, "home");
    assert.equal(data.value, 89.90);
    assert.equal(data.items[0].item_id, "anual");
    checkout.nodes.clear();
    checkout.nodes.set("[data-ga-compra]", { dataset: { gaCompra: "pedido-teste", gaPlano: "anual", gaPlanoNome: "Anual", gaValor: "89.90" } });
    checkout.load();
    checkout.load();
    assert.equal(checkout.events.filter(e => e[1] === "purchase").length, 1);
    assert.equal(checkout.events[1][2].offer_source, "home");
    assert.equal(checkout.events[1][2].transaction_id, "pedido-teste");
});

test("medição continua com armazenamento bloqueado e acesso direto", () => {
    const page = browser({ blockedStorage: true });
    page.nodes.set("[data-ga-checkout]", { dataset: { gaCheckout: "mensal", gaPlanoNome: "Mensal", gaValor: "9.90" } });
    page.load();
    assert.equal(page.events[0][2].offer_source, "direto");
    page.click("topo");
    page.click("planos", "mensal");
    assert.equal(page.events[2][2].offer_source, "topo");
});
