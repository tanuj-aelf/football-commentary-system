using FootballCommentary.Core.Abstractions;
using FootballCommentary.Web.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FootballCommentary.GAgents.Services
{
    public class LLMService : ILLMService
    {
        private readonly ILogger<LLMService> _logger;
        private readonly LlmConfiguration _llmConfig;
        private readonly HttpClient _httpClient;
        private readonly HttpClient _azureHttpClient;

        public LLMService(LlmConfiguration llmConfig, ILogger<LLMService> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _llmConfig = llmConfig;
            _httpClient = httpClientFactory.CreateClient("GeminiApi");
            _azureHttpClient = httpClientFactory.CreateClient("AzureOpenAIApi");

            _logger.LogInformation("--- LLMService constructor executed. ---");

            _logger.LogInformation("LLM Service initialized using LlmConfiguration.");
            if (!_llmConfig.IsGoogleSelected && !_llmConfig.IsAzureSelected)
            {
                 _logger.LogWarning("LLM Service: Neither Google nor Azure OpenAI is selected in configuration.");
            }
            else if (_llmConfig.IsGoogleSelected && string.IsNullOrEmpty(_llmConfig.ActiveApiKey))
            {
                _logger.LogWarning("LLM Service: Google selected, but API Key is missing.");
            }
             else if (_llmConfig.IsAzureSelected && (string.IsNullOrEmpty(_llmConfig.ActiveApiKey) || string.IsNullOrEmpty(_llmConfig.ActiveEndpoint)))
            {
                 _logger.LogWarning("LLM Service: Azure OpenAI selected, but API Key or Endpoint is missing.");
            }
        }
        
        public Task<string> GenerateCommentaryAsync(string prompt)
        {
             _logger.LogDebug("GenerateCommentaryAsync called with prompt: {Prompt}", prompt);
            return GenerateContentAsync(
                "You are an energetic football commentator. Provide concise, exciting commentary (under 30 words) for the given event.",
                prompt);
        }
        
        public async Task<string> GenerateContentAsync(string systemPrompt, string userPrompt)
        {
            _logger.LogDebug("GenerateContentAsync called. System: \"{SystemPrompt}\", User: \"{UserPrompt}\"", systemPrompt, userPrompt);

            if (_llmConfig.IsAzureSelected)
            {
                _logger.LogInformation("Using Azure OpenAI model: {Deployment}", _llmConfig.ActiveDeploymentName);
                return await CallAzureOpenAIAsync(systemPrompt, userPrompt);
            }
            else if (_llmConfig.IsGoogleSelected)
            {
                 _logger.LogInformation("Using Google Gemini model: {Model}", _llmConfig.ActiveModelName);
                return await CallGoogleGeminiAsync(systemPrompt, userPrompt);
            }
            else
            {
                _logger.LogWarning("No valid LLM selected (Azure/Google). Using fallback response.");
                return GetFallbackResponse(userPrompt);
            }
        }

        private async Task<string> CallAzureOpenAIAsync(string systemPrompt, string userPrompt)
        {
            if (string.IsNullOrEmpty(_llmConfig.ActiveApiKey) || string.IsNullOrEmpty(_llmConfig.ActiveEndpoint) || string.IsNullOrEmpty(_llmConfig.ActiveDeploymentName))
            {
                _logger.LogError("Azure OpenAI configuration (Key, Endpoint, Deployment) is incomplete.");
                return GetFallbackResponse(userPrompt);
            }

            var endpoint = $"{_llmConfig.ActiveEndpoint.TrimEnd('/')}/openai/deployments/{_llmConfig.ActiveDeploymentName}/chat/completions?api-version=2024-02-15-preview";

            var requestPayload = new
            {
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.7,
                max_tokens = 100
            };

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("api-key", _llmConfig.ActiveApiKey);
                request.Content = JsonContent.Create(requestPayload);

                var response = await _azureHttpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var generatedText = responseJson?.RootElement
                                            .GetProperty("choices")[0]
                                            .GetProperty("message")
                                            .GetProperty("content")
                                            .GetString();

                    if (!string.IsNullOrEmpty(generatedText))
                    {
                         _logger.LogInformation("Generated content from Azure OpenAI: {Text}", generatedText);
                        return generatedText;
                    }
                    else
                    {
                        _logger.LogWarning("Azure OpenAI response did not contain expected content.");
                    }
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Azure OpenAI API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Azure OpenAI API: {Message}", ex.Message);
            }

            return GetFallbackResponse(userPrompt);
        }

        private async Task<string> CallGoogleGeminiAsync(string systemPrompt, string userPrompt)
        {
             if (string.IsNullOrEmpty(_llmConfig.ActiveApiKey) || string.IsNullOrEmpty(_llmConfig.ActiveModelName))
            {
                _logger.LogError("Google Gemini configuration (Key, Model) is incomplete.");
                return GetFallbackResponse(userPrompt);
            }

            var geminiEndpoint = $"https://generativelanguage.googleapis.com/v1/models/{_llmConfig.ActiveModelName}:generateContent?key={_llmConfig.ActiveApiKey}";

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
            
            try
            {
                var response = await _httpClient.PostAsJsonAsync(geminiEndpoint, requestData);
                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadFromJsonAsync<JsonDocument>();
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
                     else
                    {
                        _logger.LogWarning("Google Gemini response did not contain expected content.");
                    }
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Gemini API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                }
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, "Error calling Google Gemini API: {Message}", ex.Message);
            }

            return GetFallbackResponse(userPrompt);
        }
        
        private string GetFallbackResponse(string prompt)
        {
            _logger.LogWarning("Using fallback commentary for prompt: {Prompt}", prompt);
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
            else if (prompt.Contains("start", StringComparison.OrdinalIgnoreCase) || prompt.Contains("kickoff", StringComparison.OrdinalIgnoreCase))
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