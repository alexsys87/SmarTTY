# Command completion database regression checks

Run with the .NET 8 SDK, without Windows, SSH or extra test packages:

```sh
dotnet run --project tests/CommandCompletionDatabaseTests
```

This console harness links the production completion classes directly. A failed
assertion exits unsuccessfully. It exercises missing/malformed/empty/nil XML,
null command and option entries, invalid and slow regexes, valid data, gzip
compatibility, DTD rejection and the document size limit.

The dictionary is optional: a load failure returns an empty database, records
`LoadWarning` and emits `Trace.TraceWarning`. Fix the dictionary and restart the
application to reload its cached database. Command/file/history completion does
not require this optional option dictionary. XML is limited to 16 Mi characters;
DTDs and external XML entities are not allowed. Regex matching has a 100 ms
timeout per rule; a timed-out rule is disabled for that session.

These checks are not a full Windows/WPF application build or a UI test.
