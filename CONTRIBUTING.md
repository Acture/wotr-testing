# Contributing

Read [README.md](README.md), [CLAUDE.md](CLAUDE.md) and [docs/offline.md](docs/offline.md)
before changing behavior. Report bugs and request features in
[GitHub Issues](https://github.com/Acture/wotr-testing/issues); the maintainer
tracks planned work in Linear.

Make one focused change and preserve unrelated edits. Run the repository checks in
[CLAUDE.md](CLAUDE.md), build the solution against your own WotR installation and
verify the change through a consumer mod's offline tests. In the pull request,
describe the behavior change, the checks actually run, the game version and the
validation limits.

Never attach game files or snapshots of them (DLLs, bundles, localization files) to
commits, pull requests, issues or test reports. Keep `GamePath.props`, credentials
and local configuration out of Git. For vulnerability reports, follow
[SECURITY.md](SECURITY.md).
