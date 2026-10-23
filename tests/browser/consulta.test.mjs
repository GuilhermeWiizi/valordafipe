import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

globalThis.HTMLElement = class {};
globalThis.customElements = { get: () => null, define() {} };
const source = await readFile(new URL("../../src/FipeDados.Web/wwwroot/js/consulta.js", import.meta.url), "utf8");
const { ConsultaState } = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
const brands = () => ({ 1: [{ id: 1, nome: "Carro" }], 2: [{ id: 2, nome: "Moto" }] });
const model = { id: 10, nome: "Modelo", marcaId: 1, marcaNome: "Carro" };
const year = { id: 100, nome: "2025 Gasolina", caminho: "/tabela-fipe/carros/carro/modelo/2025-gasolina" };
const deferred = () => { let resolve; const promise = new Promise(r => resolve = r); return { promise, resolve }; };

test("moto e suas marcas estão disponíveis sem chamada HTTP ou conexão Blazor", () => {
    const state = new ConsultaState(brands(), () => assert.fail("não deve consultar rede"));
    state.trocarTipo(2);
    assert.equal(state.tipo, 2);
    assert.deepEqual(state.marcas.map(b => b.nome), ["Todas as marcas", "Moto"]);
    state.trocarTipo(1);
    assert.equal(state.marcas[1].nome, "Carro");
});

test("busca exige dois caracteres ou marca selecionada e preserva limite de resultados", async () => {
    const calls = [];
    const state = new ConsultaState(brands(), async url => { calls.push(url); return { itens: [model], temMais: true }; });
    await state.buscar(""); await state.buscar("m");
    assert.equal(calls.length, 0);
    await state.buscar("modelo");
    assert.equal(new URL(calls[0], "https://localhost").searchParams.get("tipo"), "1");
    assert.equal(state.temMais, true);
    state.trocarMarca({ id: 1 });
    await state.buscar("");
    assert.equal(new URL(calls[1], "https://localhost").searchParams.get("marca"), "1");
});

test("troca para moto durante busca cancela e ignora resposta atrasada de carro", async () => {
    const response = deferred(); let signal;
    const state = new ConsultaState(brands(), (_, token) => { signal = token; return response.promise; });
    const pending = state.buscar("modelo");
    assert.equal(state.buscando, true);
    state.trocarTipo(2);
    assert.equal(signal.aborted, true);
    assert.equal(state.buscando, false);
    response.resolve({ itens: [model], temMais: false }); await pending;
    assert.deepEqual(state.modelos, []);
    assert.equal(state.marca, 0);
});

test("troca de tipo responde imediatamente enquanto os anos ainda estão carregando", async () => {
    const response = deferred(); let signal;
    const state = new ConsultaState(brands(), (_, token) => { signal = token; return response.promise; });
    const pending = state.selecionarModelo(model);
    assert.equal(state.carregandoAnos, true);
    state.trocarTipo(2);
    assert.equal(signal.aborted, true);
    assert.equal(state.tipo, 2);
    assert.equal(state.modelo, null);
    response.resolve([year]); await pending;
    assert.deepEqual(state.anos, []);
    assert.equal(state.ano, null);
});

test("modelo preenche marca, falha permite repetir anos e seleção produz URL correta", async () => {
    let calls = 0;
    const state = new ConsultaState(brands(), async () => {
        if (++calls === 1) throw Error("temporário");
        return [year];
    });
    await state.selecionarModelo(model);
    assert.equal(state.marca, 1);
    assert.equal(state.modelo.id, 10);
    assert.equal(state.erro.etapa, "anos");
    await state.repetir();
    assert.equal(state.erro, null);
    state.selecionarAno(state.anos[0]);
    assert.equal(state.ano.caminho, year.caminho);
    state.trocarMarca({ id: 0 });
    assert.equal(state.modelo, null); assert.equal(state.ano, null);
});

test("resposta antiga não substitui busca recente e sair cancela solicitações", async () => {
    const old = deferred(); let calls = 0, lastSignal;
    const state = new ConsultaState(brands(), async (_, signal) => {
        lastSignal = signal;
        if (++calls === 1) return old.promise;
        return { itens: [{ ...model, id: 20 }], temMais: false };
    });
    const pending = state.buscar("antigo");
    await state.buscar("atual");
    old.resolve({ itens: [model], temMais: true }); await pending;
    assert.equal(state.modelos[0].id, 20);
    state.dispose();
    assert.equal(lastSignal.aborted, true);
});
