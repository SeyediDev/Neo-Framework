# Neo Companion CLI

A .NET tool for generating a runnable administrative resource using [Neo Framework](https://github.com/SeyediDev/Neo-Framework).

## Install

Requires the .NET 10 SDK to build and run generated applications.

```sh
dotnet tool install --global Neo.Companion.Cli --version 0.6.0
neo --help
```

This package contains the `neo` feature generator. Neo's MCP server and skills are distributed separately through the repository's Companion ZIP.

## Generate a feature

Version 0.6.0 generates source against Neo's new CRUD APIs. Use the matching source checkout; do not assume older Neo NuGet packages contain these APIs.

```sh
git clone https://github.com/SeyediDev/Neo-Framework.git
git -C Neo-Framework checkout e349ba5
neo new feature Book --namespace MyApp.Catalog --route books --output ./BookApi --neo-root ./Neo-Framework
dotnet run --project BookApi -- --doctor
```

`BookApi` must be a new directory. Existing files and directories are never overwritten or merged. Symlink/junction paths and invalid identifiers are rejected. The generator writes source only; it does not build, start an application or access a database.

The output includes:

- Separate create, update and read DTOs with explicit domain mapping.
- A resource definition and generic CRUD controller with per-operation authorization.
- Optimistic version checking, or SQL Server pessimistic transaction locks.
- Entity, translation and Outbox staging within one transaction.
- Local runtime Doctor checks for DI, EF metadata and policy configuration.

Read the generated README before starting the app. Configure your own `DemoToken` environment variable for write access, then run `dotnet run --project BookApi`. The app listens on `http://127.0.0.1:5092` and uses local SQLite by default. Pessimistic mode requires SQL Server.

Before production, replace demo authentication, define tenant/ownership scope, add database migrations and configure an Outbox dispatcher and message handler. The scaffold uses `EnsureCreated` for a new demo database and hard deletion. Doctor does not validate the physical database schema.

## Documentation

- [CRUD resources and concurrency (Persian)](https://github.com/SeyediDev/Neo-Framework/blob/e349ba5/tools/Neo.Companion/docs/CRUD-RESOURCES.fa.md)
- [Runnable example (Persian)](https://github.com/SeyediDev/Neo-Framework/blob/e349ba5/tools/Neo.Companion/samples/CrudResourceDemo/README.fa.md)
- [Source and issues](https://github.com/SeyediDev/Neo-Framework)

MIT license. Maintained by Javad Seyedi.
