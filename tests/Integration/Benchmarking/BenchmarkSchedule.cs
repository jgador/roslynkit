namespace RoslynKit.Benchmarking;

/// <summary>
/// Creates the alternating paired-session schedule and filters completed tuples for resume.
/// </summary>
internal static class BenchmarkSchedule
{
    public static IReadOnlyList<BenchmarkSessionKey> Create(BenchmarkRunDocument document)
    {
        var schedule = new List<BenchmarkSessionKey>();
        for (var trial = 1; trial <= document.Configuration.Trials; trial++)
        {
            var conditions = trial % 2 == 1
                ? BenchmarkConditions.Ordered
                : BenchmarkConditions.Ordered.Reverse().ToArray();
            foreach (var benchmarkCase in document.Cases)
            {
                foreach (var condition in conditions)
                {
                    schedule.Add(new BenchmarkSessionKey(benchmarkCase.Id, condition, trial));
                }
            }
        }

        return schedule;
    }

    public static IReadOnlyList<BenchmarkSessionKey> Pending(BenchmarkRunDocument document)
    {
        var completed = document.Sessions
            .Select(session => new BenchmarkSessionKey(session.CaseId, session.Condition, session.Trial))
            .ToHashSet();
        return Create(document).Where(key => !completed.Contains(key)).ToArray();
    }
}
