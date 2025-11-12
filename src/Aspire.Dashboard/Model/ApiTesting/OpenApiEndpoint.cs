// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.ApiTesting;

/// <summary>
/// Represents an OpenAPI endpoint discovered from a service.
/// </summary>
public sealed class OpenApiEndpoint
{
    public required string Path { get; init; }
    public required string Method { get; init; }
    public required string Summary { get; init; }
    public required string? Description { get; init; }
    public List<OpenApiParameter> Parameters { get; init; } = [];
    public string? RequestBodySchema { get; init; }
}

/// <summary>
/// Represents a parameter in an OpenAPI endpoint.
/// </summary>
public sealed class OpenApiParameter
{
    public required string Name { get; init; }
    public required string In { get; init; } // query, header, path, cookie
    public required string Type { get; init; }
    public bool Required { get; init; }
    public string? Description { get; init; }
}
