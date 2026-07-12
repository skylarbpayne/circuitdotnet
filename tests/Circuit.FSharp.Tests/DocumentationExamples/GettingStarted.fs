namespace Circuit.FSharp.Tests.DocumentationExamples

open System.ComponentModel.DataAnnotations
open System.Threading
open Circuit.Core
open Circuit.FSharp
open Circuit.Testing

[<AllowNullLiteral>]
type TicketInput() =
    [<property: Required>]
    member val Message = "" with get, set

[<AllowNullLiteral>]
type TicketOutput() =
    [<property: Required>]
    member val Category = "" with get, set

    [<property: Required>]
    member val Summary = "" with get, set

module GettingStartedExample =
    let run () =
        let runtime =
            ScriptedRuntime(
                [ ScriptedResponses.OutputJson "{\"category\":\"support\",\"summary\":\"Reset the password.\"}" ]
            )
            :> ICircuitRuntime

        let agent =
            AgentDefinition.create "support.agent" "1.0.0" "Support" "Return only the structured answer."

        let signature =
            Signature.create<TicketInput, TicketOutput>
                "support.signature"
                "1.0.0"
                "Support answer"
                "Return category and summary."

        Agent.run
            runtime
            agent
            signature
            (TicketInput(Message = "Reset my password."))
            RunOptions.Default
            CancellationToken.None
