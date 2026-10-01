// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Text.Json;
using Microsoft.Crank.RegressionBot.Models;
using Xunit;

namespace Microsoft.Crank.RegressionBot.Tests;

public class BuildCacheVersionTests
{
    [Theory]
    [InlineData("netCoreAppVersion")]
    [InlineData("aspNetCoreVersion")]
    public void HistoricalCiVersionsProduceGitShasNotCiPrefixedHashes(string measurement)
    {
        var previous = "123456789012";
        var current = "abcdefabcdef0123456789abcdef0123456789abcd";
        string Document(string value) => JsonSerializer.Serialize(new
        {
            jobs = new System.Collections.Generic.Dictionary<string, object>
            {
                ["app"] = new { results = new System.Collections.Generic.Dictionary<string, string> { [measurement] = value }, dependencies = new object[0] }
            }
        });
        var regression = new Regression
        {
            PreviousResult = new() { Document = Document("12.0.0-ci+ci." + previous) },
            CurrentResult = new() { Document = Document("12.0.0-ci+" + current) }
        };
        regression.ComputeChanges();
        var change = Assert.Single(regression.Changes);
        Assert.Equal(previous, change.PreviousCommitHash);
        Assert.Equal(current, change.CurrentCommitHash);
        Assert.Equal("12.0.0-ci", change.CurrentVersion);
    }
}
