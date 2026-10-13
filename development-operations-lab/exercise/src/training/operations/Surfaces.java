package training.operations;

import java.sql.SQLException;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import static training.operations.Contract.*;

/** Authorised local adapters, not actual storage services, URLs, or durable workers. */
public final class Surfaces {
    private final Fixture fixture;
    private final Map<String, String> cache = new HashMap<>();
    public volatile Runnable afterInitialCheck = () -> { };
    public Surfaces(Fixture fixture) { this.fixture = fixture; }
    public Content read(Identity who, int id) {
        synchronized (fixture.policyLock) {
            if (who == null) return Content.denied(401);
            if (!fixture.allowed(who, id)) return Content.denied(403);
            return retrieve(id);
        }
    }
    private Content retrieve(int id) {
        try { return new Content(200, fixture.materialize(id)); }
        catch (SQLException failure) { return Content.denied(500); }
    }
    public Content preview(Identity who, int id) { return read(who, id); }
    public Content download(Identity who, int id) { return read(who, id); }
    public Content storage(Identity who, int id) { return read(who, id); }
    public Content export(Identity who, List<Integer> ids) {
        synchronized (fixture.policyLock) {
            if (who == null) return Content.denied(401);
            if (ids == null || ids.isEmpty() || ids.size() > 10 ||
                    ids.stream().anyMatch(java.util.Objects::isNull))
                return Content.denied(400);
            for (int id : ids) if (!fixture.allowed(who, id)) return Content.denied(403);
            StringBuilder out = new StringBuilder();
            for (int id : ids) {
                Content result = retrieve(id);
                if (result.status() != 200) return Content.denied(result.status());
                out.append(result.body());
            }
            return new Content(200, out.toString());
        }
    }
    public Content cached(Identity who, int id, String name) {
        synchronized (fixture.policyLock) {
            if (who == null) return Content.denied(401);
            if (!fixture.allowed(who, id)) return Content.denied(403);
            String key = who.supplier() + ":" + id;
            if (cache.containsKey(key)) return new Content(200, cache.get(key));
            Content result = retrieve(id);
            if (result.status() == 200) cache.put(key, result.body());
            return result;
        }
    }
    public Job enqueue(Identity who, int id) {
        synchronized (fixture.policyLock) { return fixture.allowed(who, id) ? new Job(who, id) : null; }
    }
    public Content run(Job job) {
        if (job == null) return Content.denied(403);
        return read(job.requester(), job.documentId());
    }
    public Content deliver(Job job, Content completed) {
        synchronized (fixture.policyLock) {
            if (job == null || !fixture.allowed(job.requester(), job.documentId()))
                return Content.denied(403);
            return completed;
        }
    }
    public Content guardedRead(Identity who, int id) {
        if (!fixture.allowed(who, id)) return Content.denied(403);
        afterInitialCheck.run();
        synchronized (fixture.policyLock) {
            if (!fixture.allowed(who, id)) return Content.denied(403);
            return retrieve(id);
        }
    }
}