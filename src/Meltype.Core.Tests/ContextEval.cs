// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using System.Text.Json;

namespace Meltype.Tests;

/// <summary>
/// 前に確定した文字列を文脈にした変換の正解率 (仮の計測。MELTYPE_MOZC に Mozc の変換ヘルパーの場所が要る)。
/// 入力ファイルは 1 行に「前に打つもの&lt;Tab&gt;次に打つもの&lt;Tab&gt;期待」。期待は | で区切って複数書ける。
///   前を確定してから: 前に打つものを Space で変換して Enter で確定し、続けて次に打つものを Space で変換する
///   前なし:           次に打つものだけを Space で変換する
/// どちらも Mozc には覚えさせない (ユーザー学習 OFF と同じ。前の例文の確定が次の例文の結果を変えないように)。
///   dotnet run --project src/Meltype.Core.Tests -- --context-eval tools/context-eval-cases.tsv 出力ファイル
/// </summary>
internal static class ContextEval
{
    public static void Run(string input, string output)
    {
        var helper = Environment.GetEnvironmentVariable("MELTYPE_MOZC");
        if (helper is null || !File.Exists(helper)) throw new InvalidOperationException("MELTYPE_MOZC に Mozc の変換ヘルパーの場所を入れてください");
        var profile = Path.Combine(Path.GetTempPath(), $"meltype-context-eval-{Environment.ProcessId}");
        try
        {
            using var mozc = new Composition.MozcConverter(helper, profile);
            CompositionTests.Detector.SpellChecker ??= Detection.BuiltInWordChecker.Shared;
            CompositionTests.Keyboard Create() => new(converter: mozc, moreCandidates: mozc.Candidates,
                userDictionary: new Composition.UserDictionary(null)) { Learning = false };

            var results = new List<object>();
            int contextOk = 0, aloneOk = 0;
            foreach (var line in File.ReadAllLines(input))
            {
                var parts = line.Split('\t');
                if (parts.Length != 3 || parts[0].StartsWith('#')) continue;
                var (before, typed, expected) = (parts[0].Trim(), parts[1].Trim(), parts[2].Trim().Split('|'));

                // 前を確定してから
                var k = Create();
                k.Type(before + " ");
                var calls = mozc.Statistics().HelperCalls;
                k.Type("\n");
                var committed = k.Host.Document;
                // 確定した文節を変換エンジンに渡すのは裏で行うので、渡し終わるまで待つ
                for (var i = 0; i < 200 && mozc.Statistics().HelperCalls == calls; i++) Thread.Sleep(10);
                k.Type(typed + " ");
                var withContext = Converted(k);

                // 前なし
                var alone = Create();
                alone.Type(typed + " ");
                var withoutContext = Converted(alone);
                mozc.ClearCache();

                var ok1 = expected.Contains(withContext);
                var ok2 = expected.Contains(withoutContext);
                if (ok1) contextOk++;
                if (ok2) aloneOk++;
                results.Add(new { before, typed, expected = string.Join("|", expected), committed, withContext, withoutContext, contextOk = ok1, aloneOk = ok2 });
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new { contextOk, aloneOk, total = results.Count, cases = results },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            Console.WriteLine($"前を確定してから {contextOk}/{results.Count}、前なし {aloneOk}/{results.Count}");
        }
        finally
        {
            try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); } catch { }
        }
    }

    /// <summary>Space で変換した結果 (変換していなければ確定した文字列)。</summary>
    private static string Converted(CompositionTests.Keyboard k) =>
        k.Host.View is { Converting: true } view ? string.Concat(view.Clauses ?? [view.Text]) : k.Host.Document.TrimEnd();
}
