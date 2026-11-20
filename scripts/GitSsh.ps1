<#
.SYNOPSIS
    Shared "talk to GitHub over SSH with the read-only deploy key" helper.
    DOT-SOURCED (. "$PSScriptRoot\scripts\GitSsh.ps1") by update-and-run.ps1,
    bootstrap-fresh.ps1 and install-shortcut.ps1 - the ONE place the key
    fetch, known_hosts pinning, core.sshCommand and migration logic live.

.DESCRIPTION
    Why: the rx-verify GitHub repo is going private. Pharmacy PCs can no longer
    read it anonymously, so each PC fetches a read-only deploy key from Manager
    HQ using the RxVerifyReportKey it already holds:

        GET  https://manager-hq.vercel.app/api/rxverify-update-key
             Authorization: Bearer <report key>   -> 200 {"key":"<OpenSSH private key>"}
        POST https://manager-hq.vercel.app/api/rxverify-update-key/confirm
             same auth, {"hostname":"..","commit":".."} -> 204

    The key is saved under %LocalAppData%\RxVerifyOverlay\ (current user only,
    via icacls) and git is pointed at it with a repo-local core.sshCommand
    over port 443 (ssh.github.com), with GitHub's published host keys PINNED
    in a known_hosts file written next to it (StrictHostKeyChecking=yes - a
    wrong host key is a hard failure, never a prompt).

    Every function here is best-effort-safe: nothing throws except
    Invoke-RxVerifyClone (callers need to know a clone failed). The key text
    is never printed, logged or placed on a command line.

    PowerShell 5.1 compatible on purpose (no ternary, ??, &&/||).
    SYNTHETIC DATA ONLY: this file contains no secrets - the host keys below
    are GitHub's PUBLIC host keys (https://api.github.com/meta "ssh_keys";
    fingerprints published at
    https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/githubs-ssh-key-fingerprints).
#>

function Get-RxVerifyHqBase { return 'https://manager-hq.vercel.app' }
function Get-RxVerifySshUrl { return 'ssh://git@ssh.github.com:443/elevatedev4/rx-verify.git' }
function Get-RxVerifyHttpsUrl { return 'https://github.com/elevatedev4/rx-verify.git' }
function Get-RxVerifyKeyDir { return (Join-Path $env:LOCALAPPDATA 'RxVerifyOverlay') }
function Get-RxVerifyKeyPath { return (Join-Path (Get-RxVerifyKeyDir) 'deploy_key') }
function Get-RxVerifyKnownHostsPath { return (Join-Path (Get-RxVerifyKeyDir) 'known_hosts') }

function Write-RxSshLog {
    param([string]$Message)
    Write-Host "  [ssh] $Message" -ForegroundColor DarkGray
}

# GitHub's published SSH host keys (ed25519, ecdsa, rsa). Fingerprints:
#   ED25519 SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU
#   ECDSA   SHA256:p2QAMXNIC1TJYWeIOttrVc98/R1BUFWu3/LiyKgUfQM
#   RSA     SHA256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s
# tests/git-ssh-scripts.test.ts re-derives these fingerprints from the lines
# below, so a typo here fails the test suite.
function Get-RxVerifyKnownHostsText {
    $lines = @(
        '[ssh.github.com]:443 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl',
        '[ssh.github.com]:443 ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBEmKSENjQEezOmxkZMy7opKgwFB9nkt5YRrYMjNuG5N87uRgg6CLrbo5wAdT/y6v0mKV0U2w0WZ2YB/++Tpockg=',
        '[ssh.github.com]:443 ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQCj7ndNxQowgcQnjshcLrqPEiiphnt+VTTvDP6mHBL9j1aNUkY4Ue1gvwnGLVlOhGeYrnZaMgRK6+PKCUXaDbC7qtbW8gIkhL7aGCsOr/C56SJMy/BCZfxd1nWzAOxSDPgVsmerOBYfNqltV9/hWCqBywINIR+5dIg6JTJ72pcEpEjcYgXkE2YEFXV1JHnsKgbLWNlhScqb2UmyRkQyytRLtL+38TGxkxCflmO+5Z8CSSNY7GidjMIZ7Q4zMjA2n1nGrlTDkzwDCsw+wqFPGQA179cnfGWOWRVruj16z6XyvxvjJwbz0wQZ75XK5tKSb7FNyeIEs4TT4jk+S4dhPeAUC5y+bDYirYgM4GC7uEnztnZyaVWQ7B381AK4Qdrwt51ZqExKbQpTUNn+EjqoTwvqNj4kqx5QUCI0ThS/YkOxJCXmPUWZbhjpCg56i+2aB6CmK2JGhn57K5mj0MNdBXA4/WnwH6XoPWJzK5Nyu2zB3nAZp+S5hpQs+p1vN1/wsjk='
    )
    return (($lines -join "`n") + "`n")
}

