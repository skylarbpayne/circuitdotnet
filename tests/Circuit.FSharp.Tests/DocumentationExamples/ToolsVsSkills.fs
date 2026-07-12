namespace Circuit.FSharp.Tests.DocumentationExamples

open System.ComponentModel.DataAnnotations
open System.Threading.Tasks
open Circuit.Core
open Circuit.FSharp

[<AllowNullLiteral>]
type CustomerLookupInput() =
    [<property: Required>]
    member val CustomerId = "" with get, set

[<AllowNullLiteral>]
type CustomerLookupOutput() =
    [<property: Required>]
    member val CustomerId = "" with get, set

    [<property: Required>]
    member val Tier = "" with get, set

module ToolsVsSkillsExample =
    let skill =
        SkillReference.Create(
            "skill.support-style",
            "1.0.0",
            "Company-specific support guidance.",
            SkillSource.CreateFile "/srv/circuit/skills/support-style"
        )

    let agent =
        AgentDefinition.create
            "triage.agent"
            "1.0.0"
            "Triage"
            "Use the attached skill for policy and call tools only when fresh data is required."
        |> AgentDefinition.withSkills [ skill ]
        |> AgentDefinition.withToolTags [ "ticket.read"; "ticket.write" ]

    let tool =
        ToolDefinition.create<CustomerLookupInput, CustomerLookupOutput>
            "customer.lookup"
            "1.0.0"
            "Read-only customer lookup."
            (fun _context input ->
                Task.FromResult(CustomerLookupOutput(CustomerId = input.CustomerId, Tier = "enterprise")))
        |> ToolDefinition.withApproval ApprovalMode.Never
