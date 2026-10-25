namespace TextPolish.Core.Llm;

/// <summary>
/// LLMの呼び出しが失敗した、または応答を整形結果として採用しなかったことを表す例外。
///
/// この例外は <see cref="ILlmFormatter"/> 自身が投げるもので、送出前に理由をログへ記録し、
/// メッセージはそのまま利用者に見せられる文面にしてある。
///
/// 専用の型にしたのは、Formatter の catch で「自分が投げた不採用通知」と
/// 「それ以外の予期しない例外」を区別するため。以前は <see cref="InvalidOperationException"/> を
/// そのまま使って <c>when (ex is not InvalidOperationException)</c> で除外していたため、
/// フレームワークやライブラリ由来の同じ型の例外までログに残らず素通りしていた。
///
/// <see cref="InvalidOperationException"/> を継承しているので、呼び出し側の既存の捕捉は
/// 変えなくてよい。
/// </summary>
public sealed class LlmRejectedException : InvalidOperationException
{
    public LlmRejectedException(string message)
        : base(message)
    {
    }
}
