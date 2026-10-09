import assert from 'node:assert/strict';
import { webcrypto } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import ts from 'typescript';

const sourceRoot = fileURLToPath(new URL('../src/', import.meta.url));
const uuidV4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

// HTTP LAN origins expose getRandomValues, but not randomUUID. Every fetch is
// intercepted here; these tests never contact the server or the network.
function browser({ storage = new Map(), crypto = {
  getRandomValues: (bytes) => webcrypto.getRandomValues(bytes),
} } = {}) {
  const requests = [];
  const context = vm.createContext({
    isSecureContext: false,
    crypto,
    Headers,
    localStorage: {
      getItem: (key) => storage.get(key) ?? null,
      setItem: (key, value) => storage.set(key, String(value)),
      removeItem: (key) => storage.delete(key),
    },
    fetch: async (url, init) => {
      requests.push({ url, ...init });
      return Response.json({ token: 'test-token' });
    },
  });
  const modules = new Map();

  function load(filename) {
    assert.ok(filename.startsWith(sourceRoot), 'Only local source modules may be loaded');
    if (modules.has(filename)) return modules.get(filename).exports;
    const module = { exports: {} };
    modules.set(filename, module);
    const { outputText } = ts.transpileModule(readFileSync(filename, 'utf8'), {
      compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
    });
    const run = new vm.Script(`(function (exports, require, module) {\n${outputText}\n})`, {
      filename,
    }).runInContext(context);
    run(module.exports, (specifier) => {
      assert.ok(specifier.startsWith('.'), 'External imports are not allowed');
      return load(path.resolve(path.dirname(filename), `${specifier}.ts`));
    }, module);
    return module.exports;
  }

  return {
    storage,
    requests,
    load: (relativePath) => load(path.resolve(sourceRoot, relativePath)),
  };
}

test('HTTP origin generates valid UUIDs without crypto.randomUUID', () => {
  const { randomUuid } = browser().load('lib/id.ts');
  const first = randomUuid();
  const second = randomUuid();
  assert.match(first, uuidV4);
  assert.match(second, uuidV4);
  assert.notEqual(first, second);
});

test('first HTTP visit persists its terminal ID across calls and page reloads', () => {
  const firstPage = browser();
  const api = firstPage.load('lib/api.ts');
  assert.equal(firstPage.storage.get('ht.terminal'), undefined);
  const terminalId = api.terminalId();
  assert.match(terminalId, /^web-.+/);
  assert.equal(firstPage.storage.get('ht.terminal'), terminalId);
  assert.equal(api.terminalId(), terminalId);

  const nextPage = browser({ storage: firstPage.storage, crypto: {
    getRandomValues() { throw new Error('An existing terminal ID must be reused'); },
  } });
  assert.equal(nextPage.load('lib/api.ts').terminalId(), terminalId);
});

test('HTTP login reaches fetch with the same terminal ID in its body and header', async () => {
  const page = browser();
  const api = page.load('lib/api.ts');
  const terminalId = api.terminalId();
  const response = await api.post('/api/auth/login', {
    username: 'test-user', password: 'test-only', terminalId,
  });

  assert.equal(response.token, 'test-token');
  assert.equal(page.requests.length, 1);
  const request = page.requests[0];
  assert.equal(request.url, '/api/auth/login');
  assert.equal(request.method, 'POST');
  assert.equal(request.headers.get('Content-Type'), 'application/json');
  assert.equal(request.headers.get('X-Terminal-Id'), terminalId);
  assert.equal(JSON.parse(request.body).terminalId, terminalId);
  assert.equal(page.storage.get('ht.terminal'), terminalId);
});

test('uses native randomUUID when available', () => {
  const expected = '01234567-89ab-4cde-8fab-0123456789ab';
  let nativeCalls = 0;
  const { randomUuid } = browser({ crypto: {
    randomUUID() { nativeCalls++; return expected; },
    getRandomValues() { throw new Error('Native randomUUID should take precedence'); },
  } }).load('lib/id.ts');
  assert.equal(randomUuid(), expected);
  assert.equal(nativeCalls, 1);
});
