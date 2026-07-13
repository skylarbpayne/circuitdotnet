using Circuit;
using Circuit.Testing;

var runtime = new ScriptedRuntime(new[] { ScriptedResponses.OutputValue(new TicketResult("ticket-42", "billing", "high")) });
var agent = new AgentDefinition("ticket-triage", "1.0.0", "Ticket triage", "Classify the ticket.");
var signature = new AgentSignature<Ticket, TicketResult>("triage", "1.0.0", "Triage", "Return structured output.");
var definition = CircuitDefinition<Ticket, TicketResult>.FromAgent(agent, signature).Define("ticket-triage-circuit", "1.0.0");
var client = new CircuitClientBuilder().UseRuntime(runtime).Build();
var response = await client.RunAsync(definition, new Ticket("ticket-42", "Duplicate invoice"));
if (!response.IsSuccess) throw new InvalidOperationException(response.Failure.Message);
Console.WriteLine($"{response.Value.Id}:{response.Value.Category}:{response.Value.Severity}");

internal sealed record Ticket(string Id, string Body);
internal sealed record TicketResult(string Id, string Category, string Severity);
