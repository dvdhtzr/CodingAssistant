using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

// Owner-local, read-only planning configuration. No HTTP or model-facing
// writer, automatic trust-on-first-use, or execution permission.
internal sealed record EvaluationPlanningHostConfiguration(
    string RepositoryRoot,string NodeExecutable,string NodeSha256,
    string BinderSha256,IReadOnlyDictionary<string,string> ModulePins,
    string DockerExecutable,string DockerSha256)
{
    internal const string FileName="evaluation-planning-host.json";
    internal const string PilotFileName="evaluation-pilot-host.json";
    private const int MaxBytes=16384;
    private static readonly Regex Digest=new("^[a-f0-9]{64}\\z",RegexOptions.CultureInvariant);
    private static readonly string[] Fields=["schemaVersion","repositoryRoot","nodeExecutable",
        "nodeSha256","binderSha256","modulePins","dockerExecutable","dockerSha256"];

    internal static EvaluationPlanningHostConfiguration Load(string ownerDataDirectory)=>
        LoadCore(ownerDataDirectory,FileName,pins=>EvaluationPlanBinder.HasExactModulePins(pins));

    internal static EvaluationPlanningHostConfiguration LoadPilot(string ownerDataDirectory)=>
        LoadCore(ownerDataDirectory,PilotFileName,pins=>PilotQualificationInspector.HasExactFilePins(pins));

    internal const string NativeRecoveryFileName="evaluation-native-recovery-host.json";
    internal const string CleanupBridgePath="tools/m4/native_cleanup_observation_bridge.mjs";
    internal const string CleanupAdapterPath="tools/m4/native_exact_cleanup.mjs";

    // Separate explicit owner configuration. Historical evidence keeps
    // its original exact pin closure; no automatic hashing or trust.
    internal static (EvaluationPlanningHostConfiguration Config,string BridgeSha256,string AdapterSha256)
        LoadNativeRecovery(string ownerDataDirectory)
    {
        var config=LoadCore(ownerDataDirectory,NativeRecoveryFileName,pins=>
            pins.Count==PilotQualificationInspector.FileNames.Count+2&&
            PilotQualificationInspector.FileNames.All(pins.ContainsKey)&&
            pins.ContainsKey(CleanupBridgePath)&&pins.ContainsKey(CleanupAdapterPath));
        var bridge=config.ModulePins[CleanupBridgePath];
        var adapter=config.ModulePins[CleanupAdapterPath];
        var historical=config.ModulePins.Where(p=>p.Key!=CleanupBridgePath&&p.Key!=CleanupAdapterPath)
            .ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
        return (config with {ModulePins=new ReadOnlyDictionary<string,string>(historical)},bridge,adapter);
    }

    private static EvaluationPlanningHostConfiguration LoadCore(string ownerDataDirectory,string fileName,
        Func<IReadOnlyDictionary<string,string>,bool> hasExactPins)
    {
        if(string.IsNullOrWhiteSpace(ownerDataDirectory)||!Path.IsPathFullyQualified(ownerDataDirectory))
            throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");
        try
        {
            var root=new DirectoryInfo(Path.GetFullPath(ownerDataDirectory));
            if(!root.Exists)throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_REQUIRED");
            for(DirectoryInfo? dir=root;dir is not null;dir=dir.Parent)
                if((dir.Attributes&FileAttributes.ReparsePoint)!=0)
                    throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");
            PrivateDataDirectory.Ensure(root.FullName);
            var file=Path.Combine(root.FullName,fileName);
            if(!File.Exists(file))throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_REQUIRED");
            PrivateDataDirectory.ValidateFile(file);
            using var stream=File.OpenRead(file);
            if(stream.Length is <2 or >MaxBytes)
                throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");
            var bytes=new byte[MaxBytes+1];
            var length=0;
            while(length<bytes.Length)
            {
                var count=stream.Read(bytes,length,bytes.Length-length);
                if(count==0)break;
                length+=count;
            }
            if(length>MaxBytes)throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");
            using var json=JsonDocument.Parse(bytes.AsMemory(0,length),
                new JsonDocumentOptions{MaxDepth=8});
            var value=json.RootElement;
            if(value.ValueKind!=JsonValueKind.Object||
                !ExactFields(value,Fields)||
                value.GetProperty("schemaVersion").ValueKind!=JsonValueKind.Number||
                !value.GetProperty("schemaVersion").TryGetInt32(out var schema)||schema!=1)
                throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");
            string Read(string key)
            {
                var property=value.GetProperty(key);
                if(property.ValueKind!=JsonValueKind.String||
                    property.GetString() is not {Length:>0 and <=4096} text)
                    throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");
                return text;
            }
            string Pin(string key)
            {
                var pin=Read(key);
                if(!Digest.IsMatch(pin))throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");
                return pin;
            }
            string Absolute(string key)
            {
                var path=Read(key);
                if(!Path.IsPathFullyQualified(path))
                    throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");
                return Path.GetFullPath(path);
            }
            var modules=value.GetProperty("modulePins");
            if(modules.ValueKind!=JsonValueKind.Object)
                throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");
            var pins=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var property in modules.EnumerateObject())
                if(property.Value.ValueKind!=JsonValueKind.String||
                    property.Value.GetString() is not string pin||!Digest.IsMatch(pin)||
                    !pins.TryAdd(property.Name,pin))
                    throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");
            if(!hasExactPins(pins))
                throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");
            return new(Absolute("repositoryRoot"),Absolute("nodeExecutable"),Pin("nodeSha256"),
                Pin("binderSha256"),new ReadOnlyDictionary<string,string>(pins),
                Absolute("dockerExecutable"),Pin("dockerSha256"));
        }
        catch(IOException){throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");}
        catch(UnauthorizedAccessException){throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");}
        catch(InvalidOperationException){throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");}
        catch(ArgumentException){throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");}
        catch(NotSupportedException){throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");}
        catch(JsonException){throw new StoreFailure("EVALUATION_HOST_CONFIGURATION_INVALID");}
    }

    private static bool ExactFields(JsonElement value,string[] fields)
    {
        var names=value.EnumerateObject().Select(x=>x.Name).ToArray();
        return names.Length==fields.Length&&names.ToHashSet(StringComparer.Ordinal).SetEquals(fields);
    }

    internal EvaluationPlanBinder CreateBinder(TaskStore tasks)=>
        new(tasks,RepositoryRoot,NodeExecutable,NodeSha256,BinderSha256,ModulePins,
            DockerExecutable,DockerSha256);
}
