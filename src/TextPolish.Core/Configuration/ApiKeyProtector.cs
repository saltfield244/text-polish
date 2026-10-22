using System.Security.Cryptography;
using System.Text;

namespace TextPolish.Core.Configuration;

/// <summary>
/// APIキーを持つプロバイダ設定の共通部分。
///
/// <see cref="ApiKey"/> はメモリ上でだけ平文を持ち、appsettings.json には
/// <see cref="ApiKeyProtected"/>(DPAPIで暗号化した値)として保存する
/// (<see cref="AppSettingsLoader"/>参照)。
/// </summary>
public interface IApiKeySettings
{
    /// <summary>平文のAPIキー。ファイルには書き出さない(プレースホルダーを除く)。</summary>
    string ApiKey { get; set; }

    /// <summary>DPAPIで暗号化したAPIキー。"dpapi:v1:" + Base64。</summary>
    string? ApiKeyProtected { get; set; }

    /// <summary>
    /// <see cref="ApiKeyProtected"/> を復号できなかった(別のユーザー・PCで暗号化された等)。
    /// この状態で保存しても暗号文は消さない(ユーザーが新しいキーを入れるか、明示的に削除するまで残す)。
    /// </summary>
    bool ApiKeyUnreadable { get; set; }
}

/// <summary>
/// APIキーの暗号化・復号。WindowsのDPAPI(現在のユーザーのスコープ)を使う。
///
/// Windows資格情報マネージャーではなくDPAPIを選んだ理由:
/// - 設定を appsettings.json 1ファイルにまとめる既存の設計を崩さずに済む
///   (フォルダごとコピーして使うポータブルな配布形態のため、資格情報マネージャーに置くと
///   どのコピーの設定かを区別する仕組みが別途必要になる)。
/// - 資格情報マネージャーはデータが2560バイトまでで、Vertex AIのアクセストークン等が
///   収まらない可能性がある。
/// - 暗号化の強度・保護範囲(同じWindowsユーザーなら復号できる)はどちらも同等。
/// System.Security.Cryptography.ProtectedData はWindows Desktopの共有フレームワークに
/// 含まれるため、NuGetパッケージの追加は不要。
/// </summary>
public static class ApiKeyProtector
{
    private const string Prefix = "dpapi:v1:";

    /// <summary>
    /// 暗号化の必要がない値(未設定、または配布用テンプレートの "TODO-..." )かどうか。
    /// </summary>
    public static bool IsPlaceholder(string? apiKey) =>
        string.IsNullOrEmpty(apiKey) || apiKey.StartsWith("TODO-", StringComparison.Ordinal);

    /// <param name="purpose">
    /// 設定のセクション名。暗号化の追加エントロピーに使い、
    /// 別のプロバイダの暗号文を書き写しても復号できないようにする。
    /// </param>
    public static string Protect(string plainText, string purpose)
    {
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainText), Entropy(purpose), DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    public static bool TryUnprotect(string? protectedText, string purpose, out string plainText)
    {
        plainText = string.Empty;

        if (string.IsNullOrEmpty(protectedText) || !protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var encrypted = Convert.FromBase64String(protectedText[Prefix.Length..]);
            var decrypted = ProtectedData.Unprotect(encrypted, Entropy(purpose), DataProtectionScope.CurrentUser);
            plainText = Encoding.UTF8.GetString(decrypted);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// 設定画面で入力された値をプロバイダ設定へ反映する。
    /// </summary>
    /// <param name="input">入力欄の値。空なら「キーなし」を意味する。</param>
    /// <param name="deleteRequested">「削除」ボタンが押された。</param>
    public static void ApplyInput(IApiKeySettings settings, string input, bool deleteRequested)
    {
        if (input.Length > 0)
        {
            settings.ApiKey = input;
            settings.ApiKeyUnreadable = false;
            return;
        }

        // 復号できなかったキーは入力欄が空で表示される。そのまま保存しただけで
        // 暗号文が消えないよう、明示的に削除が指示された場合だけ消す。
        if (settings.ApiKeyUnreadable && !deleteRequested)
        {
            return;
        }

        settings.ApiKey = string.Empty;
        settings.ApiKeyProtected = null;
        settings.ApiKeyUnreadable = false;
    }

    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes("TextPolish.ApiKey:" + purpose);
}
