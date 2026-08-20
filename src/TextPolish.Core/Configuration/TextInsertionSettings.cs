namespace TextPolish.Core.Configuration;

public sealed class TextInsertionSettings
{
    /// <summary>
    /// 挿入後に、元のクリップボード内容を復元する。
    /// 誤って別のテキストが貼り付けられる問題が起きる場合は false にする。
    /// </summary>
    public bool RestoreClipboard { get; set; } = true;

    /// <summary>
    /// クリップボードを復元するまでの待ち時間(ミリ秒)。
    /// 貼り付け先アプリはCtrl+Vを受け取ってから非同期にクリップボードを読むため、
    /// 短すぎると復元後の内容が貼り付けられてしまう。
    /// 動作の遅い端末で誤挿入が起きる場合は値を大きくする。
    /// </summary>
    public int RestoreDelayMs { get; set; } = 800;
}
