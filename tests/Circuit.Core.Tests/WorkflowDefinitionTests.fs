namespace Circuit.Core.Tests

open System
open System.Threading.Tasks
open Circuit.Core
open Xunit

module WorkflowDefinitionTests =
    [<Fact>]
    let ``choose fingerprints are stable across branch insertion order`` () =
        let low =
            Workflow.define
                "workflow.fingerprint.low"
                "1.0.0"
                (Workflow.code "low" (fun _ value -> Task.FromResult(value + 1)))

        let high =
            Workflow.define
                "workflow.fingerprint.high"
                "1.0.0"
                (Workflow.code "high" (fun _ value -> Task.FromResult(value + 10)))

        let definitionOne =
            Workflow.define
                "workflow.fingerprint"
                "1.0.0"
                (Workflow.choose
                    "branch.select"
                    (fun value -> if value > 10 then "high" else "low")
                    (Map.ofList [ "low", low; "high", high ])
                    None)

        let definitionTwo =
            Workflow.define
                "workflow.fingerprint"
                "1.0.0"
                (Workflow.choose
                    "branch.select"
                    (fun value -> if value > 10 then "high" else "low")
                    (Map.ofList [ "high", high; "low", low ])
                    None)

        Assert.Equal(definitionOne.Fingerprint, definitionTwo.Fingerprint)

    [<Fact>]
    let ``fingerprints distinguish delimiter-heavy branch keys`` () =
        let low =
            Workflow.define
                "workflow.fingerprint.branch.low"
                "1.0.0"
                (Workflow.code "step|low" (fun _ value -> Task.FromResult(value + 1)))

        let high =
            Workflow.define
                "workflow.fingerprint.branch.high"
                "1.0.0"
                (Workflow.code "step=high" (fun _ value -> Task.FromResult(value + 10)))

        let definitionOne =
            Workflow.define
                "workflow.fingerprint.root"
                "1.0.0"
                (Workflow.choose
                    "branch.select|root"
                    (fun value -> if value > 10 then "high,one" else "low=one")
                    (Map.ofList [ "low=one", low; "high,one", high ])
                    None)

        let definitionTwo =
            Workflow.define
                "workflow.fingerprint.root"
                "1.0.0"
                (Workflow.choose
                    "branch.select|root"
                    (fun value -> if value > 10 then "high=two,branch" else "low|two")
                    (Map.ofList [ "low|two", low; "high=two,branch", high ])
                    None)

        Assert.False(StringComparer.Ordinal.Equals(definitionOne.Fingerprint, definitionTwo.Fingerprint))

    [<Fact>]
    let ``fingerprints change when branch topology changes`` () =
        let low =
            Workflow.define
                "workflow.branch.low"
                "1.0.0"
                (Workflow.code "low" (fun _ value -> Task.FromResult(value + 1)))

        let high =
            Workflow.define
                "workflow.branch.high"
                "1.0.0"
                (Workflow.code "high" (fun _ value -> Task.FromResult(value + 10)))

        let highWithTail =
            Workflow.define
                "workflow.branch.high"
                "1.0.0"
                (Workflow.code "high" (fun _ value -> Task.FromResult(value + 10)))
            |> Workflow.thenStep (Workflow.code "high.tail" (fun _ value -> Task.FromResult(value + 100)))

        let definitionOne =
            Workflow.define
                "workflow.branch"
                "1.0.0"
                (Workflow.choose
                    "branch.select"
                    (fun value -> if value > 10 then "high" else "low")
                    (Map.ofList [ "low", low; "high", high ])
                    None)

        let definitionTwo =
            Workflow.define
                "workflow.branch"
                "1.0.0"
                (Workflow.choose
                    "branch.select"
                    (fun value -> if value > 10 then "high" else "low")
                    (Map.ofList [ "low", low; "high", highWithTail ])
                    None)

        Assert.False(StringComparer.Ordinal.Equals(definitionOne.Fingerprint, definitionTwo.Fingerprint))

    [<Fact>]
    let ``validate reports duplicate branch keys from internal choice construction`` () =
        let low =
            Workflow.define
                "workflow.branch.low"
                "1.0.0"
                (Workflow.code "low" (fun _ value -> Task.FromResult(value + 1)))

        let high =
            Workflow.define
                "workflow.branch.high"
                "1.0.0"
                (Workflow.code "high" (fun _ value -> Task.FromResult(value + 10)))

        let definition =
            Workflow.define
                "workflow.branch.duplicate"
                "1.0.0"
                (Workflow.chooseCases
                    "branch.select"
                    (fun value -> if value > 10 then "high" else "low")
                    [ "low", low; "low", high ]
                    None)

        let issues = Workflow.validate definition

        Assert.Contains(issues, fun issue -> issue.Code = "duplicate-branch-key" && issue.NodeId = "branch.select")

    [<Fact>]
    let ``validate reports malformed choice adapter branch keys`` () =
        let node: WorkflowGraph.Node =
            { Id = "branch.case"
              InputType = typeof<WorkflowGraph.BranchSelection<int>>
              OutputType = typeof<int>
              Kind = WorkflowGraph.ChoiceCaseAdapter "" }

        let definition =
            WorkflowDefinition<int, int>(
                DefinitionId.Create "workflow.branch.adapter",
                SemanticVersion.Parse "1.0.0",
                [ node ],
                [],
                node.Id,
                [ node.Id ]
            )

        let issues = Workflow.validate definition

        Assert.Contains(issues, fun issue -> issue.Code = "branch-key" && issue.NodeId = node.Id)

    [<Fact>]
    let ``validate reports duplicate node ids`` () =
        let first =
            Workflow.code "duplicate.step" (fun _ value -> Task.FromResult(value + 1))

        let second =
            Workflow.code "duplicate.step" (fun _ value -> Task.FromResult(value * 2))

        let definition =
            Workflow.define "workflow.duplicate" "1.0.0" first |> Workflow.thenStep second

        let issues = Workflow.validate definition

        Assert.Contains(issues, fun issue -> issue.Code = "duplicate-id" && issue.NodeId = "duplicate.step")

    [<Fact>]
    let ``validate reports unreachable nodes`` () =
        let entry: WorkflowGraph.Node =
            { Id = "entry"
              InputType = typeof<int>
              OutputType = typeof<int>
              Kind =
                WorkflowGraph.Code(
                    WorkflowGraph.CodeHandler<int, int>(fun _ value -> Task.FromResult value)
                    :> WorkflowGraph.ICodeHandler
                ) }

        let orphan: WorkflowGraph.Node =
            { Id = "orphan"
              InputType = typeof<int>
              OutputType = typeof<int>
              Kind =
                WorkflowGraph.Code(
                    WorkflowGraph.CodeHandler<int, int>(fun _ value -> Task.FromResult(value + 1))
                    :> WorkflowGraph.ICodeHandler
                ) }

        let definition =
            WorkflowDefinition<int, int>(
                DefinitionId.Create "workflow.unreachable",
                SemanticVersion.Parse "1.0.0",
                [ entry; orphan ],
                [],
                "entry",
                [ "entry" ]
            )

        let issues = Workflow.validate definition

        Assert.Contains(issues, fun issue -> issue.Code = "unreachable" && issue.NodeId = "orphan")

    [<Fact>]
    let ``validate reports type mismatches`` () =
        let source: WorkflowGraph.Node =
            { Id = "source"
              InputType = typeof<int>
              OutputType = typeof<int>
              Kind =
                WorkflowGraph.Code(
                    WorkflowGraph.CodeHandler<int, int>(fun _ value -> Task.FromResult value)
                    :> WorkflowGraph.ICodeHandler
                ) }

        let target: WorkflowGraph.Node =
            { Id = "target"
              InputType = typeof<string>
              OutputType = typeof<int>
              Kind =
                WorkflowGraph.Code(
                    WorkflowGraph.CodeHandler<string, int>(fun _ value -> Task.FromResult(value.Length))
                    :> WorkflowGraph.ICodeHandler
                ) }

        let definition =
            WorkflowDefinition<int, int>(
                DefinitionId.Create "workflow.mismatch",
                SemanticVersion.Parse "1.0.0",
                [ source; target ],
                [ { SourceId = "source"
                    TargetId = "target" } ],
                "source",
                [ "target" ]
            )

        let issues = Workflow.validate definition

        Assert.Contains(issues, fun issue -> issue.Code = "type-mismatch" && issue.NodeId = "target")

    [<Fact>]
    let ``validate reports invalid parallel and loop bounds`` () =
        let branch =
            Workflow.define
                "workflow.parallel.branch"
                "1.0.0"
                (Workflow.code "branch" (fun _ value -> Task.FromResult value))

        let invalidParallel =
            Workflow.define
                "workflow.parallel.invalid"
                "1.0.0"
                (Workflow.``parallel`` "parallel" 0 [] (fun (values: int list) -> Task.FromResult values.Length))

        let invalidLoop =
            Workflow.define "workflow.loop.invalid" "1.0.0" (Workflow.loop "loop" 0 (fun (_: int) -> true) branch)

        let parallelIssues = Workflow.validate invalidParallel
        let loopIssues = Workflow.validate invalidLoop

        Assert.Contains(parallelIssues, fun issue -> issue.Code = "empty-parallel" && issue.NodeId = "parallel")
        Assert.Contains(parallelIssues, fun issue -> issue.Code = "parallel-concurrency" && issue.NodeId = "parallel")
        Assert.Contains(loopIssues, fun issue -> issue.Code = "loop-bound" && issue.NodeId = "loop")
