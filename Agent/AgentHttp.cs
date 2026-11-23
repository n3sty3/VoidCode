using System;
using System.Net.Http;

namespace VoidCode.Agent;

/// <summary>
/// The one HTTP client the agent uses, and the URL join every provider call needs.
/// It has its own file because swaps providers behind it, and because four commands share it
/// (connection test, the loop, web_fetch, inline assist, commit message).
/// </summary>
internal static class AgentHttp
{
    internal static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

    internal static string CombineUrl(string baseUrl, string endpoint)
    {
        baseUrl = baseUrl.TrimEnd('/');
        endpoint = endpoint.TrimStart('/');
        return $"{baseUrl}/{endpoint}";
    }
}
