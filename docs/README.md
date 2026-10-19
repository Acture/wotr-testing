# Public documentation

- [Offline testing](offline.md): how it works, inputs, reports, environment
  boundaries and known limits
- [Interface fuzzing](fuzzing.md): automatic method calls, custom argument
  generators, counterexample shrinking and exact replay through `dotnet test`
- [In-game testing](ingame.md): tests and fuzz targets inside the running game,
  one `dotnet test` for offgame and in-game partitions, connection, focus, reports
  and limits
- [WotR.Testing.Offline library](../src/WotR.Testing.Offline/README.md): project
  setup, build properties and API
- [WotR.Testing.InGame library](../src/WotR.Testing.InGame/README.md): host
  installation and in-game test API
- [Changelog](../CHANGELOG.md)
- [Contributing](../CONTRIBUTING.md)

Public code must build and test without private material. Game files come from the
user's own installation and are never part of this repository.
