namespace TextPolish.Core.Llm;

/// <summary>
/// Chat Completions のリクエストボディに、設定されている場合だけ任意パラメータを追加する。
///
/// GPT-5系(推論モデル)の reasoning_effort / verbosity のように、モデルによって
/// 受け付けるかどうかが異なるパラメータを扱うための仕組み。
/// モデル名から対応可否を判定すると、Azureのデプロイ名のように任意の名前を付けられる環境で
/// 判定を外すため、コード側にモデルの知識は持たせず「設定にあれば送る」だけにしている。
/// </summary>
internal static class ChatCompletionOptions
{
    public static void ApplyOptional(
        IDictionary<string, object?> body,
        string? reasoningEffort,
        string? verbosity,
        int? maxCompletionTokens)
    {
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            body["reasoning_effort"] = reasoningEffort;
        }

        if (!string.IsNullOrWhiteSpace(verbosity))
        {
            body["verbosity"] = verbosity;
        }

        if (maxCompletionTokens is > 0)
        {
            // GPT-5系では max_tokens は使えず、max_completion_tokens を使う。
            body["max_completion_tokens"] = maxCompletionTokens;
        }
    }
}
