namespace Circuit.FSharp.Tests.DocumentationExamples

open System.ComponentModel.DataAnnotations
open System.Threading
open Circuit.Core
open Circuit.FSharp

[<AllowNullLiteral>]
type ContactRequest() =
    [<property: Required>]
    member val Message = "" with get, set

[<AllowNullLiteral>]
type ContactSummary() =
    [<property: Required>]
    member val Name = "" with get, set

    [<property: Required>]
    member val Status = "" with get, set

module StructuredOutputExample =
    let signature =
        Signature.create<ContactRequest, ContactSummary>
            "contacts.signature"
            "1.0.0"
            "Contact summary"
            "Return name and status."

    let run (runtime: ICircuitRuntime) (agent: AgentDefinition) =
        Agent.run
            runtime
            agent
            signature
            (ContactRequest(Message = "Summarize Ada Lovelace."))
            RunOptions.Default
            CancellationToken.None
