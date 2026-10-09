# Contributing

Read [README.md](README.md), [CLAUDE.md](CLAUDE.md) and [docs/offline.md](docs/offline.md)
before changing behavior. Tasks are tracked in the Linear project `wotr-testing`;
GitHub Issues are not the tracker. To propose a change, contact
[Acture](https://github.com/Acture).

Make one focused change and preserve unrelated edits. Run the repository checks in
[CLAUDE.md](CLAUDE.md), build the solution against your own WotR installation and
verify the change through a consumer mod's offline tests. In the pull request,
describe the behavior change, the checks actually run, the game version and the
validation limits.

Never attach game files or snapshots of them (DLLs, bundles, localization files) to
commits, pull requests, issues or test reports. Keep `GamePath.props`, credentials
and local configuration out of Git. For vulnerability reports, follow
[SECURITY.md](SECURITY.md).
