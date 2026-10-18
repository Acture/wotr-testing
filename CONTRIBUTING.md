# Contributing

Read [README.md](README.md), [CLAUDE.md](CLAUDE.md) and [docs/offline.md](docs/offline.md)
before changing behavior. Report bugs and request features in
[GitHub Issues](https://github.com/Acture/wotr-testing/issues).

Make one focused change and preserve unrelated edits. Run the repository checks in
[CLAUDE.md](CLAUDE.md), build the solution against your own WotR installation and
verify the change through a consumer mod's offline tests. In the pull request,
describe the behavior change, the checks actually run, the game version and the
validation limits.

## Releasing

The library compiles against a WotR installation, so packages are built on a
maintainer machine:

1. Set `Version` in `Directory.Build.props` (shared by every package), update
   `CHANGELOG.md` and commit.
2. Run `dotnet pack` at the repository root. It builds `WotR.Testing.Fuzzing`,
   `WotR.Testing.Offline` and the `WotR.Testing` meta-package into `artifacts/packages/`. A Release pack (the
   default) refuses uncommitted changes (`-p:WotrAllowDirty=true` for local test
   packages only), and `VerifyWotrPackage` (`Directory.Build.targets`) deletes a
   package and fails if it holds anything outside its project's `WotrPackageEntry`
   allowlist, so no game file can be published.
3. Verify the packages from a local feed in a test project that references only
   `WotR.Testing`, on both runtimes.
4. Create a draft GitHub release with tag `v<version>` and the full build commit
   SHA as its target. Upload only the three verified `.nupkg` files as its assets;
   never upload build folders, game files or consumer reports.
5. Run `.github/workflows/release.yml` from `main` with that tag, first with
   `publish` disabled to verify the uploaded packages, then enabled to publish.
   It verifies package identities, versions, source commit and existing content allowlists,
   then publishes `WotR.Testing.Fuzzing`, `WotR.Testing.Offline` and `WotR.Testing`
   in that order. It does not build
   against the game or check out private notes. NuGet.org trusted publishing must
   name repository `Acture/wotr-testing`, workflow file `release.yml`, package
   owner `acture`, pattern `wotr.testing*`, and allow new packages and versions.
   Leave the policy's environment empty. The workflow obtains a temporary key
   through GitHub OIDC; no long-lived API key is needed.

Published NuGet versions cannot be deleted, only unlisted. A failed publication
stops the workflow; inspect which packages were accepted before retrying.

Never attach game files or snapshots of them (DLLs, bundles, localization files) to
commits, pull requests, issues or test reports. Keep `GamePath.props`, credentials
and local configuration out of Git. For vulnerability reports, follow
[SECURITY.md](SECURITY.md).
