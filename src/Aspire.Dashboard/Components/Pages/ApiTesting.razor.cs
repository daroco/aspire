// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    }

    protected override void OnParametersSet()
    {
        var selectedResource = _allResource;

        if (!string.IsNullOrEmpty(ResourceName) && _resourceViewModels is { Count: > 0 })
        {
            var resource = _resourceViewModels.GetResource(Logger, ResourceName, canSelectGrouping: false, _allResource);
            selectedResource = resource;
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
        NavigationManager.NavigateTo(DashboardUrls.ApiTestingUrl(resource: PageViewModel.SelectedResource.Id?.ReplicaSetName));
        
        // Discover OpenAPI endpoints when a resource is selected
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

    private async Task DiscoverOpenApiEndpointsAsync()
    {
        _discoveredEndpoints.Clear();

        if (PageViewModel.SelectedResource.Id is null)
        {
            return;
        }

        // Try to fetch OpenAPI spec from common endpoints
        var resource = _resources.FirstOrDefault(r => r.ResourceKey.ToString() == PageViewModel.SelectedResource.Id.InstanceId);
        if (resource == null)
        {
            return;
        }

        // Get base URL from resource
        var baseUrl = await TryGetResourceBaseUrlAsync();
        if (string.IsNullOrEmpty(baseUrl))
        {
            return;
        }

        // Try common OpenAPI endpoints
        var openApiPaths = new[] { "/swagger/v1/swagger.json", "/openapi.json", "/api/openapi.json" };
        
        foreach (var path in openApiPaths)
        {
            try
            {
                var openApiUrl = $"{baseUrl.TrimEnd('/')}{path}";
                var response = await _httpClient.GetAsync(openApiUrl);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    ParseOpenApiSpec(content);
                    break;
                }
            }
            catch
            {
                // Continue to next path
            }
        }

        StateHasChanged();
    }

    private static async Task<string?> TryGetResourceBaseUrlAsync()
    {
        // For now, return a placeholder. In a full implementation, this would
        // query the resource's actual endpoints from the dashboard client
        await Task.CompletedTask;
        return "http://localhost:5000";
    }

    private void ParseOpenApiSpec(string openApiJson)
    {
        try
        {
            var doc = JsonDocument.Parse(openApiJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("paths", out var paths))
            {
                foreach (var path in paths.EnumerateObject())
                {
                    var pathValue = path.Name;
                    
                    foreach (var operation in path.Value.EnumerateObject())
                    {
                        var method = operation.Name.ToUpperInvariant();
                        if (method is "GET" or "POST" or "PUT" or "DELETE" or "PATCH")
                        {
                            var summary = operation.Value.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                            var description = operation.Value.TryGetProperty("description", out var d) ? d.GetString() : null;

                            _discoveredEndpoints.Add(new OpenApiEndpoint
                            {
                                Path = pathValue,
                                Method = method,
                                Summary = summary,
                                Description = description
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to parse OpenAPI spec");
        }
    }

    private void LoadEndpoint(OpenApiEndpoint endpoint)
    {
        _selectedMethod = endpoint.Method;
        _requestUrl = endpoint.Path;
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
                // For now, just use the URL as-is if it doesn't start with http
                // TODO: Integrate with resource service to auto-populate base URLs
                targetUrl = "http://localhost:5000/" + _requestUrl.TrimStart('/');
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
