---
name: security-audit
description: Run a read-only security audit of the current repository, combining a code-level vulnerability review with secret scanning across the working tree, the full git history, and every blob in the git object database. Use when the user asks for a security audit, a secret scan, or a check for committed API keys, credentials, or similar sensitive data.
---

# Security Audit

Run a read-only deep security analysis of the current repository. Do not modify any files and do not run state-changing git commands; read-only git commands such as `git log`, `git ls-files`, and `git cat-file` are allowed.

The audit has two independent tracks that should run in parallel:

1. A code-level vulnerability review, delegated to a sub-agent with the prompt template below.
2. A three-layer secret scan: working tree, all commit diffs, and every blob in the git object database (which covers amended or rebased commits, stashes, and other unreachable objects).

## Workflow

1. Size the repository first with `git rev-list --all --count` and `git ls-files`. The pipeline-based history scans below are practical for small histories (roughly under a few thousand commits); for large repositories, prefer a dedicated scanner such as gitleaks or trufflehog for layers 2 and 3.
2. Launch the code review sub-agent (see the prompt template below), then run the secret-scan layers while it works.
3. For any hit, locate the introducing commit with `git log --all -S '<string>' --oneline --name-only` before deciding whether history rewriting (git-filter-repo or BFG) is needed.
4. Report findings with file or commit, severity, a one-sentence description, and a concrete exploitation scenario. Explicitly list the areas verified clean, and state whether git-history cleanup is needed.

## Sub-Agent Prompt: Code-Level Vulnerability Review

Adapt the repository path, project description, and directory layout; the numbered checklist is general-purpose for .NET CLI repositories and transfers to other stacks with minor edits.

```text
Perform a READ-ONLY security code review of the repository at <REPO_PATH>.
Do NOT modify any files. Do NOT run any state-changing git commands.

<One-paragraph description of the project: language, runtime, entrypoints,
where production code, tests, and tooling live.>

Review all source under src/, tools/, and tests/ (and any .ps1/.cmd/.sh
scripts, .csproj, .props, .targets, nuget.config, and CI workflow files
under .github/) for security vulnerabilities. Look specifically for:

1. Command/process injection: any Process.Start, ProcessStartInfo, shell
   invocation where user-controlled arguments are interpolated into a
   command line without proper escaping (UseShellExecute, cmd.exe /c, etc.).
2. Path traversal / arbitrary file write: file path construction from user
   input; whether destination paths are validated, whether ".." in embedded
   resource names or arguments could escape the target root, and overwrite
   behavior.
3. Unsafe deserialization (BinaryFormatter, insecure JSON settings, XML
   with DTD/XXE enabled - XmlReader/XDocument settings).
4. Loading/executing untrusted code: MSBuildWorkspace loading arbitrary
   projects executes MSBuild targets - note if the tool documents/mitigates
   that risk; any Assembly.Load / add-in loading.
5. Insecure network calls: http:// URLs, disabled certificate validation
   (ServerCertificateCustomValidationCallback returning true), credentials
   in URLs.
6. Secrets in source: hardcoded API keys, tokens, passwords, connection
   strings in any file including test fixtures.
7. Dependency risks: check PackageReference entries for known-vulnerable or
   unusual packages; note any pinned prerelease/unofficial feeds in
   nuget.config; check for NuGet package source mapping issues.
8. CI/CD risks in .github/workflows: pull_request_target misuse, script
   injection via ${{ github.event... }} interpolation into run: steps,
   overly broad permissions, secrets exposure.
9. Symlink/zip-slip issues in any packaging or extraction code.
10. Anything else notable (e.g., predictable temp files, world-writable
    output, logging of sensitive data).

For each finding report: file path (repo-relative), line number, severity
(critical/high/medium/low/info), a one-sentence description of the defect,
and a concrete exploitation scenario. If an area is clean, say so briefly.
Also list the files actually examined. Be rigorous - verify each finding by
reading the actual code, not just pattern matches. Return the findings as
structured markdown text.
```

## Secret-Scan Commands (PowerShell, read-only)

