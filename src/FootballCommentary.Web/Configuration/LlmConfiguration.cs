using System;

namespace FootballCommentary.Web.Configuration
{
    public class LlmConfiguration
    {
        // Selection
        public string? SelectedModel { get; set; }

        // Google Specific
        public string? GoogleApiKey { get; set; }
        public string? GoogleModel { get; set; }

        // Azure Specific
        public string? AzureApiKey { get; set; }
        public string? AzureEndpoint { get; set; }
        public string? AzureDeploymentName { get; set; }
        public string? AzureModelName { get; set; }

        // Active Configuration (populated based on SelectedModel)
        public string? ActiveApiKey { get; set; }
        public string? ActiveModelName { get; set; }
        public string? ActiveEndpoint { get; set; } // Only relevant for Azure
        public string? ActiveDeploymentName { get; set; } // Only relevant for Azure

        public bool IsGoogleSelected => "google".Equals(SelectedModel, StringComparison.OrdinalIgnoreCase);
        public bool IsAzureSelected => "openai".Equals(SelectedModel, StringComparison.OrdinalIgnoreCase); // Assuming 'openai' refers to Azure OpenAI
    }
} 