# The exact value stored in the repo's core.sshCommand. Forward slashes so
# git's sh never sees a backslash; -o UserKnownHostsFile gets an inner
# escaped quote pair so ssh itself sees a quoted path (a path with a space,
# eg. a user profile named "Front Desk", would otherwise be split into two
# known_hosts files and the pinned check would silently not apply).
function Get-RxVerifySshCommand {
    $key = (Get-RxVerifyKeyPath) -replace '\\', '/'
    $hosts = (Get-RxVerifyKnownHostsPath) -replace '\\', '/'
    return ('ssh -i "' + $key + '" -o IdentitiesOnly=yes -o BatchMode=yes -o ConnectTimeout=20 -o "UserKnownHostsFile=\"' + $hosts + '\"" -o StrictHostKeyChecking=yes -p 443')
}

# Windows PowerShell 5.1 does not escape embedded double quotes when it
# builds a native command line; PowerShell 7.3+ does. Pre-escape for 5.1 only
# (standard CommandLineToArgvW rules: double the backslashes before a quote).
function ConvertTo-RxNativeArg {
    param([string]$Value)
    if ($PSVersionTable.PSVersion.Major -ge 7) { return $Value }
    return [regex]::Replace($Value, '(\\*)"', '$1$1\"')
}

# No credential prompts, ever: a PC with no network/auth must fail fast, not
# hang a pharmacist's launch on an invisible Git Credential Manager dialog.
function Set-RxVerifyGitNoPrompt {
    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GCM_INTERACTIVE = 'never'
}

# Runs git, returning @{ Exit = <int>; Output = <string[]> }. stderr is merged
# into Output; EAP is relaxed locally so git's progress text on stderr can't
# trip 'Stop' into a fake NativeCommandError (same reason update-and-run.ps1
# has Invoke-NativeCapture).
function Invoke-RxGit {
    param([string]$RepoPath, [string[]]$GitArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ([string]::IsNullOrEmpty($RepoPath)) {
            $out = & git @GitArgs 2>&1
        } else {
            $out = & git -C $RepoPath @GitArgs 2>&1
        }
        $code = $LASTEXITCODE
        $lines = @($out | ForEach-Object { "$_" })
        return @{ Exit = $code; Output = $lines }
    } catch {
        return @{ Exit = 1; Output = @("$($_.Exception.Message)") }
    } finally {
        $ErrorActionPreference = $prev
    }
}

function Get-RxVerifyReportKeyFromSettings {
    try {
        $path = Join-Path $env:APPDATA 'RxVerifyOverlay\settings.json'
        if (-not (Test-Path $path)) { return '' }
        $obj = (Get-Content -Path $path -Raw -ErrorAction Stop) | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $obj) { return '' }
        $prop = $obj.PSObject.Properties['RxVerifyReportKey']
        if (($null -eq $prop) -or ($null -eq $prop.Value)) { return '' }
        return ([string]$prop.Value).Trim()
    } catch {
        return ''
    }
}

function Set-RxVerifyOwnerOnlyAcl {
    param([string]$Path)
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $null = & icacls.exe $Path /inheritance:r /grant:r "${me}:F" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "icacls could not restrict $Path to the current user (exit code $LASTEXITCODE)." }
}

# GET the deploy key from HQ. Returns the key text, or $null on ANY failure
# (no key, 401, 503, offline, malformed). Never logs the key or the header.
function Get-RxVerifyDeployKeyText {
    param([string]$ReportKey)
    if ([string]::IsNullOrWhiteSpace($ReportKey)) { return $null }
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $resp = Invoke-RestMethod -Uri ((Get-RxVerifyHqBase) + '/api/rxverify-update-key') -Method Get -Headers @{ Authorization = "Bearer $ReportKey" } -TimeoutSec 20
        $key = [string]$resp.key
        if (($key -match '-----BEGIN OPENSSH PRIVATE KEY-----') -and ($key -match '-----END OPENSSH PRIVATE KEY-----')) { return $key }
        Write-RxSshLog 'HQ answered, but not with a usable deploy key.'
    } catch {
        Write-RxSshLog "Could not fetch the deploy key from HQ: $($_.Exception.Message)"
    }
    return $null
}

