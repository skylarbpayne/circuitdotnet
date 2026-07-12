namespace Circuit.FSharp.Tests.DocumentationExamples

open System.Threading
open System.Threading.Tasks
open Circuit.Core

module WorkflowsExample =
    let approvalFlow =
        Workflow.request "manager.approval" (fun approved ->
            ApprovalPrompt.Create($"Approve {approved}", "A human must approve before completion."))
        |> Workflow.define "approval.workflow" "1.0.0"
        |> Workflow.thenStep (Workflow.code "decision" (fun _ response -> Task.FromResult response.Approved))

    let start (runtime: IWorkflowRuntime) =
        Workflow.start runtime approvalFlow 42 WorkflowRunOptions.Default CancellationToken.None
