import { describe, it, expect } from 'vitest';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Static checks for the private-repo SSH scripts (branch feat/private-repo-ssh).
// There is no PowerShell on the Mac this was built on (same constraint as
// tests/report-key-settings-merge.test.ts), so these tests read the .ps1
// source as text and assert the properties that matter most and are cheapest
// to get wrong: GitHub's pinned host keys really are GitHub's (fingerprints
// re-derived here), the three scripts use the one shared helper, git can never
// prompt, a fetch failure never stops the launch, install.ps1 stays thin and
// self-consistent with the helper, and no secret is ever embedded.

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(__dirname, '..');
const read = (rel: string) => readFileSync(path.join(root, rel), 'utf8');

const helper = read('scripts/GitSsh.ps1');
const install = read('scripts/install.ps1');
const updateAndRun = read('update-and-run.ps1');
const bootstrap = read('bootstrap-fresh.ps1');
const installShortcut = read('install-shortcut.ps1');
const allScripts: Record<string, string> = {
  'scripts/GitSsh.ps1': helper,
  'scripts/install.ps1': install,
  'update-and-run.ps1': updateAndRun,
  'bootstrap-fresh.ps1': bootstrap,
  'install-shortcut.ps1': installShortcut
};

// Published at
// https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/githubs-ssh-key-fingerprints
const GITHUB_FINGERPRINTS: Record<string, string> = {
  'ssh-ed25519': 'SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU',
  'ecdsa-sha2-nistp256': 'SHA256:p2QAMXNIC1TJYWeIOttrVc98/R1BUFWu3/LiyKgUfQM',
  'ssh-rsa': 'SHA256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s'
};

function fingerprint(base64Key: string): string {
  const digest = createHash('sha256').update(Buffer.from(base64Key, 'base64')).digest('base64').replace(/=+$/, '');
  return `SHA256:${digest}`;
}

function knownHostLines(source: string): Array<{ host: string; type: string; key: string }> {
  const re = /'(\[ssh\.github\.com\]:443) (ssh-ed25519|ecdsa-sha2-nistp256|ssh-rsa) ([A-Za-z0-9+/=]+)'/g;
  return [...source.matchAll(re)].map((m) => ({ host: m[1], type: m[2], key: m[3] }));
}

/**
 * Crude PowerShell structure check: skips comments, single-quoted and
 * double-quoted strings, then verifies {} () [] balance. Not a parser (none
 * available on macOS) - it exists to catch a dropped brace/quote in an edit.
 */
function structureProblems(source: string): string[] {
  const stack: string[] = [];
  const pairs: Record<string, string> = { '}': '{', ')': '(', ']': '[' };
  let i = 0;
  while (i < source.length) {
    const c = source[i];
    const next = source[i + 1];
    if (c === '<' && next === '#') {
      const end = source.indexOf('#>', i + 2);
      if (end < 0) return ['unterminated block comment'];
      i = end + 2;
    } else if (c === '#') {
      const end = source.indexOf('\n', i);
      i = end < 0 ? source.length : end;
    } else if (c === "'") {
      let j = i + 1;
      for (;;) {
        if (j >= source.length) return ['unterminated single-quoted string'];
        if (source[j] === "'" && source[j + 1] === "'") j += 2;
        else if (source[j] === "'") break;
        else j++;
      }
      i = j + 1;
    } else if (c === '"') {
      let j = i + 1;
      for (;;) {
        if (j >= source.length) return ['unterminated double-quoted string'];
        if (source[j] === '`') j += 2;
        else if (source[j] === '"' && source[j + 1] === '"') j += 2;
        else if (source[j] === '"') break;
        else j++;
      }
      i = j + 1;
    } else if (c === '{' || c === '(' || c === '[') {
      stack.push(c);
      i++;
    } else if (c === '}' || c === ')' || c === ']') {
      if (stack.pop() !== pairs[c]) return [`unbalanced ${c} near offset ${i}`];
      i++;
    } else {
      i++;
    }
  }
  return stack.length > 0 ? [`unclosed ${stack.join('')}`] : [];
}

