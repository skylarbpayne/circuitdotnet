namespace Circuit.MicrosoftAgentFramework

open System
open System.Collections.Frozen
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Microsoft.Agents.AI
open Microsoft.Extensions.AI

[<AbstractClass; Sealed>]
type internal MafSkillAdapterProperties private () =
    static member AgentSkill = "circuit.microsoft-agent-framework.agent-skill"
    static member FileSkills = "circuit.microsoft-agent-framework.file-skills"

module private MafSkillContextProviderHelpers =
    let removeScriptInstructions (instructions: string) =
        if String.IsNullOrWhiteSpace instructions then
            instructions
        else
            instructions.Replace(
                "\n- Use `run_skill_script` to run referenced scripts, using the name exactly as listed.",
                String.Empty,
                StringComparison.Ordinal
            )

    let filterTools (tools: IEnumerable<AITool>) =
        if isNull (box tools) then
            null
        else
            tools
            |> Seq.filter (fun tool ->
                not (StringComparer.Ordinal.Equals(tool.Name, AgentSkillsProvider.RunSkillScriptToolName)))
            |> Seq.toArray
            :> IEnumerable<AITool>

[<Sealed>]
type internal MafSkillContextProvider(inner: AIContextProvider, scriptsEnabled: bool) =
    inherit AIContextProvider(null, null, null)

    override _.StateKeys = inner.StateKeys

    override _.InvokingCoreAsync(context, cancellationToken) =
        if scriptsEnabled then
            inner.InvokingAsync(context, cancellationToken)
        else
            task {
                let! aiContext = inner.InvokingAsync(context, cancellationToken).AsTask()

                if isNull aiContext then
                    return aiContext
                else
                    aiContext.Instructions <-
                        MafSkillContextProviderHelpers.removeScriptInstructions aiContext.Instructions

                    aiContext.Tools <- MafSkillContextProviderHelpers.filterTools aiContext.Tools
                    return aiContext
            }
            |> ValueTask<AIContext>

    override _.InvokedCoreAsync(context, cancellationToken) =
        inner.InvokedAsync(context, cancellationToken)

[<Sealed>]
type internal MafSkillProviderAttachment
    (provider: AgentSkillsProvider, scriptsEnabled: bool, ownedFileSkills: IReadOnlyList<IDisposable>) =
    member _.Provider = provider
    member _.ScriptsEnabled = scriptsEnabled

    interface IDisposable with
        member _.Dispose() =
            if not (isNull provider) then
                provider.Dispose()

            if not (isNull ownedFileSkills) then
                for fileSkills in ownedFileSkills do
                    if not (isNull (box fileSkills)) then
                        fileSkills.Dispose()