# Fetches the key and writes key + pinned known_hosts. Returns $true when both
# files are in place. The key file is created EMPTY and locked to the current
# user BEFORE the key text is written into it.
function Install-RxVerifyDeployKey {
    param([string]$ReportKey)
    try {
        $keyText = Get-RxVerifyDeployKeyText -ReportKey $ReportKey
        if ($null -eq $keyText) { return $false }

        $dir = Get-RxVerifyKeyDir
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $enc = New-Object System.Text.UTF8Encoding($false)

        $keyPath = Get-RxVerifyKeyPath
        if (-not (Test-Path $keyPath)) { [System.IO.File]::WriteAllText($keyPath, '', $enc) }
        Set-RxVerifyOwnerOnlyAcl -Path $keyPath
        # LF endings + trailing newline: OpenSSH rejects a CRLF key as "invalid format".
        [System.IO.File]::WriteAllText($keyPath, (($keyText -replace "`r`n", "`n").Trim() + "`n"), $enc)

        [System.IO.File]::WriteAllText((Get-RxVerifyKnownHostsPath), (Get-RxVerifyKnownHostsText), $enc)
        Write-RxSshLog 'Deploy key and pinned GitHub host keys saved (current user only).'
        return $true
    } catch {
        Write-RxSshLog "Could not save the deploy key: $($_.Exception.Message)"
        return $false
    }
}

function Set-RxVerifyRepoSshConfig {
    param([string]$RepoPath)
    $want = Get-RxVerifySshCommand
    $null = Invoke-RxGit -RepoPath $RepoPath -GitArgs @('config', 'core.sshCommand', (ConvertTo-RxNativeArg $want))
    $got = Invoke-RxGit -RepoPath $RepoPath -GitArgs @('config', '--get', 'core.sshCommand')
    return (($got.Exit -eq 0) -and ((($got.Output -join '').Trim()) -ceq $want))
}

# True if `git ls-remote <ssh url> refs/heads/main` works with the repo's own
# (persisted) core.sshCommand - so this tests the real configuration.
function Test-RxVerifySshAccess {
    param([string]$RepoPath)
    $r = Invoke-RxGit -RepoPath $RepoPath -GitArgs @('ls-remote', (Get-RxVerifySshUrl), 'refs/heads/main')
    if ($r.Exit -ne 0) { return $false }
    return [bool](@($r.Output) -match '^[0-9a-f]{40,64}\s+refs/heads/main\s*$')
}

# POST the "this PC migrated" confirmation to HQ. Best effort; remembers
# success in .launcher-cache\ssh-confirmed so it is retried on later launches
# until HQ has actually received it, then never repeated.
function Send-RxVerifyMigrationConfirm {
    param([string]$RepoPath, [string]$ReportKey)
    try {
        $marker = Join-Path $RepoPath '.launcher-cache\ssh-confirmed'
        if (Test-Path $marker) { return }
        $head = Invoke-RxGit -RepoPath $RepoPath -GitArgs @('rev-parse', 'HEAD')
        if ($head.Exit -ne 0) { return }
        $sha = ($head.Output | Select-Object -Last 1).Trim()
        $body = @{ hostname = $env:COMPUTERNAME; commit = $sha } | ConvertTo-Json -Compress
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $null = Invoke-WebRequest -UseBasicParsing -Method Post -Uri ((Get-RxVerifyHqBase) + '/api/rxverify-update-key/confirm') -Headers @{ Authorization = "Bearer $ReportKey" } -ContentType 'application/json' -Body $body -TimeoutSec 20
        $markerDir = Split-Path -Path $marker -Parent
        if (-not (Test-Path $markerDir)) { New-Item -ItemType Directory -Path $markerDir -Force | Out-Null }
        Set-Content -Path $marker -Value $sha -NoNewline
        Write-RxSshLog 'Told HQ this PC now updates over SSH.'
    } catch {
        Write-RxSshLog "Could not tell HQ about the migration yet (will retry next launch): $($_.Exception.Message)"
    }
}