All commands assume the current directory is the repository root. Define the high-confidence token pattern once per session (AWS, GitHub, Anthropic, OpenAI, Slack, Google, GitLab, npm/NuGet, Azure, private key blocks, JWTs):

```powershell
$tokenPatterns = 'AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{36}|gho_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{22,}|sk-ant-[A-Za-z0-9-]{20,}|sk-[A-Za-z0-9]{40,}|xox[baprs]-[0-9A-Za-z-]{10,}|AIza[0-9A-Za-z_-]{35}|BEGIN [A-Z ]*PRIVATE KEY|_authToken|npm_[A-Za-z0-9]{36}|glpat-[A-Za-z0-9_-]{20}|oy2[a-z0-9]{40,}|AccountKey=[A-Za-z0-9+/=]{20,}|SharedAccessSignature|eyJhbGciOi'
```

### Layer 1: working tree

Scan tracked files for `$tokenPatterns` with the session's native search tool (for example `Grep`, or `git grep -I -E`), plus a looser case-insensitive pass for `(password|passwd|secret|api[_-]?key|apikey|token|credential|connectionstring|pwd)\s*[:=]`. Expect and dismiss benign hits such as `CancellationToken`, Roslyn syntax tokens, and `Environment.GetEnvironmentVariable("SOME_API_KEY")` (reads from the environment; no value committed).

### Layer 2: full git history

Sensitive filenames ever added in any commit:

```powershell
git log --all --diff-filter=A --name-only --pretty=format:'COMMIT %h' |
  Select-String -Pattern '\.(env|pem|key|pfx|p12|jks|keystore|ppk)$|id_rsa|id_ed25519|credentials|secrets?\.|\.npmrc|nuget\.config|appsettings|\.netrc|\.pypirc|authinfo'
```

Token patterns across all history diffs:

```powershell
git log --all -p --no-color | Select-String -Pattern $tokenPatterns | Select-Object -First 50
```

Loose credential keywords and connection strings on added lines only, with a false-positive filter:

```powershell
git log --all -p --no-color |
  Select-String -Pattern '^\+.*((api[_-]?key|secret|passw(or)?d|bearer |authorization:)[^a-z0-9]{0,3}[A-Za-z0-9+/_-]{8,}|Server=.*;.*Password=|Data Source=.*Password=|mongodb(\+srv)?://[^ ]*:[^ ]*@|postgres(ql)?://[^ ]*:[^ ]*@|mysql://[^ ]*:[^ ]*@|redis://[^ ]*:[^ ]*@|amqps?://[^ ]*:[^ ]*@|https?://[^/ ]*:[^/@ ]*@)' |
  Where-Object { $_ -notmatch 'cancellationtoken|findtoken|placeholder|example|<key>|your[-_ ]?(api)?key' } |
  Select-Object -First 40
```

Inline secret values in config-file history:

```powershell
git log --all -p --no-color -- '*.json' '*.config' '*.props' '*.targets' '*.yml' '*.yaml' '*.xml' |
  Select-String -Pattern '^\+.*"(value|token|key|secret|password)"\s*:\s*"[^"]{8,}"' |
  Select-Object -First 20
```

### Layer 3: every blob in the object database

Covers unreachable objects from amended or rebased commits and stashes; this is the layer that proves nothing secret-shaped survives even in rewritten history.

```powershell
$blobs = git cat-file --batch-all-objects --batch-check='%(objecttype) %(objectname)' |
  Where-Object { $_ -like 'blob *' } |
  ForEach-Object { ($_ -split ' ')[1] }
"Scanning $($blobs.Count) blobs"
foreach ($b in $blobs) {
  $content = git cat-file -p $b 2>$null | Out-String
  if ($content -match $tokenPatterns) { "HIT: $b" }
}
```

## Report Contract

Structure the final report as:

- TLDR line: whether secrets were found and whether the code has exploitable vulnerabilities.
- Secrets scan: result per layer (working tree, history diffs and filenames, all blobs), with benign hits explained.
- Code review: findings ranked by severity with file, line, description, and exploitation scenario; then areas verified clean.
- Whether git-history cleanup (git-filter-repo or BFG) is needed.
- The single highest-priority recommended action, if any.