describe('pinned GitHub SSH host keys', () => {
  for (const [name, source] of [['scripts/GitSsh.ps1', helper], ['scripts/install.ps1', install]] as const) {
    it(`${name} pins ed25519, ecdsa and rsa for [ssh.github.com]:443 matching GitHub's published fingerprints`, () => {
      const lines = knownHostLines(source);
      expect(lines.map((l) => l.type).sort()).toEqual(['ecdsa-sha2-nistp256', 'ssh-ed25519', 'ssh-rsa']);
      for (const line of lines) {
        expect(fingerprint(line.key)).toBe(GITHUB_FINGERPRINTS[line.type]);
      }
    });
  }

  it('the helper and the stage-0 installer carry byte-identical host key lines', () => {
    expect(knownHostLines(install)).toEqual(knownHostLines(helper));
  });
});

describe('shared SSH helper wiring', () => {
  it('all three launch scripts use scripts\\GitSsh.ps1 and the repo is addressed over ssh.github.com:443', () => {
    expect(updateAndRun).toContain('scripts\\GitSsh.ps1');
    expect(bootstrap).toContain('scripts\\GitSsh.ps1');
    expect(installShortcut).toContain('scripts\\GitSsh.ps1');
    expect(helper).toContain("ssh://git@ssh.github.com:443/elevatedev4/rx-verify.git");
    expect(install).toContain("ssh://git@ssh.github.com:443/elevatedev4/rx-verify.git");
  });

  it('core.sshCommand pins the key, the known_hosts file, strict host checking and port 443', () => {
    const fragments = [
      '-o IdentitiesOnly=yes',
      '-o BatchMode=yes',
      '-o ConnectTimeout=20',
      '-o "UserKnownHostsFile=\\"',
      '\\"" -o StrictHostKeyChecking=yes -p 443'
    ];
    for (const f of fragments) {
      expect(helper).toContain(f);
      expect(install).toContain(f); // install.ps1 builds the same string for its first clone
    }
    expect(helper).not.toMatch(/StrictHostKeyChecking=(no|accept-new)/);
    expect(install).not.toMatch(/StrictHostKeyChecking=(no|accept-new)/);
  });

  it('the helper talks to exactly the contract endpoints with Bearer auth', () => {
    expect(helper).toContain("'/api/rxverify-update-key'");
    expect(helper).toContain("'/api/rxverify-update-key/confirm'");
    expect(helper).toContain('Authorization = "Bearer $ReportKey"');
    expect(helper).toContain('hostname = $env:COMPUTERNAME; commit = $sha');
    expect(install).toContain('https://manager-hq.vercel.app/api/rxverify-update-key');
  });
});

