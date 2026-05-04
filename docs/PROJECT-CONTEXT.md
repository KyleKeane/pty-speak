# Project context

This document is for human readers who want the wider context behind
`pty-speak` — who builds it, what working with it looks like in
practice, and the values frame that drives the technical decisions.
Future Claude Code sessions can read it for orientation depth; the
operational rules they need at session start live in
[`CLAUDE.md`](../CLAUDE.md).

The README ([`README.md`](../README.md)) stays focused on what the
project is, how to install it, and how to contribute. This document
exists so the README can stay focused without losing the wider story.

## About the author

`pty-speak` is built and maintained by **Dr. Kyle Keane**.

- **Current affiliation:** School of Computer Science, University of
  Bristol, United Kingdom.
- **Previously:** ~10 years at MIT, working at the intersection of
  accessibility, assistive technology, and computing education. Full
  bio, publications, and prior work at
  [https://www.kylekeane.com](https://www.kylekeane.com).

Kyle is a blind developer. The work captured in this repository is
both a technical project and a personal one: it builds the
developer-tool environment that ought to exist, and it does so from
the position of someone who needs that environment to work in order
to do the rest of the work the project has nothing to do with. The
sections below — what working with current developer tools looks like
in practice, and the values frame that drives the technical decisions
in this repository — are written from that position.

## The complexities of trying to work with technology as a blind developer

This project exists because mainstream developer tools — including
the ones built by AI labs whose mission statements include making
their products useful to everyone — keep shipping interfaces that
cannot be operated by a person using a screen reader. `pty-speak`
is one blind developer's attempt to build a usable surface around
one of those tools (Anthropic's Claude Code) so that the
capabilities it offers everyone else are actually reachable from
an assistive-technology stack.

The path to writing this code is itself a small case study in why
this work is needed.

The Claude Code desktop application is not screen-reader-accessible.
There is no usable alternative on a workstation. The iOS app is
the only viable channel, and the iOS app has its own gap: the
"Add feedback" input field — the field a user types into to send
the next message to Claude — is not activatable from VoiceOver.
The standard touch-explore gesture finds the field, but the
double-tap-to-activate gesture does not focus it for input. The
workaround, which I have used for every message in this session,
is:

1. Disable VoiceOver entirely.
2. Place a finger on the screen at the remembered pixel location
   of the input field.
3. Activate the field by sighted-touch contract.
4. Re-enable VoiceOver to hear Claude's response.
5. Repeat for the next message.

That cycle runs hundreds of times across a working session.
Disabling the screen reader temporarily strips off the access
infrastructure that makes the rest of the device navigable, on a
touchscreen designed under the assumption that sight is available
for any unrecovered fall-back. It is a workaround in the strict
sense: it gets the work done, but the cost is borne entirely on
one side.

While using this workaround to build a terminal that exposes
Claude Code to NVDA, Claude itself produced an idiom in this
session that is worth recording, because it reveals how deep the
problem runs.

The idiom was the phrase "blindly iterating" — used by the model
to mean "iterating without information," "guessing without
evidence," or "speculative iteration." That is not what the words
literally say. The words equate "blind" with "ignorant," and they
do so casually, in a position where the speaker (the model) had
many specific, accurate words available and chose the metaphor
anyway. The accurate words are concrete:

- *uninformed iteration*
- *iterating without diagnostic data*
- *speculative iteration*
- *guessing without evidence*

Each describes the behaviour. None requires a sight-based metaphor.
The reason "blindly" is offensive is straightforward: it embeds
the assumption that the absence of sight is the canonical example
of the absence of information. Blind people are not less informed
than sighted people; we use different channels. A sighted person
who runs CI three times without reading the build log is operating
without information. A blind developer reading that log via a
screen reader is operating with full information. The metaphor
inverts the actual relationship between sight and knowledge, and
it does so in casual technical writing where it costs the writer
nothing to say what they actually mean.

This is not a request to police every word. It is a request to
prefer the literal phrasing when the literal phrasing is
available, because the literal phrasing communicates more
precisely *and* it does not casually demean the people whose
working conditions this project is being built to improve.

If you are reading this from inside Anthropic and would like a
report from a customer using your product through the iOS
workaround above to ship accessibility infrastructure: please
fix the iOS "Add feedback" field's VoiceOver activation, ship a
screen-reader-accessible desktop application, and review training
data for sight-based idioms used as synonyms for cognitive
deficit. The work that goes into that review is the same work
that produces a more precise model output for everyone — there is
no trade-off to mitigate.

## Access, dignity, and full participation

`pty-speak` is built on a stronger claim than "this should also work
for blind users." The claim is that access to computers — and to the
developer tools that compound human capability — is a modern
necessity of human dignity. To work with a computer is to participate
in contemporary society, contemporary culture, and contemporary work.
Any environment that denies that participation to a class of people
is a barrier, not a property of the person.

This is the framing the World Health Organization established in the
[International Classification of Functioning, Disability and
Health (ICF)](https://www.who.int/standards/classifications/international-classification-of-functioning-disability-and-health):
disability emerges from the interaction between a person and their
environment, not from the person alone. A blind developer using a
screen reader is not less informed; the developer-tool environment
that fails to support that access is the source of the disability,
and the cost is borne entirely on the developer rather than on the
tool. The ICF reframes the work: the right object to repair is the
environment, and the right metric is whether full participation in
the relevant life activity becomes possible.

`pty-speak`'s answer is concrete. Build the environment correctly. A
terminal that exposes its content through a real
`ITextRangeProvider` and a real semantic event stream is not
"accessible additionally"; it is accessible by construction. The
technical decisions in this repository — the ANSI parser, the
typed-event substrate, the UIA Document + Text-pattern surface, the
verbosity-profile design queued for the Output framework cycle — all
follow from that values position. They are how the values are made
operational rather than rhetorical.

## Where to next

- Back to the project orientation: [`README.md`](../README.md).
- The auto-loaded operational rules for Claude Code sessions:
  [`CLAUDE.md`](../CLAUDE.md).
- The full doc-ownership index for everything else in this repo:
  [`docs/DOC-MAP.md`](DOC-MAP.md).
