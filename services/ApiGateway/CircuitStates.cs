using System.Collections.Concurrent;

namespace ApiGateway;

// Remembers the current state of each service's circuit breaker, so GET /api/diagnostics/circuits
// can show it. The circuit breaker's OnOpened/OnHalfOpened/OnClosed callbacks (Resilience.cs) update it.
// A singleton, because there is one circuit per service for the whole gateway, not one per request.
//
// Note: Polly only moves from Open to HalfOpen when a call comes in after the break duration.
// So the state stays "Open" here until someone calls the service again; that call is the trial call.
public class CircuitStates(IEnumerable<string> services)
{
    // ConcurrentDictionary, because many requests can change a state at the same time.
    private readonly ConcurrentDictionary<string, string> states = new(services.ToDictionary(s => s, _ => "Closed"));

    public void Set(string service, string state) => states[service] = state;

    public string Get(string service) => states.GetValueOrDefault(service, "Closed");

    public SortedDictionary<string, string> All() => new(states);
}
