# リリース前チェックリスト(v1.0.2)

v1.0.1 からの差分は、どれも**書き戻し・クリップボード・APIキーといった壊れると困る箇所**に
手を入れています。静的解析と自動テストで確認できる範囲は限られるため、
**Windows実機での確認を終えるまで公開しない**という前提で進めます。

状態の記号: ✅ 済み / ⬜ 未 / ⚠ 実機確認が必要

---

## 1. 現在の状態

| 区分 | 内容 | コード | 自動テスト | 実機確認 |
|---|---|---|---|---|
| クリップボード | 全形式の退避・復元、復元待ち中の上書き防止 | ✅ | ✅ 12件 | ⚠ 未(手順A・B) |
| APIキー | DPAPIで暗号化、平文設定からの移行 | ✅ | ✅ 10件 | ⚠ 未(手順C) |
| ホットキー | 連打・並行実行の排他 | ✅ | ✅ 11件 | ⚠ 未(手順D) |
| 書き戻し | フォーカス復帰の失敗で中止、送出直前の照合 | ✅ | ✅ 8件 | ⚠ 未(手順E) |
| LLM応答 | 終了理由の検査(途中終了を採用しない) | ✅ | ✅ 16件 | ⚠ 未(手順F) |
| パレット | 再整形失敗時に前回の結果を書き戻さない | ✅ | ✅ 12件 | ⚠ 未(手順G) |
| プライバシー | 周辺文脈の送信ON/OFF(既定OFF) | ✅ | ✅ 7件 | ⚠ 未(手順H) |
| CI | GitHub Actions(Releaseビルド + `--no-clipboard`) | ✅ | ✅ 成功。失敗時に赤くなることも確認済み | — |

「自動テスト」の件数は `src/TextPolish.Tests` の回帰テストです。実機確認の手順は
[src/TextPolish.Tests/README.md](src/TextPolish.Tests/README.md) にあります。

**コードが入っていることは、動くことの証明ではありません。** 上の ⚠ を消すまでは
「実装済み・未検証」として扱ってください。

---

## 2. 公開前に必ず終わらせること

### 2.1 自動で確認できるもの

```bash
dotnet build TextPolish.sln -c Release
```

```bash
dotnet run --project src\TextPolish.Tests
```

- [ ] Releaseビルドが0警告0エラーで通る
- [ ] 回帰テストが全件成功する(クリップボードのテストを含む)
- [ ] GitHub Actions の `build` が main で成功している(下記4)

クリップボードのテストは以前、実行するたびに1〜2件が不定に失敗していました。
原因はテスト側の WinForms `Clipboard` 呼び出しが他プロセスとの競合に弱いことで、
検証対象の `ClipboardSnapshot` 側ではありませんでした(詳細は
[src/TextPolish.Tests/README.md](src/TextPolish.Tests/README.md))。
テスト側にリトライを入れ、連続10回の実行がすべて 76/76 で成功することを確認しています。
もし再び不定に失敗するようなら、原因を確かめるまで公開しないでください。

### 2.2 実機で確認するもの

`src/TextPolish.Tests/README.md` の手順を実施します。

- [ ] A. クリップボードの保持(A1〜A8)
- [ ] B. 復元待ち中の競合(B1〜B5)
- [ ] C. APIキーの暗号化保存(C1〜C8)
- [ ] D. ホットキーの連打・同時実行(D1〜D12)
- [ ] E. 書き戻し先の検証(E1〜E8)
- [ ] F. LLM応答の途中終了(F1〜F5)
- [ ] G. パレットの再整形(G1〜G6)
- [ ] H. 周辺文脈の送信ON/OFF(H1〜H5)

### 2.3 更新手順の確認(既存利用者の環境を壊さないこと)

- [ ] v1.0.1 の `appsettings.json`(平文キー・編集済みプリセット)と編集済みの
      `Glossary.txt` / `PolishSystemPrompt.txt` を持つフォルダを用意する
- [ ] [README-更新手順.md](README-更新手順.md) の手順Aをそのとおりに実施する
- [ ] 更新後、同ドキュメント「4. 更新後に確認すること」の1〜7がすべて期待どおり
- [ ] 控えのフォルダへ戻せば v1.0.1 として動く(ロールバックできる)

---

## 3. 配布物の準備

- [x] `src/TextPolish.App/TextPolish.App.csproj` の `<Version>` を `1.0.2` にする
- [ ] [CHANGELOG.md](CHANGELOG.md) の「未リリース」を外し、公開日を入れる
- [ ] publish する

  ```bash
  dotnet publish src\TextPolish.App\TextPolish.App.csproj -c Release -r win-x64 --self-contained false -o publish
  ```

- [ ] publish 出力の `appsettings.json` が**配布用のテンプレート**であることを確認する
      (`TODO-api-key` のまま。自分のAPIキーが入っていないこと。`ApiKeyProtected` がないこと)
