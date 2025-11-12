// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.ApiTesting;

/// <summary>
/// Represents a saved HTTP request that can be stored in history or as a favorite.
/// </summary>
public sealed class SavedRequest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required string Headers { get; init; }
    public required string Body { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime LastUsedAt { get; init; }
    public bool IsFavorite { get; set; }
    public string? Description { get; init; }
}
