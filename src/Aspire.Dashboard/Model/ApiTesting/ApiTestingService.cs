// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aspire.Dashboard.Model.ApiTesting;

/// <summary>
/// Service for managing API testing requests, history, favorites, and environment variables.
/// </summary>
public sealed partial class ApiTestingService
{
    private const string HistoryStorageKey = "ApiTesting_History";
    private const string FavoritesStorageKey = "ApiTesting_Favorites";
    private const string EnvironmentVariablesStorageKey = "ApiTesting_Environment";
    private const int MaxHistoryCount = 50;

    private readonly ILocalStorage _localStorage;
    private readonly ConcurrentDictionary<string, SavedRequest> _history = new();
    private readonly ConcurrentDictionary<string, SavedRequest> _favorites = new();
    private readonly ConcurrentDictionary<string, EnvironmentVariable> _environmentVariables = new();
    private bool _isInitialized;

    public ApiTestingService(ILocalStorage localStorage)
    {
        _localStorage = localStorage;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        // Load history
        var historyResult = await _localStorage.GetAsync<string>(HistoryStorageKey).ConfigureAwait(false);
        if (historyResult.Success && !string.IsNullOrEmpty(historyResult.Value))
        {
            var history = JsonSerializer.Deserialize<List<SavedRequest>>(historyResult.Value);
            if (history != null)
            {
                foreach (var request in history)
                {
                    _history[request.Id] = request;
                }
            }
        }

        // Load favorites
        var favoritesResult = await _localStorage.GetAsync<string>(FavoritesStorageKey).ConfigureAwait(false);
        if (favoritesResult.Success && !string.IsNullOrEmpty(favoritesResult.Value))
        {
            var favorites = JsonSerializer.Deserialize<List<SavedRequest>>(favoritesResult.Value);
            if (favorites != null)
            {
                foreach (var request in favorites)
                {
                    _favorites[request.Id] = request;
                }
            }
        }

        // Load environment variables
        var envResult = await _localStorage.GetAsync<string>(EnvironmentVariablesStorageKey).ConfigureAwait(false);
        if (envResult.Success && !string.IsNullOrEmpty(envResult.Value))
        {
            var env = JsonSerializer.Deserialize<List<EnvironmentVariable>>(envResult.Value);
            if (env != null)
            {
                foreach (var variable in env)
                {
                    _environmentVariables[variable.Key] = variable;
                }
            }
        }

        _isInitialized = true;
    }

    public IEnumerable<SavedRequest> GetHistory()
    {
        return _history.Values.OrderByDescending(r => r.LastUsedAt).Take(MaxHistoryCount);
    }

    public IEnumerable<SavedRequest> GetFavorites()
    {
        return _favorites.Values.OrderBy(r => r.Name);
    }

    public IEnumerable<EnvironmentVariable> GetEnvironmentVariables()
    {
        return _environmentVariables.Values.OrderBy(v => v.Key);
    }

    public async Task AddToHistoryAsync(string method, string url, string headers, string body)
    {
        var request = new SavedRequest
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{method} {GetPathFromUrl(url)}",
            Method = method,
            Url = url,
            Headers = headers,
            Body = body,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            IsFavorite = false
        };

        _history[request.Id] = request;

        // Keep only the last MaxHistoryCount items
        if (_history.Count > MaxHistoryCount)
        {
            var oldestKeys = _history.Values
                .OrderBy(r => r.LastUsedAt)
                .Take(_history.Count - MaxHistoryCount)
                .Select(r => r.Id)
                .ToList();

            foreach (var key in oldestKeys)
            {
                _history.TryRemove(key, out _);
            }
        }

        await SaveHistoryAsync().ConfigureAwait(false);
    }

    public async Task AddToFavoritesAsync(SavedRequest request)
    {
        request.IsFavorite = true;
        _favorites[request.Id] = request;
        await SaveFavoritesAsync().ConfigureAwait(false);
    }

    public async Task RemoveFromFavoritesAsync(string id)
    {
        _favorites.TryRemove(id, out _);
        await SaveFavoritesAsync().ConfigureAwait(false);
    }

    public async Task AddEnvironmentVariableAsync(string key, string value, bool isSecret = false)
    {
        _environmentVariables[key] = new EnvironmentVariable
        {
            Key = key,
            Value = value,
            IsSecret = isSecret
        };
        await SaveEnvironmentVariablesAsync().ConfigureAwait(false);
    }

    public async Task RemoveEnvironmentVariableAsync(string key)
    {
        _environmentVariables.TryRemove(key, out _);
        await SaveEnvironmentVariablesAsync().ConfigureAwait(false);
    }

    public string ReplaceEnvironmentVariables(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return EnvironmentVariableRegex().Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            return _environmentVariables.TryGetValue(key, out var variable) ? variable.Value : match.Value;
        });
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex EnvironmentVariableRegex();

    private static string GetPathFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.PathAndQuery;
        }
        catch
        {
            return url;
        }
    }

    private async Task SaveHistoryAsync()
    {
        var history = _history.Values.OrderByDescending(r => r.LastUsedAt).Take(MaxHistoryCount).ToList();
        var json = JsonSerializer.Serialize(history);
        await _localStorage.SetAsync(HistoryStorageKey, json).ConfigureAwait(false);
    }

    private async Task SaveFavoritesAsync()
    {
        var favorites = _favorites.Values.ToList();
        var json = JsonSerializer.Serialize(favorites);
        await _localStorage.SetAsync(FavoritesStorageKey, json).ConfigureAwait(false);
    }

    private async Task SaveEnvironmentVariablesAsync()
    {
        var env = _environmentVariables.Values.ToList();
        var json = JsonSerializer.Serialize(env);
        await _localStorage.SetAsync(EnvironmentVariablesStorageKey, json).ConfigureAwait(false);
    }

    public async Task ClearHistoryAsync()
    {
        _history.Clear();
        await _localStorage.SetAsync(HistoryStorageKey, "[]").ConfigureAwait(false);
    }
}
