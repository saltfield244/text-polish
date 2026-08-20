using System.Net.Http;
using TextPolish.Core.Configuration;
using TextPolish.Core.Logging;

namespace TextPolish.Core.Llm;

public static class LlmFormatterFactory
{
    public static ILlmFormatter Create(
        AppSettings settings,
        HttpClient httpClient,
        ISystemPromptProvider promptProvider,
        IAppLogger logger)
    {
        return settings.LlmProvider switch
        {
            "AzureOpenAI" => new AzureOpenAiFormatter(httpClient, settings.AzureOpenAI, promptProvider, logger),
            "Gemini" => new GeminiFormatter(httpClient, settings.Gemini, promptProvider, logger),
            "OpenAiCompatible" => new OpenAiCompatibleFormatter(
                httpClient, settings.OpenAiCompatible, promptProvider, logger),
            _ => throw new NotSupportedException($"未知のLlmProviderです: {settings.LlmProvider}"),
        };
    }
}
