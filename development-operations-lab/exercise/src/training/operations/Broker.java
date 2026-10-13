package training.operations;

import java.sql.SQLException;
import java.util.Set;
import static training.operations.Contract.*;

/** Strict structured mock-output boundary; never executes an external tool. */
public final class Broker {
    private final Fixture fixture;
    private final Budget budget;
    public volatile Runnable afterInitialCheck = () -> { };
    public Broker(Fixture fixture, Budget budget) { this.fixture = fixture; this.budget = budget; }
    public Outcome call(Identity requester, Proposal proposal) {
        Decision decision = Decision.REJECTED;
        boolean reserved = false;
        boolean consumed = false;
        try {
            if (proposal == null || proposal.tool() == null || proposal.arguments() == null)
                return new Outcome(decision, "");
            if (!Set.of("readDocument", "approveRisk").contains(proposal.tool()))
                return new Outcome(decision, "");
            if (!proposal.arguments().keySet().equals(Set.of("documentId")))
                return new Outcome(decision, "");
            Object rawId = proposal.arguments().get("documentId");
            if (!(rawId instanceof Integer id) || id < 1 || id > 999999)
                return new Outcome(decision, "");
            decision = Decision.DENIED;
            if (requester == null || proposal.tool().equals("approveRisk") || !fixture.allowed(requester, id))
                return new Outcome(decision, "");
            if (!budget.reserve()) { decision = Decision.STOPPED; return new Outcome(decision, ""); }
            reserved = true;
            afterInitialCheck.run();
            synchronized (fixture.policyLock) {
                if (!fixture.allowed(requester, id)) return new Outcome(decision, "");
                consumed = true;
                String body = fixture.materialize(id);
                fixture.toolEffects.incrementAndGet();
                decision = Decision.ALLOWED;
                return new Outcome(decision, body);
            }
        } catch (SQLException | RuntimeException failure) {
            decision = Decision.FAILED;
            return new Outcome(decision, "");
        } finally {
            if (reserved && !consumed) budget.release();
            fixture.audit(decision);
        }
    }
}