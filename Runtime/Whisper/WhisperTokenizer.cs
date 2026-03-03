using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TeamflowSDK
{
    internal static class WhisperTokenizer
    {
        private static Dictionary<int, string> _vocab;
        private static bool _loaded = false;

        public static string Decode(int tokenId)
        {
            if (!_loaded) LoadVocab();
            if (_vocab != null && _vocab.TryGetValue(tokenId, out var word))
                return word;
            return "";
        }

        private static void LoadVocab()
        {
            _loaded = true;
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, "Whisper", "vocab.json");
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                _vocab = ParseSimpleVocabJson(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WhisperTokenizer] Could not load vocab.json: {ex.Message}");
            }
        }

        private static Dictionary<int, string> ParseSimpleVocabJson(string json)
        {
            var result = new Dictionary<int, string>();
            json = json.Trim().TrimStart('{').TrimEnd('}');
            foreach (var entry in json.Split(','))
            {
                var parts = entry.Split(':');
                if (parts.Length != 2) continue;
                var key   = parts[0].Trim().Trim('"').Replace("\\u0120", " ").Replace("\\n", "\n");
                var value = parts[1].Trim();
                if (int.TryParse(value, out int id))
                    result[id] = key;
            }
            return result;
        }
    }
}
