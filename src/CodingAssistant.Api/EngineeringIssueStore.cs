using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record EngineeringIssueReference(string ReviewId,string Path,int FindingIndex,
    string FileSha256,string Origin);
public sealed record EngineeringIssueChallenge(string MessageId,string IssueId,
    string ArchitectureChoiceSha256,string Summary,string ToAgent,string CreatedUtc);
public sealed record EngineeringIssueTriage(string ReviewId,string Classification,
    string Rationale,string CreatedUtc);
public sealed record IssueDecisionContext(string TaskId,string RunId,string SourceSha256,
    string FromAgent,int PolicyRevision,string PolicySha256);
public sealed record IssueDecisionDraft(string IssueId,int ExpectedRevision,string ReviewId,
    string Classification,string Rationale,string? ChallengeMessageId=null);
public sealed record IssueDecision(int Revision,string IssueId,string ReviewId,string Classification,
    string Rationale,string? ChallengeMessageId,int PolicyRevision,string PolicySha256,string CreatedUtc);
public sealed record EngineeringIssue(string Id,string TaskId,string EngineeringRunId,
    string SourceSha256,string PlanSha256,string Origin,string Severity,string Category,
    string Path,int? Line,string Requirement,string Evidence,string Recommendation,
    string? Verification, IReadOnlyList<EngineeringIssueReference> References,
    bool CurrentSource,bool LatestReview,string State,string? Classification,
    string? AssignedOwner,string? RepairCycleId,int? RepairAttempt,string? RepairState,
    IReadOnlyList<EngineeringIssueTriage> TriageHistory,
    IReadOnlyList<EngineeringIssueChallenge> Challenges,
    IReadOnlyList<IssueDecision> Decisions,
    IReadOnlyList<IssueVerification> Verifications);
public sealed record EngineeringIssuePage(string TaskId,string? EngineeringRunId,
    bool CurrentSource,IReadOnlyList<EngineeringIssue> Issues);

/// <summary>
/// Read projection of durable independent review evidence and the existing owner
/// triage/repair ledger. No copied mutable finding database and no implicit
/// closure: a newer review or successful static repair review alone cannot
/// establish resolution. Identical findings on the same source revision are
/// grouped without discarding their underlying review references.
/// </summary>
public sealed class EngineeringIssueStore(TaskStore tasks)
{
    private sealed record Occurrence(string ReviewId,string Origin,string Path,
        string FileSha256,int Index,string Severity,string Category,int? Line,
        string Requirement,string Evidence,string Recommendation,string? Verification,
        bool Latest);

