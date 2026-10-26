'use strict';
const vscode = require('vscode');
const fs = require('fs');
const path = require('path');
const net = require('net');
const crypto = require('crypto');

const integrationRoot = () => path.join(process.env.LOCALAPPDATA, 'CodexAccountSwitcher', 'integrations', 'vscode');
const settingsKey = 'codexAccountSwitcher.integration';
const consumed = new Set();
let polling = false;
let disposed = false;

function readJson(file) {
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 65536) throw new Error('Invalid integration file.');
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function samePath(left, right) {
  return typeof left === 'string' && typeof right === 'string' && path.resolve(left).toLowerCase() === path.resolve(right).toLowerCase();
}

function noLinks(directory) {
  let current = path.resolve(directory);
  for (;;) {
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error('Linked integration folders are not supported.');
    const parent = path.dirname(current);
    if (parent === current) return;
    current = parent;
  }
}

function requestStatus(catalog) {
  return new Promise((resolve, reject) => {
    const id = crypto.randomBytes(16).toString('hex');
    const socket = net.createConnection('\\\\.\\pipe\\' + catalog.PipeName);
    let buffer = '', finished = false;
    const finish = (error, result) => {
      if (finished) return; finished = true; socket.destroy();
      if (error) reject(error); else resolve(result);
    };
    socket.setEncoding('utf8'); socket.setTimeout(3000);
    socket.on('timeout', () => finish(new Error('Integration connection timed out.')));
    socket.on('error', error => finish(error));
    socket.on('end', () => { if (!finished) finish(new Error('Integration connection closed.')); });
    socket.on('connect', () => socket.write(JSON.stringify({ id, method: 'status', params: { expectedGeneration: catalog.Generation, timeoutMs: 1500 } }) + '\n'));
    socket.on('data', chunk => {
      buffer += chunk;
      if (buffer.length > 65536) return finish(new Error('Integration response exceeded its limit.'));
      const end = buffer.indexOf('\n'); if (end < 0) return;
      try {
        const response = JSON.parse(buffer.slice(0, end));
        if (response.id !== id || !response.result || response.error) throw new Error('Invalid integration response.');
        finish(null, response.result);
      } catch (error) { finish(error); }
    });
  });
}

function verifyResume(proxyPath, instance) {
  return new Promise((resolve, reject) => require('child_process').execFile(proxyPath, ['--check-resume', instance, String(process.pid)],
    { windowsHide: true, timeout: 6000 }, error => error ? reject(new Error('Resume could not be verified.')) : resolve()));
}

async function poll(context) {
  if (disposed || polling || process.platform !== 'win32' || vscode.env.remoteName || vscode.workspace.getConfiguration('chatgpt').get('runCodexInWindowsSubsystemForLinux')) return;
  const enabled = context.globalState.get(settingsKey);
  if (!enabled || enabled.enabled !== true) return;
  if (!samePath(vscode.workspace.getConfiguration('chatgpt').get('cliExecutable'), enabled.proxyPath)) return;
  polling = true;
  try {
    const directory = path.join(integrationRoot(), 'runtimes'); noLinks(directory);
    if (!fs.existsSync(directory)) return;
    const files = fs.readdirSync(directory).filter(name => /^[a-f0-9]{32}\.json$/.test(name));
    for (const name of files.slice(0, 256)) {
      let catalog;
      try { catalog = readJson(path.join(directory, name)); } catch { continue; }
      if (catalog.ProtocolVersion !== 1 || catalog.Kind !== 'vscode-stdio' || catalog.OwnerPid !== process.pid ||
          catalog.InstanceId !== name.slice(0, -5) || catalog.Generation !== catalog.InstanceId ||
          catalog.PipeName !== 'CodexAccountSwitcher.vscode.' + catalog.InstanceId ||
          catalog.State !== 'quiesced' || catalog.ResumeRequested !== true || consumed.has(catalog.InstanceId)) continue;
      // A live supervisor verifies the owner's creation time, avoiding a stale catalog after PID reuse.
      let status;
      try { status = await requestStatus(catalog); } catch { continue; }
      if (status.instanceId !== catalog.InstanceId || status.generation !== catalog.Generation || status.ownerAlive !== true ||
          status.state !== 'quiesced' || status.known !== true || status.idle !== true || status.resumeRequested !== true || status.requiresReload !== true) continue;
      try { await verifyResume(enabled.proxyPath, catalog.InstanceId); } catch { continue; }
      const current = context.globalState.get(settingsKey);
      if (disposed || !current || current.enabled !== true || !samePath(current.proxyPath, enabled.proxyPath) || vscode.env.remoteName ||
          vscode.workspace.getConfiguration('chatgpt').get('runCodexInWindowsSubsystemForLinux') ||
          !samePath(vscode.workspace.getConfiguration('chatgpt').get('cliExecutable'), enabled.proxyPath)) return;
      consumed.add(catalog.InstanceId);
      await vscode.commands.executeCommand('workbench.action.reloadWindow');
      return;
    }
  } catch { /* A missing, malformed, or stale catalog cannot authorize a reload. */ }
  finally { polling = false; }
}

