using System.Diagnostics;
using System.Text.Json;
using KHZ.Sheet.Core;

const int warmup = 1000;
const int rounds = 9;
const int operationsPerRound = 5000;

if (NativeSheet.TryCreate(8u << 20, 4096, out NativeSheet? sheet) != SheetStatus.Ok || sheet is null)
    return 2;

using (sheet)
{
    sheet.SetInt64(0, 0, 4);
    sheet.SetInt64(0, 1, 3);
    SheetStatus parsed = FormulaParser.TryParse(
        "IF(A1>2,PRODUCT(A1:A2),ABS(-1))", out FormulaNode? node);
    if (parsed != SheetStatus.Ok || node is null)
        return 3;

    for (int i = 0; i < warmup; i++)
    {
        if (sheet.TryEvaluate(1, 0, node, out _) != SheetStatus.Ok)
            return 4;
    }

    var samples = new double[rounds];
    for (int r = 0; r < rounds; r++)
    {
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < operationsPerRound; i++)
        {
            if (sheet.TryEvaluate(1, 0, node, out KhzFormulaResult result) != SheetStatus.Ok ||
                result.IsError || result.Value.Numerator != 12 || result.Value.Denominator != 1)
                return 5;
        }

        long stop = Stopwatch.GetTimestamp();
        samples[r] = (stop - start) * 1_000_000_000.0 /
                     Stopwatch.Frequency / operationsPerRound;
    }

    Array.Sort(samples);
    double median = samples[rounds / 2];
    double p95 = samples[(int)Math.Ceiling(rounds * 0.95) - 1];

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        benchmark = "managed-tryevaluate-scratch-arena",
        warmup,
        rounds,
        operationsPerRound,
        medianNsPerOp = median,
        p95NsPerOp = p95,
        minNsPerOp = samples[0],
        maxNsPerOp = samples[^1]
    }));
}
return 0;
