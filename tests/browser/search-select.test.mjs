import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = await readFile(new URL("../../src/FipeDados.Web/wwwroot/js/search-select.js", import.meta.url), "utf8");
const { prepare, scrollActive } = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

test("fechar o combo antes da chamada JS não causa erro nem tenta focar ou rolar", () => {
    for (const input of [null, undefined, {
        isConnected: false,
        addEventListener() { assert.fail("campo removido"); },
        focus() { assert.fail("campo removido"); },
        getAttribute() { assert.fail("campo removido"); }
    }]) {
        prepare(input);
        scrollActive(input);
    }
});

test("abertura foca o campo e preserva navegação por teclado", () => {
    const listeners = new Map();
    let focus = 0;
    prepare({
        isConnected: true,
        addEventListener: (name, callback) => listeners.set(name, callback),
        focus(options) { assert.equal(options.preventScroll, true); focus++; }
    });
    assert.equal(focus, 1);
    for (const key of ["ArrowDown", "ArrowUp", "Home", "End", "Enter", "Tab", "a"]) {
        let prevented = false;
        listeners.get("keydown")({ key, preventDefault() { prevented = true; } });
        assert.equal(prevented, ["ArrowDown", "ArrowUp", "Home", "End"].includes(key));
    }
});

test("rolagem acompanha a opção ativa e tolera lista sem resultados", () => {
    let active = null;
    const calls = [];
    const input = {
        isConnected: true,
        getAttribute(name) { assert.equal(name, "aria-activedescendant"); return "modelo-2"; },
        ownerDocument: {
            getElementById(id) { assert.equal(id, "modelo-2"); return active; }
        }
    };
    scrollActive(input);
    active = { scrollIntoView(options) { calls.push(options); } };
    scrollActive(input);
    assert.deepEqual(calls, [{ block: "nearest" }]);
});