- [ ] `Glossary.txt` / `PolishSystemPrompt.txt` が配布用の内容であることを確認する
      (開発中の個人的な用語が入っていないこと)
- [ ] `README-インストール手順.txt` と `README-更新手順.md` を同梱する
- [ ] ZIP 名を `TextPolish-v1.0.2-win-x64.zip` にする
- [ ] 別のPC(または別のWindowsユーザー)でZIPを展開し、新規インストールとして動くことを確認する
- [ ] `git tag v1.0.2` を打ち、リリースを作成して
      [README-更新手順.md](README-更新手順.md) の「5. 既存利用者向けの更新案内」を本文に載せる

### 配布前の秘密情報チェック

```bash
dotnet publish src\TextPolish.App\TextPolish.App.csproj -c Release -r win-x64 --self-contained false -o publish
```

```bash
git grep -n "TODO-api-key" -- src/TextPolish.App/appsettings.json
```

- [ ] publish 出力に自分のAPIキー(平文・暗号文とも)が含まれていない
- [ ] リポジトリに追跡されている `appsettings.json` はテンプレートだけ
      (`.gitignore` で `**/bin/**/appsettings.json` と `dist/` を除外済み)

---

## 4. GitHub Actions の初回実行で確認すること

`.github/workflows/build.yml` を push したあと、リポジトリの **Actions** タブで
`build` ワークフローの実行を開きます。

### 初回実行の結果(2026-10-23)

[build #1](https://github.com/saltfield244/text-polish/actions/runs/40016691802)
(commit `c12bda7`)は **Success / 36秒** で完了しました。

- [x] ワークフローが自動で起動している(Actionsタブに `build` が出る)
- [x] ランナーが `windows-latest` で、`Setup .NET 10 SDK` が成功している
- [x] `Build (Release)` が成功する
- [x] `Test (クリップボードを使わないもの)` が成功し、ジョブが緑になる
      (テストは失敗が1件でもあれば終了コード1を返すため、ジョブが緑であること自体が全件成功を意味する)
- [ ] `Show SDK version` のログに `10.x.x` が出ている
      (`10.0.x` が解決できない場合はここで失敗する。失敗したら
      `dotnet-version` を実機と同じ具体的なバージョンに変える)
      → **ビルドが成功したので解決はできている。ただしログ本文はサインインしないと読めないため、
      出力そのものは未確認。**
- [ ] `Test (クリップボードを使わないもの)` の最後に `63/63 件成功` のような行が出ている
      → 同上。ジョブの成否からは分かるが、行そのものは未確認
- [x] 意図的にテストを1件壊した状態で push すると**赤くなる**ことを一度確認する
      (通っているのではなく、見ていることを確かめる)
      → `PasteTargetTests` の期待値を1つ誤った値にして push し、
      [build #4](https://github.com/saltfield244/text-polish/actions/runs/40046679888) が
      **failure** になることを確認。revert 後の
      [build #5](https://github.com/saltfield244/text-polish/actions/runs/40046823027) で
      緑に戻ることも確認した(コミット `35368c3` と、その revert `aeecfae`)。
- [ ] Pull Request を作ると同じワークフローが走る

初回実行では、`actions/checkout@v4` と `actions/setup-dotnet@v4` が Node.js 20 を
対象にしているという非推奨の警告が出ました(実行自体は Node.js 24 で成功)。
`checkout@v7` / `setup-dotnet@v6` へ更新し、
[build #2](https://github.com/saltfield244/text-polish/actions/runs/40045526150)
が警告なしで成功することを確認しています。

CIは `--no-clipboard` で実行するため、**クリップボードのテストはCIでは通りません**。
CIが緑でも、上記2.2の実機確認を省略しないでください。

---

## 5. 公開を判断する条件

次のすべてが満たされるまで公開しません。

1. 2.1 のすべてにチェックが入っている
2. 2.2 の A〜H のすべてにチェックが入っている
   (特に **E(書き戻し先の検証)** と **F(途中終了の検査)** は今回の新規修正で、
   自動テストでは判定ロジックしか見ていないため実機確認が必須)
3. 2.3 の更新手順を実際に通し、既存の設定・APIキー・プリセット・用語辞書が残ることを確認した
4. 3 の配布物チェックで、秘密情報が混入していないことを確認した
5. 4 のCIが main で緑になっている
   (初回実行・失敗検出とも確認済み。残るのは Pull Request での起動確認だけで、
   次にPRを作ったときに分かる)

### 一部だけ先に出したい場合

E・F・G・H は互いに独立しているため、実機確認が終わった範囲だけで出すこともできます。
その場合は未検証の変更を revert してからビルドし、CHANGELOG からも該当項目を外してください。
「実装したが未検証」のものを含めたまま公開しないでください。
