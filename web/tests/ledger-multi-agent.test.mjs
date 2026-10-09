import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { fileURLToPath } from 'node:url';

const source = fileURLToPath(new URL('../src/lib/types.ts', import.meta.url));
const compiled = ts.transpileModule(readFileSync(source, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText;
const module = { exports: {} };
// types.ts has no runtime dependencies; evaluating the actual helper keeps this
// regression test independent of React and the browser DOM.
new Function('exports', 'module', compiled)(module.exports, module);
const { unwrapLedgerTable, ledgerFilterSummary } = module.exports;

test('unwraps both direct and wrapped ledger SSE payloads', () => {
  const table = {
    filters: { customer: null, deviceType: null, yearFrom: null, yearTo: null },
    rows: [], amountVisible: false, note: '待核实地点条件', isCandidate: true,
  };
  assert.deepEqual(unwrapLedgerTable(table), table);
  assert.deepEqual(unwrapLedgerTable({ table }), table);
  assert.equal(unwrapLedgerTable({ table: { answer: 'summary' } }), null);
});

test('summarizes only parsed conditions and preserves location wording', () => {
  const summary = ledgerFilterSummary({
    customer: null, deviceType: null, yearFrom: null, yearTo: null,
    locationHint: '上海', keyword: '项目', deliveryStatus: 'Delivered', amountMin: 100000,
  });
  assert.match(summary, /地点条件=上海/);
  assert.match(summary, /关键词=项目/);
  assert.match(summary, /交付=已交付/);
  assert.match(summary, /金额≥100,000/);
  assert.doesNotMatch(summary, /最近记录/);
});
