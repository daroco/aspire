// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.ApiTesting;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Pages;

public partial class ApiTesting : IDisposable
{
    private SelectViewModel<ResourceTypeDetails> _allResource = default!;
    private List<OtlpResource> _resources = default!;
    private List<SelectViewModel<ResourceTypeDetails>> _resourceViewModels = default!;
    private Subscription? _resourcesSubscription;
    private AspirePageContentLayout? _contentLayout;
    private readonly ConcurrentDictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private CancellationTokenSource? _dashboardResourceSubscriptionCts;
    
    private string _selectedMethod = "GET";
    private string _requestUrl = string.Empty;
    private string _requestHeaders = string.Empty;
    private string _requestBody = string.Empty;
    private bool _isLoading;
    private ResponseInfo? _responseInfo;
    private readonly HttpClient _httpClient = new();
    private readonly List<SavedRequest> _history = [];
    private readonly List<SavedRequest> _favorites = [];
    private readonly List<EnvironmentVariable> _environmentVariables = [];
    private readonly List<OpenApiEndpoint> _discoveredEndpoints = [];
    private ApiTestingService _apiTestingService = default!;

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.ApiTesting> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required TelemetryRepository TelemetryRepository { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required ILogger<ApiTesting> Logger { get; init; }

    [Inject]
    public required ILocalStorage LocalStorage { get; init; }

    [Inject]
    public required IJSRuntime JSRuntime { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    [Parameter]
    public string? ResourceName { get; set; }

    public ApiTestingPageViewModel PageViewModel { get; set; } = null!;

    protected override async Task OnInitializedAsync()
    {
        _allResource = new SelectViewModel<ResourceTypeDetails>
        {
            Id = null,
            Name = ControlsStringsLoc[nameof(Dashboard.Resources.ControlsStrings.LabelAll)]
        };

        PageViewModel = new ApiTestingPageViewModel { SelectedResource = _allResource };

        UpdateResources();
        _resourcesSubscription = TelemetryRepository.OnNewResources(() => InvokeAsync(() =>
        {
            UpdateResources();
            StateHasChanged();
        }));

        // Initialize API Testing Service
        _apiTestingService = new ApiTestingService(LocalStorage);
        await _apiTestingService.InitializeAsync();
        RefreshCollections();

        // Subscribe to dashboard resources to get URLs
        if (DashboardClient.IsEnabled)
        {
            Logger.LogInformation("[API Testing] DashboardClient is enabled, subscribing to resources");
            _dashboardResourceSubscriptionCts = new CancellationTokenSource();
            var (snapshot, subscription) = await DashboardClient.SubscribeResourcesAsync(_dashboardResourceSubscriptionCts.Token);

            Logger.LogInformation("[API Testing] Received {Count} resources in initial snapshot", snapshot.Length);
            foreach (var resource in snapshot)
            {
                _resourceByName[resource.Name] = resource;
                Logger.LogDebug("[API Testing] Initial resource: {ResourceName} with {UrlCount} URLs", 
                    resource.Name, resource.Urls.Length);
            }

            _ = Task.Run(async () =>
            {
                await foreach (var batch in subscription.ConfigureAwait(false))
                {
                    foreach (var (changeType, resource) in batch)
                    {
                        // Update the dictionary but don't trigger UI refresh unless it affects the selected resource
                        var shouldRefresh = false;
                        
                        if (changeType == ResourceViewModelChangeType.Upsert)
                        {
                            _resourceByName[resource.Name] = resource;
                            Logger.LogDebug("[API Testing] Resource update: {ResourceName} with {UrlCount} URLs", 
                                resource.Name, resource.Urls.Length);
                            
                            // Only refresh if this update affects the currently selected resource
                            if (PageViewModel.SelectedResource.Id != null)
                            {
                                var selectedReplicaSet = PageViewModel.SelectedResource.Id.ReplicaSetName;
                                var selectedInstance = PageViewModel.SelectedResource.Id.InstanceId;
                                
                                shouldRefresh = resource.Name == selectedReplicaSet ||
                                               resource.Name == selectedInstance ||
                                               (selectedReplicaSet != null && resource.Name.StartsWith(selectedReplicaSet, StringComparison.OrdinalIgnoreCase));
                            }
                        }
                        else if (changeType == ResourceViewModelChangeType.Delete)
                        {
                            _resourceByName.TryRemove(resource.Name, out _);
                            Logger.LogDebug("[API Testing] Resource deleted: {ResourceName}", resource.Name);
                        }
                        
                        if (shouldRefresh)
                        {
                            await InvokeAsync(StateHasChanged);
                        }
                    }
                }
            }, _dashboardResourceSubscriptionCts.Token);
        }
        else
        {
            Logger.LogWarning("[API Testing] DashboardClient is NOT enabled!");
        }
    }

    protected override void OnParametersSet()
    {
        Logger.LogInformation("[API Testing] OnParametersSet called. ResourceName parameter: {ResourceName}", ResourceName);
        
        var selectedResource = _allResource;

        if (!string.IsNullOrEmpty(ResourceName) && _resourceViewModels is { Count: > 0 })
        {
            var resource = _resourceViewModels.GetResource(Logger, ResourceName, canSelectGrouping: false, _allResource);
            selectedResource = resource;
            Logger.LogInformation("[API Testing] OnParametersSet: Found resource. ReplicaSetName: {ReplicaSetName}, InstanceId: {InstanceId}", 
                selectedResource.Id?.ReplicaSetName, selectedResource.Id?.InstanceId);
        }
        else
        {
            Logger.LogInformation("[API Testing] OnParametersSet: Using default 'All' resource");
        }

        PageViewModel.SelectedResource = selectedResource;
    }

    private void UpdateResources()
    {
        _resources = TelemetryRepository.GetResources();
        _resourceViewModels = ResourcesSelectHelpers.CreateResources(_resources);
        _resourceViewModels.Insert(0, _allResource);
    }

    private async Task HandleSelectedResourceChanged()
    {
        Logger.LogInformation("[API Testing] HandleSelectedResourceChanged called. Name: {Name}, ReplicaSetName: {ReplicaSetName}, InstanceId: {InstanceId}", 
            PageViewModel.SelectedResource.Name,
            PageViewModel.SelectedResource.Id?.ReplicaSetName, 
            PageViewModel.SelectedResource.Id?.InstanceId);
        
        await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] HandleSelectedResourceChanged called. Name:", PageViewModel.SelectedResource.Name,
            "ReplicaSetName:", PageViewModel.SelectedResource.Id?.ReplicaSetName, "InstanceId:", PageViewModel.SelectedResource.Id?.InstanceId);
        
        // Clear endpoints immediately to show we're discovering
        _discoveredEndpoints.Clear();
        
        // Determine the resource identifier to use in the URL
        // For replica resources, use ReplicaSetName to group them together
        // For single resources, use the Name
        string? urlResourceName = null;
        if (PageViewModel.SelectedResource.Id != null)
        {
            urlResourceName = !string.IsNullOrEmpty(PageViewModel.SelectedResource.Id.ReplicaSetName)
                ? PageViewModel.SelectedResource.Id.ReplicaSetName
                : PageViewModel.SelectedResource.Name;
        }
            
        Logger.LogInformation("[API Testing] Navigating to URL with resource: {Resource}", urlResourceName);
        NavigationManager.NavigateTo(DashboardUrls.ApiTestingUrl(resource: urlResourceName));
        
        // Discover OpenAPI endpoints when a resource is selected
        // NOTE: The PageViewModel.SelectedResource is already set correctly by the dropdown binding
        // We don't need to wait for OnParametersSet
        await DiscoverOpenApiEndpointsAsync();
    }

