// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

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

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    [Parameter]
    public string? ResourceName { get; set; }

    public ApiTestingPageViewModel PageViewModel { get; set; } = null!;

    protected override Task OnInitializedAsync()
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

        return Task.CompletedTask;
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

    private Task HandleSelectedResourceChanged()
    {
        NavigationManager.NavigateTo(DashboardUrls.ApiTestingUrl(resource: PageViewModel.SelectedResource.Id?.ReplicaSetName));
        return Task.CompletedTask;
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