module internal MafSkillAdapter =
    type private SkillDiscoverySession() =
        inherit AgentSession()

    type private SkillDiscoveryAgent() =
        inherit AIAgent()

        override _.CreateSessionCoreAsync(_cancellationToken) =
            raise (InvalidOperationException("This agent is only used for file skill discovery."))

        override _.SerializeSessionCoreAsync(_session, _options, _cancellationToken) =
            raise (InvalidOperationException("This agent is only used for file skill discovery."))

        override _.DeserializeSessionCoreAsync(_element, _options, _cancellationToken) =
            raise (InvalidOperationException("This agent is only used for file skill discovery."))

        override _.RunCoreAsync(_messages, _session, _options, _cancellationToken) =
            raise (InvalidOperationException("This agent is only used for file skill discovery."))

        override _.RunCoreStreamingAsync(_messages, _session, _options, _cancellationToken) =
            raise (InvalidOperationException("This agent is only used for file skill discovery."))

    let private createSkillSourceContext () =
        AgentSkillsSourceContext(SkillDiscoveryAgent(), SkillDiscoverySession())

    let private disabledFileScriptRunner =
        AgentFileSkillScriptRunner(fun _skill _script _arguments _serviceProvider _cancellationToken ->
            Task.FromException<obj>(InvalidOperationException("Skill scripts are disabled.")))

    [<RequireQualifiedAccess>]
    type internal SnapshotCleanupResult =
        | NotMaterialized
        | Deleted
        | Failed of string

    [<RequireQualifiedAccess>]
    type internal SnapshotCaptureEntryKind =
        | File
        | Directory

    [<NoEquality; NoComparison>]
    type internal SnapshotCaptureEntryState =
        { RelativePath: string
          ResolvedPath: string
          Kind: SnapshotCaptureEntryKind
          IsSymbolicLink: bool
          Length: int64
          LastWriteTimeUtc: DateTime }

    [<NoEquality; NoComparison>]
    type internal SnapshotCaptureHooks =
        { BeforeOpen: Action<SnapshotCaptureEntryState> voption }

    module private SnapshotMaterialization =
        let private directoryMode =
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

        let private fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

        let private ensurePrivateDirectory (path: string) =
            if OperatingSystem.IsWindows() then
                Directory.CreateDirectory(path) |> ignore
            else
                Directory.CreateDirectory(path, directoryMode) |> ignore
                File.SetUnixFileMode(path, directoryMode)

        let createPrivateRoot () =
            if OperatingSystem.IsWindows() then
                Directory.CreateTempSubdirectory("circuit-maf-skill-snapshots-").FullName
            else
                let parent = Path.Combine(Path.GetTempPath(), "circuit-maf-skill-snapshots")
                ensurePrivateDirectory parent

                let root = Path.Combine(parent, Guid.NewGuid().ToString("N"))
                ensurePrivateDirectory root
                root

        let writePrivateTextFile (path: string) (content: string) =
            let directory = Path.GetDirectoryName path

            if not (String.IsNullOrEmpty directory) then
                ensurePrivateDirectory directory

            use stream =
                new FileStream(
                    path,
                    (if OperatingSystem.IsWindows() then
                         FileMode.Create
                     else
                         FileMode.CreateNew),
                    FileAccess.Write,
                    FileShare.None
                )

            use writer = new StreamWriter(stream, UTF8Encoding(false))
            writer.Write(content)
            writer.Flush()

            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(path, fileMode)

        let deleteRoot (path: string) =
            if Directory.Exists path then
                Directory.Delete(path, true)

    module internal SnapshotCapture =
        let private pathComparison =
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        let private createCaptureFailure message = InvalidOperationException(message)

        let private pathsEqual (left: string) (right: string) =
            String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), pathComparison)

        let private getEntryState (canonicalRoot: string) (entryPath: string) (relativePath: string) =
            let attributes = File.GetAttributes entryPath
            let isDirectory = attributes.HasFlag(FileAttributes.Directory)

            let resolvedPath =
                if isDirectory then
                    SkillPathSecurity.resolveExistingRelativeDirectoryWithinRoot canonicalRoot relativePath
                else
                    SkillPathSecurity.resolveExistingRelativeFileWithinRoot canonicalRoot relativePath

            let kind =
                if isDirectory then
                    SnapshotCaptureEntryKind.Directory
                else
                    SnapshotCaptureEntryKind.File

            let lastWriteTimeUtc =
                if isDirectory then
                    Directory.GetLastWriteTimeUtc resolvedPath
                else
                    File.GetLastWriteTimeUtc resolvedPath

            let length = if isDirectory then 0L else FileInfo(resolvedPath).Length

            { RelativePath = relativePath
              ResolvedPath = Path.GetFullPath resolvedPath
              Kind = kind
              IsSymbolicLink = attributes.HasFlag(FileAttributes.ReparsePoint)
              Length = length
              LastWriteTimeUtc = lastWriteTimeUtc }

        let private ensureSupportedSymlinkState (state: SnapshotCaptureEntryState) =
            if state.IsSymbolicLink && state.Kind = SnapshotCaptureEntryKind.Directory then
                raise (
                    createCaptureFailure "Symbolic linked skill directories are not supported during snapshot capture."
                )

            if state.IsSymbolicLink && not (OperatingSystem.IsLinux()) then
                raise (
                    createCaptureFailure
                        "Symbolic linked skill files are not supported during snapshot capture on this platform."
                )

        let private tryResolveOpenedFilePath (stream: FileStream) =
            if OperatingSystem.IsLinux() then
                let descriptorPath =
                    $"/proc/self/fd/{stream.SafeFileHandle.DangerousGetHandle().ToInt64()}"

                let target = File.ResolveLinkTarget(descriptorPath, true)

                if isNull target then
                    ValueNone
                else
                    ValueSome(Path.GetFullPath target.FullName)
            else
                ValueNone

        let private verifyOpenedFileMatches (state: SnapshotCaptureEntryState) (stream: FileStream) =
            let openedPath =
                if state.IsSymbolicLink then
                    match tryResolveOpenedFilePath stream with
                    | ValueSome value -> value
                    | ValueNone ->
                        raise (
                            createCaptureFailure
                                "Skill snapshot capture could not verify the opened symbolic link target."
                        )
                else
                    state.ResolvedPath

            if not (pathsEqual state.ResolvedPath openedPath) then
                raise (
                    createCaptureFailure "Skill snapshot capture detected a path change before reading a source file."
                )

            if stream.Length <> state.Length then
                raise (
                    createCaptureFailure "Skill snapshot capture detected a length change before reading a source file."
                )

        let private verifyPathStateUnchanged
            (canonicalRoot: string)
            (entryPath: string)
            (relativePath: string)
            (initialState: SnapshotCaptureEntryState)
            =
            let currentState = getEntryState canonicalRoot entryPath relativePath

            if currentState.Kind <> initialState.Kind then
                raise (
                    createCaptureFailure "Skill snapshot capture detected a type change while reading a source file."
                )

            if currentState.IsSymbolicLink <> initialState.IsSymbolicLink then
                raise (
                    createCaptureFailure
                        "Skill snapshot capture detected a symbolic link change while reading a source file."
                )

            if not (pathsEqual currentState.ResolvedPath initialState.ResolvedPath) then
                raise (
                    createCaptureFailure "Skill snapshot capture detected a path change while reading a source file."
                )

            if currentState.Length <> initialState.Length then
                raise (
                    createCaptureFailure "Skill snapshot capture detected a length change while reading a source file."
                )

            if currentState.LastWriteTimeUtc <> initialState.LastWriteTimeUtc then
                raise (
                    createCaptureFailure
                        "Skill snapshot capture detected a last-write change while reading a source file."
                )

        let internal readTextFileWithHooks
            (canonicalRoot: string)
            (entryPath: string)
            (relativePath: string)
            (hooks: SnapshotCaptureHooks)
            =
            let initialState = getEntryState canonicalRoot entryPath relativePath

            if initialState.Kind <> SnapshotCaptureEntryKind.File then
                raise (createCaptureFailure "Skill snapshot capture can only read regular files.")

            ensureSupportedSymlinkState initialState

            match hooks.BeforeOpen with
            | ValueSome callback -> callback.Invoke(initialState)
            | ValueNone -> ()

            use stream =
                new FileStream(entryPath, FileMode.Open, FileAccess.Read, FileShare.Read)

            verifyOpenedFileMatches initialState stream

            use reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true)
            let content = reader.ReadToEnd()
            verifyPathStateUnchanged canonicalRoot entryPath relativePath initialState
            content

        let internal readTextFile (canonicalRoot: string) (entryPath: string) (relativePath: string) =
            readTextFileWithHooks canonicalRoot entryPath relativePath { BeforeOpen = ValueNone }

    type internal MafPreparedFileSkills(snapshots: IReadOnlyDictionary<string, MafFileSkillSnapshot>) =
        member _.Snapshots = snapshots

        member _.GetSnapshot(canonicalRoot: string) =
            let mutable snapshot = Unchecked.defaultof<MafFileSkillSnapshot>

            if snapshots.TryGetValue(canonicalRoot, &snapshot) then
                snapshot
            else
                invalidOp "The requested file skill root was not part of the prepared snapshot set."

        interface IDisposable with
            member _.Dispose() =
                for snapshot in snapshots.Values do
                    if not (isNull (box snapshot)) then
                        (snapshot :> IDisposable).Dispose()

    and MafFileSkillSnapshot
        (
            canonicalRoot: string,
            skillMarkdown: string,
            fileContents: IReadOnlyDictionary<string, string>,
            resolvedPaths: IReadOnlyDictionary<string, string>,
            resourceNames: IReadOnlySet<string>,
            scriptNames: IReadOnlySet<string>,
            manifestFingerprint: string,
            createMaterializedRoot: Func<string>,
            deleteMaterializedRoot: Action<string>
        ) =
        let gate = obj ()
        let mutable materializedRoot = null
        let mutable cleanupResult = SnapshotCleanupResult.NotMaterialized

        new
            (
                canonicalRoot: string,
                skillMarkdown: string,
                fileContents: IReadOnlyDictionary<string, string>,
                resolvedPaths: IReadOnlyDictionary<string, string>,
                resourceNames: IReadOnlySet<string>,
                scriptNames: IReadOnlySet<string>,
                manifestFingerprint: string
            ) =
            new MafFileSkillSnapshot(
                canonicalRoot,
                skillMarkdown,
                fileContents,
                resolvedPaths,
                resourceNames,
                scriptNames,
                manifestFingerprint,
                Func<string>(SnapshotMaterialization.createPrivateRoot),
                Action<string>(SnapshotMaterialization.deleteRoot)
            )

        member _.CanonicalRoot = canonicalRoot
        member _.SkillMarkdown = skillMarkdown
        member _.ResourceNames = resourceNames
        member _.ScriptNames = scriptNames
        member _.ManifestFingerprint = manifestFingerprint
        member internal _.CleanupResult = cleanupResult

        member _.TryReadResource(name: string) =
            let mutable content = null

            if resourceNames.Contains name && fileContents.TryGetValue(name, &content) then
                ValueSome content
            else
                ValueNone

        member internal _.TryGetOriginalResolvedPath(name: string) =
            let mutable resolvedPath = null

            if resolvedPaths.TryGetValue(name, &resolvedPath) then
                ValueSome resolvedPath
            else
                ValueNone

        member private _.WriteSnapshotContents(root: string) =
            SnapshotMaterialization.writePrivateTextFile (Path.Combine(root, "SKILL.md")) skillMarkdown

            for KeyValue(relativePath, content) in fileContents do
                let fullPath =
                    Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))

                SnapshotMaterialization.writePrivateTextFile fullPath content

        member private this.EnsureMaterializedRoot() =
            lock gate (fun () ->
                if String.IsNullOrEmpty materializedRoot then
                    let root = createMaterializedRoot.Invoke()

                    try
                        this.WriteSnapshotContents(root)
                        materializedRoot <- root
                    with exn ->
                        try
                            deleteMaterializedRoot.Invoke(root)
                        with _ ->
                            ()

                        raise exn

                materializedRoot)

        member _.TryGetMaterializedRoot() =
            if String.IsNullOrEmpty materializedRoot then
                ValueNone
            else
                ValueSome materializedRoot

        member _.ReadFrontmatter() =
            let metadata = AdditionalPropertiesDictionary()

            let lines =
                skillMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')

            let mutable name = Path.GetFileName canonicalRoot
            let mutable description = String.Empty

            if lines.Length > 0 && StringComparer.Ordinal.Equals(lines[0].Trim(), "---") then
                let mutable index = 1
                let mutable inFrontmatter = true

                while inFrontmatter && index < lines.Length do
                    let line = lines[index].Trim()

                    if StringComparer.Ordinal.Equals(line, "---") then
                        inFrontmatter <- false
                    elif not (String.IsNullOrWhiteSpace line) then
                        let separatorIndex = line.IndexOf(':')

                        if separatorIndex > 0 then
                            let key = line.Substring(0, separatorIndex).Trim()
                            let value = line.Substring(separatorIndex + 1).Trim().Trim('"')

                            match key with
                            | "name" when not (String.IsNullOrWhiteSpace value) -> name <- value
                            | "description" -> description <- value
                            | _ when not (String.IsNullOrWhiteSpace key) -> metadata[key] <- value
                            | _ -> ()

                    index <- index + 1

            let frontmatter = AgentSkillFrontmatter(name, description, null)

            if metadata.Count > 0 then
                frontmatter.Metadata <- metadata

            frontmatter

        member this.TryResolveScriptLocation(name: string) =
            if not (scriptNames.Contains name) then
                ValueNone
            else
                let root = this.EnsureMaterializedRoot()
                let path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar))
                ValueSome(struct (root, path))

        interface IDisposable with
            member _.Dispose() =
                lock gate (fun () ->
                    if not (String.IsNullOrEmpty materializedRoot) then
                        try
                            deleteMaterializedRoot.Invoke(materializedRoot)
                            cleanupResult <- SnapshotCleanupResult.Deleted
                        with exn ->
                            cleanupResult <- SnapshotCleanupResult.Failed(exn.GetType().Name)

                        materializedRoot <- null)

    let private normalizeInlineSkillName (reference: SkillReference) =
        let normalized =
            Regex.Replace(reference.Id.Value, "[._]+", "-")
            |> fun value -> Regex.Replace(value, "-+", "-")
            |> fun value -> value.Trim('-')

        let versionSuffix =
            $"v{reference.Version.Value.Major}-{reference.Version.Value.Minor}-{reference.Version.Value.Build}"

        let name =
            if String.IsNullOrWhiteSpace normalized then
                versionSuffix
            else
                $"{normalized}-{versionSuffix}"

        try
            let mutable validationError = null
            AgentSkillFrontmatter.ValidateName(name, &validationError) |> ignore
            name
        with _ ->
            raise (
                InvalidOperationException(
                    $"Inline skill '{reference.Id.Value}@{reference.Version}' could not be mapped to a valid MAF skill name."
                )
            )

    let private toAdditionalProperties (metadata: IReadOnlyDictionary<string, string>) =
        let properties = AdditionalPropertiesDictionary()

        if not (isNull metadata) then
            for KeyValue(key, value) in metadata do
                properties[key] <- value

        properties

    let private createInlineFrontmatter (reference: SkillReference) =
        let description =
            if String.IsNullOrWhiteSpace reference.Description then
                $"Circuit skill {reference.Id.Value} version {reference.Version}."
            else
                reference.Description

        let frontmatter =
            AgentSkillFrontmatter(normalizeInlineSkillName reference, description, null)

        frontmatter.Metadata <- toAdditionalProperties reference.Metadata
        frontmatter

    let private createResourceContext (runContext: RunContext) =
        SkillResourceContext(
            runContext.RunId,
            runContext.Options.TenantId,
            runContext.Options.UserId,
            runContext.Options.Services
        )

    let private createSanitizedScriptFailure (innerException: exn) =
        InvalidOperationException("Skill script execution failed.", innerException)

    let private createSanitizedResourceFailure (innerException: exn) =
        InvalidOperationException("Skill resource access failed.", innerException)

    let private tryGetConfiguredRunner (runtimeOptions: MafRuntimeOptions) =
        match runtimeOptions.SkillScriptRunner with
        | ValueSome runner -> runner
        | ValueNone -> invalidOp "Skill scripts are disabled because no Circuit skill script runner is configured."

    let private createSkillScriptDescriptor (name: string) (description: string) =
        if String.IsNullOrWhiteSpace description then
            SkillScriptDescriptor.Create(name)
        else
            SkillScriptDescriptor.Create(name, description)

    let private createSnapshotBoundSkillReference (reference: SkillReference) (snapshotRoot: string) =
        SkillReference.Create(
            reference.Id.Value,
            reference.Version.ToString(),
            reference.Description,
            SkillSource.CreateFile(snapshotRoot),
            reference.Metadata
        )

    let private assertRequestDoesNotExposeOriginalPaths (request: SkillScriptRequest) (originalPaths: seq<string>) =
        let pathComparison =
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        let unsafePaths =
            originalPaths
            |> Seq.filter (String.IsNullOrWhiteSpace >> not)
            |> Seq.distinct
            |> Seq.toArray

        let containsOriginalPath (value: string) =
            not (String.IsNullOrEmpty value)
            && unsafePaths
               |> Array.exists (fun originalPath -> value.IndexOf(originalPath, pathComparison) >= 0)

        let requestStrings =
            seq {
                match request.SkillRoot with
                | ValueSome value -> yield value
                | ValueNone -> ()

                match request.ScriptPath with
                | ValueSome value -> yield value
                | ValueNone -> ()

                yield request.Skill.Id.Value
                yield request.Skill.Version.ToString()
                yield request.Skill.Description
                yield request.Skill.Source.Instructions

                for fileRoot in request.Skill.Source.FileRoots do
                    yield fileRoot

                for resource in request.Skill.Source.Resources do
                    yield resource.Name
                    yield resource.Description

                    if not resource.IsDynamic then
                        match resource.StaticValue with
                        | :? string as text -> yield text
                        | _ -> ()

                for script in request.Skill.Source.Scripts do
                    yield script.Name
                    yield script.Description

                    for entry in script.Metadata do
                        yield entry.Key
                        yield entry.Value

                for entry in request.Skill.Metadata do
                    yield entry.Key
                    yield entry.Value

                yield request.Script.Name
                yield request.Script.Description

                for entry in request.Script.Metadata do
                    yield entry.Key
                    yield entry.Value
            }

        if requestStrings |> Seq.exists containsOriginalPath then
            invalidOp "Skill script request exposed an original skill path."

    let private createInlineScriptDelegate
        (runtimeOptions: MafRuntimeOptions)
        (runContext: RunContext)
        (reference: SkillReference)
        (script: SkillScriptDescriptor)
        =
        let runner = tryGetConfiguredRunner runtimeOptions

        Func<Nullable<JsonElement>, CancellationToken, Task<obj>>(fun arguments cancellationToken ->
            task {
                let request =
                    SkillScriptRequest(
                        runContext.RunId,
                        runContext.Options.TenantId,
                        runContext.Options.UserId,
                        runContext.Options.Services,
                        reference,
                        script,
                        arguments,
                        ValueNone,
                        ValueNone
                    )

                try
                    let! result = runner.RunAsync(request, cancellationToken)

                    if isNull (box result) then
                        raise (
                            createSanitizedScriptFailure (
                                InvalidOperationException("The skill script runner returned null.")
                            )
                        )

                    return result.Output
                with
                | :? OperationCanceledException -> return raise (OperationCanceledException(cancellationToken))
                | ex -> return raise (createSanitizedScriptFailure ex)
            })

    let private toSkillRelativePath (rootRelativePath: string) (entryName: string) =
        if String.IsNullOrEmpty rootRelativePath then
            entryName
        else
            rootRelativePath + "/" + entryName

    let private defaultFileScriptSchema =
        let scriptType =
            typeof<AgentSkill>.Assembly.GetType("Microsoft.Agents.AI.AgentFileSkillScript", true)

        let schemaField =
            scriptType.GetField(
                "s_defaultSchema",
                Reflection.BindingFlags.Static
                ||| Reflection.BindingFlags.NonPublic
                ||| Reflection.BindingFlags.Public
            )

        if isNull schemaField then
            Nullable()
        else
            Nullable(schemaField.GetValue(null) :?> JsonElement)

    let private buildAvailableBlock (name: string) (entries: string[]) =
        if entries.Length = 0 then
            $"<{name} />"
        else
            let items =
                entries
                |> Array.map (fun entry -> $"  <item>{entry}</item>")
                |> String.concat "\n"

            $"<{name}>\n{items}\n</{name}>"

    let private buildSafeFileSkillContent (skillMarkdown: string) (resources: string[]) (scripts: string[]) =
        String.concat
            "\n\n"
            [ skillMarkdown.TrimEnd()
              buildAvailableBlock "available_resources" resources
              buildAvailableBlock "available_scripts" scripts ]

    let private createSha256 (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexStringLower

    let private computeManifestFingerprint
        (canonicalRoot: string)
        (skillMarkdown: string)
        (fileContents: IReadOnlyDictionary<string, string>)
        (resourceNames: IReadOnlySet<string>)
        (scriptNames: IReadOnlySet<string>)
        =
        let builder = StringBuilder()

        let appendPart (name: string) (value: string) =
            builder.Append(name).Append('=').Append(value).Append('\n') |> ignore

        appendPart "canonicalRoot" canonicalRoot
        appendPart "path" "SKILL.md"
        appendPart "kind" "skill"
        appendPart "length" (skillMarkdown.Length.ToString())
        appendPart "sha256" (createSha256 skillMarkdown)

        let filePaths =
            fileContents.Keys
            |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
            |> Seq.toArray

        for relativePath in filePaths do
            let content = fileContents[relativePath]
            appendPart "path" relativePath
            appendPart "resource" (resourceNames.Contains(relativePath).ToString())
            appendPart "script" (scriptNames.Contains(relativePath).ToString())
            appendPart "length" (content.Length.ToString())
            appendPart "sha256" (createSha256 content)

        builder.ToString()
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower

    let private discoverFileSkillSnapshot (skillRoot: string) =
        let canonicalRoot = SkillPathSecurity.validateSkillRootPath skillRoot
        let options = AgentFileSkillsSourceOptions()

        let resourceExtensionValues =
            if isNull options.AllowedResourceExtensions then
                seq [ ".md"; ".txt"; ".json"; ".yaml"; ".yml"; ".csv"; ".xml"; ".html"; ".htm" ]
            else
                options.AllowedResourceExtensions

        let scriptExtensionValues =
            if isNull options.AllowedScriptExtensions then
                seq [ ".py"; ".js"; ".ts"; ".sh"; ".ps1"; ".cmd"; ".bat" ]
            else
                options.AllowedScriptExtensions

        let resourceExtensions =
            HashSet<string>(resourceExtensionValues, StringComparer.OrdinalIgnoreCase)

        let scriptExtensions =
            HashSet<string>(scriptExtensionValues, StringComparer.OrdinalIgnoreCase)

        let visitedDirectories = HashSet<string>(SkillPathSecurity.PathComparer)
        let fileContents = Dictionary<string, string>(StringComparer.Ordinal)
        let resolvedPaths = Dictionary<string, string>(StringComparer.Ordinal)
        let resourceNames = HashSet<string>(StringComparer.Ordinal)
        let scriptNames = HashSet<string>(StringComparer.Ordinal)

        let rec scanDirectory (rootRelativePath: string) =
            let resolvedDirectory =
                if String.IsNullOrEmpty rootRelativePath then
                    canonicalRoot
                else
                    SkillPathSecurity.resolveExistingRelativeDirectoryWithinRoot canonicalRoot rootRelativePath

            if visitedDirectories.Add resolvedDirectory then
                for entryPath in Directory.EnumerateFileSystemEntries(resolvedDirectory) do
                    let entryName = Path.GetFileName entryPath
                    let relativePath = toSkillRelativePath rootRelativePath entryName
                    let attributes = File.GetAttributes entryPath
                    let isDirectory = attributes.HasFlag(FileAttributes.Directory)

                    if isDirectory then
                        if attributes.HasFlag(FileAttributes.ReparsePoint) then
                            invalidOp "Symbolic linked skill directories are not supported during snapshot capture."

                        SkillPathSecurity.resolveExistingRelativeDirectoryWithinRoot canonicalRoot relativePath
                        |> ignore

                        scanDirectory relativePath
                    else
                        let resolvedFile =
                            SkillPathSecurity.resolveExistingRelativeFileWithinRoot canonicalRoot relativePath

                        if not (StringComparer.Ordinal.Equals(relativePath, "SKILL.md")) then
                            let extension = Path.GetExtension resolvedFile
                            let isResource = resourceExtensions.Contains extension
                            let isScript = scriptExtensions.Contains extension

                            if isResource || isScript then
                                let content = SnapshotCapture.readTextFile canonicalRoot entryPath relativePath
                                fileContents[relativePath] <- content
                                resolvedPaths[relativePath] <- resolvedFile

                                if isResource then
                                    resourceNames.Add relativePath |> ignore

                                if isScript then
                                    scriptNames.Add relativePath |> ignore

        let skillMarkdown =
            SnapshotCapture.readTextFile canonicalRoot (Path.Combine(canonicalRoot, "SKILL.md")) "SKILL.md"

        resolvedPaths["SKILL.md"] <- SkillPathSecurity.resolveExistingRelativeFileWithinRoot canonicalRoot "SKILL.md"

        scanDirectory String.Empty

        let frozenFileContents =
            if fileContents.Count = 0 then
                Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal)
                :> IReadOnlyDictionary<string, string>
            else
                fileContents.ToFrozenDictionary(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

        let frozenResolvedPaths =
            if resolvedPaths.Count = 0 then
                Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal)
                :> IReadOnlyDictionary<string, string>
            else
                resolvedPaths.ToFrozenDictionary(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

        let frozenResourceNames =
            if resourceNames.Count = 0 then
                HashSet<string>(StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal) :> IReadOnlySet<string>
            else
                resourceNames.ToFrozenSet(StringComparer.Ordinal) :> IReadOnlySet<string>

        let frozenScriptNames =
            if scriptNames.Count = 0 then
                HashSet<string>(StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal) :> IReadOnlySet<string>
            else
                scriptNames.ToFrozenSet(StringComparer.Ordinal) :> IReadOnlySet<string>

        new MafFileSkillSnapshot(
            canonicalRoot,
            skillMarkdown,
            frozenFileContents,
            frozenResolvedPaths,
            frozenResourceNames,
            frozenScriptNames,
            computeManifestFingerprint
                canonicalRoot
                skillMarkdown
                frozenFileContents
                frozenResourceNames
                frozenScriptNames
        )

    let private tryGetPreparedFileSkills (skill: ResolvedSkill) =
        skill.TryGetProperty<MafPreparedFileSkills>(MafSkillAdapterProperties.FileSkills)

    let internal prepareResolvedSkill (skill: ResolvedSkill) =
        if isNull (box skill) then
            nullArg "skill"

        match skill.Reference.Source.Kind, tryGetPreparedFileSkills skill with
        | SkillSourceKind.File, ValueNone ->
            let snapshots =
                Dictionary<string, MafFileSkillSnapshot>(SkillPathSecurity.PathComparer)

            for fileRoot in skill.Reference.Source.FileRoots do
                let snapshot = discoverFileSkillSnapshot fileRoot

                if snapshots.ContainsKey(snapshot.CanonicalRoot) then
                    invalidOp
                        $"Multiple file skill roots normalized to the same filesystem identity '{snapshot.CanonicalRoot}'."

                snapshots[snapshot.CanonicalRoot] <- snapshot

            let prepared =
                new MafPreparedFileSkills(
                    snapshots.ToFrozenDictionary(SkillPathSecurity.PathComparer)
                    :> IReadOnlyDictionary<string, MafFileSkillSnapshot>
                )

            let properties = ResizeArray<KeyValuePair<string, obj>>()

            for entry in skill.Properties do
                properties.Add(KeyValuePair(entry.Key, entry.Value))

            properties.Add(KeyValuePair(MafSkillAdapterProperties.FileSkills, box prepared))
            ResolvedSkill.Create(skill.Reference, properties)
        | _ -> skill

    let private runPreparedFileScriptAsync
        (runtimeOptions: MafRuntimeOptions)
        (runContext: RunContext)
        (reference: SkillReference)
        (snapshot: MafFileSkillSnapshot)
        (scriptName: string)
        (scriptDescription: string)
        (arguments: Nullable<JsonElement>)
        (serviceProvider: IServiceProvider)
        (cancellationToken: CancellationToken)
        =
        task {
            let runner = tryGetConfiguredRunner runtimeOptions

            try
                match snapshot.TryResolveScriptLocation scriptName with
                | ValueNone ->
                    return
                        raise (
                            createSanitizedScriptFailure (
                                FileNotFoundException("The requested script was not found in the prepared snapshot.")
                            )
                        )
                | ValueSome(struct (skillRoot, scriptPath)) ->
                    let request =
                        SkillScriptRequest(
                            runContext.RunId,
                            runContext.Options.TenantId,
                            runContext.Options.UserId,
                            (if isNull serviceProvider then
                                 runContext.Options.Services
                             else
                                 serviceProvider),
                            createSnapshotBoundSkillReference reference skillRoot,
                            createSkillScriptDescriptor scriptName scriptDescription,
                            arguments,
                            ValueSome skillRoot,
                            ValueSome scriptPath
                        )

                    let originalPaths =
                        seq {
                            yield snapshot.CanonicalRoot

                            for fileRoot in reference.Source.FileRoots do
                                yield fileRoot

                            match snapshot.TryGetOriginalResolvedPath scriptName with
                            | ValueSome value -> yield value
                            | ValueNone -> ()
                        }

                    assertRequestDoesNotExposeOriginalPaths request originalPaths

                    let! result = runner.RunAsync(request, cancellationToken)

                    if isNull (box result) then
                        return
                            raise (
                                createSanitizedScriptFailure (
                                    InvalidOperationException("The skill script runner returned null.")
                                )
                            )
                    else
                        return result.Output
            with
            | :? OperationCanceledException -> return raise (OperationCanceledException(cancellationToken))
            | ex -> return raise (createSanitizedScriptFailure ex)
        }

    type private SafeFileSkillResource(snapshot: MafFileSkillSnapshot, name: string, description: string) =
        inherit AgentSkillResource(name, description)

        override _.ReadAsync(_serviceProvider, cancellationToken) =
            task {
                try
                    cancellationToken.ThrowIfCancellationRequested()

                    match snapshot.TryReadResource name with
                    | ValueSome content -> return box content
                    | ValueNone ->
                        return
                            raise (
                                createSanitizedResourceFailure (
                                    FileNotFoundException(
                                        "The requested resource was not found in the prepared snapshot."
                                    )
                                )
                            )
                with
                | :? OperationCanceledException -> return raise (OperationCanceledException(cancellationToken))
                | ex -> return raise (createSanitizedResourceFailure ex)
            }

    type private SafeFileSkillScript
        (
            runtimeOptions: MafRuntimeOptions,
            runContext: RunContext,
            reference: SkillReference,
            snapshot: MafFileSkillSnapshot,
            name: string,
            description: string,
            parametersSchema: Nullable<JsonElement>
        ) =
        inherit AgentSkillScript(name, description)

        override _.ParametersSchema = parametersSchema

        override _.RunAsync(_skill, arguments, serviceProvider, cancellationToken) =
            runPreparedFileScriptAsync
                runtimeOptions
                runContext
                reference
                snapshot
                name
                description
                arguments
                serviceProvider
                cancellationToken

    type private SafeFileSkill
        (
            frontmatter: AgentSkillFrontmatter,
            snapshot: MafFileSkillSnapshot,
            content: string,
            resources: IReadOnlyDictionary<string, string>,
            scripts: IReadOnlyDictionary<string, Nullable<JsonElement>>,
            runtimeOptions: MafRuntimeOptions,
            runContext: RunContext,
            reference: SkillReference
        ) =
        inherit AgentSkill()

        override _.Frontmatter = frontmatter

        override _.GetContentAsync(_cancellationToken) = ValueTask<string>(content)

        override _.GetResourceAsync(name, _cancellationToken) =
            let mutable description = null

            if resources.TryGetValue(name, &description) then
                ValueTask<AgentSkillResource>(SafeFileSkillResource(snapshot, name, description) :> AgentSkillResource)
            else
                ValueTask<AgentSkillResource>(null :> AgentSkillResource)

        override _.GetScriptAsync(name, _cancellationToken) =
            let mutable parametersSchema = Nullable<JsonElement>()

            if scripts.TryGetValue(name, &parametersSchema) then
                ValueTask<AgentSkillScript>(
                    SafeFileSkillScript(
                        runtimeOptions,
                        runContext,
                        reference,
                        snapshot,
                        name,
                        String.Empty,
                        parametersSchema
                    )
                    :> AgentSkillScript
                )
            else
                ValueTask<AgentSkillScript>(null :> AgentSkillScript)

    let private createSafeFileSkillFromSnapshot
        (runtimeOptions: MafRuntimeOptions)
        (runContext: RunContext)
        (reference: SkillReference)
        (snapshot: MafFileSkillSnapshot)
        =
        let resourceNames =
            snapshot.ResourceNames
            |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
            |> Seq.toArray

        let scriptNames =
            if runtimeOptions.SkillScriptRunner.IsSome then
                snapshot.ScriptNames
                |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
                |> Seq.toArray
            else
                Array.empty<string>

        let resourceMap = Dictionary<string, string>(StringComparer.Ordinal)
        let scriptMap = Dictionary<string, Nullable<JsonElement>>(StringComparer.Ordinal)

        for resourceName in resourceNames do
            resourceMap[resourceName] <- String.Empty

        for scriptName in scriptNames do
            scriptMap[scriptName] <- defaultFileScriptSchema

        SafeFileSkill(
            snapshot.ReadFrontmatter(),
            snapshot,
            buildSafeFileSkillContent snapshot.SkillMarkdown resourceNames scriptNames,
            resourceMap,
            scriptMap,
            runtimeOptions,
            runContext,
            reference
        )
        :> AgentSkill

    let internal loadFileSkill
        (runtimeOptions: MafRuntimeOptions)
        (runContext: RunContext)
        (reference: SkillReference)
        (skillRoot: string)
        =
        createSafeFileSkillFromSnapshot runtimeOptions runContext reference (discoverFileSkillSnapshot skillRoot)

    let private createInlineSkill (runtimeOptions: MafRuntimeOptions) (runContext: RunContext) (skill: ResolvedSkill) =
        let reference = skill.Reference
        let source = reference.Source

        let inlineSkill =
            AgentInlineSkill(createInlineFrontmatter reference, source.Instructions, null, null)

        let resourceContext = createResourceContext runContext

        for resource in source.Resources do
            if resource.IsDynamic then
                let resourceDelegate =
                    Func<CancellationToken, Task<obj>>(fun cancellationToken ->
                        resource.ReadAsync(resourceContext, cancellationToken))

                inlineSkill.AddResource(resource.Name, resourceDelegate, resource.Description, null)
                |> ignore
            else
                inlineSkill.AddResource(resource.Name, resource.StaticValue, resource.Description)
                |> ignore

        if runtimeOptions.SkillScriptRunner.IsSome then
            for script in source.Scripts do
                inlineSkill.AddScript(
                    script.Name,
                    createInlineScriptDelegate runtimeOptions runContext reference script,
                    script.Description,
                    null
                )
                |> ignore

        inlineSkill :> AgentSkill

    let private tryGetCustomAgentSkill (skill: ResolvedSkill) =
        skill.TryGetProperty<AgentSkill>(MafSkillAdapterProperties.AgentSkill)

    let private validateCustomSkill (skill: ResolvedSkill) =
        match tryGetCustomAgentSkill skill with
        | ValueSome agentSkill -> agentSkill
        | ValueNone ->
            invalidOp
                $"Custom skill '{skill.Reference.Id.Value}@{skill.Reference.Version}' is missing the '{MafSkillAdapterProperties.AgentSkill}' MAF payload."

    let private createExpectedSkillFilter (expectedSkills: IReadOnlySet<AgentSkill>) =
        Func<AgentSkill, AgentSkillsSourceContext, bool>(fun discoveredSkill _context ->
            expectedSkills.Contains discoveredSkill)

    let private buildProvider
        (runtimeOptions: MafRuntimeOptions)
        (runContext: RunContext)
        (selectedSkills: IReadOnlyList<ResolvedSkill>)
        =
        if isNull selectedSkills || selectedSkills.Count = 0 then
            ValueNone
        else
            let builder = AgentSkillsProviderBuilder()
            let expectedSkills = HashSet<AgentSkill>(HashIdentity.Reference)
            let seenFileRoots = HashSet<string>(SkillPathSecurity.PathComparer)
            let ownedFileSkills = ResizeArray<MafPreparedFileSkills>()

            for selectedSkill in selectedSkills do
                let skill = prepareResolvedSkill selectedSkill

                match skill.Reference.Source.Kind with
                | SkillSourceKind.File ->
                    let preparedFileSkills =
                        match tryGetPreparedFileSkills skill with
                        | ValueSome preparedFileSkills -> preparedFileSkills
                        | ValueNone -> invalidOp "Prepared file skills were not attached to the resolved skill."

                    ownedFileSkills.Add preparedFileSkills

                    for fileRoot in skill.Reference.Source.FileRoots do
                        let canonicalRoot = SkillPathSecurity.validateSkillRootPath fileRoot

                        if not (seenFileRoots.Add canonicalRoot) then
                            invalidOp
                                $"Multiple skill sources resolved the same file root for skill '{skill.Reference.Id.Value}@{skill.Reference.Version}'."

                        let fileSkill =
                            createSafeFileSkillFromSnapshot
                                runtimeOptions
                                runContext
                                skill.Reference
                                (preparedFileSkills.GetSnapshot canonicalRoot)

                        expectedSkills.Add fileSkill |> ignore
                        builder.UseSkill(fileSkill) |> ignore
                | SkillSourceKind.Inline ->
                    let inlineSkill: AgentSkill = createInlineSkill runtimeOptions runContext skill
                    expectedSkills.Add inlineSkill |> ignore
                    builder.UseSkill(inlineSkill) |> ignore
                | SkillSourceKind.Custom ->
                    let customSkill: AgentSkill = validateCustomSkill skill
                    expectedSkills.Add customSkill |> ignore
                    builder.UseSkill(customSkill) |> ignore
                | _ -> invalidOp "Unknown skill source kind."

            builder.UseFilter(createExpectedSkillFilter expectedSkills) |> ignore

            let provider = builder.Build()

            ValueSome(
                new MafSkillProviderAttachment(
                    provider,
                    runtimeOptions.SkillScriptRunner.IsSome,
                    (ownedFileSkills
                     |> Seq.map (fun fileSkills -> fileSkills :> IDisposable)
                     |> Seq.toArray)
                    :> IReadOnlyList<IDisposable>
                )
            )

    let createAttachment
        (runtimeOptions: MafRuntimeOptions)
        (runContext: RunContext)
        (skills: IReadOnlyList<ResolvedSkill>)
        =
        buildProvider runtimeOptions runContext skills

    let getProviders (attachment: MafSkillProviderAttachment voption) =
        match attachment with
        | ValueSome providerAttachment ->
            let provider =
                if providerAttachment.ScriptsEnabled then
                    providerAttachment.Provider :> AIContextProvider
                else
                    MafSkillContextProvider(providerAttachment.Provider, false) :> AIContextProvider

            [| provider |] :> IReadOnlyList<AIContextProvider>
        | ValueNone -> Array.empty<AIContextProvider> :> IReadOnlyList<AIContextProvider>

    let dispose (attachment: MafSkillProviderAttachment voption) =
        match attachment with
        | ValueSome providerAttachment -> (providerAttachment :> IDisposable).Dispose()
        | ValueNone -> ()
