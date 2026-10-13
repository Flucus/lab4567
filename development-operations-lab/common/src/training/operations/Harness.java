package training.operations;

import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.CyclicBarrier;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import static training.operations.Contract.*;

/** Independent literal security oracles shared unchanged across tracks. No JUnit required. */
public final class Harness {
    private Harness() { }
    @FunctionalInterface private interface Check { void run(Fixture fixture) throws Exception; }
    private record Test(String group, String name, Check check) { }
    private static final String ALICE = "Bearer ALICE-DEMO";
    private static final String BOB = "Bearer BOB-DEMO";
    private static Identity alice(Fixture f) { return f.authenticate(ALICE); }
    private static Identity bob(Fixture f) { return f.authenticate(BOB); }
    private static Proposal read(int id) { return new Proposal("readDocument", Map.of("documentId", id)); }
    private static void require(boolean condition, String safeMessage) {
        if (!condition) throw new AssertionError(safeMessage);
    }
    private static void exact(Response response, int... expected) {
        require(response.status() == 200, "expected status 200");
        List<Integer> actual = response.rows().stream().map(row -> (Integer) row.get("id")).toList();
        require(actual.equals(java.util.Arrays.stream(expected).boxed().toList()), "exact document IDs differ");
    }
    private static void denied(Fixture f, Content result, int readsBefore) {
        require(result.status() == 403 && result.body().isEmpty(), "expected empty 403");
        require(f.contentReads.get() == readsBefore, "denial caused a content read");
    }
    private static void outcome(Fixture f, Proposal proposal, Decision expected, int reads, int effects) {
        Outcome result = new Broker(f, new Budget(3)).call(alice(f), proposal);
        require(result.decision() == expected, "broker decision differs");
        require(f.contentReads.get() == reads && f.toolEffects.get() == effects, "forbidden read/effect count");
        if (expected != Decision.ALLOWED) require(result.body().isEmpty(), "denial disclosed content");
        require(f.audit.equals(List.of("broker:" + expected.name())), "missing/minimal audit differs");
    }
    private static void await(CountDownLatch latch) {
        try { require(latch.await(5, TimeUnit.SECONDS), "barrier timed out"); }
        catch (InterruptedException failure) { Thread.currentThread().interrupt(); throw new AssertionError("interrupted"); }
    }
    private static void barrier(CyclicBarrier barrier) {
        try { barrier.await(5, TimeUnit.SECONDS); }
        catch (Exception failure) { throw new AssertionError("budget barrier failed"); }
    }
    private static Test lab1(String name, Check check) { return new Test("lab1", name, check); }
    private static Test lab2(String name, Check check) { return new Test("lab2", name, check); }

