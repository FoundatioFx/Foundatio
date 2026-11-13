---
name: foundatio-release
description: Orchestrates a coordinated, staggered release of the Foundatio core library (whose tag also releases Foundatio.Redis, Foundatio.AWS, Foundatio.Kafka, Foundatio.AzureServiceBus and Foundatio.RabbitMQ) and its dependent repos (AzureStorage, Aliyun, Storage.SshNet, plus Parsers/Repositories when they need a tag). Verifies every step (CI on exact commits, API/binary compat, nuget.org restorability, version pins) before promoting. Use when cutting a Foundatio release, bumping repos from a preview/nightly to a stable Foundatio version, tagging provider repos, checking release readiness, or when the user says "cut 13.x.y", "release foundatio", or "check CI for all repos".
---

# Foundatio Release

Coordinated release across the Foundatio repos under `~/code`. **Verification at every step is the point of this skill.** Never advance a phase on an assumption; every "go" is backed by a command output you can quote. When in doubt, ask the user (AskQuestion) before any irreversible action (tag push, release publish, nuget publish).

Scripts live in [scripts/](scripts/) (PowerShell 7, run from any directory). Detailed templates and failure playbook: [reference.md](reference.md). Pre-merge validation of core changes against providers (local source builds, emulators) is the separate `cross-repo-validation` skill; this skill starts once those changes are merged to `main`.

## Repo roles

| Role | Repos | Rule |
|---|---|---|
| **Core** | `Foundatio` | Tag first. Its tag build publishes stable to nuget.org, including `Foundatio.Redis`, `Foundatio.AWS`, `Foundatio.Kafka`, `Foundatio.AzureServiceBus` and `Foundatio.RabbitMQ`, which live in this repo and need no bump or tag of their own. |
| **Providers (tag with core version)** | AzureStorage, Aliyun, Storage.SshNet | Tag `v<core version>` after bumping to the stable core version. All depend only on core. |
| **Dependents with their own version scheme** | Parsers (`v8.x`), Repositories (`v8.x`) | Bump pins and keep CI green on every core release. Tag **only** when the user says they need a release. If tagged, they go in their own stage (see Staggering). |
| **Never tagged here** | Mediator, CommandQuery, Lucene | No core Foundatio dependency (Mediator only uses it in tests/samples). Always skip tagging. Still bump Mediator's pins. Still report their CI; ask the user whether red CI blocks (it did not for 13.0.5; they were red from unrelated NU1902/NU1903 advisories). |

## Version mechanics (MinVer)

- Push to `main`: CI computes `<next>-preview.0.N` and publishes it to GitHub Packages and Feedz ("nightly"). This is how dependents consume unreleased core.
- Push tag `vX.Y.Z`: CI computes stable `X.Y.Z`, publishes to GitHub, Feedz and **nuget.org** ("Publish Release Packages").
- Branch pushes also publish nightlies. Releases come only from tags.
- "Publish Release Packages: success" means nuget.org accepted the push (`Created`). For a **leaf** release that nothing else restores (all providers) that is the end of verification; do **not** poll nuget.org.
- Only wait for nuget.org indexing when a **downstream commit or tag must restore the package first**: core `Foundatio` before bumping any dependent, and Parsers before Repositories (staggered flow). The flat container can lag >5 min (seen for `Foundatio` in 13.0.5), so there gate on [scripts/wait-nuget.ps1](scripts/wait-nuget.ps1) before committing the bump. Restoring from a pinned stable that is not yet listed fails with NU1102.

## Workflow

Copy and track:

```
Release <X.Y.Z>
- [ ] 0. Preflight: ci-status.ps1 on all repos; everything on main, in sync, clean
- [ ] 1. Release notes for core (confirm with user)
- [ ] 2. Core compat gate: api-compat.ps1 (flag breaks BEFORE anything else)
- [ ] 3. Tag core, watch tag build to green (publish step success)
- [ ] 4. wait-nuget.ps1 for core packages (required: dependents must restore them next)
- [ ] 5. Bump all dependents preview -> stable (dry run, apply, restore check)
- [ ] 6. Commit + push bumps; CI green on EXACT commits with test counts
- [ ] 7. Pre-tag gate per provider; create the GitHub releases (this creates the tags)
- [ ] 8. Per tag: build green, stable version, tests ran, publish step success. No nuget polling for leaf repos
- [ ] 9. Final ci-status.ps1 summary
```

### 0. Preflight (read-only)

Run `scripts/ci-status.ps1`. Require for every repo that will be bumped or tagged:
- local branch `main`, `inSyncWithMain=True`, `dirtyFiles=0` (uncommitted work, e.g. a WIP file, must be flagged to the user, never committed silently)
- a `Build` run exists **on the exact HEAD commit** and is `success`
- test summary present with `failed: 0` and a total that matches the previous run (compare skipped counts; a sudden jump means tests were silently skipped)

Any repo failing CI at preflight: STOP and report root cause (`gh run view <id> --log-failed`). Do not paper over; do not bump on top of red. Pre-existing unrelated reds (Lucene/CommandQuery) are reported, then ask the user whether they block.

### 2. Compat gate

