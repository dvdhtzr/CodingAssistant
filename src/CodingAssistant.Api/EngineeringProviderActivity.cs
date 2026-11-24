namespace CodingAssistant.Api;

/// <summary>Trusted invocation context for the real local provider. AsyncLocal
/// keeps concurrent project calls separate and restores any enclosing scope.
/// Only fixed request lifecycle metadata is recorded. No prompt, response,
/// hidden reasoning, endpoint override or execution permission is accepted.</summary>
public static class EngineeringProviderActivity
{
    private sealed record Scope(TaskStore Tasks,string TaskId,string AgentId,
        string ReferenceId,string? SourceSha256);
    private static readonly AsyncLocal<Scope?> Current = new();

    private sealed class Restore(Scope? prior) : IDisposable
    {
        public void Dispose() => Current.Value = prior;
    }

    public static IDisposable Enter(TaskStore tasks,string taskId,string agentId,
        string referenceId,string? sourceSha256 = null)
    {
        var previous = Current.Value;
        Current.Value = new Scope(tasks,taskId,agentId,referenceId,sourceSha256);
        return new Restore(previous);
    }

    private static string Role(MafAgentPurpose purpose) => purpose switch
    {
        MafAgentPurpose.Interview or MafAgentPurpose.Specification => "ProductOwner",
        MafAgentPurpose.Architecture => "SolutionArchitect",
        MafAgentPurpose.TechnicalLead => "TechnicalLead",
        MafAgentPurpose.SoftwareEngineer => "SoftwareEngineer",
        MafAgentPurpose.QaEngineer => "QaEngineer",
        MafAgentPurpose.LeadSoftwareEngineer => "LeadSoftwareEngineer",
        _ => ""
    };

    public static void RecordQueued(string model,MafAgentPurpose purpose)
    {
        var scope=Current.Value;
        if(scope is null || Role(purpose)!=scope.AgentId || !LocalInterviewModel.AllowedModel(model))return;
        try
        {
            using var db=scope.Tasks.Open();using var tx=db.BeginTransaction();
            EngineeringActivityStore.Record(db,tx,scope.TaskId,"OllamaRequestQueued",scope.AgentId,
                "OllamaQueue",scope.ReferenceId+":"+Guid.NewGuid().ToString("N"),sourceSha256:scope.SourceSha256,model:model);
            tx.Commit();scope.Tasks.SignalEngineeringActivity(scope.TaskId);
        }
        catch(Exception){/* No fabricated provider activity on telemetry failure. */}
    }

    public static ProviderRequest? StartProvider(string model,MafAgentPurpose purpose,
        CancellationToken cancellation)
    {
        var scope = Current.Value;
        if (scope is null || Role(purpose) != scope.AgentId ||
            !LocalInterviewModel.AllowedModel(model)) return null;
        return new ProviderRequest(scope.Tasks,scope.TaskId,scope.AgentId,
            scope.ReferenceId + ":" + Guid.NewGuid().ToString("N"),
            scope.SourceSha256,model,cancellation);
    }

    public sealed class ProviderRequest : IDisposable
    {
        private readonly TaskStore _tasks;
        private readonly string _taskId,_agentId,_referenceId,_model;
        private readonly string? _sourceSha256;
        private readonly CancellationToken _cancellation;
        private int _finished;

        internal ProviderRequest(TaskStore tasks,string taskId,string agentId,
            string referenceId,string? sourceSha256,string model,CancellationToken cancellation)
        {
            _tasks=tasks; _taskId=taskId; _agentId=agentId; _referenceId=referenceId;
            _sourceSha256=sourceSha256; _model=model; _cancellation=cancellation;
            Emit("OllamaRequestStarted");
        }

        private void Emit(string kind)
        {
            try
            {
                using var db = _tasks.Open();
                using var tx = db.BeginTransaction();
                EngineeringActivityStore.Record(db,tx,_taskId,kind,_agentId,
                    "OllamaRequest",_referenceId,sourceSha256:_sourceSha256,model:_model);
                tx.Commit();
                _tasks.SignalEngineeringActivity(_taskId);
            }
            catch (Exception)
            {
                // Observability failure never alters transport validation or
                // fabricates a response. Missing metadata stays missing.
            }
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _finished,1)==0)
                Emit("OllamaResponseAccepted");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finished,1)==0)
                Emit(_cancellation.IsCancellationRequested
                    ? "OllamaRequestCancelled" : "OllamaRequestStopped");
        }
    }
}
