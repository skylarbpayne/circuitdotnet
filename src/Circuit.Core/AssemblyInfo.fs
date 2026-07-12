namespace Circuit.Core

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("Circuit")>]
[<assembly: InternalsVisibleTo("Circuit.MicrosoftAgentFramework")>]
[<assembly: InternalsVisibleTo("Circuit.Core.Tests")>]
[<assembly: InternalsVisibleTo("Circuit.MicrosoftAgentFramework.Tests")>]
[<assembly: InternalsVisibleTo("Circuit.FSharp")>]
[<assembly: InternalsVisibleTo("Circuit.FSharp.Tests")>]
do ()