async function enable(context) {
  if (process.platform !== 'win32' || vscode.env.remoteName) {
    vscode.window.showErrorMessage('This experimental integration currently supports only a local Windows VS Code window.'); return;
  }
  if (vscode.workspace.getConfiguration('chatgpt').get('runCodexInWindowsSubsystemForLinux')) {
    vscode.window.showErrorMessage('Disable this integration for WSL. It currently supports only the native Windows Codex backend.'); return;
  }
  const official = vscode.extensions.getExtension('openai.chatgpt');
  if (!official) { vscode.window.showErrorMessage('Install the official Codex extension first.'); return; }
  const selected = await vscode.window.showOpenDialog({ title: 'Select ManagedBridge.exe from the account switcher release', canSelectFiles: true, canSelectFolders: false, canSelectMany: false, filters: { 'Managed Bridge': ['exe'] } });
  if (!selected || selected.length !== 1) return;
  const proxyPath = path.resolve(selected[0].fsPath);
  if (path.basename(proxyPath).toLowerCase() !== 'managedbridge.exe') { vscode.window.showErrorMessage('Choose ManagedBridge.exe.'); return; }
  const realCliPath = path.join(official.extensionPath, 'bin', 'windows-x86_64', 'codex.exe');
  if (!fs.existsSync(realCliPath) || samePath(realCliPath, proxyPath)) { vscode.window.showErrorMessage('The bundled Windows Codex backend could not be located.'); return; }
  const choice = await vscode.window.showWarningMessage(
    'Enable the experimental local account-switcher bridge? Codex’s development-only executable setting has application scope and can affect other windows and profiles of this VS Code installation when they next start. Account switching may reload this managed window; unsent Codex drafts and extension state may be affected. Existing Codex work continues until you reload.',
    { modal: true }, 'Enable');
  if (choice !== 'Enable') return;
  const configuration = vscode.workspace.getConfiguration('chatgpt');
  const inspected = configuration.inspect('cliExecutable');
  const existing = context.globalState.get(settingsKey);
  const previous = existing && existing.enabled ? existing.previous : (inspected && inspected.globalValue);
  const previousWasUnset = existing && existing.enabled ? existing.previousWasUnset : previous === undefined;
  // Ask the local helper to create private directories and save local-only configuration.
  const childProcess = require('child_process');
  await new Promise((resolve, reject) => childProcess.execFile(proxyPath, ['--configure', realCliPath], { windowsHide: true }, (error) => error ? reject(new Error('Could not save the private integration configuration.')) : resolve()));
  await context.globalState.update(settingsKey, { enabled: true, proxyPath, previous: previous === undefined ? null : previous, previousWasUnset });
  try { await configuration.update('cliExecutable', proxyPath, vscode.ConfigurationTarget.Global); }
  catch (error) { await context.globalState.update(settingsKey, existing); throw error; }
  const reload = await vscode.window.showInformationMessage('Integration enabled. Reload this window when its Codex work has finished.', 'Reload Window');
  if (reload === 'Reload Window') await vscode.commands.executeCommand('workbench.action.reloadWindow');
}

async function disable(context) {
  const saved = context.globalState.get(settingsKey);
  if (!saved || !saved.enabled) { vscode.window.showInformationMessage('The integration is not enabled in this VS Code profile.'); return; }
  await new Promise((resolve, reject) => require('child_process').execFile(saved.proxyPath, ['--disable-control'], { windowsHide: true },
    error => error ? reject(new Error('Could not revoke integration control.')) : resolve()));
  const configuration = vscode.workspace.getConfiguration('chatgpt');
  if (samePath(configuration.get('cliExecutable'), saved.proxyPath))
    await configuration.update('cliExecutable', saved.previousWasUnset ? undefined : saved.previous, vscode.ConfigurationTarget.Global);
  await context.globalState.update(settingsKey, { enabled: false });
  const reload = await vscode.window.showInformationMessage('Integration control is revoked immediately for managed windows. Reload this window after its current work finishes; a paused Codex backend needs a reload to reconnect.', 'Reload Window');
  if (reload === 'Reload Window') await vscode.commands.executeCommand('workbench.action.reloadWindow');
}

function activate(context) {
  disposed = false;
  const safely = action => async () => { try { await action(context); } catch { vscode.window.showErrorMessage('The integration change could not be completed. Your existing VS Code window was not reloaded.'); } };
  context.subscriptions.push(vscode.commands.registerCommand('codexAccountSwitcher.enableIntegration', safely(enable)));
  context.subscriptions.push(vscode.commands.registerCommand('codexAccountSwitcher.disableIntegration', safely(disable)));
  const timer = setInterval(() => { void poll(context); }, 1500);
  context.subscriptions.push({ dispose() { disposed = true; clearInterval(timer); } });
}

module.exports = { activate };
