// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Test suite for the scheduler expression subsystem. Runs the shared conformance
// fixtures plus the .NET unit tests, and returns a non zero exit code on failure
// so it can gate CI.
//
// Usage: dotnet run [path-to-schedule-expression.json]

using Tide.Asgard.Scheduler.Tests;

var fixturePath = args.Length > 0 ? args[0] : FixtureTests.Locate();
if (fixturePath is null)
{
	Console.Error.WriteLine("could not locate tests/fixtures/schedule-expression.json");
	return 2;
}

var runner = new TestRunner();
FixtureTests.Run(runner, fixturePath);
ExpressionTests.Run(runner);

return runner.Report("schedule expression tests");
