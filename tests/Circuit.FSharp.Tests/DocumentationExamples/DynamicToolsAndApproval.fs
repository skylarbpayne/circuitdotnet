namespace Circuit.FSharp.Tests.DocumentationExamples

open System.Collections.Generic
open System.ComponentModel.DataAnnotations
open System.Threading.Tasks
open Circuit.Core
open Circuit.MicrosoftAgentFramework

[<AllowNullLiteral>]
type SearchInput() =
    [<property: Required>]
    member val Query = "" with get, set

[<AllowNullLiteral>]
type SearchOutput() =
    [<property: Required>]
    member val Result = "" with get, set

[<AllowNullLiteral>]
type WriteInput() =
    [<property: Required>]
    member val TicketId = "" with get, set

[<AllowNullLiteral>]
type WriteOutput() =
    [<property: Required>]
    member val TicketId = "" with get, set

    [<property: Required>]
    member val Status = "" with get, set

type EnterpriseApprovalPolicy() =
    interface IToolApprovalPolicy with
        member _.IsApprovedAsync(_policyName, _context) = ValueTask<bool>(false)

type TenantToolResolver() =
    interface IToolResolver with
        member _.ResolveAsync(context, _cancellationToken) =
            let tools = ResizeArray<ResolvedTool>()

            tools.Add(
                ToolDefinition<SearchInput, SearchOutput>
                    .Create(
                        "kb.search",
                        "1.0.0",
                        "Read-only knowledge-base search.",
                        Contract<SearchInput>.Create(CircuitJson.createOptions (), Seq.empty),
                        Contract<SearchOutput>.Create(CircuitJson.createOptions (), Seq.empty),
                        ApprovalMode.Never,
                        ValueNone,
                        System.Func<ToolContext, SearchInput, Task<SearchOutput>>(fun _ input ->
                            Task.FromResult(SearchOutput(Result = $"tenant={context.TenantId}: {input.Query}")))
                    )
                |> ResolvedTool.Create
            )

            if context.TenantId = ValueSome "enterprise" then
                tools.Add(
                    ToolDefinition<WriteInput, WriteOutput>
                        .Create(
                            "ticket.escalate",
                            "1.0.0",
                            "Escalate a ticket.",
                            Contract<WriteInput>.Create(CircuitJson.createOptions (), Seq.empty),
                            Contract<WriteOutput>.Create(CircuitJson.createOptions (), Seq.empty),
                            ApprovalMode.ByPolicy,
                            ValueSome "enterprise-writes",
                            System.Func<ToolContext, WriteInput, Task<WriteOutput>>(fun _ input ->
                                Task.FromResult(WriteOutput(TicketId = input.TicketId, Status = "queued")))
                        )
                    |> ResolvedTool.Create
                )

            ValueTask<IReadOnlyList<ResolvedTool>>(tools.ToArray() :> IReadOnlyList<ResolvedTool>)

module DynamicToolsAndApprovalExample =
    let createOptions () =
        let options = MafRuntimeOptions()
        options.ToolResolvers <- [| TenantToolResolver() :> IToolResolver |]
        options.ToolApprovalPolicy <- ValueSome(EnterpriseApprovalPolicy() :> IToolApprovalPolicy)
        options
