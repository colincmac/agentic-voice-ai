# Instructions for GitHub and VisualStudio Copilot

## General

* Make only high confidence suggestions when reviewing code changes.
* Always use the latest version C#, currently C# 13 features.
* Never change `global.json` unless explicitly asked to.

## Formatting

* Apply code-formatting style if defined in `.editorconfig`.
* Prefer file-scoped namespace declarations and single-line using directives.
* Insert a newline before the opening curly brace of any code block (e.g., after `if`, `for`, `while`, `foreach`, `using`, `try`, etc.).
* Ensure that the final return statement of a method is on its own line.
* Use pattern matching and switch expressions wherever possible.
* Use `nameof` instead of string literals when referring to member names.
* Place private class declarations at the bottom of the file.

### Nullable Reference Types

* Declare variables non-nullable, and check for `null` at entry points.
* Always use `is null` or `is not null` instead of `== null` or `!= null`.
* Trust the C# null annotations and don't add null checks when the type system says a value cannot be null.

### Building

* Build from the root with `dotnet build`.
* If temporarily introducing warnings during a refactoring, you can add the flag `/p:TreatWarningsAsErrors=false` to your build command to prevent the build from failing. However before you finish your work, you must strive to fix any warnings as well.

### Testing

* We use xUnit SDK v3 with Microsoft.Testing.Platform (https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro)
* Do not emit "Act", "Arrange" or "Assert" comments.
* We do not use any mocking framework at the moment.
* Copy existing style in nearby files for test method names and capitalization.
* Do not use Directory.SetCurrentDirectory in tests as it can cause side effects when tests execute concurrently.

## Running tests

(1) Build from the root with `dotnet build`.
(2) If that produces errors, fix those errors and build again. Repeat until the build is successful.
(3) To then run tests, use a command similar to this `dotnet test test/Showcase.Seq.Tests/Showcase.Seq.Tests.csproj` (using the path to whatever projects are applicable to the change).

Note that tests for a project can be executed without first building from the root.

(4) To run just certain tests, it's important to include the filter after `--`, for example `dotnet test test/Showcase.Seq.Tests/Showcase.Seq.Tests.csproj --no-build --logger "console;verbosity=detailed" -- --filter "TestingBuilderHasAllPropertiesFromRealBuilder"`

## Snapshot Testing with Verify

* We use the Verify library (Verify.XunitV3) for snapshot testing in several test projects.
* Snapshot files are stored in `Snapshots` directories within test projects.
* When tests that use snapshot testing are updated and generate new output, the snapshots need to be accepted.
* Use `dotnet verify accept -y` to accept all pending snapshot changes after running tests.
* The verify tool is available globally as part of the copilot setup.
