# Third-party notices

Inbix is distributed under the [MIT License](LICENSE). It depends on the following
third-party components. All are permissive (MIT, Apache-2.0, public domain, or OFL)
and compatible with MIT distribution.

## MIT License

These are used under the MIT License (https://opensource.org/license/mit):

- SmtpServer — © cosullivan
- MimeKit — © Jeffrey Stedfast
- BouncyCastle.Cryptography — © The Legion of the Bouncy Castle Inc.
- Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core — © .NET Foundation and Contributors
- Microsoft.Extensions.* and Microsoft.AspNetCore.* — © .NET Foundation and Contributors
- Microsoft.OpenApi — © Microsoft
- Newtonsoft.Json — © James Newton-King
- System.* runtime packages — © .NET Foundation and Contributors

## Apache License 2.0

These are used under the Apache License, Version 2.0
(https://www.apache.org/licenses/LICENSE-2.0). The license requires that this
notice and any upstream NOTICE files be preserved when redistributing binaries:

- Dapper — © .NET Foundation and Contributors / Stack Exchange
- SQLitePCLRaw (core, bundle_e_sqlite3, lib.e_sqlite3, provider.e_sqlite3) — © Eric Sink / SourceGear
- DnsClient.NET — © Michael Conrad (used by the status page for MX/A/PTR lookups)

## Public domain

- SQLite (the `e_sqlite3` native engine bundled by SQLitePCLRaw) — public domain
  (https://www.sqlite.org/copyright.html)

## SIL Open Font License 1.1

The admin UI uses the following fonts, **vendored** (self-hosted) under
`src/Inbix.Web/wwwroot/fonts/` as `latin` / `latin-ext` woff2 subsets. They are
licensed under the SIL Open Font License 1.1 (https://openfontlicense.org), which
applies to the font files only and does not affect Inbix's MIT license. The full
license text for each is kept alongside the fonts:

- Inter — © The Inter Project Authors — see `wwwroot/fonts/Inter-OFL.txt`
- JetBrains Mono — © The JetBrains Mono Project Authors — see `wwwroot/fonts/JetBrainsMono-OFL.txt`

## Test-only dependencies (not shipped)

The following are used only to build and run the test suite (`Inbix.Tests`) and are
**not** part of the distributed application:

- xunit and related packages — Apache License 2.0
- Microsoft.NET.Test.Sdk, Microsoft.CodeCoverage — Microsoft .NET Library license
  (build/test tooling only; not redistributed)
