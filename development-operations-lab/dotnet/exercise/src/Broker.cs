namespace Training.Operations;

// Strict structured mock-output boundary; never executes any external tool.
public sealed class Broker(Fixture fixture, Budget budget)
{
    public Action AfterInitialCheck = () => { };
    public Outcome Call(Identity? requester, Proposal? proposal)
    {
        var decision = Decision.REJECTED;
        bool reserved = false, consumed = false;
        try
        {
            if (proposal?.Tool is null || proposal.Arguments is null) return new(decision, "");
            if (proposal.Tool != "readDocument" && proposal.Tool != "approveRisk") return new(decision, "");
            if (proposal.Arguments.Count != 1 || !proposal.Arguments.Keys.Single().Equals("documentId", StringComparison.Ordinal))
                return new(decision, "");
            if (proposal.Arguments["documentId"] is not int id || id < 1 || id > 999999) return new(decision, "");
            decision = Decision.DENIED;
            if (requester is null || proposal.Tool == "approveRisk" || !fixture.Allowed(requester, id))
                return new(decision, "");
            if (!budget.Reserve()) { decision = Decision.STOPPED; return new(decision, ""); }
            reserved = true;
            AfterInitialCheck();
            lock (fixture.PolicyLock)
            {
                if (!fixture.Allowed(requester, id)) return new(decision, "");
                consumed = true;
                string body = fixture.Materialize(id);
                Interlocked.Increment(ref fixture.ToolEffects);
                decision = Decision.ALLOWED;
                return new(decision, body);
            }
        }
        catch (Exception) { decision = Decision.FAILED; return new(decision, ""); }
        finally
        {
            if (reserved && !consumed) budget.Release();
            fixture.RecordAudit(decision);
        }
    }
}