    private static List<Test> tests() {
        return List.of(
            // Eight original mandatory cases. Expectations are literals, not production filters.
            lab1("01 Alice Contract -> 101", f -> exact(new Search(f).search(ALICE, "Contract"), 101)),
            lab1("02 Bob Contract -> 102", f -> exact(new Search(f).search(BOB, "Contract"), 102)),
            lab1("03 apostrophe exact match -> 103", f -> exact(new Search(f).search(ALICE, "O'Reilly Guide"), 103)),
            lab1("04 Anonymous -> 401 without DB", f -> {
                require(new Search(f).search(null, "Contract").status() == 401, "expected 401");
                require(f.searchQueries.get() == 0, "anonymous queried DB");
            }),
            lab1("05 NoScope -> 403 before DB", f -> {
                require(new Search(f).search("Bearer NOSCOPE-DEMO", "Contract").status() == 403, "expected 403");
                require(f.searchQueries.get() == 0, "NoScope queried DB");
            }),
            lab1("06 unmatched SQL-control literal -> zero rows", f ->
                exact(new Search(f).search(ALICE, "' OR '1'='1"))),
            lab1("07 >100 characters -> 400 before DB", f -> {
                require(new Search(f).search(ALICE, "x".repeat(101)).status() == 400, "expected 400");
                require(f.searchQueries.get() == 0, "oversize name queried DB");
            }),
            lab1("08 synthetic token absent from all captured sinks", f -> {
                new Search(f).search(ALICE, "Contract");
                new Broker(f, new Budget(1)).call(alice(f), read(101));
                String captured = String.join("\n", f.logs) + String.join("\n", f.audit);
                require(!captured.contains("ALICE-DEMO") && !captured.contains("Bearer"), "token leaked to captured sink");
                require(!captured.contains("retention") && !captured.contains("documentId"), "content/raw args logged");
            }),
            lab1("09 blank/null name -> 400", f -> {
                for (String value : new String[] {null, "", "  "})
                    require(new Search(f).search(ALICE, value).status() == 400, "expected blank/null 400");
                require(f.searchQueries.get() == 0, "blank/null name queried DB");
            }),
            lab1("10 response is only id/name DTO", f -> {
                Response result = new Search(f).search(ALICE, "Contract");
                require(result.status() == 200 && !result.rows().isEmpty(), "missing positive DTO result");
                for (Map<String, Object> row : result.rows())
                    require(row.keySet().equals(Set.of("id", "name")), "full entity fields exposed");
            }),
            lab1("11 exact matching, case and 100 boundary", f -> {
                exact(new Search(f).search(ALICE, "contract"));
                exact(new Search(f).search(ALICE, "Contract "));
                exact(new Search(f).search(ALICE, "x".repeat(100)));
            }),
            lab1("12 unrecognised token -> 401", f -> {
                require(new Search(f).search("Bearer NOT-A-FIXTURE", "Contract").status() == 401, "unknown token accepted");
                require(f.searchQueries.get() == 0, "unknown token queried DB");
            }),
            // Six original broker cases, plus adversarial schema and effect assertions.
            lab2("13 broker authorised read", f -> outcome(f, read(101), Decision.ALLOWED, 1, 1)),
            lab2("14 broker cross-supplier denied before content", f -> outcome(f, read(102), Decision.DENIED, 0, 0)),
            lab2("15 broker approveRisk always denied", f -> outcome(f,
                new Proposal("approveRisk", Map.of("documentId", 101)), Decision.DENIED, 0, 0)),
            lab2("16 broker unknown tool rejected", f -> outcome(f,
                new Proposal("shell", Map.of("documentId", 101)), Decision.REJECTED, 0, 0)),
            lab2("17 broker malformed arguments rejected", f -> outcome(f,
                new Proposal("readDocument", Map.of("documentId", "101")), Decision.REJECTED, 0, 0)),
            lab2("18 broker budget stopped", f -> {
                Broker b = new Broker(f, new Budget(1));
                require(b.call(alice(f), read(101)).decision() == Decision.ALLOWED, "first call not allowed");
                Outcome result = b.call(alice(f), read(101));
                require(result.decision() == Decision.STOPPED && result.body().isEmpty(), "exhausted budget not stopped");
                require(f.contentReads.get() == 1 && f.toolEffects.get() == 1, "budget caused extra read/effect");
            }),
            lab2("19 model identity/extra field rejected", f -> outcome(f,
                new Proposal("readDocument", Map.of("documentId", 102, "supplierId", "SUP-B")), Decision.REJECTED, 0, 0)),
            lab2("20 malformed schema matrix", f -> {
                Broker b = new Broker(f, new Budget(20));
                for (Proposal proposal : new Proposal[] {null, new Proposal(null, Map.of()),
                        new Proposal("readDocument", null), new Proposal("readDocument", Map.of()),
                        new Proposal("readDocument", Map.of("documentId", 0)),
                        new Proposal("readDocument", Map.of("documentId", 1000000)),
                        new Proposal("readDocument", Map.of("documentId", 101L))})
                    require(b.call(alice(f), proposal).decision() == Decision.REJECTED, "schema variant accepted");
                require(f.contentReads.get() == 0 && f.toolEffects.get() == 0, "malformed schema had effects");
            }),
            lab2("21 unknown resource indistinguishable denial", f -> outcome(f, read(999), Decision.DENIED, 0, 0)),
            lab2("22 broker Anonymous and NoScope denied", f -> {
                Broker b = new Broker(f, new Budget(2));
                require(b.call(null, read(101)).decision() == Decision.DENIED, "anonymous broker accepted");
                require(b.call(f.authenticate("Bearer NOSCOPE-DEMO"), read(101)).decision() == Decision.DENIED, "NoScope broker accepted");
                require(f.contentReads.get() == 0 && f.toolEffects.get() == 0, "unauthenticated/scope denial had effects");
            }),
            surface("23 read wrong supplier", Surfaces::read),
            surface("24 preview wrong supplier", Surfaces::preview),
            surface("25 download wrong supplier", Surfaces::download),
            surface("26 storage wrong supplier", Surfaces::storage),
            lab2("27 export authorises ALL before ANY content", f -> {
                denied(f, new Surfaces(f).export(alice(f), List.of(101, 102)), 0);
            }),
            lab2("28 cache Bob prime cannot reach Alice by name", f -> {
                Surfaces s = new Surfaces(f);
                require(s.cached(bob(f), 102, "Contract").status() == 200, "Bob cache prime failed");
                int before = f.contentReads.get();
                Content own = s.cached(alice(f), 101, "Contract");
                require(own.status() == 200 && own.body().startsWith("SUP-A"), "name cache crossed tenant");
                denied(f, s.cached(alice(f), 102, "Contract"), f.contentReads.get());
                require(f.contentReads.get() >= before, "invalid instrumentation");
            }),
            lab2("29 cache hit rechecks revocation", f -> {
                Surfaces s = new Surfaces(f); s.cached(alice(f), 101, "Contract");
                int before = f.contentReads.get(); f.revoke(alice(f));
                denied(f, s.cached(alice(f), 101, "Contract"), before);
            }),
            lab2("30 job revocation between enqueue/execution", f -> {
                Surfaces s = new Surfaces(f); Job job = s.enqueue(alice(f), 101);
                require(job != null, "positive enqueue failed"); f.revoke(alice(f));
                denied(f, s.run(job), 0);
            }),
            lab2("31 job revocation before delivery", f -> {
                Surfaces s = new Surfaces(f); Job job = s.enqueue(alice(f), 101);
                Content complete = s.run(job); require(complete.status() == 200, "positive job failed");
                int before = f.contentReads.get(); f.revoke(alice(f));
                denied(f, s.deliver(job, complete), before);
            }),
            lab2("32 positive sweep and anonymous contract", f -> {
                Surfaces s = new Surfaces(f);
                for (Content result : List.of(s.read(alice(f), 101), s.preview(alice(f), 101),
                        s.download(alice(f), 101), s.storage(alice(f), 101),
                        s.export(alice(f), List.of(101, 103)), s.cached(alice(f), 101, "Contract")))
                    require(result.status() == 200 && result.body().startsWith("SUP-A"), "authorised surface unavailable");
                require(s.read(null, 101).status() == 401, "anonymous surface not 401");
            }),
            lab2("33 guarded read deterministic revocation race", Harness::surfaceRace),
            lab2("34 broker deterministic revocation + reservation release", Harness::brokerRace),
            lab2("35 concurrent one-slot budget permits only one effect", Harness::budgetRace),
            lab2("36 content query and mock effect under policy lock", f -> {
                AtomicBoolean allGuarded = new AtomicBoolean(true);
                f.beforeContentQuery = () -> {
                    if (!Thread.holdsLock(f.policyLock)) allGuarded.set(false);
                };
                new Surfaces(f).guardedRead(alice(f), 101);
                new Broker(f, new Budget(1)).call(alice(f), read(101));
                require(f.contentReads.get() == 2 && allGuarded.get(), "materialisation outside shared revocation lock");
            }),
            lab2("37 database failure safe, charged, audited", f -> {
                f.close(); Broker b = new Broker(f, new Budget(1));
                Outcome result = b.call(alice(f), read(101));
                require(result.decision() == Decision.FAILED && result.body().isEmpty(), "failure not safely mapped");
                require(f.contentReads.get() == 1 && f.toolEffects.get() == 0, "failed read produced effect");
                require(f.audit.equals(List.of("broker:FAILED")), "failed path not audited");
                require(b.call(alice(f), read(101)).decision() == Decision.STOPPED, "started failed read refunded incorrectly");
            })
        );
    }
    @FunctionalInterface private interface Access { Content call(Surfaces s, Identity who, int id); }
    private static Test surface(String name, Access access) {
        return lab2(name, f -> denied(f, access.call(new Surfaces(f), alice(f), 102), 0));
    }
    private static void surfaceRace(Fixture f) throws Exception {
        Surfaces s = new Surfaces(f);
        CountDownLatch checked = new CountDownLatch(1), resume = new CountDownLatch(1);
        s.afterInitialCheck = () -> { checked.countDown(); await(resume); };
        ExecutorService pool = Executors.newSingleThreadExecutor();
        try {
            Future<Content> work = pool.submit(() -> s.guardedRead(alice(f), 101));
            await(checked); f.revoke(alice(f)); resume.countDown();
            denied(f, work.get(5, TimeUnit.SECONDS), 0);
        } finally { resume.countDown(); pool.shutdownNow(); }
    }
    private static void brokerRace(Fixture f) throws Exception {
        Budget budget = new Budget(1); Broker b = new Broker(f, budget);
        CountDownLatch checked = new CountDownLatch(1), resume = new CountDownLatch(1);
        b.afterInitialCheck = () -> { checked.countDown(); await(resume); };
        ExecutorService pool = Executors.newSingleThreadExecutor();
        try {
            Future<Outcome> work = pool.submit(() -> b.call(alice(f), read(101)));
            await(checked); f.revoke(alice(f)); resume.countDown();
            Outcome result = work.get(5, TimeUnit.SECONDS);
            require(result.decision() == Decision.DENIED && result.body().isEmpty(), "stale allow survived revocation");
            require(f.contentReads.get() == 0 && f.toolEffects.get() == 0, "revocation had forbidden effects");
            require(budget.remaining() == 1, "unused reservation not released");
            require(f.audit.equals(List.of("broker:DENIED")), "race denial not audited");
        } finally { resume.countDown(); pool.shutdownNow(); }
    }
    private static void budgetRace(Fixture f) throws Exception {
        Budget budget = new Budget(1); Broker b = new Broker(f, budget);
        CyclicBarrier start = new CyclicBarrier(2), snapshots = new CyclicBarrier(2);
        budget.unsafeAfterSnapshot = () -> barrier(snapshots); // Deterministically expose exercise lost update.
        ExecutorService pool = Executors.newFixedThreadPool(2);
        try {
            java.util.concurrent.Callable<Outcome> call = () -> { barrier(start); return b.call(alice(f), read(101)); };
            Future<Outcome> one = pool.submit(call), two = pool.submit(call);
            List<Decision> decisions = List.of(one.get(8, TimeUnit.SECONDS).decision(), two.get(8, TimeUnit.SECONDS).decision());
            require(decisions.stream().filter(d -> d == Decision.ALLOWED).count() == 1 &&
                decisions.stream().filter(d -> d == Decision.STOPPED).count() == 1, "one quota slot allowed two calls");
            require(f.contentReads.get() == 1 && f.toolEffects.get() == 1, "quota race produced extra effect");
        } finally { pool.shutdownNow(); }
    }
    public static int run(String track, String group) {
        int passed = 0, failed = 0;
        for (Test test : tests()) {
            if (!group.equals("test") && !test.group().equals(group)) continue;
            try (Fixture f = new Fixture()) {
                test.check().run(f); passed++;
                System.out.println("PASS " + test.name());
            } catch (AssertionError | Exception failure) {
                failed++;
                // Do not print raw exceptions/arguments/tokens/SQL on failed tests.
                String reason = failure instanceof AssertionError ? failure.getMessage() : "unexpected infrastructure failure";
                System.out.println("FAIL " + test.name() + " | " + reason);
            }
        }
        System.out.printf("OBSERVED track=%s group=%s total=%d passed=%d failed=%d%n", track, group, passed + failed, passed, failed);
        if (track.equals("exercise") && failed > 0)
            System.out.println("Exercise failures are expected security findings; they remain FAIL and return nonzero.");
        return failed == 0 ? 0 : 1;
    }
}