# Called by update-and-run.ps1 BEFORE git fetch. NEVER throws and never blocks
# the launch - any failure leaves origin exactly as it was (HTTPS keeps
# working while the repo is still public) and the script carries on.
#   origin is HTTPS + report key present -> fetch key, configure, TEST
#       (git ls-remote), and only if that works switch origin to SSH, then
#       confirm to HQ.
#   origin already SSH -> make sure the key files / core.sshCommand are still
#       in place (repair if not), and confirm to HQ if it never got through.
function Invoke-RxVerifySshMigration {
    param([string]$RepoPath, [string]$ReportKey)
    try {
        $o = Invoke-RxGit -RepoPath $RepoPath -GitArgs @('remote', 'get-url', 'origin')
        if ($o.Exit -ne 0) { Write-RxSshLog 'No origin remote found - skipping SSH migration.'; return }
        $origin = (($o.Output | Select-Object -First 1)).Trim()

        if ([string]::IsNullOrWhiteSpace($ReportKey)) {
            Write-RxSshLog 'No RxVerifyReportKey in settings.json - cannot fetch the deploy key; origin unchanged.'
            return
        }

        if ($origin -match '^(ssh://|git@)') {
            $filesOk = (Test-Path (Get-RxVerifyKeyPath)) -and (Test-Path (Get-RxVerifyKnownHostsPath))
            $cfg = Invoke-RxGit -RepoPath $RepoPath -GitArgs @('config', '--get', 'core.sshCommand')
            $cfgOk = ($cfg.Exit -eq 0) -and ((($cfg.Output -join '').Trim()) -ceq (Get-RxVerifySshCommand))
            if (-not ($filesOk -and $cfgOk)) {
                Write-RxSshLog 'SSH key files or core.sshCommand missing - repairing...'
                if ((Install-RxVerifyDeployKey -ReportKey $ReportKey)) { $null = Set-RxVerifyRepoSshConfig -RepoPath $RepoPath }
            }
            Send-RxVerifyMigrationConfirm -RepoPath $RepoPath -ReportKey $ReportKey
            return
        }

        if ($origin -notmatch '^https://github\.com/elevatedev4/rx-verify(\.git)?/?$') {
            Write-RxSshLog 'origin is not the rx-verify GitHub HTTPS URL - leaving it alone.'
            return
        }

        Write-RxSshLog 'Switching this PC to read-only SSH updates...'
        if (-not (Install-RxVerifyDeployKey -ReportKey $ReportKey)) { Write-RxSshLog 'Staying on HTTPS for now.'; return }
        if (-not (Set-RxVerifyRepoSshConfig -RepoPath $RepoPath)) {
            Write-RxSshLog 'Could not set core.sshCommand - staying on HTTPS.'
            return
        }
        if (-not (Test-RxVerifySshAccess -RepoPath $RepoPath)) {
            Write-RxSshLog 'SSH test (git ls-remote) failed - staying on HTTPS.'
            $null = Invoke-RxGit -RepoPath $RepoPath -GitArgs @('config', '--unset', 'core.sshCommand')
            return
        }
        $set = Invoke-RxGit -RepoPath $RepoPath -GitArgs @('remote', 'set-url', 'origin', (Get-RxVerifySshUrl))
        if ($set.Exit -ne 0) { Write-RxSshLog 'Could not switch origin to SSH - staying on HTTPS.'; return }
        Write-RxSshLog 'origin now uses SSH.'
        Send-RxVerifyMigrationConfirm -RepoPath $RepoPath -ReportKey $ReportKey
    } catch {
        Write-RxSshLog "SSH migration skipped: $($_.Exception.Message)"
    }
}

# Clone into $RepoPath unless it already holds a checkout. Prefers SSH with
# the deploy key; if the key can't be obtained or the SSH clone fails it falls
# back to the HTTPS URL (which still works only while the repo is public).
# THROWS if no clone succeeded - callers decide how to surface that.
function Invoke-RxVerifyClone {
    param([string]$RepoPath, [string]$ReportKey)
    if (Test-Path (Join-Path $RepoPath '.git')) {
        Write-RxSshLog 'Repo folder already has a checkout - not cloning.'
        return
    }
    $parent = Split-Path -Path $RepoPath -Parent
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Set-RxVerifyGitNoPrompt

    if (Install-RxVerifyDeployKey -ReportKey $ReportKey) {
        $prevSsh = $env:GIT_SSH_COMMAND
        $env:GIT_SSH_COMMAND = Get-RxVerifySshCommand
        try {
            $c = Invoke-RxGit -RepoPath '' -GitArgs @('clone', (Get-RxVerifySshUrl), $RepoPath)
        } finally {
            if ($null -eq $prevSsh) { Remove-Item Env:\GIT_SSH_COMMAND -ErrorAction SilentlyContinue } else { $env:GIT_SSH_COMMAND = $prevSsh }
        }
        if ($c.Exit -eq 0) {
            $null = Set-RxVerifyRepoSshConfig -RepoPath $RepoPath
            return
        }
        Write-RxSshLog 'SSH clone failed - trying HTTPS (works only while the repo is public).'
    }

    $h = Invoke-RxGit -RepoPath '' -GitArgs @('clone', (Get-RxVerifyHttpsUrl), $RepoPath)
    if ($h.Exit -ne 0) {
        throw "git clone failed (exit code $($h.Exit)): $(($h.Output | Select-Object -Last 3) -join ' | ')"
    }
}
