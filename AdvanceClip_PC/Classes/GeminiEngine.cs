using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AdvanceClip.Classes
{
    public static class GeminiEngine
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public static async Task<string> ExtractFormattedTableFromImageAsync(string imagePath, string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey)) throw new Exception("Gemini API Key is completely missing.");

            byte[] imageBytes = await File.ReadAllBytesAsync(imagePath);
            string base64Image = Convert.ToBase64String(imageBytes);

            string mimeType = "image/jpeg";
            string extension = Path.GetExtension(imagePath).ToLowerInvariant();
            if (extension == ".png") mimeType = "image/png";
            else if (extension == ".webp") mimeType = "image/webp";

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = "Extract tabular data from this image natively into a raw strict JSON coordinate array matching EXACTLY this pattern: { \"(row_integer,col_integer)\": { \"text\": \"Extracted String\", \"conf\": 1.0 } }. Never output HTML. Start row/col from 0. Example: {\"(0,0)\": {\"text\": \"ID\", \"conf\": 1.0}, \"(0,1)\": {\"text\": \"Name\", \"conf\": 1.0}}. Do not include markdown wraps." },
                            new { inline_data = new { mime_type = mimeType, data = base64Image } }
                        }
                    }
                }
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
            var requestContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={apiKey}";
            
            var response = await _httpClient.PostAsync(endpoint, requestContent);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                throw new Exception($"Gemini HTTP Engine Failure: {response.StatusCode} - {err}");
            }

            string responseJson = await response.Content.ReadAsStringAsync();
            
            try 
            {
                using JsonDocument doc = JsonDocument.Parse(responseJson);
                var textObj = doc.RootElement
                                 .GetProperty("candidates")[0]
                                 .GetProperty("content")
                                 .GetProperty("parts")[0]
                                 .GetProperty("text");

                string rawExtraction = textObj.GetString() ?? string.Empty;
                // Pre-process any rogue markdown block wraps inserted by LLMs
                if (rawExtraction.StartsWith("```json")) rawExtraction = rawExtraction.Substring(7);
                if (rawExtraction.StartsWith("```")) rawExtraction = rawExtraction.Substring(3);
                if (rawExtraction.EndsWith("```")) rawExtraction = rawExtraction.Substring(0, rawExtraction.Length - 3);

                return rawExtraction.Trim();
            }
            catch (Exception ex)
            {
                throw new Exception($"Gemini parsing fault! {ex.Message}");
            }
        }
    }
}