    private void RefreshCollections()
    {
        _history.Clear();
        _history.AddRange(_apiTestingService.GetHistory());
        _favorites.Clear();
        _favorites.AddRange(_apiTestingService.GetFavorites());
        _environmentVariables.Clear();
        _environmentVariables.AddRange(_apiTestingService.GetEnvironmentVariables());
    }

    private static string CleanBaseUrl(string rawUrl)
    {
        // Strip UI-only paths from the base URL as they shouldn't be part of API base URLs
        // Common UI paths: /swagger, /swagger/index.html, /openapi, etc.
        // These are just UI endpoints, not the base URL for API calls
        var baseUrl = rawUrl.TrimEnd('/');
        
        if (baseUrl.EndsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.EndsWith("/swagger/index.html", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.EndsWith("/openapi", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(rawUrl);
            var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            
            // Remove the last segment if it's a UI path
            if (pathSegments.Length > 0)
            {
                var lastSegment = pathSegments[^1];
                if (lastSegment.Equals("swagger", StringComparison.OrdinalIgnoreCase) ||
                    lastSegment.Equals("index.html", StringComparison.OrdinalIgnoreCase) ||
                    lastSegment.Equals("openapi", StringComparison.OrdinalIgnoreCase))
                {
                    baseUrl = $"{uri.Scheme}://{uri.Authority}";
                    if (pathSegments.Length > 1)
                    {
                        // Rebuild path without the last segment(s)
                        var segments = pathSegments.Take(pathSegments.Length - 1).ToList();
                        // Also remove "swagger" from earlier positions if it exists
                        segments.RemoveAll(s => s.Equals("swagger", StringComparison.OrdinalIgnoreCase) || 
                                               s.Equals("openapi", StringComparison.OrdinalIgnoreCase));
                        if (segments.Count > 0)
                        {
                            baseUrl += "/" + string.Join("/", segments);
                        }
                    }
                }
            }
        }
        
        return baseUrl;
    }

    private ResourceViewModel? FindDashboardResource()
    {
        if (PageViewModel.SelectedResource.Id is null)
        {
            Logger.LogDebug("[API Testing] FindDashboardResource: SelectedResource.Id is null");
            return null;
        }

        Logger.LogInformation("[API Testing] FindDashboardResource called. ReplicaSetName: {ReplicaSetName}, InstanceId: {InstanceId}", 
            PageViewModel.SelectedResource.Id.ReplicaSetName, 
            PageViewModel.SelectedResource.Id.InstanceId);
        Logger.LogInformation("[API Testing] _resourceByName dictionary has {Count} resources", _resourceByName.Count);
        Logger.LogInformation("[API Testing] Available resources in _resourceByName: {Resources}", 
            string.Join(", ", _resourceByName.Keys));

        ResourceViewModel? dashboardResource = null;
        
        // FIRST: Try exact InstanceId match (this is the actual resource name like "catalogservice-pxmnvdbz")
        if (!string.IsNullOrEmpty(PageViewModel.SelectedResource.Id.InstanceId))
        {
            var instanceId = PageViewModel.SelectedResource.Id.InstanceId;
            Logger.LogInformation("[API Testing] [1] Trying to find resource by exact InstanceId: {InstanceId}", instanceId);
            
            if (_resourceByName.TryGetValue(instanceId, out dashboardResource))
            {
                Logger.LogInformation("[API Testing] ✓ Found resource by exact InstanceId match: {ResourceName}", dashboardResource.Name);
                return dashboardResource;
            }
            else
            {
                Logger.LogDebug("[API Testing] Not found by exact InstanceId");
            }
        }
        
        // SECOND: Try with ReplicaSetName (for resources without replicas or grouped resources)
        if (!string.IsNullOrEmpty(PageViewModel.SelectedResource.Id.ReplicaSetName))
        {
            var replicaSetName = PageViewModel.SelectedResource.Id.ReplicaSetName;
            Logger.LogInformation("[API Testing] [2] Trying to find resource by ReplicaSetName: {ReplicaSetName}", replicaSetName);
            
            if (_resourceByName.TryGetValue(replicaSetName, out dashboardResource))
            {
                Logger.LogInformation("[API Testing] ✓ Found resource by exact ReplicaSetName match: {ResourceName}", dashboardResource.Name);
                return dashboardResource;
            }
            else
            {
                Logger.LogDebug("[API Testing] Not found by exact ReplicaSetName");
            }
            
            // Try with instance ID appended (pattern: ReplicaSetName_InstanceId)
            if (!string.IsNullOrEmpty(PageViewModel.SelectedResource.Id.InstanceId))
            {
                var fullName = $"{replicaSetName}_{PageViewModel.SelectedResource.Id.InstanceId}";
                Logger.LogInformation("[API Testing] [3] Trying with full name pattern: {FullName}", fullName);
                
                if (_resourceByName.TryGetValue(fullName, out dashboardResource))
                {
                    Logger.LogInformation("[API Testing] ✓ Found resource by full name pattern: {ResourceName}", dashboardResource.Name);
                    return dashboardResource;
                }
                else
                {
                    Logger.LogDebug("[API Testing] Not found by full name pattern");
                }
            }
            
            // Last resort: prefix match (finds any resource starting with replica set name)
            Logger.LogInformation("[API Testing] [4] Trying prefix match for: {ReplicaSetName}", replicaSetName);
            dashboardResource = _resourceByName.Values.FirstOrDefault(r => 
                r.Name.StartsWith(replicaSetName, StringComparison.OrdinalIgnoreCase));
            
            if (dashboardResource != null)
            {
                Logger.LogWarning("[API Testing] ! Found resource by prefix match (fallback): {ResourceName} - This may not be the exact instance selected!", 
                    dashboardResource.Name);
                return dashboardResource;
            }
        }

        if (dashboardResource != null)
        {
            Logger.LogInformation("[API Testing] FindDashboardResource SUCCESS: Found {ResourceName} with {UrlCount} URLs", 
                dashboardResource.Name, dashboardResource.Urls.Length);
            
            for (int i = 0; i < dashboardResource.Urls.Length; i++)
            {
                Logger.LogInformation("[API Testing]   URL[{Index}]: {Url}", i, dashboardResource.Urls[i].Url);
            }
        }
        else
        {
            Logger.LogWarning("[API Testing] FindDashboardResource FAILED: Resource not found");
        }

        return dashboardResource;
    }

    private async Task DiscoverOpenApiEndpointsAsync()
    {
        Logger.LogInformation("[API Testing] ===== DiscoverOpenApiEndpointsAsync START =====");
        await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] ===== DiscoverOpenApiEndpointsAsync START =====");
        _discoveredEndpoints.Clear();

        var dashboardResource = FindDashboardResource();
        if (dashboardResource == null)
        {
            Logger.LogWarning("[API Testing] DiscoverOpenApiEndpointsAsync: dashboardResource is NULL - aborting");
            await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] ===== END - dashboardResource is NULL =====");
            if (PageViewModel.SelectedResource.Id != null)
            {
                Logger.LogDebug("[API Testing] Resource not found in dashboard client. ReplicaSetName: {ReplicaSetName}, InstanceId: {InstanceId}", 
                    PageViewModel.SelectedResource.Id.ReplicaSetName, 
                    PageViewModel.SelectedResource.Id.InstanceId);
                Logger.LogDebug("[API Testing] Available resources: {Resources}", string.Join(", ", _resourceByName.Keys));
            }
            StateHasChanged();
            return;
        }

        Logger.LogInformation("[API Testing] Found dashboard resource: {ResourceName}, URLs count: {UrlCount}", 
            dashboardResource.Name, dashboardResource.Urls.Length);
        await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] Found dashboard resource:", dashboardResource.Name, "URLs count:", dashboardResource.Urls.Length);

