namespace Circuit.Core.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Xunit

type private AgentRunEmptyEvents<'T>() =
    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(_cancellationToken) =
            { new IAsyncEnumerator<'T> with
                member _.Current = Unchecked.defaultof<'T>
                member _.MoveNextAsync() = ValueTask<bool>(false)
                member _.DisposeAsync() = ValueTask() }

module AgentRunsTests =
    let private runId = RunId.Parse("0123456789abcdef0123456789abcdef")

    let private events () =
        AgentRunEmptyEvents<RunEvent<string>>() :> IAsyncEnumerable<RunEvent<string>>

    let private createRun respondAsync disposeAsync =
        AgentRun<string>.Create(runId, events (), respondAsync, disposeAsync)

    [<Fact>]
    let ``factory validates its public inputs`` () =
        let respond =
            Func<ApprovalResponse, CancellationToken, ValueTask>(fun _ _ -> ValueTask())

        let dispose = Func<ValueTask>(fun () -> ValueTask())

        Assert.Throws<ArgumentException>(fun () ->
            AgentRun<string>.Create(Unchecked.defaultof<RunId>, events (), respond, dispose)
            |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () -> AgentRun<string>.Create(runId, null, respond, dispose) |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () ->
            AgentRun<string>.Create(runId, events (), null, dispose) |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () ->
            AgentRun<string>.Create(runId, events (), respond, null) |> ignore)
        |> ignore

    [<Fact>]
    let ``response validates null before forwarding`` () =
        let mutable forwarded = false

        let run =
            createRun
                (Func<ApprovalResponse, CancellationToken, ValueTask>(fun _ _ ->
                    forwarded <- true
                    ValueTask()))
                (Func<ValueTask>(fun () -> ValueTask()))

        Assert.Throws<ArgumentNullException>(fun () -> run.RespondAsync(null, CancellationToken.None) |> ignore)
        |> ignore

        Assert.False(forwarded)

    [<Fact>]
    let ``disposal is thread safe and makes the handle unavailable`` () =
        task {
            let mutable disposeCount = 0

            let run =
                createRun
                    (Func<ApprovalResponse, CancellationToken, ValueTask>(fun _ _ -> ValueTask()))
                    (Func<ValueTask>(fun () ->
                        Interlocked.Increment(&disposeCount) |> ignore
                        ValueTask()))

            let disposals =
                Array.init 32 (fun _ -> (run :> IAsyncDisposable).DisposeAsync().AsTask())

            do! Task.WhenAll(disposals)
            Assert.Equal(1, disposeCount)

            Assert.Throws<ObjectDisposedException>(fun () -> run.Events |> ignore) |> ignore

            Assert.Throws<ObjectDisposedException>(fun () ->
                run.RespondAsync(ApprovalResponse.Create("approval-1", true), CancellationToken.None)
                |> ignore)
            |> ignore
        }
