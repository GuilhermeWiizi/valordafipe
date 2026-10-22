import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import test from 'node:test';

const source = await readFile(new URL('../../src/FipeDados.Web/wwwroot/js/pix.js', import.meta.url), 'utf8');

function page({ state = 0, code = true, clipboardFails = false } = {}) {
    let interval, reloads = 0, calls = 0, cleared = 0, selected = false, copied;
    const listeners = new Map();
    const field = { value: 'PIX-DE-TESTE', focus() {}, select() { selected = true; } };
    const status = { textContent: '' };
    const panel = { isConnected: true, dataset: { pixStatus: '/conta/pagamentos/teste/situacao', pixState: String(state), pixCode: String(code) } };
    const document = {
        visibilityState: 'visible', querySelector: () => panel,
        addEventListener: (name, fn) => listeners.set(name, fn),
        getElementById: id => id === 'pix-copy-status' ? status : field,
    };
    const context = {
        document, setInterval: fn => { interval = fn; return 1; }, clearInterval: () => cleared++,
        location: { reload: () => reloads++ },
        navigator: { clipboard: { async writeText(value) { if (clipboardFails) throw Error('indisponível'); copied = value; } } },
        fetch: async (url, options) => { calls++; assert.equal(url, panel.dataset.pixStatus); assert.equal(options.cache, 'no-store'); return context.response; },
        response: { ok: true, async json() { return { status: state, temCodigo: code }; } },
    };
    vm.runInNewContext(source, context);
    return {
        context, panel, document, tick: () => interval?.(), listeners, field, status,
        values: () => ({ reloads, calls, cleared, selected, copied }),
    };
}

test('copia código Pix e oferece seleção manual se a área de transferência falhar', async () => {
    for (const clipboardFails of [false, true]) {
        const p = page({ clipboardFails });
        await p.listeners.get('click')({ target: { closest: () => ({ dataset: { pixCopy: 'pix-codigo' } }) } });
        if (clipboardFails) { assert.equal(p.values().selected, true); assert.match(p.status.textContent, /Selecione/); }
        else { assert.equal(p.values().copied, 'PIX-DE-TESTE'); assert.match(p.status.textContent, /copiado/); }
    }
});

test('consulta apenas quando visível e recarrega na confirmação', async () => {
    const p = page();
    p.document.visibilityState = 'hidden';
    await p.tick();
    assert.equal(p.values().calls, 0);
    p.document.visibilityState = 'visible';
    await p.tick();
    assert.equal(p.values().reloads, 0);
    p.context.response = { ok: true, async json() { return { status: 2, temCodigo: true }; } };
    await p.tick();
    assert.equal(p.values().reloads, 1);
    assert.equal(p.values().cleared, 1);
});

test('atualiza ao disponibilizar QR Code e tolera falha temporária', async () => {
    const p = page({ state: 4, code: false });
    p.context.response = { ok: false, status: 503 };
    await p.tick();
    assert.equal(p.values().reloads, 0);
    p.context.response = { ok: true, async json() { return { status: 4, temCodigo: true }; } };
    await p.tick();
    assert.equal(p.values().reloads, 1);
});

test('encerra acompanhamento se a sessão expirar ou a página sair do documento', async () => {
    const p = page();
    p.context.response = { ok: false, status: 401 };
    await p.tick();
    assert.equal(p.values().cleared, 1);
    const q = page();
    q.panel.isConnected = false;
    await q.tick();
    assert.equal(q.values().calls, 0);
    assert.equal(q.values().cleared, 1);
});
