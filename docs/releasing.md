# Releasing

Packages are built, tested and packed by `.github/workflows/ci.yml` on every push and pull request. Publishing to nuget.org is a separate workflow, `.github/workflows/publish.yml`, that runs only when started by hand and only after approval. It signs in with [nuget.org Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so no API key is stored.

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

`VersionPrefix` in `Directory.Build.props` holds the `MAJOR.MINOR.PATCH` part. The prerelease label is not stored there: it is given when the package is built (`dotnet pack -p:VersionSuffix=rc.1`). Both packages always share one version, and the test kit depends on exactly that version of the library.

## Each release

1. Set `VersionPrefix` in `Directory.Build.props` to the next version, by the rules above, and merge.
2. Start the publish workflow by hand. For a prerelease, enter its label (such as `rc.1`) in the `prerelease` box; leave the box empty for a stable release. The workflow rejects a label that is not valid SemVer.
3. Approve the `nuget` environment when asked.

The workflow creates no tag.

## Checking for breaking changes

`EnablePackageValidation` is on in `Directory.Build.props`. Once a stable version is on nuget.org, set `PackageValidationBaselineVersion` beside it to the last stable version, for example:

```xml
<PackageValidationBaselineVersion>1.0.0</PackageValidationBaselineVersion>
```

`dotnet pack` then downloads that version and fails if the public API has lost or changed anything it had, so a breaking change cannot ship by mistake. When the break is intended, raise the major version and move the baseline to the last stable version after release. Leave the property out until 1.0.0 is published: pack fails when the baseline cannot be downloaded.
