namespace TextPolish.Core.Configuration;

/// <summary>アクティブウィンドウのアプリに応じた文体の切り替えルール。</summary>
public sealed class AppStyleRule
{
    /// <summary>
    /// プロセス名に対する部分一致(大文字小文字を区別しない)。
    /// 例: "slack" は slack.exe に一致する。
    /// </summary>
    public string MatchProcessName { get; set; } = string.Empty;

    /// <summary>このアプリで使う文体の指示。整形プロンプトにそのまま差し込まれる。</summary>
    public string Style { get; set; } = string.Empty;
}

public sealed class FormattingSettings
{
    /// <summary>どのルールにも一致しなかった場合に使う文体の指示。</summary>
    public string DefaultStyle { get; set; } =
        "文体は「ですます調」を基本とする。ただし話者が明らかに「である調」で話している場合はそれに合わせる。";

    /// <summary>
    /// 上から順に評価し、最初に一致したルールの文体を使う。
    /// TODO: 利用するアプリに合わせて調整すること。プロセス名は
    ///       ログの「送出先フォアグラウンド」やタスクマネージャーで確認できる。
    /// </summary>
    public List<AppStyleRule> AppStyleRules { get; set; } = new()
    {
        new AppStyleRule
        {
            MatchProcessName = "slack",
            Style = "チャット向けに、簡潔で自然な話し言葉にする。過度に丁寧にせず、" +
                    "語尾を無理に「です・ます」に変換しない。話者の口調をそのまま活かす。",
        },
        new AppStyleRule
        {
            MatchProcessName = "teams",
            Style = "チャット向けに、簡潔で自然な話し言葉にする。過度に丁寧にせず、" +
                    "語尾を無理に「です・ます」に変換しない。",
        },
        new AppStyleRule
        {
            MatchProcessName = "outlook",
            Style = "ビジネスメール向けに、丁寧な「ですます調」に整える。",
        },
        new AppStyleRule
        {
            MatchProcessName = "notepad",
            Style = "原文に忠実にする。語尾の変換や言い回しの追加は行わず、" +
                    "フィラー除去と句読点の補正にとどめる。",
        },
    };

    /// <summary>プロセス名から適用する文体指示を解決する。</summary>
    public string ResolveStyle(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return DefaultStyle;
        }

        var matched = AppStyleRules.FirstOrDefault(rule =>
            !string.IsNullOrWhiteSpace(rule.MatchProcessName) &&
            processName.Contains(rule.MatchProcessName, StringComparison.OrdinalIgnoreCase));

        return matched is null || string.IsNullOrWhiteSpace(matched.Style)
            ? DefaultStyle
            : matched.Style;
    }
}
