using Azure.AI.OpenAI;
using OpenAI.Chat;
using System;
using System.ClientModel;

namespace AccountingApp.Designer.Lib.AI
{
    public static class AISettingsExtensions
    {
        public static ChatClient CreateChatClient(this AISettings settings)
            => new AzureOpenAIClient(new Uri(settings.OpenAIEndPoint), new ApiKeyCredential(settings.OpenAIKey))
                .GetChatClient(settings.ChatModel);
    }
}