    private static string Key(string taskId,string runId,string sourceSha,Occurrence finding)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            schema=1,taskId,runId,sourceSha,finding.Origin,finding.Path,
            finding.FileSha256,finding.Severity,finding.Category,finding.Line,
            finding.Requirement,finding.Evidence,finding.Recommendation,finding.Verification
        }))).ToLowerInvariant();

    internal static void Initialize(SqliteConnection db) =>
        TaskStore.Execute(db,null,"""
            CREATE TABLE IF NOT EXISTS engineering_issue_challenges (
                task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                message_id TEXT NOT NULL,
                issue_id TEXT NOT NULL,
                architecture_choice_sha256 TEXT NOT NULL,
                PRIMARY KEY(task_id,message_id),
                FOREIGN KEY(task_id,message_id) REFERENCES
                    engineering_agent_communications(task_id,message_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS engineering_issue_challenge_issue
                ON engineering_issue_challenges(task_id,issue_id);
            CREATE TABLE IF NOT EXISTS engineering_issue_decisions (
                task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                issue_id TEXT NOT NULL,revision INTEGER NOT NULL CHECK(revision>0),
                run_id TEXT NOT NULL,source_sha256 TEXT NOT NULL,plan_sha256 TEXT NOT NULL,
                review_id TEXT NOT NULL,classification TEXT NOT NULL CHECK(classification IN
                    ('ImplementationDefect','NeedsVerification','SpecificationAmbiguity',
                    'PlanChangeRequired','NonBlockingImprovement','Deferred')),
                rationale TEXT NOT NULL,challenge_message_id TEXT,
                policy_revision INTEGER NOT NULL,policy_sha256 TEXT NOT NULL,created_utc TEXT NOT NULL,
                PRIMARY KEY(task_id,issue_id,revision)
            );
            CREATE INDEX IF NOT EXISTS engineering_issue_decision_run
                ON engineering_issue_decisions(task_id,run_id);
            """);

    public EngineeringIssuePage Read(string taskId,string? runId=null)
    {
        using var db=tasks.Open();
        using var tx=db.BeginTransaction(deferred:true);
        var result=Read(db,tx,taskId,runId);
        tx.Commit();
        return result;
    }

    internal static EngineeringIssuePage Read(SqliteConnection db,SqliteTransaction tx,
        string taskId,string? runId)
    {
        if(TaskStore.FindTask(db,tx,taskId) is null) throw new StoreFailure("NOT_FOUND");
        var latest=TaskStore.Scalar(db,tx,"""
            SELECT id FROM engineering_runs WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """,("@task",taskId));
        runId ??= latest;
        if(runId is null) return new(taskId,null,false,[]);
        var run=EngineeringRunStore.Find(db,tx,runId);
        if(run is null || run.TaskId!=taskId) throw new StoreFailure("NOT_FOUND");
        var sourceSha=EngineeringMessageStore.SourceFingerprint(db,tx,run.Id);
        var current=run.Id==latest && run.State is ("Generated" or "Failed" or "Interrupted");
        if(current)
            try {EngineeringRunStore.RequireBinding(db,tx,run);}
            catch(StoreFailure) {current=false;}

        // Include all completed reports for this immutable snapshot, including
        // pre-upgrade projects. Never import an incomplete or differently bound review.
        var occurrences=new List<Occurrence>();
        var latestQa=TaskStore.Scalar(db,tx,"""
            SELECT id FROM qa_reviews WHERE task_id=@task AND engineering_run_id=@run
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """,("@task",taskId),("@run",run.Id));
        var latestLead=TaskStore.Scalar(db,tx,"""
            SELECT id FROM lead_reviews WHERE task_id=@task AND engineering_run_id=@run
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """,("@task",taskId),("@run",run.Id));
        using(var cmd=TaskStore.Command(db,tx,"""
            SELECT r.id,f.path,f.source_sha256,f.assessment_json
            FROM qa_reviews r JOIN qa_review_files f ON f.qa_id=r.id
            JOIN engineering_run_files src ON src.run_id=r.engineering_run_id
              AND src.path=f.path AND src.sha256=f.source_sha256
            WHERE r.task_id=@task AND r.engineering_run_id=@run AND r.state='Reviewed'
              AND r.source_sha256=@source AND r.plan_sha256=@plan
            ORDER BY r.created_utc,r.id,f.path
            """,("@task",taskId),("@run",run.Id),("@source",sourceSha),("@plan",run.PlanSha256)))
        using(var reader=cmd.ExecuteReader())
            while(reader.Read())
            {
                var id=reader.GetString(0);var path=reader.GetString(1);var fileSha=reader.GetString(2);
                var report=JsonSerializer.Deserialize<QaFileAssessment>(reader.GetString(3))
                    ?? throw new StoreFailure("INVALID_STATE");
                for(var i=0;i<report.Findings.Count;i++)
                {
                    var finding=report.Findings[i];
                    occurrences.Add(new(id,"QaEngineer",path,fileSha,i,finding.Severity,
                        "Acceptance",null,finding.Requirement,finding.Evidence,finding.Recommendation,
                        null,id==latestQa));
                }
            }
        using(var cmd=TaskStore.Command(db,tx,"""
            SELECT r.id,f.path,f.source_sha256,f.assessment_json
            FROM lead_reviews r JOIN lead_review_files f ON f.review_id=r.id
            JOIN engineering_run_files src ON src.run_id=r.engineering_run_id
              AND src.path=f.path AND src.sha256=f.source_sha256
            WHERE r.task_id=@task AND r.engineering_run_id=@run AND r.state='Reviewed'
              AND r.source_sha256=@source AND r.plan_sha256=@plan
            ORDER BY r.created_utc,r.id,f.path
            """,("@task",taskId),("@run",run.Id),("@source",sourceSha),("@plan",run.PlanSha256)))
        using(var reader=cmd.ExecuteReader())
            while(reader.Read())
            {
                var id=reader.GetString(0);var path=reader.GetString(1);var fileSha=reader.GetString(2);
                var report=JsonSerializer.Deserialize<LeadFileAssessment>(reader.GetString(3))
                    ?? throw new StoreFailure("INVALID_STATE");
                for(var i=0;i<report.Findings.Count;i++)
                {
                    var finding=report.Findings[i];
                    occurrences.Add(new(id,"LeadSoftwareEngineer",path,fileSha,i,finding.Severity,
                        finding.Category,finding.Line,finding.Impact,finding.Evidence,finding.Resolution,
                        finding.Verification,id==latestLead));
                }
            }

        // Preserve the existing exact-scope repair mechanism. A cycle only
        // relates to a finding if the selected original path/index matches.
        var triage=new Dictionary<(string Review,string Path,int Index),EngineeringIssueTriage>();
        using(var cmd=TaskStore.Command(db,tx,"""
            SELECT COALESCE(p.qa_review_id,p.lead_review_id),d.path,d.finding_index,d.classification,d.rationale,d.created_utc
            FROM repair_proposals p JOIN repair_triage_decisions d ON d.proposal_id=p.id
            WHERE p.task_id=@task AND p.engineering_run_id=@run AND p.source_sha256=@source
            """,("@task",taskId),("@run",run.Id),("@source",sourceSha)))
        using(var reader=cmd.ExecuteReader())
            while(reader.Read()) triage[(reader.GetString(0),reader.GetString(1).ToUpperInvariant(),
                reader.GetInt32(2))]=new(reader.GetString(0),reader.GetString(3),reader.GetString(4),reader.GetString(5));
        var cycles=new Dictionary<(string Review,string Path,int Index),(string Id,int Attempt,string State)>();
        using(var cmd=TaskStore.Command(db,tx,"""
            SELECT COALESCE(p.qa_review_id,p.lead_review_id),c.id,c.attempt,c.state,c.findings_json
            FROM repair_cycles c JOIN repair_proposals p ON p.id=c.proposal_id
            WHERE c.task_id=@task AND c.base_run_id=@run AND c.base_source_sha256=@source
            ORDER BY c.attempt
            """,("@task",taskId),("@run",run.Id),("@source",sourceSha)))
        using(var reader=cmd.ExecuteReader())
            while(reader.Read())
                foreach(var f in JsonSerializer.Deserialize<RepairFindingReference[]>(reader.GetString(4)) ?? [])
                    cycles[(reader.GetString(0),f.Path.ToUpperInvariant(),f.Index)]=
                        (reader.GetString(1),reader.GetInt32(2),reader.GetString(3));

        var challenges=new Dictionary<string,List<EngineeringIssueChallenge>>(StringComparer.Ordinal);
        using(var cmd=TaskStore.Command(db,tx,"""
            SELECT c.message_id,c.issue_id,c.architecture_choice_sha256,m.summary,m.to_agent,m.created_utc
            FROM engineering_issue_challenges c
            JOIN engineering_agent_communications m ON m.task_id=c.task_id AND m.message_id=c.message_id
            WHERE c.task_id=@task AND m.run_id=@run AND m.source_sha256=@source AND m.plan_sha256=@plan
            ORDER BY m.sequence
            """,("@task",taskId),("@run",run.Id),("@source",sourceSha),("@plan",run.PlanSha256)))
        using(var reader=cmd.ExecuteReader())
            while(reader.Read())
            {
                var item=new EngineeringIssueChallenge(reader.GetString(0),reader.GetString(1),
                    reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5));
                if(!challenges.TryGetValue(item.IssueId,out var list))
                    challenges[item.IssueId]=list=[];
                list.Add(item);
            }

        var arbitration=new Dictionary<string,List<IssueDecision>>(StringComparer.Ordinal);
        using(var cmd=TaskStore.Command(db,tx,"""
            SELECT revision,issue_id,review_id,classification,rationale,challenge_message_id,
                policy_revision,policy_sha256,created_utc
            FROM engineering_issue_decisions WHERE task_id=@task AND run_id=@run
                AND source_sha256=@source AND plan_sha256=@plan ORDER BY revision
            """,("@task",taskId),("@run",run.Id),("@source",sourceSha),("@plan",run.PlanSha256)))
        using(var reader=cmd.ExecuteReader())
            while(reader.Read())
            {
                var decision=new IssueDecision(reader.GetInt32(0),reader.GetString(1),
                    reader.GetString(2),reader.GetString(3),reader.GetString(4),
                    reader.IsDBNull(5)?null:reader.GetString(5),reader.GetInt32(6),
                    reader.GetString(7),reader.GetString(8));
                if(!arbitration.TryGetValue(decision.IssueId,out var list))
                    arbitration[decision.IssueId]=list=[];
                list.Add(decision);
            }

        var verifications=IssueVerificationStore.History(db,tx,taskId,run.Id,latest)
            .GroupBy(x=>x.IssueId).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
        var issues=occurrences.GroupBy(f=>Key(taskId,run.Id,sourceSha,f),StringComparer.Ordinal)
            .Select(group=>{
                var f=group.Last();
                var refs=group.Select(o=>new EngineeringIssueReference(o.ReviewId,o.Path,o.Index,o.FileSha256,o.Origin)).ToArray();
                var history=group.Select(o=>
                    triage.GetValueOrDefault((o.ReviewId,o.Path.ToUpperInvariant(),o.Index)))
                    .OfType<EngineeringIssueTriage>().Distinct().OrderBy(v=>v.CreatedUtc).ToArray();
                var decisions=history.Select(v=>v.Classification).Distinct().ToArray();
                var classification=decisions.Length==1?decisions[0]:null;
                var relatedChallenges=challenges.GetValueOrDefault(group.Key)?.ToArray() ?? [];
                var decisionHistory=arbitration.GetValueOrDefault(group.Key)?.ToArray() ?? [];
                var lastDecision=decisionHistory.LastOrDefault();
                // A new review or new challenge requires fresh arbitration.
                var effectiveDecision=lastDecision is not null &&
                    group.Any(o=>o.Latest && o.ReviewId==lastDecision.ReviewId) &&
                    lastDecision.ChallengeMessageId==relatedChallenges.LastOrDefault()?.MessageId
                    ?lastDecision:null;
                if(effectiveDecision is not null) classification=effectiveDecision.Classification;
                var repair=group.Select(o=>
                    cycles.GetValueOrDefault((o.ReviewId,o.Path.ToUpperInvariant(),o.Index)))
                    .Where(c=>c.Id is not null).OrderByDescending(c=>c.Attempt).FirstOrDefault();
                var state=repair.Id is not null ? repair.State switch {
                    "Running"=>"RepairInProgress","Generated" or "QaRunning" or "QaReviewed"=>"AwaitingIndependentVerification",
                    _=>"RepairStopped"
                } : effectiveDecision is not null ?
                    classification=="ImplementationDefect"?"ConfirmedDefect":
                    classification=="Deferred"?"Deferred":"Classified"
                    : decisions.Length>1?"TriageConflict":
                    classification=="ImplementationDefect"?"ConfirmedDefect":
                    classification is not null?"Classified":relatedChallenges.Length>0?"Challenged":"NeedsTriage";
                var binding=IssueVerificationStore.Binding(group.Key,refs,history,decisionHistory,
                    relatedChallenges,repair.Id,group.Any(o=>o.Latest));
                var verificationHistory=(verifications.GetValueOrDefault(group.Key)??[])
                    .Select(x=>x with {Current=x.Current && x.BindingSha256==binding}).ToArray();
                var verification=verificationHistory.LastOrDefault();
                if(verification is {Current:true} &&
                    verification.ChallengeMessageId==relatedChallenges.LastOrDefault()?.MessageId)
                {
                    // Only static architectural/maintainability findings can
                    // be dispositioned on static evidence. Functional/security
                    // concerns still need qualified independent verification.
                    var staticOnly=f.Origin=="LeadSoftwareEngineer" &&
                        f.Category is ("Architecture" or "Maintainability");
                    state=verification.Assessment.Outcome switch {
                        "StillPresent"=>"StillPresent",
                        "ResolvedByStaticEvidence"=>staticOnly?"StaticResolutionRecorded":"AwaitingQualifiedVerification",
                        "ChallengeSupported"=>staticOnly?"WithdrawnByReviewer":"AwaitingQualifiedVerification",
                        _=>state
                    };
                }
                return new EngineeringIssue(group.Key,taskId,run.Id,sourceSha,run.PlanSha256,
                    f.Origin,f.Severity,f.Category,f.Path,f.Line,f.Requirement,f.Evidence,
                    f.Recommendation,f.Verification,refs,current,group.Any(o=>o.Latest),state,
                    classification,repair.Id is not null || effectiveDecision?.Classification=="ImplementationDefect"
                        ?"SoftwareEngineer":null,
                    repair.Id,repair.Id is null?null:repair.Attempt,repair.State,history,relatedChallenges,decisionHistory,verificationHistory);
            }).OrderBy(x=>x.Severity switch {"Critical"=>0,"Major"=>1,"Minor"=>2,_=>3})
            .ThenBy(x=>x.Path,StringComparer.Ordinal).ThenBy(x=>x.Id,StringComparer.Ordinal).ToArray();
        return new(taskId,run.Id,current,issues);
    }

    // Application-only authority boundary. No route accepts an agent identity.
    // Arbitration is not repair admission, execution permission or closure.
    public IssueDecision Decide(IssueDecisionContext context,IssueDecisionDraft draft)
    {
        EngineeringRoleCapabilities.Require(context.FromAgent,EngineeringCapability.ScheduleWork);
        if(draft.ExpectedRevision<0 || draft.ExpectedRevision==int.MaxValue ||
            draft.Rationale is null || draft.Rationale.Trim().Length is <12 or >1000 ||
            draft.Rationale.Any(char.IsControl) || draft.Classification is not
            ("ImplementationDefect" or "NeedsVerification" or "SpecificationAmbiguity" or
             "PlanChangeRequired" or "NonBlockingImprovement" or "Deferred"))
            throw new StoreFailure("INVALID_INPUT");
        using var db=tasks.Open();
        using var tx=db.BeginTransaction();
        EngineeringDelegationStore.Require(db,tx,context.TaskId,context.PolicyRevision,
            context.PolicySha256,context.FromAgent,"ScheduleWork");
        var issue=Read(db,tx,context.TaskId,context.RunId).Issues.SingleOrDefault(x=>x.Id==draft.IssueId)
            ?? throw new StoreFailure("NOT_FOUND");
        if(!issue.CurrentSource || issue.SourceSha256!=context.SourceSha256 ||
            !issue.LatestReview || issue.RepairCycleId is not null ||
            !issue.References.Any(x=>x.ReviewId==draft.ReviewId) ||
            TaskStore.Scalar(db,tx,issue.Origin=="QaEngineer"?
                "SELECT id FROM qa_reviews WHERE task_id=@task AND engineering_run_id=@run ORDER BY created_utc DESC,id DESC LIMIT 1":
                "SELECT id FROM lead_reviews WHERE task_id=@task AND engineering_run_id=@run ORDER BY created_utc DESC,id DESC LIMIT 1",
                ("@task",context.TaskId),("@run",context.RunId))!=draft.ReviewId ||
            draft.ChallengeMessageId!=issue.Challenges.LastOrDefault()?.MessageId)
            throw new StoreFailure("SOURCE_CHANGED");
        // Blocking concerns cannot be waived via a classification change.
        if(issue.Severity is "Critical" or "Major" &&
            draft.Classification is ("Deferred" or "NonBlockingImprovement"))
            throw new StoreFailure("INVALID_INPUT");
        var latest=issue.Decisions.LastOrDefault();
        var rationale=draft.Rationale.Trim();
        if(latest is not null && latest.Revision==draft.ExpectedRevision+1 &&
            latest.ReviewId==draft.ReviewId && latest.Classification==draft.Classification &&
            latest.Rationale==rationale && latest.ChallengeMessageId==draft.ChallengeMessageId &&
            latest.PolicyRevision==context.PolicyRevision && latest.PolicySha256==context.PolicySha256)
        {
            tx.Commit();return latest;
        }
        if((latest?.Revision??0)!=draft.ExpectedRevision)
            throw new StoreFailure("IDEMPOTENCY_CONFLICT");
        var decision=new IssueDecision(draft.ExpectedRevision+1,issue.Id,draft.ReviewId,
            draft.Classification,rationale,draft.ChallengeMessageId,context.PolicyRevision,
            context.PolicySha256,DateTimeOffset.UtcNow.ToString("O"));
        TaskStore.Execute(db,tx,"""
            INSERT INTO engineering_issue_decisions(task_id,issue_id,revision,run_id,source_sha256,
                plan_sha256,review_id,classification,rationale,challenge_message_id,policy_revision,
                policy_sha256,created_utc)
            VALUES(@task,@issue,@rev,@run,@source,@plan,@review,@classification,@rationale,@challenge,
                @policy,@policysha,@utc)
            """,("@task",context.TaskId),("@issue",issue.Id),("@rev",decision.Revision),
            ("@run",context.RunId),("@source",context.SourceSha256),("@plan",issue.PlanSha256),
            ("@review",decision.ReviewId),("@classification",decision.Classification),
            ("@rationale",decision.Rationale),("@challenge",(object?)decision.ChallengeMessageId??DBNull.Value),
            ("@policy",context.PolicyRevision),("@policysha",context.PolicySha256),("@utc",decision.CreatedUtc));
        EngineeringActivityStore.Record(db,tx,context.TaskId,"IssueDecisionRecorded","TechnicalLead",
            "EngineeringIssue",issue.Id,decision.Revision,toAgent:
                decision.Classification=="ImplementationDefect"?"SoftwareEngineer":issue.Origin,
                sourceSha256:context.SourceSha256);
        tx.Commit();tasks.SignalEngineeringActivity(context.TaskId);return decision;
    }
}
