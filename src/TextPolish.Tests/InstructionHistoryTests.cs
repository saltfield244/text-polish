using System.IO;
using TextPolish.Core.History;

namespace TextPolish.Tests;

/// <summary>
/// 自由指示の履歴(<see cref="InstructionHistory"/>)。
///
/// 残るのは指示の文面だけだが、ファイルは**平文**で、自由指示には実務の文脈が
/// 混ざりやすい(「〇〇社向けの謝罪文に直す」など)。そのため v1.0.3 で
/// 「保存するかどうか」と「消す」を足した。ここで固定したいのは
/// **保存しないと決めたら本当に残らない**ことで、具体的には次の3点。
///
///   ・無効なら新しい指示をファイルへ書かない
///   ・無効なら、その場の候補としても出てこない(閉じるまで出続けない)
///   ・<see cref="InstructionHistory.Clear"/> でファイルそのものが消える
///
/// 保存先は実際のファイルなので、テストごとに一時フォルダを作って使い捨てる
/// (既定の %AppData%\TextPolish\instruction-history.txt には触らない)。
/// ファイルI/Oだけでデスクトップを使わないため <c>--no-clipboard</c> でも実行される。
/// </summary>
internal static class InstructionHistoryTests
{
    public static IEnumerable<(string, Action)> All()
    {
        yield return ("自由指示の履歴: 既定では保存され、読み直せる", EnabledSavesAndReloads);
        yield return ("自由指示の履歴: 同じ指示は重複せず先頭へ移る", DuplicateMovesToFront);
        yield return ("自由指示の履歴: 20件を超えたら古いものから捨てる", OldestEntriesAreDropped);
        yield return ("自由指示の履歴: 保存しない設定ならファイルを作らない", DisabledWritesNoFile);
        yield return ("自由指示の履歴: 保存しない設定ならその場の候補にも残らない", DisabledKeepsNoEntryInMemory);
        yield return ("自由指示の履歴: 保存しない設定なら既存ファイルを読み込まない", DisabledDoesNotLoadExistingFile);
        yield return ("自由指示の履歴: 途中で無効にすると、それ以降は書かない", DisablingStopsFurtherWrites);
        yield return ("自由指示の履歴: Clear でファイルが消える", ClearDeletesFile);
        yield return ("自由指示の履歴: Clear はファイルが無くても成功する", ClearSucceedsWithoutFile);
        yield return ("自由指示の履歴: 消せなかったときは false を返して警告する", ClearFailureIsReported);
    }

    private static void EnabledSavesAndReloads()
    {
        WithTempFile(path =>
        {
            var history = new InstructionHistory(new NullLogger(), path);
            history.Add("もっと丁寧に");

            Assert.True(File.Exists(path), "ファイルが作られる");

            // 別のインスタンスで読み直す(アプリを起こし直したのと同じ状況)。
            var reloaded = new InstructionHistory(new NullLogger(), path);
            reloaded.Load();

            Assert.Equal(1, reloaded.Entries.Count, "読み直した件数");
            Assert.Equal("もっと丁寧に", reloaded.Entries[0], "読み直した内容");
        });
    }

    private static void DuplicateMovesToFront()
    {
        WithTempFile(path =>
        {
            var history = new InstructionHistory(new NullLogger(), path);
            history.Add("丁寧に");
            history.Add("英訳して");
            history.Add("丁寧に");

            Assert.Equal(2, history.Entries.Count, "重複しない件数");
            Assert.Equal("丁寧に", history.Entries[0], "先頭");
        });
    }

    private static void OldestEntriesAreDropped()
    {
        WithTempFile(path =>
        {
            var history = new InstructionHistory(new NullLogger(), path);
            for (var i = 1; i <= 25; i++)
            {
                history.Add($"指示{i}");
            }

            Assert.Equal(20, history.Entries.Count, "保持する件数");
            Assert.Equal("指示25", history.Entries[0], "先頭は最後に足したもの");
            Assert.True(
                history.Entries.Contains("指示1") == false,
                "最初の指示は捨てられている");
            Assert.True(
                File.ReadAllLines(path).Length == 20,
                "ファイルにも20件だけ残る");
        });
    }

    private static void DisabledWritesNoFile()
    {
        WithTempFile(path =>
        {
            var history = new InstructionHistory(new NullLogger(), path, enabled: false);
            history.Add("社外秘の案件Xについて柔らかく書き直す");

            Assert.True(File.Exists(path) == false, "ファイルは作られない");
        });
    }

    private static void DisabledKeepsNoEntryInMemory()
    {
        WithTempFile(path =>
        {
            var history = new InstructionHistory(new NullLogger(), path, enabled: false);
            history.Add("社外秘の案件Xについて柔らかく書き直す");

            // ファイルに書かないだけでは足りない。「保存しない」を選んだのに
            // 閉じるまでドロップダウンに出続けるのは、選んだことと食い違う。
            Assert.Equal(0, history.Entries.Count, "その場の候補の件数");
        });
    }

    private static void DisabledDoesNotLoadExistingFile()
    {
        WithTempFile(path =>
        {
            File.WriteAllLines(path, new[] { "前に保存した指示" });

            var history = new InstructionHistory(new NullLogger(), path, enabled: false);
            history.Load();

            Assert.Equal(0, history.Entries.Count, "読み込まれた件数");
        });
    }

    private static void DisablingStopsFurtherWrites()
    {
        WithTempFile(path =>
        {
            var history = new InstructionHistory(new NullLogger(), path);
            history.Add("丁寧に");

            // 設定画面で切り替えると、アプリを起こし直さずにここが変わる。
            history.Enabled = false;
            history.Add("社外秘の案件Xについて柔らかく書き直す");

            var lines = File.ReadAllLines(path);
            Assert.Equal(1, lines.Length, "ファイルの行数");
            Assert.Equal("丁寧に", lines[0], "残っている内容");
        });
    }

    private static void ClearDeletesFile()
    {
        WithTempFile(path =>
        {
            var history = new InstructionHistory(new NullLogger(), path);
            history.Add("丁寧に");
            history.Add("英訳して");

            Assert.True(history.Clear(), "消せたと報告する");
            Assert.True(File.Exists(path) == false, "ファイルが消えている");
            Assert.Equal(0, history.Entries.Count, "その場の候補も空になる");
        });
    }

    private static void ClearSucceedsWithoutFile()
    {
        WithTempFile(path =>
        {
            var history = new InstructionHistory(new NullLogger(), path);

            // 一度も保存していない状態で押されることがある。例外にしない。
            Assert.True(history.Clear(), "ファイルが無くても成功とする");
        });
    }

    private static void ClearFailureIsReported()
    {
        WithTempFile(path =>
        {
            var logger = new NullLogger();
            var history = new InstructionHistory(logger, path);
            history.Add("丁寧に");

            // 開いたまま掴んでおくと削除できない。黙って成功したように見せないこと。
            using (var holding = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.True(history.Clear() == false, "消せなかったと報告する");
            }

            Assert.True(logger.Warnings.Count > 0, "警告がログに残る");
            Assert.True(File.Exists(path), "ファイルはまだある");
        });
    }

    /// <summary>
    /// 使い捨ての一時フォルダにファイル名を1つ用意して渡し、後片付けする。
    /// 既定の保存先(%AppData%)を汚さないため、パスは必ずここから取る。
    /// </summary>
    private static void WithTempFile(Action<string> body)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "TextPolishTests-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            body(Path.Combine(dir, "instruction-history.txt"));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 後片付けに失敗してもテストの結果は変えない(一時フォルダなので残っても害がない)。
            }
        }
    }
}
