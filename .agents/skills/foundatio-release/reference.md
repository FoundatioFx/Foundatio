# Foundatio Release: Reference

## Release notes templates

**Core (`Foundatio`)** - detailed. Sections: Highlights, one section per feature/PR (with PR links), Documentation, Build & CI, Dependency Updates (old -> **new**, derived from `git diff vPrev vNew -- '*.csproj' '*.props'`), consumer note about aligned xUnit packages, Packages list, `dotnet add package Foundatio --version X.Y.Z`, Full Changelog compare link. Take the style from the previous release (`gh release view vPrev -R FoundatioFx/Foundatio --json body`). Do not invent links or PR numbers; derive from `git log vPrev..vNew`.

**Providers** - short, aligned with core:

```markdown
## Foundatio.<Name> vX.Y.Z

Aligned with [Foundatio vX.Y.Z](https://github.com/FoundatioFx/Foundatio/releases/tag/vX.Y.Z).

### Highlights
- <only if the provider has user-facing changes since its last tag; derive from git log vPrev..HEAD>

### Dependency Updates
- `Foundatio` A -> **X.Y.Z**
- <other package bumps from the diff>

**Full Changelog**: https://github.com/FoundatioFx/Foundatio.<Name>/compare/vPrev...vX.Y.Z
```

Always generate notes from `git log vPrev..vNew` for that repo. Highlights are real changes (for 13.0.5: GroupId support in Redis, Azure Storage, SQS, Service Bus). Show the draft to the user before publishing.

## Commands

```bash
# tag + release (only after the pre-tag gate PASSes). Creating the release creates the tag;
# --target pins it to the verified commit. Do the gate and the create in ONE script.
gh release create vX.Y.Z -R FoundatioFx/<repo> --target <bump SHA> --title vX.Y.Z --notes-file notes.md
# core can also be: git tag vX.Y.Z && git push origin vX.Y.Z

# CI for an exact commit
gh run list -R FoundatioFx/<repo> --commit <sha> --json databaseId,workflowName,status,conclusion
gh run view <id> -R FoundatioFx/<repo> --log-failed

# test counts live in the log as "total: N / succeeded: N / failed: N / skipped: N"
gh run view <id> -R FoundatioFx/<repo> --log
```

Past releases: tags are lightweight (`git cat-file -t` -> `commit`), releases target `main`, authored by the maintainer.

## Failure playbook

| Symptom | Cause / action |
|---|---|
| Restore fails `NU1902/NU1903 ... Warning As Error` | Vulnerable transitive package (seen: `System.Security.Cryptography.Pkcs`). Pin a patched version in the affected csproj(s). Not caused by the Foundatio bump; confirm by checking the previous commit also failed. |
| `NU1605` package downgrade | A project overrides a package (`VersionOverride`) below what a project reference requires. Remove the override or align versions. |
| Restore `NU1102` unable to find version after bump | Version not yet listed on nuget.org. Only matters when a downstream restore needs it (core, Parsers). Run `wait-nuget.ps1` before committing the bump. |
| ApiCompat prints "Could not resolve reference" | You used the standalone tool; use `api-compat.ps1` (SDK package validation). |
| `CP0002` member removed | Methods with optional params added to a signature count as removals for binary compat. Check who overrides/calls it; test harness only matters out of lockstep. Flag to the user. |
| Test total drops or skipped jumps vs previous run | Do not accept a green check on its own. Find out why before tagging. (RabbitMQ skips ~111 of 249 baseline; unexplained as of 13.0.5.) |
| Tag build green but a downstream bump cannot restore | nuget.org indexing lag on core or Parsers. Wait; a package page returning 200 does not mean restorable. Not applicable to leaf providers. |
| `rg`/grep finds nothing in `obj/` | Ignored files; use `--no-ignore` or `Get-ChildItem`. |
| Script exit code looks wrong | `$LASTEXITCODE` is overwritten by the next native command; capture immediately. |

## Decisions already made (13.0.5; re-ask if the situation differs)

- Red CI in never-tagged repos (Lucene, CommandQuery) did **not** block provider tags. They have no core dependency. Still report them.
- Providers were released all at once (no canary). GitHub release is created directly, no draft step; the release creates the tag.
- nuget.org listing is not checked for leaf releases.

## Open questions to ask the user (do not guess)

- Does any untagged dependent (Parsers, Repositories) need a tag this cycle? If yes, switch to the staggered flow.
- Should uncommitted WIP in a repo be left alone? (Default: yes, report it.)
- Any `CPxxxx` break in core `Foundatio` / `Extensions.Hosting`: stop and ask before bumping or tagging anything.
