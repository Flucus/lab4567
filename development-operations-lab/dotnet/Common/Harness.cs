namespace Training.Operations;

// Independent literal oracles compiled unchanged into either track. No test framework.
public static class Harness
{
    private sealed class CheckFailure(string message) : Exception(message) { }
    private sealed record Test(string Group, string Name, Action<Fixture> Check);
    private const string AliceToken = "Bearer ALICE-DEMO";
    private const string BobToken = "Bearer BOB-DEMO";
    private static Identity Alice(Fixture f) => f.Authenticate(AliceToken)!;
    private static Identity Bob(Fixture f) => f.Authenticate(BobToken)!;
    public static Proposal Read(int id) => new("readDocument", new() { ["documentId"] = id });
    private static Proposal Propose(string? tool, params (string Key, object Value)[] args) =>
        new(tool, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));
    private static void Require(bool condition, string safeMessage)
    {
        if (!condition) throw new CheckFailure(safeMessage);
    }
    private static void Exact(Response response, params int[] expected)
    {
        Require(response.Status == 200, "expected status 200");
        Require(response.Rows.Select(r => (int)r["id"]).SequenceEqual(expected), "exact document IDs differ");
    }
    private static void Denied(Fixture f, Content result, int readsBefore)
    {
        Require(result.Status == 403 && result.Body.Length == 0, "expected empty 403");
        Require(f.ContentReads == readsBefore, "denial caused a content read");
    }
    private static void OutcomeCheck(Fixture f, Proposal proposal, Decision expected, int reads, int effects)
    {
        var result = new Broker(f, new Budget(3)).Call(Alice(f), proposal);
        Require(result.Decision == expected, "broker decision differs");
        Require(f.ContentReads == reads && f.ToolEffects == effects, "forbidden read/effect count");
        if (expected != Decision.ALLOWED) Require(result.Body.Length == 0, "denial disclosed content");
        Require(f.Audit.SequenceEqual(new[] { "broker:" + expected }), "missing/minimal audit differs");
    }
    private static void Await(ManualResetEventSlim signal) => Require(signal.Wait(TimeSpan.FromSeconds(5)), "barrier timed out");
    private static void Rendezvous(Barrier barrier) => Require(barrier.SignalAndWait(TimeSpan.FromSeconds(5)), "budget barrier failed");
    private static T Complete<T>(Task<T> task)
    {
        Require(task.Wait(TimeSpan.FromSeconds(8)), "worker timed out");
        return task.GetAwaiter().GetResult();
    }
    private static Task<T> Worker<T>(Func<T> call) => Task.Factory.StartNew(call,
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    private static void Drain(params Task[] tasks)
    {
        // All worker waits are bounded; ensure their test seams finish before disposing fixture/events.
        try { Require(Task.WaitAll(tasks, TimeSpan.FromSeconds(8)), "worker cleanup timed out"); }
        catch (AggregateException) { /* The test reports the worker failure, never raw exceptions. */ }
    }
    private static Test Lab1(string name, Action<Fixture> check) => new("lab1", name, check);
    private static Test Lab2(string name, Action<Fixture> check) => new("lab2", name, check);
    private static Test Surface(string name, Func<Surfaces, Identity, int, Content> access) =>
        Lab2(name, f => Denied(f, access(new Surfaces(f), Alice(f), 102), 0));

    private static List<Test> Tests() => new()
    {
        Lab1("01 Alice Contract -> 101", f => Exact(new Search(f).Find(AliceToken, "Contract"), 101)),
        Lab1("02 Bob Contract -> 102", f => Exact(new Search(f).Find(BobToken, "Contract"), 102)),
        Lab1("03 apostrophe exact match -> 103", f => Exact(new Search(f).Find(AliceToken, "O'Reilly Guide"), 103)),
        Lab1("04 Anonymous -> 401 without DB", f =>
        {
            Require(new Search(f).Find(null, "Contract").Status == 401, "expected 401");
            Require(f.SearchQueries == 0, "anonymous queried DB");
        }),
        Lab1("05 NoScope -> 403 before DB", f =>
        {
            Require(new Search(f).Find("Bearer NOSCOPE-DEMO", "Contract").Status == 403, "expected 403");
            Require(f.SearchQueries == 0, "NoScope queried DB");
        }),
        Lab1("06 unmatched SQL-control literal -> zero rows", f => Exact(new Search(f).Find(AliceToken, "' OR '1'='1"))),
        Lab1("07 >100 characters -> 400 before DB", f =>
        {
            Require(new Search(f).Find(AliceToken, new string('x', 101)).Status == 400, "expected 400");
            Require(f.SearchQueries == 0, "oversize name queried DB");
        }),
        Lab1("08 synthetic token absent from all captured sinks", f =>
        {
            new Search(f).Find(AliceToken, "Contract");
            new Broker(f, new Budget(1)).Call(Alice(f), Read(101));
            string captured = string.Join("\n", f.Logs) + string.Join("\n", f.Audit);
            Require(!captured.Contains("ALICE-DEMO") && !captured.Contains("Bearer"), "token leaked to captured sink");
            Require(!captured.Contains("retention") && !captured.Contains("documentId"), "content/raw args logged");
        }),
        Lab1("09 blank/null name -> 400", f =>
        {
            foreach (string? value in new string?[] { null, "", "  " })
                Require(new Search(f).Find(AliceToken, value).Status == 400, "expected blank/null 400");
            Require(f.SearchQueries == 0, "blank/null name queried DB");
        }),
        Lab1("10 response is only id/name DTO", f =>
        {
            var result = new Search(f).Find(AliceToken, "Contract");
            Require(result.Status == 200 && result.Rows.Count > 0, "missing positive DTO result");
            foreach (var row in result.Rows)
                Require(new HashSet<string>(row.Keys, StringComparer.Ordinal).SetEquals(new[] { "id", "name" }), "full entity fields exposed");
        }),
        Lab1("11 exact matching, case and 100 boundary", f =>
        {
            Exact(new Search(f).Find(AliceToken, "contract"));
            Exact(new Search(f).Find(AliceToken, "Contract "));
            Exact(new Search(f).Find(AliceToken, new string('x', 100)));
        }),
        Lab1("12 unrecognised token -> 401", f =>
        {
            Require(new Search(f).Find("Bearer NOT-A-FIXTURE", "Contract").Status == 401, "unknown token accepted");
            Require(f.SearchQueries == 0, "unknown token queried DB");
        }),
        Lab2("13 broker authorised read", f => OutcomeCheck(f, Read(101), Decision.ALLOWED, 1, 1)),
        Lab2("14 broker cross-supplier denied before content", f => OutcomeCheck(f, Read(102), Decision.DENIED, 0, 0)),
        Lab2("15 broker approveRisk always denied", f => OutcomeCheck(f, Propose("approveRisk", ("documentId", 101)), Decision.DENIED, 0, 0)),
        Lab2("16 broker unknown tool rejected", f => OutcomeCheck(f, Propose("shell", ("documentId", 101)), Decision.REJECTED, 0, 0)),
        Lab2("17 broker malformed arguments rejected", f => OutcomeCheck(f, Propose("readDocument", ("documentId", "101")), Decision.REJECTED, 0, 0)),
        Lab2("18 broker budget stopped", f =>
        {
            var broker = new Broker(f, new Budget(1));
            Require(broker.Call(Alice(f), Read(101)).Decision == Decision.ALLOWED, "first call not allowed");
            var result = broker.Call(Alice(f), Read(101));
            Require(result.Decision == Decision.STOPPED && result.Body.Length == 0, "exhausted budget not stopped");
            Require(f.ContentReads == 1 && f.ToolEffects == 1, "budget caused extra read/effect");
        }),
        Lab2("19 model identity/extra field rejected", f => OutcomeCheck(f,
            Propose("readDocument", ("documentId", 102), ("supplierId", "SUP-B")), Decision.REJECTED, 0, 0)),
        Lab2("20 malformed schema matrix", f =>
        {
            var broker = new Broker(f, new Budget(20));
            foreach (var proposal in new Proposal?[]
            {
                null, Propose(null), new("readDocument", null), Propose("readDocument"),
                Propose("readDocument", ("documentId", 0)), Propose("readDocument", ("documentId", 1000000)),
                Propose("readDocument", ("documentId", 101L))
            }) Require(broker.Call(Alice(f), proposal).Decision == Decision.REJECTED, "schema variant accepted");
            Require(f.ContentReads == 0 && f.ToolEffects == 0, "malformed schema had effects");
        }),
        Lab2("21 unknown resource indistinguishable denial", f => OutcomeCheck(f, Read(999), Decision.DENIED, 0, 0)),
        Lab2("22 broker Anonymous and NoScope denied", f =>
        {
            var broker = new Broker(f, new Budget(2));
            Require(broker.Call(null, Read(101)).Decision == Decision.DENIED, "anonymous broker accepted");
            Require(broker.Call(f.Authenticate("Bearer NOSCOPE-DEMO"), Read(101)).Decision == Decision.DENIED, "NoScope broker accepted");
            Require(f.ContentReads == 0 && f.ToolEffects == 0, "unauthenticated/scope denial had effects");
        }),
        Surface("23 read wrong supplier", (s, who, id) => s.Read(who, id)),
        Surface("24 preview wrong supplier", (s, who, id) => s.Preview(who, id)),
        Surface("25 download wrong supplier", (s, who, id) => s.Download(who, id)),
        Surface("26 storage wrong supplier", (s, who, id) => s.Storage(who, id)),
        Lab2("27 export authorises ALL before ANY content", f => Denied(f, new Surfaces(f).Export(Alice(f), new[] { 101, 102 }), 0)),
        Lab2("28 cache Bob prime cannot reach Alice by name", f =>
        {
            var surfaces = new Surfaces(f);
            Require(surfaces.Cached(Bob(f), 102, "Contract").Status == 200, "Bob cache prime failed");
            int before = f.ContentReads;
            var own = surfaces.Cached(Alice(f), 101, "Contract");
            Require(own.Status == 200 && own.Body.StartsWith("SUP-A", StringComparison.Ordinal), "name cache crossed tenant");
            int beforeDenied = f.ContentReads;
            Denied(f, surfaces.Cached(Alice(f), 102, "Contract"), beforeDenied);
            Require(f.ContentReads >= before, "invalid instrumentation");
        }),
        Lab2("29 cache hit rechecks revocation", f =>
        {
            var surfaces = new Surfaces(f);
            surfaces.Cached(Alice(f), 101, "Contract");
            int before = f.ContentReads;
            f.Revoke(Alice(f));
            Denied(f, surfaces.Cached(Alice(f), 101, "Contract"), before);
        }),
        Lab2("30 job revocation between enqueue/execution", f =>
        {
            var surfaces = new Surfaces(f);
            var job = surfaces.Enqueue(Alice(f), 101);
            Require(job is not null, "positive enqueue failed");
            f.Revoke(Alice(f));
            Denied(f, surfaces.Run(job), 0);
        }),
        Lab2("31 job revocation before delivery", f =>
        {
            var surfaces = new Surfaces(f);
            var job = surfaces.Enqueue(Alice(f), 101);
            var complete = surfaces.Run(job);
            Require(complete.Status == 200, "positive job failed");
            int before = f.ContentReads;
            f.Revoke(Alice(f));
            Denied(f, surfaces.Deliver(job, complete), before);
        }),
        Lab2("32 positive sweep and anonymous contract", f =>
        {
            var s = new Surfaces(f);
            foreach (var result in new[] { s.Read(Alice(f), 101), s.Preview(Alice(f), 101),
                s.Download(Alice(f), 101), s.Storage(Alice(f), 101), s.Export(Alice(f), new[] { 101, 103 }),
                s.Cached(Alice(f), 101, "Contract") })
                Require(result.Status == 200 && result.Body.StartsWith("SUP-A", StringComparison.Ordinal), "authorised surface unavailable");
            Require(s.Read(null, 101).Status == 401, "anonymous surface not 401");
        }),
        Lab2("33 guarded read deterministic revocation race", SurfaceRace),
        Lab2("34 broker deterministic revocation + reservation release", BrokerRace),
        Lab2("35 concurrent one-slot budget permits only one effect", BudgetRace),
        Lab2("36 content query and mock effect under policy lock", f =>
        {
            int allGuarded = 1;
            f.BeforeContentQuery = () =>
            {
                if (!Monitor.IsEntered(f.PolicyLock)) Interlocked.Exchange(ref allGuarded, 0);
            };
            new Surfaces(f).GuardedRead(Alice(f), 101);
            new Broker(f, new Budget(1)).Call(Alice(f), Read(101));
            Require(f.ContentReads == 2 && Volatile.Read(ref allGuarded) == 1, "materialisation outside shared revocation lock");
        }),
        Lab2("37 database failure safe, charged, audited", f =>
        {
            f.Dispose();
            var broker = new Broker(f, new Budget(1));
            var result = broker.Call(Alice(f), Read(101));
            Require(result.Decision == Decision.FAILED && result.Body.Length == 0, "failure not safely mapped");
            Require(f.ContentReads == 1 && f.ToolEffects == 0, "failed read produced effect");
            Require(f.Audit.SequenceEqual(new[] { "broker:FAILED" }), "failed path not audited");
            Require(broker.Call(Alice(f), Read(101)).Decision == Decision.STOPPED, "started failed read refunded incorrectly");
        })
    };

    private static void SurfaceRace(Fixture f)
    {
        var surfaces = new Surfaces(f);
        using var checkedSignal = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        surfaces.AfterInitialCheck = () => { checkedSignal.Set(); Await(resume); };
        var work = Worker(() => surfaces.GuardedRead(Alice(f), 101));
        try
        {
            Await(checkedSignal);
            f.Revoke(Alice(f));
            resume.Set();
            Denied(f, Complete(work), 0);
        }
        finally { resume.Set(); Drain(work); }
    }
    private static void BrokerRace(Fixture f)
    {
        var budget = new Budget(1);
        var broker = new Broker(f, budget);
        using var checkedSignal = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        broker.AfterInitialCheck = () => { checkedSignal.Set(); Await(resume); };
        var work = Worker(() => broker.Call(Alice(f), Read(101)));
        try
        {
            Await(checkedSignal);
            f.Revoke(Alice(f));
            resume.Set();
            var result = Complete(work);
            Require(result.Decision == Decision.DENIED && result.Body.Length == 0, "stale allow survived revocation");
            Require(f.ContentReads == 0 && f.ToolEffects == 0, "revocation had forbidden effects");
            Require(budget.Remaining == 1, "unused reservation not released");
            Require(f.Audit.SequenceEqual(new[] { "broker:DENIED" }), "race denial not audited");
        }
        finally { resume.Set(); Drain(work); }
    }
    private static void BudgetRace(Fixture f)
    {
        var budget = new Budget(1);
        var broker = new Broker(f, budget);
        using var start = new Barrier(2);
        using var snapshots = new Barrier(2);
        budget.UnsafeAfterSnapshot = () => Rendezvous(snapshots);
        Outcome Call() { Rendezvous(start); return broker.Call(Alice(f), Read(101)); }
        var one = Worker(Call);
        var two = Worker(Call);
        try
        {
            var decisions = new[] { Complete(one).Decision, Complete(two).Decision };
            Require(decisions.Count(d => d == Decision.ALLOWED) == 1 && decisions.Count(d => d == Decision.STOPPED) == 1,
                "one quota slot allowed two calls");
            Require(f.ContentReads == 1 && f.ToolEffects == 1, "quota race produced extra effect");
        }
        finally { Drain(one, two); }
    }
    public static int Run(string track, string group)
    {
        int passed = 0, failed = 0;
        foreach (var test in Tests())
        {
            if (group != "test" && test.Group != group) continue;
            try
            {
                using var fixture = new Fixture();
                test.Check(fixture);
                passed++;
                Console.WriteLine("PASS " + test.Name);
            }
            catch (Exception failure)
            {
                failed++;
                string reason = failure is CheckFailure ? failure.Message : "unexpected infrastructure failure";
                Console.WriteLine("FAIL " + test.Name + " | " + reason);
            }
        }
        Console.WriteLine($"OBSERVED track={track} group={group} total={passed + failed} passed={passed} failed={failed}");
        if (track == "exercise" && failed > 0)
            Console.WriteLine("Exercise failures are expected security findings; they remain FAIL and return nonzero.");
        return failed == 0 ? 0 : 1;
    }
}