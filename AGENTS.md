# ElasticLM: agent entry

A software licensing platform for .NET: signed license verification, an on-premises floating license server, and a hosted activation service, all NativeAOT-compatible and shipped as single binaries. This file is the entry point for agents. Steps for people are in `CONTRIBUTING.md`. Enforced rules are in `.cursor/rules/`.

## Source

| What | Where |
| --- | --- |
| Design (canonical, Japanese): requirements, threat model, formats, protocols | `docs/design.ja.md` |
| Rejected alternatives | `docs/design-decisions.md` |
| Decisions with a measured or outside reason | `docs/adr/` (one per file) |
| Order and status of work | `docs/roadmap.md` |
| Projects, folders, coding conventions | `docs/conventions.md` |
| Build, format, and test gate | `./build.ps1` |

The current row lives only in `docs/roadmap.md`. `.dev/` is local scratch; nothing there is a decision.

The earlier attempts (`_ElasticLM`, `_ElasticLM2` beside this repository) are read for lessons only. No code is carried over; `docs/design-decisions.md` lists what they did and why it was rejected.

## Required rules

Read the rules that match the work before changing anything. `alwaysApply` rules apply to every change; always read `workflow`, `consent`, `git`, `scope`, and `security`. After every merge comes the retrospective of `workflow`: what the work taught goes into these files, not into an agent's memory.

| Rule | One line |
| --- | --- |
| `workflow` | Grill, Issue, branch, PR, human squash merge, retrospective; labels; gates |
| `consent` | A question is not a decision; options need a recommendation; outward actions need an order |
| `git` | Finished work goes to a PR on its branch without asking; destructive git needs a named order; never `main` |
| `scope` | Requirements decide scope; no half-shipped capability or security property |
| `security` | No keys or secrets in the repo or binaries; BCL crypto only; untrusted input; `TimeProvider` |
| `defer` | Deferral only as a named roadmap row |
| `pace` | Split off what is outside the row; limit an investigation; long runs on the side |
| `open-ends` | No unmeasured step on a measured path and no unexplained figure in a PR |
| `record-corrections` | A correction lands in the rule, design, ADR, or skill it belongs to |
| `review-comments` | A finding is about the head it was written on; a review the owner asks for is posted on the PR |
| `similar-findings` | Fix the same kind of problem where it also occurs |
| `no-dead-code` | Remove everything that only served a removed path |
| `spec-checks` | A test implements the whole design statement |
| `scratch` | Remove scratch files, generated keys, and tokens in the turn that made them |
| `conventional-commits` | `type(scope): subject`, English; no `perf` type |
| `pull-requests` | Title and six-heading body from the template |
| `github-closing` | `Closes #N` alone on a line; never a closing verb before an unfinished Issue |
| `language` | Code and rules in English; the design, Issues, and PR bodies may be Japanese |
| `design-docs` | Where the design lives; design PR before code; what a statement must carry |
| `powershell-shell` | Shell syntax on this setup |
| `engineering` | Layout, net10.0, warnings as errors, CSharpier, analyzers, banned APIs, the gate |
| `dependencies` | A dependency must keep NativeAOT, the single binary, size, and a trusted supply chain |
| `aot` | No reflection or dynamic code; source-generated JSON; the NativeAOT publish check |
| `csharp` | Style, naming, types, errors, XML docs (`*.cs`) |
| `testing` | xUnit v3, fake time, generated keys, test docs, mutation checks (`tests/**`) |
| `dev-docs` | Mermaid, math, and pseudo-code in `docs/` |

## Skills

Skills live in `.agents/skills/`. `skills-lock.json` pins the ones that come from outside. Git tracks `.claude/skills/` as a copy, not as a link: a change to a skill changes both copies in the same commit.

- `grilling`: when the design branches, before an Issue.
- `github-closing`: before `gh pr create`, to check that the PR closes only what it finishes.
- `english-writing`: before writing XML docs, comments, test remarks, or rules.
- `natural-japanese`: write and check Japanese documents.

## Environment

- .NET 10 SDK (`global.json`). If `dotnet --list-sdks` shows none, install it; do not stop to ask.
- Windows is the primary host. CI runs the same gate on Windows, Linux, and macOS.
- The agent shell is PowerShell (`.cursor/rules/powershell-shell.mdc`).
