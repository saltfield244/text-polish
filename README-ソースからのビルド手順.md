# TextPolish - ソースコードからのビルド手順

このパッケージにはソースコードのみが含まれています(実行ファイルは含みません)。
配布先の端末でビルドして使用します。

---

## 1. 前提条件

### .NET 10 SDK (x64) が必要です

実行するだけなら Runtime で足りますが、**ビルドには SDK が必要**です。

  https://dotnet.microsoft.com/download/dotnet/10.0

ダウンロードページの「**SDK 10.x.x**」→「**Windows x64 Installer**」を選んでください。
(「Runtime」ではなく「SDK」です)

インストール後、コマンドプロンプトまたはPowerShellで確認します。

```
dotnet --list-sdks
```

`10.x.x` が表示されればOKです。

### ネットワーク接続

このプロジェクトは外部NuGetパッケージに依存していませんが、
初回ビルド時に .NET SDK が内部的に参照を解決します。

---

## 2. ビルド

ソースを任意の場所に展開し、そのフォルダで以下を実行します。

```
dotnet publish src\TextPolish.App\TextPolish.App.csproj -c Release -r win-x64 --self-contained false -o publish
```

`ビルドに成功しました` と表示されれば完了です。
生成物は `publish\` フォルダに出力されます。

```
publish\TextPolish.exe
```

### ランタイム同梱版を作りたい場合(.NET未導入の端末向け)

配布先に .NET を入れられない場合は、自己完結型でビルドできます。
サイズは大きくなりますが、.NETのインストールが不要になります。

```
dotnet publish src\TextPolish.App\TextPolish.App.csproj -c Release -r win-x64 --self-contained true -o publish-standalone
```

---

## 3. 起動と初期設定

`publish\TextPolish.exe` を実行するとタスクトレイに常駐します。
設定手順は同梱の **README-インストール手順.txt** を参照してください。

要点:

1. トレイアイコンを右クリック →「設定」
2. 「LLM」タブでプロバイダを選び、APIキーとモデルを入力
3. 「一般」タブでホットキー(既定 F10)を確認
4. テキストを選択して F10 → パレットで整形 → 「置き換える」

---

## 4. 動作確認

GUIを起動せずにLLM整形だけを試せるコンソールツールがあります。
プロンプトやプリセットを調整するときに便利です。

```
dotnet build TextPolish.sln -c Release
```

```
src\TextPolish.ConsoleTest\bin\Release\net10.0-windows\TextPolish.ConsoleTest.exe publish\appsettings.json
```

任意の文とプリセットを指定することもできます。

```
TextPolish.ConsoleTest.exe publish\appsettings.json "あーときょうのばんごはんは" "誤変換だけ直す"
```

プリセットはカンマ区切りで複数指定でき、パレットで複数選択したときと同じ
組み合わせを確認できます(指定順ではなく Presets の定義順に適用されます)。

```
TextPolish.ConsoleTest.exe publish\appsettings.json "あーと明日の会議ですが3時からで" "要約,英訳"
```

---

## 5. うまくいかないとき

### `dotnet` コマンドが見つからない
SDKインストール後、コマンドプロンプトを開き直してください(PATHの再読み込みが必要)。

### ビルドは通るが起動しない
`--self-contained false` でビルドした場合、実行には
**.NET 10 Desktop Runtime (x64)** が必要です。SDKを入れていれば含まれています。

### ソースを直したのにアプリの挙動が変わらない

`dotnet build` はビルドするだけで、配布先のフォルダは更新しません。
配布物を使っている場合は publish し直し、常駐中のアプリを再起動してください。

```
dotnet publish src\TextPolish.App\TextPolish.App.csproj -c Release -r win-x64 --self-contained false -o publish
```

publish は `appsettings.json` も上書きします(`CopyToOutputDirectory=PreserveNewest`)。
配布先で設定したAPIキーが失われるため、**上書き前に退避してください**。

### 整形が失敗する
ログにHTTPステータスが出ます。401はAPIキーの誤り、404はモデル名かエンドポイントの誤りです。

```
%AppData%\TextPolish\logs\
```

---

## 6. 構成

```
TextPolish.sln
src\
  TextPolish.Core\        … 中核ロジック(LLM連携・ホットキー・選択取得・テキスト挿入)
  TextPolish.App\         … WPFトレイ常駐アプリ(これをビルドします)
  TextPolish.ConsoleTest\ … 整形の動作確認用コンソールツール
```

### 主要な構成要素

| 場所 | 役割 |
|---|---|
| `Core/Hotkey/GlobalHotkeyService.cs` | グローバルホットキー(低レベルフック) |
| `Core/Hotkey/HotkeyDefinition.cs` | ホットキー表記の解釈("Ctrl+Shift+W" 等) |
| `Core/Automation/SelectionCapture.cs` | 選択テキストの取得(Ctrl+C経由) |
| `Core/Automation/ClipboardTextInserter.cs` | 整形結果の書き戻し(Ctrl+V経由) |
| `Core/Llm/` | LLMプロバイダ実装(Azure OpenAI / Gemini / OpenAI互換) |
| `Core/Configuration/PolishSettings.cs` | 整形プリセットの定義 |
| `Core/Llm/GlossarySystemPromptProvider.cs` | 用語辞書の読み込みとプロンプトへの追記 |
| `App/Glossary.txt` | 用語辞書(誤変換されがちな表記 → 正しい表記) |
| `Core/Diff/TextDiff.cs` | 整形結果の差分計算(LCS、外部ライブラリ不使用) |
| `Core/History/InstructionHistory.cs` | 自由指示の履歴 |
| `App/PaletteWindow.xaml` | 整形パレットのUI |

設計上の判断や、開発過程で得られた知見は
**IMPLEMENTATION-NOTES.md** に記載しています。
