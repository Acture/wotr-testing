# Public documentation

- [Offline testing](offline.md): how it works, inputs, reports, environment
  boundaries and known limits
- [Interface fuzzing](fuzzing.md): automatic method calls, custom argument
  generators, counterexample shrinking and exact replay through `dotnet test`
- [WotR.Testing.Offline library](../src/WotR.Testing.Offline/README.md): project
  setup, build properties and API
- [Changelog](../CHANGELOG.md)
- [Contributing](../CONTRIBUTING.md)

Public code must build and test without private material. Game files come from the
user's own installation and are never part of this repository.
