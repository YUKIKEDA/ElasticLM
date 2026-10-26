## Summary

<!-- What changed and why. Keep it short. May be Japanese. -->

-

## Related

<!-- `Closes #N` alone on a line when this PR finishes the Issue; `Part of #N` when it does not. -->

Closes #N

- Design: <!-- docs/design.ja.md §x.y / N/A -->

## Verification

<!-- Commands that ran and their result. List each acceptance line of the Issue and how it was checked. -->

- [ ] `./build.ps1`
- [ ] NativeAOT publish (warnings / executable size): <!-- N/A unless the Issue names it -->
- [ ] Searches behind any "every X does Y" claim: <!-- N/A -->

## Measurements

<!-- Verification time, startup, binary size, or throughput before and after, against a named baseline. N/A otherwise. -->

N/A

## Risk / Rollback

- Risk:
- Rollback:

## Checklist

- [ ] Title follows Conventional Commits (`type(scope): subject`)
- [ ] CSharpier applied (`dotnet csharpier format .`)
- [ ] New public API has XML docs; new tests have `summary` + `remarks` (Preconditions / Steps / Expected)
- [ ] No key material, real license token, or secret in the diff, tests, or logs (`.cursor/rules/security.mdc`)
- [ ] `docs/`, `.cursor/rules/`, and skills updated when behavior or workflow changed — or N/A
- [ ] Corrections from the owner written into the file they belong to (`.cursor/rules/record-corrections.mdc`) — or N/A
