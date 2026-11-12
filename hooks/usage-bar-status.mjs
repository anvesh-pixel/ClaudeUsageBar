// Reports Claude Code session state to the Claude Usage Bar taskbar widget.
// Usage (from a hook): node usage-bar-status.mjs <working|input|done|end>
// Writes %LOCALAPPDATA%\ClaudeUsageBar\sessions\<session_id>.json. Always exits 0, never blocks Claude.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const state = process.argv[2];
const dir = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), '.local', 'share'), 'ClaudeUsageBar', 'sessions');

function write(raw) {
  try {
    let input = {};
    try { input = JSON.parse(raw || '{}'); } catch {}
    const sid = String(input.session_id || 'unknown').replace(/[^\w-]/g, '_');
    const file = path.join(dir, sid + '.json');
    if (state === 'end') { fs.rmSync(file, { force: true }); return; }
    fs.mkdirSync(dir, { recursive: true });
    const tmp = file + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify({ state, t: Date.now(), project: path.basename(input.cwd || '') }));
    fs.renameSync(tmp, file);
  } catch {}
}

let raw = '';
const timer = setTimeout(() => { write(raw); process.exit(0); }, 1500);
process.stdin.on('data', d => { raw += d; });
process.stdin.on('end', () => { clearTimeout(timer); write(raw); process.exit(0); });
process.stdin.on('error', () => { clearTimeout(timer); write(raw); process.exit(0); });
