// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.ApiTesting;

/// <summary>
/// Represents an environment variable that can be used in API requests.
/// </summary>
public sealed class EnvironmentVariable
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public bool IsSecret { get; init; }
}
