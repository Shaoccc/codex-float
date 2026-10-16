using System;
class ParserTests
{
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
    static int Main()
    {
        var q = Quota.Parse("{\"result\":{\"rateLimits\":{\"primary\":null,\"secondary\":{\"usedPercent\":100,\"windowDurationMins\":10080,\"resetsAt\":1800000000}}}}");
        Check(q.Five == null && q.Week.Remaining == 0 && q.Week.Reset == 1800000000, "weekly-only / zero remaining");
        q = Quota.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":90,\"windowDurationMins\":300}},\"rateLimitsByLimitId\":{\"codex\":{\"secondary\":{\"usedPercent\":12,\"windowDurationMins\":300}}}}");
        Check(q.Five.Remaining == 88 && q.Week == null, "prefer account bucket / swapped windows");
        q = Quota.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":null,\"windowDurationMins\":300}}}");
        Check(q.Five == null, "null is unavailable");
        q = Quota.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":0,\"windowDurationMins\":15}}}");
        Check(q.Five == null, "do not label non-five-hour window as five-hour");
        q = Quota.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":120,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":-10,\"windowDurationMins\":10080}}}");
        Check(q.Five.Remaining == 0 && q.Week.Remaining == 100, "clamp percentages");
        bool rejected = false;
        try { Quota.Parse("{\"rateLimits\":{\"limitId\":\"codex_other\"}}"); } catch { rejected = true; }
        Check(rejected, "reject unrelated model bucket");
        Console.WriteLine("PASS: 6 quota parsing checks");
        return 0;
    }
}
