'use strict';
// Read a reviewed bundle and invoke its pure dispatcher with synthetic owners only.
const fs = require('fs'), vm = require('vm'), assert = require('assert'), crypto = require('crypto');
async function main() {
  if (!process.argv[2]) throw Error('Pass the reviewed app.asar path.');
  const fd = fs.openSync(process.argv[2], 'r');
  try {
    const hash = crypto.createHash('sha256'), chunk = Buffer.alloc(65536); let position = 0, n;
    while ((n = fs.readSync(fd, chunk, 0, chunk.length, position)) > 0) { hash.update(chunk.subarray(0, n)); position += n; }
    assert.strictEqual(position, 674296148);
    assert.strictEqual(hash.digest('hex'), '6fbedf054a8d8cafaf5c745b40aa59176512e106e0bd6308275ae123b60fd074');
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
    const text = scripts.find(s => s.includes('"thread-owner-discovery":1,"thread-follower-start-turn":2')); assert(text);
    const point = text.indexOf('"thread-owner-discovery":1,"thread-follower-start-turn":2');
    const begin = text.lastIndexOf('={', point) + 1, end = text.indexOf('};function ', point) + 1; assert(begin > 0 && end > begin);
    const versions = vm.runInNewContext('(' + text.slice(begin, end) + ')', {}, { timeout: 1000 });
    assert.strictEqual(versions['thread-owner-discovery'], 1); assert.strictEqual(versions['thread-follower-interrupt-turn'], 4); assert.strictEqual(versions['thread-follower-start-turn'], 2);
    const source = scripts.find(s => s.includes('case"thread-follower-interrupt-turn":try{')); assert(source);
    const at = source.indexOf('case"thread-follower-interrupt-turn":try{');
    const starts = [...source.slice(0, at).matchAll(/async function [\w$]+\(e,t\)\{/g)]; assert(starts.length);
    const start = starts.at(-1).index, next = source.slice(start + 20).search(/async function [\w$]+\(/); assert(next > 0);
    const dispatch = vm.runInNewContext('(' + source.slice(start, start + 20 + next) + ')', {}, { timeout: 1000 });
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
    console.log(JSON.stringify({ passed: 5, reviewedPackage: '26.1027.3384.0', actualBundleDispatch: true, syntheticOnly: true, appStarted: false }));
  } finally { fs.closeSync(fd); }
}
main().catch(() => { console.error('Reviewed continuation protocol test failed; no App requests were sent.'); process.exitCode = 1; });
