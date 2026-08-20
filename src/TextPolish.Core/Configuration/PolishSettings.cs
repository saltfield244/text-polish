namespace TextPolish.Core.Configuration;

/// <summary>整形プリセット。パレットのチェックボックス1つに対応する。</summary>
public sealed class FormatPreset
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// LLMへ渡す指示。プリセットごとの整形方針そのもの。
    ///
    /// 複数選択したときは手順として連結されるため、「選択されたテキストを」のような
    /// 入力を名指しする書き方は避け、処理内容だけを書くこと
    /// (2段目以降では、対象が原文ではなく前の手順の結果になるため)。
    /// </summary>
    public string Instruction { get; set; } = string.Empty;

    /// <summary>
    /// 排他グループ。同じ値を持つプリセット同士は同時に選べない
    /// (片方を選ぶともう片方のチェックが外れる)。空ならどれとでも組み合わせられる。
    ///
    /// 「同じテキストをどう書き換えるか」を指定するもの同士は指示が矛盾するため分けている。
    /// 例:「誤変換だけ直す」(文体を変えるな)と「ビジネスメール」(ですます調にしろ)は両立しない。
    /// </summary>
    public string Group { get; set; } = string.Empty;
}

/// <summary>
/// 選択テキストの整形機能(パレット)の設定。
///
/// 音声認識は Windows 標準の音声入力(Win+H)に任せ、本ツールは
/// 「選択したテキストをLLMで整形して置き換える」ことに専念する構成のための設定。
/// </summary>
public sealed class PolishSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>整形パレットを開くホットキー。</summary>
    public string Hotkey { get; set; } = "F10";

    /// <summary>整形の共通方針を書いたシステムプロンプトのファイルパス。</summary>
    public string SystemPromptFilePath { get; set; } = "PolishSystemPrompt.txt";

    /// <summary>
    /// 用語辞書のファイルパス。「誤変換されがちな表記 → 正しい表記」を1行1組で書く。
    /// ファイルがなければ辞書なしとして動作する。
    ///
    /// appsettings.json に含めていないのは、設定画面から保存するとこのファイルが
    /// 丸ごと書き換わるため。手編集で増やしていく前提なので独立させている。
    /// </summary>
    public string GlossaryFilePath { get; set; } = "Glossary.txt";

    /// <summary>
    /// 整形結果を差分ハイライト表示にするかどうかの初期値。
    /// パレット上のチェックボックスでいつでも切り替えられる。
    ///
    /// 書き換え率が高い場合(翻訳など、原文がほとんど残らない場合)は、
    /// この値に関わらず初期状態では差分を出さない。差分が
    /// 「全部削除して全部追加」にしかならず、読む意味がないため。
    /// </summary>
    public bool ShowDiffByDefault { get; set; } = true;

    /// <summary>
    /// パレットを出さずに即座に整形して置き換えるホットキー。
    /// 空にすると無効。<see cref="Hotkey"/> と同じキーは指定できない(起動時に無効化する)。
    ///
    /// 確認を挟まないため、結果は直接書き戻される。取り消しは貼り付け先で Ctrl+Z。
    /// </summary>
    public string QuickHotkey { get; set; } = string.Empty;

    /// <summary>
    /// 即実行で使うプリセット名。<see cref="Presets"/> の中から名前で探す。
    /// 見つからない場合は先頭のプリセットを使う。
    /// </summary>
    public string QuickPresetName { get; set; } = "整える";

    /// <summary>
    /// パレットに並ぶプリセット。上から順に表示される。
    /// 複数選択したときは<b>この並び順が適用順</b>になるため、
    /// 文体の調整 → 要約 → 翻訳 の順に並べている(翻訳を最後にしないと、
    /// 訳文に対して日本語向けの文体調整をかけることになる)。
    ///
    /// 自由に追加・編集してよい。Instruction がそのままLLMへの指示になる。
    /// </summary>
    public List<FormatPreset> Presets { get; set; } = new()
    {
        new FormatPreset
        {
            Name = "整える",
            Group = "Style",
            Instruction =
                "音声入力の書き起こしを、そのまま使える自然な日本語に整えてください。" +
                "「えーと」「あの」などのフィラーを除去し、言い直しがあれば最終的な内容だけを残し、" +
                "適切な句読点を補い、明らかな誤変換を文脈から修正してください。" +
                "内容の意味は変えず、要約や情報の追加はしないでください。",
        },
        new FormatPreset
        {
            Name = "誤変換だけ直す",
            Group = "Style",
            Instruction =
                "音声入力の誤変換だけを修正してください。" +
                "同音異義語の誤り、固有名詞・専門用語の誤変換のみを、文脈から判断して直します。" +
                "文体・語尾・語順・句読点は一切変更しないでください。" +
                "誤変換が見当たらない場合は、入力をそのまま出力してください。",
        },
        new FormatPreset
        {
            Name = "ビジネスメール",
            Group = "Style",
            Instruction =
                "ビジネスメールの本文として通用する文章に整えてください。" +
                "丁寧な「ですます調」にし、フィラーや口語表現を除去し、読みやすく段落を整えます。" +
                "内容にない情報(宛名・挨拶・署名など)は勝手に追加しないでください。",
        },
        new FormatPreset
        {
            Name = "チャット",
            Group = "Style",
            Instruction =
                "チャットに投稿する文章として整えてください。" +
                "簡潔で自然な話し言葉にし、フィラーと言い直しを除去します。" +
                "無理に「です・ます」へ変換せず、話者の口調を活かしてください。",
        },
        // 以下3種は、システムプロンプトの既定方針(要約・翻訳をしない)を明示的に上書きする指示。
        new FormatPreset
        {
            Name = "要約",
            Group = "Length",
            Instruction =
                "要点をまとめてください。" +
                "書かれている情報の範囲内でまとめ、推測で内容を補わないでください。" +
                "箇条書きが適した内容なら箇条書きに、そうでなければ短い文章にまとめます。",
        },
        new FormatPreset
        {
            Name = "英訳",
            Group = "Translate",
            Instruction =
                "英語に翻訳してください。" +
                "フィラーや言い直しは訳出せず、最終的な内容だけを自然な英語にします。" +
                "情報の追加・省略はしないでください。出力は英訳のみとし、原文を併記しないでください。",
        },
        new FormatPreset
        {
            Name = "和訳",
            Group = "Translate",
            Instruction =
                "日本語に翻訳してください。" +
                "直訳にこだわらず自然な日本語にし、情報の追加・省略はしないでください。" +
                "出力は和訳のみとし、原文を併記しないでください。",
        },
    };
}
