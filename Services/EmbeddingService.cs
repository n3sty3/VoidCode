using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoidCode.Services;

/// <summary>
/// Semantic vector embeddings for VoidCode's hybrid codebase search:
/// provides fast zero-dependency L2-normalized subword + trigram feature vectors locally,
/// plus optional Ollama neural embeddings (/api/embed) when an embedding model is running.
/// </summary>
public static class EmbeddingService
{
    public const int LocalVectorDimensions = 128;

    private static readonly Regex WordRegex = new(@"[A-Za-z_][A-Za-z0-9_]{1,}", RegexOptions.Compiled);
    private static readonly Regex CamelSplitRegex = new(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

    private static readonly HttpClient OllamaEmbedClient = new()
    {
        Timeout = TimeSpan.FromMilliseconds(1500)
    };

    /// <summary>
    /// Computes a deterministic L2-normalized dense semantic vector combining identifier tokens,
    /// camelCase/snake_case subwords, and character trigrams so fuzzy &amp; morphological queries
    /// (e.g. "authenticate" vs "AuthTokenHandler") score high cosine similarity with zero network latency.
    /// </summary>
    public static float[] ComputeLocalVector(string? text, int dimensions = LocalVectorDimensions)
    {
        var vec = new float[dimensions];
        if (string.IsNullOrWhiteSpace(text)) return vec;

        foreach (Match m in WordRegex.Matches(text))
        {
            var raw = m.Value;
            AddSignedFeature(vec, raw.ToLowerInvariant(), 1.5f);

            // Split snake_case and CamelCase into subwords
            foreach (var snakePart in raw.Split('_', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var sub in CamelSplitRegex.Split(snakePart))
                {
                    var norm = sub.ToLowerInvariant();
                    if (norm.Length < 2) continue;
                    AddSignedFeature(vec, norm, 1.25f);

                    // Character 3-grams capture stems/morphological overlap (auth / authentication / authorize)
                    if (norm.Length >= 4)
                    {
                        var padded = "^" + norm + "$";
                        for (int i = 0; i <= padded.Length - 3; i++)
                        {
                            AddSignedFeature(vec, padded.Substring(i, 3), 0.35f);
                        }
                    }
                }
            }
        }

        NormalizeInPlace(vec);
        return vec;
    }

    public static double CosineSimilarity(float[]? a, float[]? b)
    {
        if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return 0.0;
        double dot = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot;
    }

    /// <summary>
    /// Optional neural embedding call against local Ollama (/api/embed). Returns null cleanly if unreachable.
    /// </summary>
    public static async Task<float[]?> TryEmbedViaOllamaAsync(
        string text,
        string model = "nomic-embed-text",
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var client = http ?? OllamaEmbedClient;
        try
        {
            var payload = new JsonObject
            {
                ["model"] = model,
                ["input"] = text.Length > 4000 ? text[..4000] : text
            };
            using var resp = await client.PostAsync(
                "http://localhost:11434/api/embed",
                new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonNode.Parse(json);
            var arr = root?["embeddings"]?[0]?.AsArray() ?? root?["embedding"]?.AsArray();
            if (arr == null || arr.Count == 0) return null;

            var result = new float[arr.Count];
            for (int i = 0; i < arr.Count; i++)
            {
                result[i] = arr[i]?.GetValue<float>() ?? 0f;
            }
            NormalizeInPlace(result);
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AddSignedFeature(float[] vec, string feature, float weight)
    {
        unchecked
        {
            uint h = 2166136261u;
            for (int i = 0; i < feature.Length; i++)
            {
                h ^= feature[i];
                h *= 16777619u;
            }
            int idx = (int)(h % (uint)vec.Length);
            float sign = ((h >> 31) & 1u) == 0 ? 1f : -1f;
            vec[idx] += sign * weight;
        }
    }

    private static void NormalizeInPlace(float[] vec)
    {
        double sumSq = 0.0;
        for (int i = 0; i < vec.Length; i++) sumSq += vec[i] * vec[i];
        if (sumSq <= 1e-12) return;
        float inv = (float)(1.0 / Math.Sqrt(sumSq));
        for (int i = 0; i < vec.Length; i++) vec[i] *= inv;
    }
}