        // Get URLs from the resource
        if (dashboardResource.Urls.Length == 0)
        {
            Logger.LogWarning("[API Testing] Resource {ResourceName} has no URLs - aborting", dashboardResource.Name);
            await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] ===== END - No URLs found for resource:", dashboardResource.Name);
            StateHasChanged();
            return;
        }

        Logger.LogInformation("[API Testing] Resource {ResourceName} has {UrlCount} URLs, attempting OpenAPI discovery", 
            dashboardResource.Name, dashboardResource.Urls.Length);

        // Log all URLs that will be tried
        await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] Will try OpenAPI discovery on these base URLs:");
        for (int i = 0; i < dashboardResource.Urls.Length; i++)
        {
            var url = dashboardResource.Urls[i].Url.ToString();
            Logger.LogInformation("[API Testing]   Base URL[{Index}]: {Url}", i, url);
            await JSRuntime.InvokeVoidAsync("console.log", $"[API Testing]   Base URL[{i}]: {url}");
        }

        // Try each URL with common OpenAPI endpoints
        var openApiPaths = new[] { "/swagger/v1/swagger.json", "/openapi.json", "/api/openapi.json" };
        
        foreach (var urlViewModel in dashboardResource.Urls)
        {
            var rawUrl = urlViewModel.Url.ToString();
            var baseUrl = CleanBaseUrl(rawUrl);
            
            if (rawUrl != baseUrl)
            {
                Logger.LogInformation("[API Testing] Cleaned base URL. Original: {RawUrl}, Cleaned: {BaseUrl}", rawUrl, baseUrl);
                await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] Cleaned base URL. Original:", rawUrl, "Cleaned:", baseUrl);
            }
            
            Logger.LogInformation("[API Testing] Trying base URL: {BaseUrl}", baseUrl);
            await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] Trying base URL:", baseUrl);
            
            foreach (var path in openApiPaths)
            {
                try
                {
                    var openApiUrl = baseUrl + path;
                    
                    Logger.LogInformation("[API Testing] >>> Attempting to fetch OpenAPI spec from: {OpenApiUrl}", openApiUrl);
                    await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] >>> Attempting to fetch OpenAPI spec from:", openApiUrl);
                    
                    var response = await _httpClient.GetAsync(openApiUrl);
                    
                    Logger.LogInformation("[API Testing] >>> Response status: {StatusCode}", response.StatusCode);
                    await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] >>> Response status:", response.StatusCode.ToString());
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        Logger.LogInformation("[API Testing] ✓✓✓ SUCCESS! Retrieved OpenAPI spec from {OpenApiUrl}, content length: {Length}", 
                            openApiUrl, content.Length);
                        await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] ✓✓✓ SUCCESS! Retrieved OpenAPI spec from:", openApiUrl, "content length:", content.Length);
                        ParseOpenApiSpec(content);
                        Logger.LogInformation("[API Testing] Parsed {EndpointCount} endpoints from OpenAPI spec", _discoveredEndpoints.Count);
                        await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] Parsed endpoints:", _discoveredEndpoints.Count);
                        StateHasChanged();
                        return; // Found and parsed successfully
                    }
                    else
                    {
                        Logger.LogDebug("[API Testing] >>> Failed with status {StatusCode}", response.StatusCode);
                        await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] >>> Failed with status:", response.StatusCode.ToString());
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "[API Testing] >>> Exception fetching OpenAPI spec from {BaseUrl}{Path}: {Message}", 
                        baseUrl, path, ex.Message);
                    await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] >>> Exception:", ex.Message);
                    // Continue to next path
                }
            }
        }

        Logger.LogWarning("[API Testing] ===== DiscoverOpenApiEndpointsAsync END - No OpenAPI spec found for resource {ResourceName} =====", 
            dashboardResource.Name);
        await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] ===== END - No OpenAPI spec found for resource:", dashboardResource.Name);
        StateHasChanged();
    }

    private void ParseOpenApiSpec(string openApiJson)
    {
        Logger.LogInformation("[API Testing] ParseOpenApiSpec: Starting to parse OpenAPI spec, length: {Length}", openApiJson.Length);
        try
        {
            var doc = JsonDocument.Parse(openApiJson);
            Logger.LogInformation("[API Testing] ParseOpenApiSpec: Successfully parsed JSON");
            var root = doc.RootElement;

            if (root.TryGetProperty("paths", out var paths))
            {
                Logger.LogInformation("[API Testing] ParseOpenApiSpec: Found 'paths' property");
                var pathCount = 0;
                foreach (var path in paths.EnumerateObject())
                {
                    var pathValue = path.Name;
                    Logger.LogDebug("[API Testing] ParseOpenApiSpec: Processing path: {Path}", pathValue);
                    
                    foreach (var operation in path.Value.EnumerateObject())
                    {
                        var method = operation.Name.ToUpperInvariant();
                        if (method is "GET" or "POST" or "PUT" or "DELETE" or "PATCH")
                        {
                            var summary = operation.Value.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                            var description = operation.Value.TryGetProperty("description", out var d) ? d.GetString() : null;

                            var endpoint = new OpenApiEndpoint
                            {
                                Path = pathValue,
                                Method = method,
                                Summary = summary,
                                Description = description
                            };
                            _discoveredEndpoints.Add(endpoint);
                            pathCount++;
                            Logger.LogInformation("[API Testing] ParseOpenApiSpec: Added endpoint: {Method} {Path}", method, pathValue);
                        }
                    }
                }
                Logger.LogInformation("[API Testing] ParseOpenApiSpec: FINISHED - Added {Count} endpoints total", pathCount);
            }
            else
            {
                Logger.LogWarning("[API Testing] ParseOpenApiSpec: No 'paths' property found in OpenAPI spec!");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[API Testing] ParseOpenApiSpec: Exception parsing OpenAPI spec: {Message}", ex.Message);
        }
    }

    private async Task LoadEndpoint(OpenApiEndpoint endpoint)
    {
        Logger.LogInformation("[API Testing] LoadEndpoint called: {Method} {Path}", endpoint.Method, endpoint.Path);
        await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] LoadEndpoint called:", endpoint.Method, endpoint.Path);
        
        _selectedMethod = endpoint.Method;
        
        // Try to get the base URL from the selected resource
        var dashboardResource = FindDashboardResource();
        if (dashboardResource != null && dashboardResource.Urls.Length > 0)
        {
            var rawUrl = dashboardResource.Urls[0].Url.ToString();
            var baseUrl = CleanBaseUrl(rawUrl);
            _requestUrl = $"{baseUrl}{endpoint.Path}";
            Logger.LogInformation("[API Testing] Set request URL to: {Url}", _requestUrl);
            await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] Set request URL to:", _requestUrl);
        }
        else
        {
            _requestUrl = endpoint.Path;
            Logger.LogInformation("[API Testing] Set request URL to path only: {Path}", _requestUrl);
            await JSRuntime.InvokeVoidAsync("console.log", "[API Testing] Set request URL to path only:", _requestUrl);
        }
        
        StateHasChanged();
    }

    private void LoadFromHistory(SavedRequest request)
    {
        _selectedMethod = request.Method;
        _requestUrl = request.Url;
        _requestHeaders = request.Headers;
        _requestBody = request.Body;
        StateHasChanged();
    }

    private async Task AddToFavoritesAsync()
    {
        var request = new SavedRequest
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{_selectedMethod} {_requestUrl}",
            Method = _selectedMethod,
            Url = _requestUrl,
            Headers = _requestHeaders,
            Body = _requestBody,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            IsFavorite = true
        };

        await _apiTestingService.AddToFavoritesAsync(request);
        RefreshCollections();
        StateHasChanged();
    }

    private async Task RemoveFromFavoritesAsync(string id)
    {
        await _apiTestingService.RemoveFromFavoritesAsync(id);
        RefreshCollections();
        StateHasChanged();
    }

    private async Task ClearHistoryAsync()
    {
        await _apiTestingService.ClearHistoryAsync();
        RefreshCollections();
        StateHasChanged();
    }

    private async Task ExportAsCurlAsync()
    {
        var curl = GenerateCurlCommand();
        await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", curl);
    }

    private async Task ExportAsCSharpAsync()
    {
        var csharp = GenerateCSharpCode();
        await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", csharp);
    }

    private string GenerateCurlCommand()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"curl -X {_selectedMethod}");

        // Apply environment variable substitution
        var url = _apiTestingService.ReplaceEnvironmentVariables(_requestUrl);
        sb.Append(CultureInfo.InvariantCulture, $" \"{url}\"");

        // Add headers
        if (!string.IsNullOrWhiteSpace(_requestHeaders))
        {
            foreach (var line in _requestHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(':', 2);
                if (parts.Length == 2)
                {
                    var headerValue = _apiTestingService.ReplaceEnvironmentVariables(parts[1].Trim());
                    sb.Append(CultureInfo.InvariantCulture, $" -H \"{parts[0].Trim()}: {headerValue}\"");
                }
            }
        }

        // Add body
        if (!string.IsNullOrWhiteSpace(_requestBody))
        {
            var body = _apiTestingService.ReplaceEnvironmentVariables(_requestBody);
            sb.Append(CultureInfo.InvariantCulture, $" -d '{body}'");
        }

        return sb.ToString();
    }

    private string GenerateCSharpCode()
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Net.Http;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine();
        sb.AppendLine("var httpClient = new HttpClient();");
        sb.AppendLine();

        // Apply environment variable substitution
        var url = _apiTestingService.ReplaceEnvironmentVariables(_requestUrl);
        sb.AppendLine($"var request = new HttpRequestMessage");
        sb.AppendLine("{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    Method = HttpMethod.{_selectedMethod[..1] + _selectedMethod[1..].ToLowerInvariant()},");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    RequestUri = new Uri(\"{url}\")");
        sb.AppendLine("};");
        sb.AppendLine();

        // Add headers
        if (!string.IsNullOrWhiteSpace(_requestHeaders))
        {
            foreach (var line in _requestHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(':', 2);
                if (parts.Length == 2)
                {
                    var headerValue = _apiTestingService.ReplaceEnvironmentVariables(parts[1].Trim());
                    sb.AppendLine(CultureInfo.InvariantCulture, $"request.Headers.Add(\"{parts[0].Trim()}\", \"{headerValue}\");");
                }
            }
            sb.AppendLine();
        }

        // Add body
        if (!string.IsNullOrWhiteSpace(_requestBody))
        {
            var body = _apiTestingService.ReplaceEnvironmentVariables(_requestBody);
            sb.AppendLine(CultureInfo.InvariantCulture, $"request.Content = new StringContent(@\"{body}\", Encoding.UTF8, \"application/json\");");
            sb.AppendLine();
        }

        sb.AppendLine("var response = await httpClient.SendAsync(request);");
        sb.AppendLine("var content = await response.Content.ReadAsStringAsync();");
        sb.AppendLine("Console.WriteLine(content);");

        return sb.ToString();
    }

    private async Task SendRequestAsync()
    {
        if (string.IsNullOrWhiteSpace(_requestUrl))
        {
            return;
        }

        _isLoading = true;
        _responseInfo = null;
        StateHasChanged();

        try
        {
            // Build the request URL
            var targetUrl = _requestUrl;
            if (!targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                !targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Try to get base URL from selected resource
                var dashboardResource = FindDashboardResource();
                if (dashboardResource != null && dashboardResource.Urls.Length > 0)
                {
                    var rawUrl = dashboardResource.Urls[0].Url.ToString();
                    var baseUrl = CleanBaseUrl(rawUrl);
                    targetUrl = $"{baseUrl}/{_requestUrl.TrimStart('/')}";
                }
                else
                {
                    Logger.LogWarning("No resource selected or resource not found, using relative URL as-is");
                }
            }

            var stopwatch = Stopwatch.StartNew();
            
            // Create request
            using var request = new HttpRequestMessage(new HttpMethod(_selectedMethod), targetUrl);
            
            // Add headers
            if (!string.IsNullOrWhiteSpace(_requestHeaders))
            {
                foreach (var headerLine in _requestHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = headerLine.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var headerName = parts[0].Trim();
                        var headerValue = parts[1].Trim();
                        
                        if (string.Equals(headerName, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            request.Content?.Headers.ContentType = MediaTypeHeaderValue.Parse(headerValue);
                        }
                        else
                        {
                            request.Headers.TryAddWithoutValidation(headerName, headerValue);
                        }
                    }
                }
            }
            
            // Add body for non-GET requests
            if (_selectedMethod != "GET" && !string.IsNullOrWhiteSpace(_requestBody))
            {
                request.Content = new StringContent(_requestBody, Encoding.UTF8, "application/json");
            }

            // Send request
            using var response = await _httpClient.SendAsync(request);
            stopwatch.Stop();

            // Read response
            var responseBody = await response.Content.ReadAsStringAsync();
            var responseHeaders = new StringBuilder();
            foreach (var header in response.Headers)
            {
                responseHeaders.AppendLine(CultureInfo.InvariantCulture, $"{header.Key}: {string.Join(", ", header.Value)}");
            }
            foreach (var header in response.Content.Headers)
            {
                responseHeaders.AppendLine(CultureInfo.InvariantCulture, $"{header.Key}: {string.Join(", ", header.Value)}");
            }

            // Try to extract trace ID from response headers
            string? traceId = null;
            if (response.Headers.TryGetValues("traceparent", out var traceparentValues))
            {
                var traceparent = traceparentValues.FirstOrDefault();
                if (traceparent != null)
                {
                    var parts = traceparent.Split('-');
                    if (parts.Length >= 2)
                    {
                        traceId = parts[1];
                    }
                }
            }

            _responseInfo = new ResponseInfo
            {
                StatusCode = (int)response.StatusCode,
                StatusDescription = response.ReasonPhrase ?? string.Empty,
                Body = responseBody,
                Headers = responseHeaders.ToString(),
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                TraceId = traceId
            };

            // Add to history
            await _apiTestingService.AddToHistoryAsync(_selectedMethod, targetUrl, _requestHeaders, _requestBody);
            RefreshCollections();
        }
        catch (Exception ex)
        {
            _responseInfo = new ResponseInfo
            {
                StatusCode = 0,
                StatusDescription = "Error",
                Body = $"Error: {ex.Message}\n\n{ex.StackTrace}",
                Headers = string.Empty,
                ElapsedMilliseconds = 0,
                TraceId = null
            };
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    private static string GetStatusClass(int statusCode)
    {
        return statusCode switch
        {
            >= 200 and < 300 => "status-success",
            >= 400 and < 500 => "status-client-error",
            >= 500 => "status-server-error",
            _ => "status-other"
        };
    }

    private static string GetTraceUrl(string traceId)
    {
        return DashboardUrls.TraceDetailUrl(traceId);
    }

    public void Dispose()
    {
        _resourcesSubscription?.Dispose();
        _dashboardResourceSubscriptionCts?.Cancel();
        _dashboardResourceSubscriptionCts?.Dispose();
        _httpClient.Dispose();
    }

    public sealed class ApiTestingPageViewModel
    {
        public required SelectViewModel<ResourceTypeDetails> SelectedResource { get; set; }
    }

    private sealed class ResponseInfo
    {
        public required int StatusCode { get; init; }
        public required string StatusDescription { get; init; }
        public required string Body { get; init; }
        public required string Headers { get; init; }
        public required long ElapsedMilliseconds { get; init; }
        public required string? TraceId { get; init; }
    }
}