`scripts/api-compat.ps1 -Ref <tag-or-main> -Baseline <previous stable> -Version <new>` uses SDK package validation (CP#### errors). Do **not** use the standalone `apicompat` tool on extracted nupkgs; it cannot resolve references and prints noise.

Triage any `CPxxxx` before proceeding and **always tell the user, even if you conclude it is harmless**:
- Does an untagged/previously-released dependent (Parsers, Repositories, or any provider already published against an older core) call or override the changed member?
- Test-only packages (`Foundatio.TestHarness`, `Foundatio.Xunit*`) matter only to consumers that move out of lockstep. Providers overriding harness members are rebuilt in the same release; verify by `rg` for the member in each repo.
- A break in `Foundatio` / `Foundatio.Extensions.Hosting` public API is a stop-and-ask.

### 3-4. Tag core and wait

Core tags are lightweight commit tags on `main` HEAD (`git tag vX.Y.Z && git push origin vX.Y.Z`), or created by `gh release create`. Verify the tag build: version `MinVer: Calculated version X.Y.Z`, Run Tests success, Package success, Publish Release Packages success and nuget.org lines say `Created` with zero `already exists`. Core is the one release that **does** need `scripts/wait-nuget.ps1 -Version X.Y.Z` (exit 0) before bumping dependents, because their restore needs it.

### 5-6. Bump dependents

1. Dry run: `scripts/bump-version.ps1 -From <preview> -To <stable> -Repos ...` (call in-process with `&` and an array; `pwsh -File` with a comma list passes one string).
2. Read the dry run. Every hit must be a Foundatio-family PackageReference. Review "SKIPPED OCCURRENCES". Also check pins that are *not* the preview (for example `Foundatio.TestHarness` stuck on an old stable): the stable promotion must leave **no** Foundatio package on an older version unless intentional and stated.
3. `-Apply`, then verify: `git diff` lines are pure version swaps; `dotnet restore <repo>.slnx --force-evaluate` exits 0 for every repo and `project.assets.json` contains `Foundatio*/X.Y.Z` and no `-preview`. Capture `$LASTEXITCODE` immediately after the restore (other commands overwrite it). `rg` skips git-ignored `obj/`; use PowerShell `Get-ChildItem` or `rg --no-ignore`.
4. Commit message `Upgraded Foundatio to X.Y.Z`, push to `main`, record SHAs.
5. For each SHA, poll `gh run list --commit <sha>` until `Build` completes. Require `success`, and parse test totals (`total/succeeded/failed/skipped`) to prove tests actually ran. One red repo stops the line.

### 7. Tagging providers

Gate per provider (all must PASS): `origin/main` == the bump commit, GitHub main == same SHA, Build on that SHA `success`, no `-preview` refs remain, every Foundatio/TestHarness/Xunit pin is the stable version, tag `vX.Y.Z` and the GitHub release do not already exist. If any provider fails, none are tagged. Run the gate and the release creation in one script so nothing can change in between.

Create each release with `gh release create vX.Y.Z -R FoundatioFx/<repo> --target <bump SHA> --title vX.Y.Z --notes-file <notes>`. Creating the release creates the tag; `--target` pins it to the verified commit rather than a moving `main`. Providers are independent of one another, so tag them together (the user chose all-at-once for 13.0.5; a canary-first option exists if they ask).

Release notes: short, aligned with core, real highlights from `git log vPrev..HEAD` (see [reference.md](reference.md)). Confirm with the user how they want notes handled (the 13.0.5 decision was: create the release directly).

### 8. Per-tag verification

For each tag build: `completed/success`; tag points at the bump SHA (`gh api repos/FoundatioFx/<repo>/git/ref/tags/vX.Y.Z --jq .object.sha`); log shows `MinVer: Calculated version X.Y.Z`; tests ran with totals matching the pre-tag run; Publish Release Packages success with nuget.org `Created` and no `already exists`. That completes a leaf release. Do not poll nuget.org for it.

## Staggering (dependents that must be tagged)

Order is dictated by the dependency graph; never promote out of order. Example if Parsers needs a tag this cycle:

1. Tag Foundatio -> green -> `wait-nuget` (needed: Parsers restores it next).
2. Bump Parsers to Foundatio X.Y.Z -> CI green on that SHA.
3. Tag Parsers with **its own** scheme (next `v8.x`; read `git describe` and MinVer config, never assume) -> green -> `wait-nuget` for Parsers packages (needed: Repositories restores them next).
4. Bump Repositories to Foundatio X.Y.Z **and** the new Foundatio.Parsers.* version -> CI green -> tag with its own scheme. Repositories is a leaf, so no nuget wait after it.
5. Providers can be tagged in parallel with steps 2-4 once step 1 completes; they do not depend on Parsers/Repositories, and no nuget wait is needed after them.

If a bump reveals a build or test failure against the new stable, stop before tagging anything downstream, diagnose, and ask the user.

## Hard rules

- Never tag, publish a release, or push to `main` on an unverified assumption. Quote the evidence.
- Match CI to the exact commit SHA, never "latest run on the branch".
- Never continue past a red build, a missing test summary, a core/Parsers package that a downstream restore needs but is not yet listed on nuget.org, or an unflagged compat break.
- Never modify or commit unrelated uncommitted work (e.g. a dirty `SQSQueue.cs`); report it.
- Nightly previews on `main` must build and pass for **every** repo before any tag starts.
- Do not claim a package version is "used" from log mentions. Prove it via committed pins plus a successful restore, or `project.assets.json`.

## Output to the user

Use one table per phase: repo, commit, CI run link, tests (total/passed/skipped), verdict. End each phase with an explicit "safe to proceed: yes/no, because ...".
