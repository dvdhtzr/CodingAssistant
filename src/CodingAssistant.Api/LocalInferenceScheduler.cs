namespace CodingAssistant.Api;

public sealed record InferenceCapacity(int Capacity,int Active,int Waiting,int MaxWaiting);

/// <summary>Process-wide local Ollama admission gate shared by every actual
/// inference transport. Default one request protects constrained hardware.
/// A lease owns only a provider slot, never source/execution authority.</summary>
public sealed class LocalInferenceScheduler
{
    private static readonly Lazy<LocalInferenceScheduler> Instance=new(()=>{
        var raw=Environment.GetEnvironmentVariable("CODINGASSISTANT_INFERENCE_CAPACITY");
        if(string.IsNullOrWhiteSpace(raw))return new LocalInferenceScheduler(1);
        if(!int.TryParse(raw,out var capacity) || capacity is <1 or >4)
            throw new InvalidOperationException("CODINGASSISTANT_INFERENCE_CAPACITY must be 1 through 4");
        return new LocalInferenceScheduler(capacity);
    });
    public static LocalInferenceScheduler Shared=>Instance.Value;
    private readonly SemaphoreSlim _slots;
    private readonly object _sync=new();
    private readonly int _capacity,_maxWaiting;
    private readonly TimeSpan _waitTimeout;
    private int _active,_waiting;

    public LocalInferenceScheduler(int capacity,int maxWaiting=32,TimeSpan? waitTimeout=null)
    {
        if(capacity is <1 or >4 || maxWaiting is <1 or >128)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity=capacity;_maxWaiting=maxWaiting;
        _waitTimeout=waitTimeout??TimeSpan.FromMinutes(10);
        if(_waitTimeout<=TimeSpan.Zero || _waitTimeout>TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        _slots=new SemaphoreSlim(capacity,capacity);
    }

    public InferenceCapacity Snapshot()
    {lock(_sync)return new(_capacity,_active,_waiting,_maxWaiting);}

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellation,
        Action? onQueued=null)
    {
        cancellation.ThrowIfCancellationRequested();
        lock(_sync)
        {
            // Avoid queue jumping when requests are already waiting.
            if(_waiting==0 && _slots.Wait(0))
            {
                _active++;return new Lease(this);
            }
            if(_waiting>=_maxWaiting)throw new StoreFailure("INFERENCE_QUEUE_FULL");
            _waiting++;
        }
        var acquired=false;
        try
        {
            onQueued?.Invoke();
            acquired=await _slots.WaitAsync(_waitTimeout,cancellation);
            if(!acquired)throw new StoreFailure("INFERENCE_QUEUE_TIMEOUT");
            lock(_sync)
            {
                _waiting--;_active++;
                return new Lease(this);
            }
        }
        catch
        {
            lock(_sync)
            {
                _waiting--;
                if(acquired)_slots.Release();
            }
            throw;
        }
    }

    private void Release()
    {
        lock(_sync){_active--;_slots.Release();}
    }

    private sealed class Lease(LocalInferenceScheduler owner):IDisposable
    {
        private int _disposed;
        public void Dispose()
        {if(Interlocked.Exchange(ref _disposed,1)==0)owner.Release();}
    }
}
