using Codeer.LowCode.Blazor.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccountingApp.Designer.Lib.AI
{
    internal static class AiJsonValidation
    {
        static readonly JsonSerializerOptions StrictOptions = BuildStrictOptions();

        static JsonSerializerOptions BuildStrictOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                // 通常オプションと違い、定義に無いプロパティは黙って捨てず例外にする。
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            };
            // enum は文字列表現で受ける（AI は "Equal" 等の文字列で出力する）。
            options.Converters.Add(new JsonStringEnumConverter());
            // JsonAbstract 派生(Field/Layout/MatchCondition 等)のポリモーフィックコンバータ。
            // これにより Disallow がネストした派生型にも伝播する。
            options.Converters.AddJsonConverters();
            return options;
        }

        public static string? GetUnmappedMemberError<T>(string json)
        {
            try
            {
                JsonSerializer.Deserialize<T>(json, StrictOptions);
                return null;
            }
            catch (JsonException ex)
            {
                return ex.Message;
            }
        }
    }
}
