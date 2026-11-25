# Releasing

Packages are built, tested and packed by `.github/workflows/ci.yml` on every push and pull request. Publishing to nuget.org is a separate workflow, `.github/workflows/publish.yml`, that runs only when started by hand and only after approval. It signs in with [nuget.org Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so no API key is stored.

## One-time setup

Before the first release, the repository owner sets up:

1. On nuget.org, a trusted publishing policy for this repository and the workflow file `publish.yml`, with the environment `nuget`.
2. In the GitHub repository settings, an environment named `nuget` with the owner as a required reviewer.
3. A repository variable `NUGET_USER` holding the nuget.org user name (a variable, not a secret).
4. `RepositoryUrl` and `PackageProjectUrl` in `Directory.Build.props`, once the repository URL is known.

## Each release

The version is `Version` in `Directory.Build.props`. Change it, merge, then start the publish workflow by hand and approve it. The workflow creates no tag.
