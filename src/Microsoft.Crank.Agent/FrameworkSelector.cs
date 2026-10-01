// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text.RegularExpressions;

namespace Microsoft.Crank.Agent;

internal sealed record FrameworkSelector(string Requested, string Value, bool IsBuildCache)
{
    private static readonly Regex Commit = new("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);

    public string CommitSha => IsBuildCache && Value != "ci" ? Value : null;

    public static bool IsCommit(string value) => value != null && Commit.IsMatch(value);

    public static FrameworkSelector Parse(string requested, string channel)
    {
        var value = string.IsNullOrEmpty(requested) ? channel ?? "" : requested;
        if (IsCommit(value))
        {
            return new(requested ?? "", value.ToLowerInvariant(), true);
        }

        if (string.Equals(value, "ci", StringComparison.OrdinalIgnoreCase))
        {
            return new(requested ?? "", "ci", true);
        }

        // Resolve feed aliases and legacy version prefixes only after distinguishing commit pins.
        return new(requested ?? "", value.ToLowerInvariant(), false);
    }
}
