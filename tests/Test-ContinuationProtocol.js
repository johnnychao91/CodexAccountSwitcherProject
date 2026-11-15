'use strict';
// Read a reviewed bundle and invoke its pure dispatcher with synthetic owners only.
const fs = require('fs'), vm = require('vm'), assert = require('assert'), crypto = require('crypto');
const profiles = [
  { version: '26.1027.3384.0', length: 674296148, hash: '6fbedf054a8d8cafaf5c745b40aa59176512e106e0bd6308275ae123b60fd074',
    symbols: { override: 'EL', path: 'ei', fs: 'eb', error: 'E5', options: 'E8', diagnostic: 'E4' } },
  { version: '26.1103.7773.0', length: 761033804, hash: 'ee88fdd77fb7346efb5e91ce73c7c0eee4eba390ffba3192635d5cdbe44e4097',
    symbols: { override: 'F2', path: 'ee', fs: 'ef', error: 'Wm', options: 'Wg', diagnostic: 'Wh' } },
  { version: '26.1106.6628.0', length: 772825694, hash: '54b74a9131680fc9eead711ed5d9f718f6127c22222ca60d2e3b577a707e2485',
    symbols: { override: 'WI', path: 'et', fs: 'eb', error: 'WL', options: 'WO', diagnostic: 'WJ' } },
  { version: '26.1111.2228.0', length: 783750654, hash: '2551cba1057a05cdc5cf5d75b146fac4092bff4ce77a10c43e3286c88d24c82d',
    symbols: { override: 'MA', path: 'eJ', fs: 'rt', error: 'MX', options: 'M0', diagnostic: 'MY' } }
];
async function main() {
  if (!process.argv[2]) throw Error('Pass the reviewed app.asar path.');
  const fd = fs.openSync(process.argv[2], 'r');
  try {
    const hash = crypto.createHash('sha256'), chunk = Buffer.alloc(65536); let position = 0, n;
    while ((n = fs.readSync(fd, chunk, 0, chunk.length, position)) > 0) { hash.update(chunk.subarray(0, n)); position += n; }
    const digest = hash.digest('hex');
    const profile = profiles.find(candidate => candidate.length === position && candidate.hash === digest);
    assert(profile, 'Unknown or modified package');
    const prefix = Buffer.alloc(8); fs.readSync(fd, prefix, 0, 8, 0);
    const size = prefix.readUInt32LE(4); assert(size > 8 && size < 16777216);
    const header = Buffer.alloc(size); fs.readSync(fd, header, 0, size, 8);
    const tree = JSON.parse(header.subarray(8, 8 + header.readUInt32LE(4)).toString('utf8')), scripts = [];
    function walk(node, path = '') { for (const [name, item] of Object.entries(node.files || {})) {
      const p = path + name;
      if (item.files) walk(item, p + '/');
      else if (p.startsWith('.vite/') && name.startsWith('early-bootstrap') && p.endsWith('.js') && !item.unpacked && item.size < 10000000) {
        const bytes = Buffer.alloc(item.size); assert.strictEqual(fs.readSync(fd, bytes, 0, bytes.length, 8 + size + Number(item.offset)), bytes.length); scripts.push(bytes.toString('utf8'));
      }
    } }
    walk(tree);
    const work = scripts.find(s => s.includes('async startOnce(){') && s.includes('CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH')); assert(work);
    const symbols = profile.symbols;
    const workStart = work.indexOf('async startOnce(){'), workEnd = work.indexOf('handleConnectionEvent(e,t){', workStart);
    assert(workEnd > workStart && workEnd - workStart < 10000);
    const errorStart = work.indexOf('class ' + symbols.error + ' extends Error{');
    const errorEnd = work.indexOf('function ' + symbols.options + '(', errorStart);
    assert(errorStart > 0 && errorEnd > errorStart);
    const NativeError = vm.runInNewContext('(' + work.slice(errorStart, errorEnd) + ')', {}, { timeout: 1000 });
    for (const kind of ['directory', 'missing', 'file']) {
      let registrations = 0, restarts = 0, registryOptions = 0;
      const native = vm.runInNewContext('({' + work.slice(workStart, workEnd) + '})', {
        process: { platform: 'win32', env: { CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH: 'synthetic-override' } },
        [symbols.override]: 'CODEX_TPP_LOCAL_EXECUTOR_CLI_PATH', [symbols.path]: { resolve: value => value },
        [symbols.fs]: { statSync() { if (kind === 'missing') throw Error('synthetic-missing'); return { isFile: () => kind === 'file' }; } },
        [symbols.error]: NativeError, [symbols.options]() { registryOptions++; return {}; }, [symbols.diagnostic]: () => 'synthetic-error'
      }, { timeout: 1000 });
      const executor = { lifetime: { signal: { aborted: false } }, attempt: 0, state: {}, disposed: false, enabled: true, options: {},
        invalidateReadiness() {}, resetThreadReadiness() {}, appendDiagnostic() {}, logger: () => ({ info() {}, warning() {} }),
        connectionObservations: { processFailed() {} }, reportFailure() {}, scheduleRestart() { restarts++; },
        async registerExecutor() { registrations++; throw new NativeError('synthetic-stop-before-network', false); } };
      await native.startOnce.call(executor);
      assert.strictEqual(registrations, kind === 'file' ? 1 : 0); assert.strictEqual(registryOptions, kind === 'file' ? 1 : 0);
      assert.strictEqual(restarts, 0); assert.strictEqual(executor.state.ready, false);
      if (kind !== 'file') assert(executor.state.error.includes('must point to an executable file'));
    }
    const text = scripts.find(s => s.includes('"thread-owner-discovery":1,"thread-follower-start-turn":2')); assert(text);
    const point = text.indexOf('"thread-owner-discovery":1,"thread-follower-start-turn":2');
    const begin = text.lastIndexOf('={', point) + 1, end = text.indexOf('};function ', point) + 1; assert(begin > 0 && end > begin);
    const versions = vm.runInNewContext('(' + text.slice(begin, end) + ')', {}, { timeout: 1000 });
    assert.strictEqual(versions['thread-owner-discovery'], 1); assert.strictEqual(versions['thread-follower-interrupt-turn'], 4); assert.strictEqual(versions['thread-follower-start-turn'], 2);
    const source = scripts.find(s => s.includes('case"thread-follower-interrupt-turn":try{')); assert(source);
    const at = source.indexOf('case"thread-follower-interrupt-turn":try{');
    const starts = [...source.slice(0, at).matchAll(/async function(?: [\w$]+)?\(e,t\)\{switch\(e\.assertThreadFollowerOwner\(/g)];
    assert(starts.length);
    const start = starts.at(-1).index;
    const anonymous = source.startsWith('async function(e,t)', start);
    const dispatchEnd = anonymous ? source.indexOf('}(this,e)}getDefaultFeatureOverride(', at) + 1 :
      at + source.slice(at).search(/async function [\w$]+\(/);
    assert(dispatchEnd > at);
    const dispatch = vm.runInNewContext('(' + source.slice(start, dispatchEnd) + ')', {}, { timeout: 1000 });
    let interrupts = 0, sends = 0;
    const turnStart = { request: { threadId: 'synthetic-thread', input: [{ type: 'text', text: '繼續執行' }], clientUserMessageId: 'synthetic-operation', approvalPolicy: 'never', sandboxPolicy: { type: 'readOnly' } },
      context: { useAppServerPermissionDefault: false, inheritThreadSettings: false, writingBlockContextPrepared: true } };
    const owner = { assertThreadFollowerOwner(id) { assert.strictEqual(id, 'synthetic-thread'); },
      async interruptConversation(id, mode, expected) { interrupts++; assert.strictEqual(mode, 'system'); assert.strictEqual(expected, 'original-turn'); return expected; },
      async startTurn(id, value) { sends++; assert.strictEqual(value, turnStart); return { turn: { id: 'new-turn' } }; } };
    const interrupted = await dispatch(owner, { method: 'thread-follower-interrupt-turn', params: { conversationId: 'synthetic-thread', mode: 'system', expectedTurnId: 'original-turn' } });
    assert.strictEqual(interrupted.result.interruptedTurnId, 'original-turn'); assert.strictEqual(interrupted.result.ok, true); assert.strictEqual(interrupts, 1);
    const request = { method: 'thread-follower-start-turn', params: { conversationId: 'synthetic-thread', turnStart } };
    const continued = await dispatch(owner, request); assert.strictEqual(continued.result.result.turn.id, 'new-turn'); assert.strictEqual(sends, 1);
    await assert.rejects(dispatch({ ...owner, assertThreadFollowerOwner() { throw Error('synthetic-owner-change'); } }, request)); assert.strictEqual(sends, 1);
    await assert.rejects(dispatch({ ...owner, async startTurn() { sends++; throw Error('synthetic-lost-reply'); } }, request)); assert.strictEqual(sends, 2);
    console.log(JSON.stringify({ passed: 8, reviewedPackage: profile.version, actualBundleDispatch: true, localWorkStartupVerified: true, syntheticOnly: true, appStarted: false }));
  } finally { fs.closeSync(fd); }
}
main().catch(error => { console.error('Reviewed continuation protocol test failed:', error.name, error.message); process.exitCode = 1; });
