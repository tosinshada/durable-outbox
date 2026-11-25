# Releasing

Two packages, `DurableOutbox` and its test kit `DurableOutbox.Testing`, are built, tested and packed by `.github/workflows/ci.yml` on every push to `main`. Publishing to nuget.org is a separate workflow, `.github/workflows/publish.yml`, that runs only when started by hand and only after approval. It signs in with [nuget.org Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so no API key is stored.

## One-time setup

Before the first release, the repository owner sets up:

1. On nuget.org, a trusted publishing policy for this repository and the workflow file `publish.yml`, with the environment `nuget`.
2. In the GitHub repository settings, an environment named `nuget` with the owner as a required reviewer.
3. A repository variable `NUGET_USER` holding the nuget.org user name (a variable, not a secret).

## Versions

The packages follow [Semantic Versioning 2.0.0](https://semver.org) (SemVer), which nuget.org supports. A version is `MAJOR.MINOR.PATCH`:

- **Major** goes up for a breaking change: a public type or member removed or changed, a guarantee in section 5.3 of the design weakened, or a table change a service must make before it can upgrade.
- **Minor** goes up for something added that existing services can ignore, such as a new option or method.
- **Patch** goes up for a fix that changes no public API.

A prerelease adds a label after a hyphen, such as `1.1.0-rc.1`. NuGet does not install a prerelease unless asked to, and sorts it before the stable version with the same number.

`VersionPrefix` in `Directory.Build.props` holds the `MAJOR.MINOR.PATCH` part. The prerelease label is not stored there: it is given when the package is built (`dotnet pack -p:VersionSuffix=rc.1`). Both packages always share one version, and the test kit depends on exactly that version of the library, because it uses the library's internals.

The workflow pushes both packages and skips one whose version is already on nuget.org. That is how the kit's 1.1.0 is published: `DurableOutbox` 1.1.0 went out alone, so a run from the same source adds only `DurableOutbox.Testing` 1.1.0. It also means a run with a version that was not raised publishes nothing and still succeeds, so check the log of the push step.

## Each release

1. Set `VersionPrefix` in `Directory.Build.props` to the next version, by the rules above, and merge.
2. Start the publish workflow by hand. For a prerelease, enter its label (such as `rc.1`) in the `prerelease` box; leave the box empty for a stable release. The workflow rejects a label that is not valid SemVer.
3. Approve the `nuget` environment when asked.
4. After a stable release, set `PackageValidationBaselineVersion` in `Directory.Build.props` to it (see below).

The workflow creates no tag.

## Checking for breaking changes

`EnablePackageValidation` is on in `Directory.Build.props`, with `PackageValidationBaselineVersion` set to the last stable version on nuget.org (now `1.0.0`). `dotnet pack` downloads that version and fails if the public API has lost or changed anything it had, so a breaking change cannot ship by mistake.

When a break is intended, raise the major version and tell package validation the break is expected, either by suppressing the specific errors or by leaving out the baseline for that one release. After each stable release, move `PackageValidationBaselineVersion` to that release, so the next one is checked against it. A prerelease is never the baseline.