describe('update-and-run.ps1 behaviour', () => {
  it('disables git prompts before the fetch and migrates before the fetch', () => {
    const fetchIdx = updateAndRun.indexOf('git fetch origin }');
    const promptIdx = updateAndRun.indexOf("$env:GIT_TERMINAL_PROMPT = '0'");
    const gcmIdx = updateAndRun.indexOf("$env:GCM_INTERACTIVE = 'never'");
    const migrateIdx = updateAndRun.indexOf('Invoke-RxVerifySshMigration');
    expect(fetchIdx).toBeGreaterThan(0);
    expect(promptIdx).toBeGreaterThan(0);
    expect(promptIdx).toBeLessThan(fetchIdx);
    expect(gcmIdx).toBeLessThan(fetchIdx);
    expect(migrateIdx).toBeGreaterThan(promptIdx);
    expect(migrateIdx).toBeLessThan(fetchIdx);
  });

  it('a failed fetch launches the last good build instead of Stop-WithMessage', () => {
    const afterFetch = updateAndRun.slice(updateAndRun.indexOf('git fetch origin }'));
    const failBranch = afterFetch.slice(0, afterFetch.indexOf('$checkoutOutput'));
    expect(failBranch).toContain('Start-LastGoodBuildAndExit');
    expect(failBranch).not.toContain('Stop-WithMessage');
    // ...and only a missing build is a real stop:
    const fn = updateAndRun.slice(updateAndRun.indexOf('function Start-LastGoodBuildAndExit'));
    expect(fn.slice(0, 900)).toContain('there is no previous build');
  });

  it('a migration failure never blocks: the call is wrapped in try/catch and the helper swallows errors', () => {
    expect(updateAndRun).toMatch(/try \{\s*\. \$gitSshHelperPath[\s\S]*?Invoke-RxVerifySshMigration[\s\S]*?\} catch \{/);
    const fn = helper.slice(helper.indexOf('function Invoke-RxVerifySshMigration'), helper.indexOf('function Invoke-RxVerifyClone'));
    expect(fn).toContain('} catch {');
    // origin is only switched after the ls-remote test passes
    expect(fn.indexOf('Test-RxVerifySshAccess')).toBeGreaterThan(0);
    expect(fn.indexOf("'set-url', 'origin'")).toBeGreaterThan(fn.indexOf('Test-RxVerifySshAccess -RepoPath $RepoPath))'));
    // the confirm POST comes after the switch
    expect(fn.lastIndexOf('Send-RxVerifyMigrationConfirm')).toBeGreaterThan(fn.indexOf("'set-url', 'origin'"));
  });
});

describe('stage-0 installer and secrets hygiene', () => {
  it('scripts/install.ps1 stays thin (<= 150 lines) and exits via throw, never exit', () => {
    expect(install.split('\n').length).toBeLessThanOrEqual(150);
    const code = install.replace(/<#[\s\S]*?#>/, '');
    expect(code).not.toMatch(/^\s*exit\b/m);
  });

  it('install.ps1 skips the clone when a checkout exists and hands off to the clone\'s bootstrap-fresh.ps1', () => {
    expect(install).toContain("Test-Path (Join-Path $repoPath '.git')");
    expect(install).toContain("bootstrap-fresh.ps1') -ReportKey \"$ReportKey\"");
  });

  it('bootstrap-fresh.ps1 still skips cloning when the launcher script already exists', () => {
    expect(bootstrap).toContain('if (Test-Path $LauncherScriptPath) {');
    expect(bootstrap.indexOf('if (Test-Path $LauncherScriptPath) {')).toBeLessThan(bootstrap.indexOf('Invoke-RxVerifyClone'));
  });

  it('no private key material, report key or HQ secret is embedded in any script', () => {
    for (const [name, source] of Object.entries(allScripts)) {
      // The BEGIN/END markers appear only as -match validators; never followed by a key body.
      expect(source, name).not.toMatch(/BEGIN OPENSSH PRIVATE KEY-----\s*[A-Za-z0-9+/=]{20,}/);
      expect(source, name).not.toMatch(/PRIVATE KEY-----\s*\n\s*[A-Za-z0-9+/=]{40,}/);
      expect(source, name).not.toMatch(/ReportKey\s*=\s*'[A-Za-z0-9_-]{16,}'/);
    }
  });

  it('the helper never writes the key, the Authorization header or the report key to output', () => {
    // Every string literal handed to a log/Write-Host call (the message text itself).
    const messages = [...helper.matchAll(/(?:Write-RxSshLog|Write-Host)\s+("(?:[^"\\]|\\.)*"|'[^']*')/g)].map((m) => m[1]);
    expect(messages.length).toBeGreaterThan(10);
    for (const m of messages) {
      expect(m).not.toMatch(/\$keyText|\$key\b|\$resp\b|\$ReportKey|Authorization|\$head\b/);
    }
  });
});

describe('PowerShell structure sanity (no pwsh on macOS)', () => {
  for (const [name, source] of Object.entries(allScripts)) {
    it(`${name} has balanced braces, parens, brackets and quotes`, () => {
      expect(structureProblems(source)).toEqual([]);
    });
  }

  it('the structure checker itself catches a dropped brace', () => {
    expect(structureProblems('function X { if ($a) { 1 }')).not.toEqual([]);
    expect(structureProblems("$s = 'a''b'; if ($x) { \"q`\"r\" }")).toEqual([]);
  });
});
