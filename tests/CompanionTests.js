'use strict';
// Independent, synthetic review harness: no real VS Code API, files, processes, or pipes.
const fs = require('fs');
const vm = require('vm');
const assert = require('assert');
const EventEmitter = require('events');
const path = require('path').win32;
const sourcePath = process.argv[2] || require('path').join(__dirname, '..', 'integrations', 'vscode', 'extension.js');
const source = fs.readFileSync(sourcePath, 'utf8');
const instance = 'a'.repeat(32);
const syntheticRoot = path.join('fixture', 'synthetic-profile');
function harness(previous, options = {}) {
  let configured = previous;
  let saved;
  const commands = [], calls = [];
  const context = { subscriptions: [], globalState: {
    get() { return saved; }, async update(key, value) { saved = value; }
  }};
  const configuration = {
    get(key) { return key === 'cliExecutable' ? configured : false; },
    inspect() { return { globalValue: configured }; },
    async update(key, value) { configured = value; }
  };
  const catalog = { ProtocolVersion: 1, Kind: 'vscode-stdio', OwnerPid: options.ownerPid || 123,
    InstanceId: instance, Generation: instance, PipeName: 'CodexAccountSwitcher.vscode.' + instance,
    State: 'quiesced', ResumeRequested: true };
  const vscode = {
    env: { remoteName: options.remoteName },
    ConfigurationTarget: { Global: 1 },
    workspace: { getConfiguration() { return configuration; } },
    extensions: { getExtension() { return { extensionPath: path.join(syntheticRoot, 'extension') }; } },
    commands: {
      registerCommand() { return { dispose() {} }; },
      async executeCommand(command) { commands.push(command); }
    },
    window: {
      async showOpenDialog() { return [{ fsPath: path.join(syntheticRoot, 'ManagedBridge.exe') }]; },
      async showWarningMessage() { return options.cancelEnable ? undefined : 'Enable'; },
      async showInformationMessage() { return undefined; },
      showErrorMessage() {}
    }
  };
  const childProcess = { execFile(file, args, opts, callback) {
    calls.push(args[0]);
    if (args[0] === '--check-resume' && options.beforeVerifyReturns) options.beforeVerifyReturns(context);
    setImmediate(() => callback(null));
  }};
  const net = { createConnection() {
    const socket = new EventEmitter();
    socket.setEncoding = socket.setTimeout = () => socket;
    socket.destroy = () => {};
    socket.write = line => {
      const request = JSON.parse(line);
      const result = { instanceId: instance, generation: instance, ownerAlive: true, state: 'quiesced',
        known: true, idle: true, resumeRequested: true, requiresReload: true };
      setImmediate(() => socket.emit('data', JSON.stringify({ id: request.id, result }) + '\n'));
    };
    setImmediate(() => socket.emit('connect'));
    return socket;
  }};
  const fakeFs = {
    existsSync() { return true; },
    lstatSync() { return { isFile: () => true, isSymbolicLink: () => false, size: 200 }; },
    readdirSync() { return [instance + '.json']; },
    readFileSync() { return JSON.stringify(catalog); }
  };
  const sandbox = { module: { exports: {} }, process: { platform: 'win32', pid: 123, env: { LOCALAPPDATA: syntheticRoot } },
    setInterval() { return 1; }, clearInterval() {}, setTimeout, clearTimeout, console,
    require(name) { return ({ vscode, fs: fakeFs, path, net, crypto: require('crypto'), child_process: childProcess })[name]; }
  };
  vm.runInNewContext(source + '\nmodule.exports.review = { enable, disable, poll };', sandbox);
  sandbox.module.exports.activate(context);
  return { api: sandbox.module.exports.review, context, commands, calls,
    get configured() { return configured; }, get saved() { return saved; } };
}
(async () => {
  let count = 0;
  for (const previous of [undefined, null, path.join(syntheticRoot, 'custom-codex.exe')]) {
    const h = harness(previous);
    await h.api.enable(h.context); await h.api.enable(h.context); await h.api.disable(h.context);
    assert.strictEqual(h.configured, previous);
    assert.strictEqual(h.commands.length, 0);
    count++;
  }
  {
    const h = harness(undefined, { cancelEnable: true }); await h.api.enable(h.context);
    assert.strictEqual(h.calls.length, 0); assert.strictEqual(h.configured, undefined); count++;
  }
  {
    const h = harness(undefined, { remoteName: 'ssh-remote' }); await h.api.enable(h.context);
    assert.strictEqual(h.calls.length, 0); count++;
  }
  {
    const h = harness(undefined, { ownerPid: 999 }); await h.api.enable(h.context); await h.api.poll(h.context);
    assert.strictEqual(h.commands.length, 0); assert(!h.calls.includes('--check-resume')); count++;
  }
  {
    const h = harness(undefined, { beforeVerifyReturns: context => context.globalState.update('ignored', { enabled: false }) });
    await h.api.enable(h.context); await h.api.poll(h.context);
    assert.strictEqual(h.commands.length, 0); count++;
  }
  {
    const h = harness(undefined, { beforeVerifyReturns: context => context.subscriptions.at(-1).dispose() });
    await h.api.enable(h.context); await h.api.poll(h.context);
    assert.strictEqual(h.commands.length, 0); count++;
  }
  {
    const h = harness(undefined); await h.api.enable(h.context); await h.api.poll(h.context); await h.api.poll(h.context);
    assert.deepStrictEqual(h.commands, ['workbench.action.reloadWindow']); count++;
  }
  console.log(JSON.stringify({ syntheticOnly: true, passed: count, realFilesOrProcessesModified: false }));
})().catch(error => { console.error(error.stack); process.exitCode = 1; });
