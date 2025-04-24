using FootballCommentary.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FootballCommentary.GAgents.Services
{
    public class LLMService : ILLMService
    {
        private readonly ILogger<LLMService> _logger;
        private readonly string _apiKey;
        private readonly string _modelName;
        private readonly bool _useFallbackLLM;
        private readonly HttpClient _httpClient;
        
        public LLMService(IConfiguration configuration, ILogger<LLMService> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient("GeminiApi");
            
            string configApiKey = configuration["GOOGLE_GEMINI_API_KEY"];
            string envApiKey = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_API_KEY");
            _apiKey = configApiKey ?? envApiKey ??
                      string.Empty;
            
            string configModel = configuration["GOOGLE_GEMINI_MODEL"];
            string envModel = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_MODEL");
            _modelName = configModel ?? envModel ??
                         "gemini-1.5-flash";
            
            string configFallback = configuration["USE_FALLBACK_LLM"];
            string envFallback = Environment.GetEnvironmentVariable("USE_FALLBACK_LLM");
            var useFallbackStr = configFallback ?? envFallback ??
                                "false";
            
            _useFallbackLLM = bool.TryParse(useFallbackStr, out var fallback) && fallback;
            
            _logger.LogInformation("--- LLM Service Configuration ---");
            _logger.LogInformation("Config Key Present: {Present}", !string.IsNullOrEmpty(configApiKey));
            _logger.LogInformation("Env Var Key Present: {Present}", !string.IsNullOrEmpty(envApiKey));
            _logger.LogInformation("Final API Key Present: {Present}", !string.IsNullOrEmpty(_apiKey));
            _logger.LogInformation("Config Model: {Model}", configModel ?? "<null>");
            _logger.LogInformation("Env Var Model: {Model}", envModel ?? "<null>");
            _logger.LogInformation("Final Model Used: {Model}", _modelName);
            _logger.LogInformation("Config Fallback: {Fallback}", configFallback ?? "<null>");
            _logger.LogInformation("Env Var Fallback: {Fallback}", envFallback ?? "<null>");
            _logger.LogInformation("Final Use Fallback Setting: {UseFallback}", useFallbackStr);
            _logger.LogInformation("Parsed Use Fallback: {UseFallback}", _useFallbackLLM);
            _logger.LogInformation("---------------------------------");
            
            if (string.IsNullOrEmpty(_apiKey) && !_useFallbackLLM)
            {
                _logger.LogWarning("Google Gemini API key not found. Using fallback LLM.");
                _useFallbackLLM = true;
            }
            
            _logger.LogInformation("LLM Service initialized. Model: {Model}, Use Fallback: {Fallback}, API Key Present: {HasKey}", 
                _modelName, _useFallbackLLM, !string.IsNullOrEmpty(_apiKey));
        }
        
        public Task<string> GenerateCommentaryAsync(string prompt)
        {
            return GenerateContentAsync(
                "You are a passionate football commentator. " +
                "Provide energetic, colorful, and concise commentary about the events in a football match. " +
                "Keep your responses brief and under 30 words, focused on the specific event described.",
                prompt);
        }
        
        public async Task<string> GenerateContentAsync(string systemPrompt, string userPrompt)
        {
            if (_useFallbackLLM)
            {
                return GetFallbackResponse(userPrompt);
            }
            
            try
            {
                // Use the Google Gemini API directly
                var geminiEndpoint = $"https://generativelanguage.googleapis.com/v1/models/{_modelName}:generateContent?key={_apiKey}";
                
                var requestData = new
                {
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new[]
                            {
                                new { text = $"{systemPrompt}\n\n{userPrompt}" }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.7,
                        maxOutputTokens = 100
                    }
                };
                
                var response = await _httpClient.PostAsJsonAsync(geminiEndpoint, requestData);
                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    // Extract the text from the response
                    var generatedText = responseContent?.RootElement
                        .GetProperty("candidates")[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text")
                        .GetString();
                    
                    if (!string.IsNullOrEmpty(generatedText))
                    {
                        _logger.LogInformation("Generated content from Gemini API: {Text}", generatedText);
                        return generatedText;
                    }
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Gemini API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                }
                
                // Fallback if API call fails
                return GetFallbackResponse(userPrompt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating content: {Message}", ex.Message);
                return GetFallbackResponse(userPrompt);
            }
        }
        
        private string GetFallbackResponse(string prompt)
        {
            // Simple fallbacks based on keywords in the prompt
            if (prompt.Contains("goal", StringComparison.OrdinalIgnoreCase))
            {
                return "GOAL! What a fantastic finish! The crowd goes wild!";
            }
            else if (prompt.Contains("save", StringComparison.OrdinalIgnoreCase))
            {
                return "Great save by the goalkeeper! Keeping their team in the game.";
            }
            else if (prompt.Contains("pass", StringComparison.OrdinalIgnoreCase))
            {
                return "Beautiful passing. They're moving the ball with precision.";
            }
            else if (prompt.Contains("tackle", StringComparison.OrdinalIgnoreCase))
            {
                return "Strong challenge! They won the ball cleanly.";
            }
            else if (prompt.Contains("start", StringComparison.OrdinalIgnoreCase))
            {
                return "And we're underway! The match begins with high intensity.";
            }
            else if (prompt.Contains("end", StringComparison.OrdinalIgnoreCase))
            {
                return "The referee blows the final whistle! What a match we've witnessed today.";
            }
            else
            {
                return "The action continues on the pitch. Both teams looking for an advantage.";
            }
        }
    }
} 