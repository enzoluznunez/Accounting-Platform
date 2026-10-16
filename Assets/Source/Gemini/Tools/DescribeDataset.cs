using System;
using System.Collections.Generic;
using System.Text;
using Google.GenAI.Types;

public sealed class DescribeDataset : AgenticTool<DescribeDataset.Args> {

    private const int DefaultLines = 200;
    private const int MaxCharacters = 15000;

    private static string lastPagedLabel;

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => lastPagedLabel = null;

    public static void Forget() => lastPagedLabel = null;

    public class Args {
        [Doc("The first line to return, 1-based."), Limits(1, int.MaxValue), DefaultsTo(1), Optional]
        public int? fromLine;
        [Doc("How many lines to return."), Limits(1, 100000), DefaultsTo(200), Optional]
        public int? lineCount;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "DescribeDataset",
        Description = "Read the open dataset's raw source text, as it was scanned. " +
                      "Use it for what the grid does not carry: header lines, units, footnotes, and columns that " +
                      "could not be read as numbers. For the numbers themselves use GetNumbers, which is far cheaper. " +
                      "Returns 200 lines at a time, so read the start first and page on with 'fromLine' only if you " +
                      "still need to; 'totalLines' says how many there are.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        var data = Scene.Data;
        if (data == null || !data.IsLoaded) { result["error"] = "No dataset is open."; return; }

        string raw = data.RawText;
        if (string.IsNullOrEmpty(raw)) {
            result["error"] = "The open dataset kept no source text; read it with GetNumbers instead.";
            return;
        }

        string[] lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        int total = lines.Length;

        int from = Math.Max(1, args.fromLine ?? 1);

        string current = Scene.DatasetLabel;
        if (from > 1 && lastPagedLabel != null && !string.Equals(lastPagedLabel, current)) {
            Refuse(result, "continuation from a different dataset",
                $"Your last read came from {lastPagedLabel}, but {current} is open now. " +
                "Page reads start over after a switch; call again from line 1.");
            result["dataset"] = current;
            return;
        }

        if (from > total) {
            result["error"] = $"The file has {total} lines, so line {from} is past its end.";
            result["totalLines"] = total;
            return;
        }

        int take = Math.Max(args.lineCount ?? DefaultLines, 1);
        take = Math.Min(take, total - from + 1);

        var page = new StringBuilder();
        int included = 0;
        bool truncated = false;
        for (int i = 0; i < take; i++) {
            string line = lines[from - 1 + i];
            if (included > 0) {
                if (page.Length + 1 + line.Length > MaxCharacters) { truncated = true; break; }
                page.Append('\n');
                page.Append(line);
            }
            else if (line.Length > MaxCharacters) {
                page.Append(line, 0, MaxCharacters);
                truncated = true;
                included++;
                break;
            }
            else {
                page.Append(line);
            }
            included++;
        }

        lastPagedLabel = current;
        result["dataset"] = current;
        result["fromLine"] = from;
        result["lineCount"] = included;
        result["totalLines"] = total;
        result["text"] = page.ToString();
        if (truncated) result["truncated"] = true;
        if (from + included - 1 < total) result["more"] = $"Lines {from + included} to {total} were not returned.";
    }
}
