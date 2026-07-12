namespace Circuit.Core

open System
open System.Collections.Frozen
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks

/// Identifies how a skill's contents are supplied to the runtime.
[<RequireQualifiedAccess>]
type SkillSourceKind =
    /// Load the skill from one or more on-disk roots that each contain a <c>SKILL.md</c> entry point.
    | File = 0
    /// Embed the skill instructions, resources, and scripts directly in memory.
    | Inline = 1
    /// Delegate skill materialization to a custom runtime-specific implementation.
    | Custom = 2

module internal SkillPathSecurity =
    [<RequireQualifiedAccess>]
    type ExistingPathKind =
        | File
        | Directory

    let private separators =
        [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]

    let private pathComparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    let internal PathComparer =
        if OperatingSystem.IsWindows() then
            StringComparer.OrdinalIgnoreCase
        else
            StringComparer.Ordinal

    let private requireNonBlank name (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg name $"{name} cannot be blank."

        value

    let private isDirectorySeparator (character: char) =
        character = Path.DirectorySeparatorChar
        || character = Path.AltDirectorySeparatorChar

    let private normalizePath (path: string) =
        let fullPath = Path.GetFullPath(path)
        let root = Path.GetPathRoot(fullPath)

        if String.IsNullOrEmpty root || String.Equals(fullPath, root, pathComparison) then
            fullPath
        else
            Path.TrimEndingDirectorySeparator fullPath

    let private ensureTrailingSeparator (path: string) =
        if String.IsNullOrEmpty path || isDirectorySeparator path[path.Length - 1] then
            path
        else
            path + string Path.DirectorySeparatorChar

    let private createUnsafePathFailure (innerException: exn) =
        InvalidOperationException(
            "The requested skill path could not be safely resolved inside the configured skill root.",
            innerException
        )

    let private isContainedInRoot (root: string) (candidate: string) =
        let normalizedRoot = normalizePath root
        let normalizedCandidate = normalizePath candidate

        String.Equals(normalizedRoot, normalizedCandidate, pathComparison)
        || normalizedCandidate.StartsWith(ensureTrailingSeparator normalizedRoot, pathComparison)

    let private getExistingInfo (path: string) (kind: ExistingPathKind) =
        match kind with
        | ExistingPathKind.Directory ->
            if not (Directory.Exists path) then
                raise (DirectoryNotFoundException())

            DirectoryInfo(path) :> FileSystemInfo
        | ExistingPathKind.File ->
            if not (File.Exists path) then
                raise (FileNotFoundException())

            FileInfo(path) :> FileSystemInfo

    let private resolveLinkTargetPath (info: FileSystemInfo) =
        let target = info.ResolveLinkTarget(true)

        if isNull target then
            raise (IOException("The symbolic link target could not be resolved."))

        let targetPath = normalizePath target.FullName

        if not (File.Exists targetPath || Directory.Exists targetPath) then
            raise (IOException("The symbolic link target does not exist."))

        targetPath

    let private wrapResolution work =
        try
            work ()
        with
        | :? InvalidOperationException as ex -> raise ex
        | :? ArgumentException as ex -> raise ex
        | :? DirectoryNotFoundException as ex -> raise (createUnsafePathFailure ex)
        | :? FileNotFoundException as ex -> raise (createUnsafePathFailure ex)
        | :? PathTooLongException as ex -> raise (createUnsafePathFailure ex)
        | :? IOException as ex -> raise (createUnsafePathFailure ex)
        | :? NotSupportedException as ex -> raise (createUnsafePathFailure ex)
        | :? UnauthorizedAccessException as ex -> raise (createUnsafePathFailure ex)

    let private splitFullPath (path: string) =
        let normalizedPath = normalizePath path
        let root = Path.GetPathRoot(normalizedPath)

        if String.IsNullOrEmpty root then
            invalidArg "path" "The path must be rooted."

        let remainder = normalizedPath.Substring(root.Length)

        let segments =
            if remainder.Length = 0 then
                Array.empty<string>
            else
                remainder.Split(separators, StringSplitOptions.RemoveEmptyEntries)

        normalizePath root, segments

    let private splitSafeRelativePath (relativePath: string) =
        let normalizedRelativePath = requireNonBlank "relativePath" relativePath

        if Path.IsPathRooted normalizedRelativePath then
            raise (createUnsafePathFailure (InvalidOperationException()))

        let segments =
            normalizedRelativePath.Split(separators, StringSplitOptions.RemoveEmptyEntries)

        if segments.Length = 0 then
            raise (createUnsafePathFailure (InvalidOperationException()))

        for segment in segments do
            if
                StringComparer.Ordinal.Equals(segment, ".")
                || StringComparer.Ordinal.Equals(segment, "..")
            then
                raise (createUnsafePathFailure (InvalidOperationException()))

        segments

    let private resolveExistingPathIdentityUnsafe (path: string) (kind: ExistingPathKind) =
        let root, segments = splitFullPath path
        let mutable current = root

        if segments.Length = 0 then
            let info = getExistingInfo current kind

            if info.Attributes.HasFlag(FileAttributes.ReparsePoint) then
                resolveLinkTargetPath info |> normalizePath
            else
                normalizePath info.FullName
        else
            for index = 0 to segments.Length - 1 do
                let expectedKind =
                    if index = segments.Length - 1 then
                        kind
                    else
                        ExistingPathKind.Directory

                let info = getExistingInfo (Path.Combine(current, segments[index])) expectedKind

                current <-
                    if info.Attributes.HasFlag(FileAttributes.ReparsePoint) then
                        resolveLinkTargetPath info |> normalizePath
                    else
                        normalizePath info.FullName

            current

    let private resolveExistingRelativePathWithinRootUnsafe
        (root: string)
        (relativePath: string)
        (kind: ExistingPathKind)
        =
        let canonicalRoot =
            resolveExistingPathIdentityUnsafe root ExistingPathKind.Directory

        let segments = splitSafeRelativePath relativePath
        let mutable current = canonicalRoot

        for index = 0 to segments.Length - 1 do
            let expectedKind =
                if index = segments.Length - 1 then
                    kind
                else
                    ExistingPathKind.Directory

            let info = getExistingInfo (Path.Combine(current, segments[index])) expectedKind

            let resolvedPath =
                if info.Attributes.HasFlag(FileAttributes.ReparsePoint) then
                    resolveLinkTargetPath info |> normalizePath
                else
                    normalizePath info.FullName

            if not (isContainedInRoot canonicalRoot resolvedPath) then
                raise (createUnsafePathFailure (InvalidOperationException()))

            current <- resolvedPath

        current

    let resolveExistingRelativeFileWithinRoot (root: string) (relativePath: string) =
        wrapResolution (fun () -> resolveExistingRelativePathWithinRootUnsafe root relativePath ExistingPathKind.File)

    let resolveExistingRelativeDirectoryWithinRoot (root: string) (relativePath: string) =
        wrapResolution (fun () ->
            resolveExistingRelativePathWithinRootUnsafe root relativePath ExistingPathKind.Directory)

    let validateSkillRootPath (path: string) =
        wrapResolution (fun () ->
            let canonicalRoot =
                resolveExistingPathIdentityUnsafe (requireNonBlank "fileRoots" path) ExistingPathKind.Directory

            resolveExistingRelativeFileWithinRoot canonicalRoot "SKILL.md" |> ignore
            canonicalRoot)

module private SkillValidation =
    let private skillNameCharacters =
        Set.ofList ([ 'a' .. 'z' ] @ [ '0' .. '9' ] @ [ '.'; '_'; '-'; '/' ])

    let emptyStringDictionary =
        Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal)
        :> IReadOnlyDictionary<string, string>

    let emptyObjectDictionary =
        Dictionary<string, obj>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal)
        :> IReadOnlyDictionary<string, obj>

    let emptyStringList = Array.empty<string> :> IReadOnlyList<string>

    let requireNonBlank name (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg name $"{name} cannot be blank."

        value

    let validateOptionalDescription name (value: string) =
        if isNull value || value.Length = 0 then
            String.Empty
        elif String.IsNullOrWhiteSpace value then
            invalidArg name $"{name} cannot be blank when provided."
        else
            value

    let validateMetadataEntry name (entry: KeyValuePair<string, string>) =
        if String.IsNullOrWhiteSpace entry.Key then
            invalidArg name "Metadata keys cannot be blank."

        if entry.Key.Length > 64 then
            invalidArg name "Metadata keys must be 64 characters or fewer."

        if isNull entry.Value then
            nullArg name

        if entry.Value.Length > 256 then
            invalidArg name "Metadata values must be 256 characters or fewer."

        entry

    let copyMetadata name (entries: seq<KeyValuePair<string, string>>) =
        let dictionary = Dictionary<string, string>(StringComparer.Ordinal)

        for entry in entries do
            let entry = validateMetadataEntry name entry

            if dictionary.ContainsKey entry.Key then
                invalidArg name "Duplicate metadata keys are not allowed."

            dictionary.Add(entry.Key, entry.Value)

        if dictionary.Count = 0 then
            emptyStringDictionary
        else
            dictionary.ToFrozenDictionary(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

    let validatePropertyKey (entry: KeyValuePair<string, obj>) =
        if String.IsNullOrWhiteSpace entry.Key then
            invalidArg "properties" "Property keys cannot be blank."

        if entry.Key.Length > 128 then
            invalidArg "properties" "Property keys must be 128 characters or fewer."

        if isNull entry.Value then
            nullArg "properties"

        entry

    let copyProperties (entries: seq<KeyValuePair<string, obj>>) =
        let dictionary = Dictionary<string, obj>(StringComparer.Ordinal)

        for entry in entries do
            let entry = validatePropertyKey entry

            if dictionary.ContainsKey entry.Key then
                invalidArg "properties" "Duplicate property keys are not allowed."

            dictionary.Add(entry.Key, entry.Value)

        if dictionary.Count = 0 then
            emptyObjectDictionary
        else
            dictionary.ToFrozenDictionary(StringComparer.Ordinal) :> IReadOnlyDictionary<string, obj>

    let validateSkillResourceName (value: string) =
        let normalized = requireNonBlank "name" value

        if normalized.Length > 128 then
            invalidArg "name" "Skill resource names must be 128 characters or fewer."

        if
            normalized
            |> Seq.exists (fun character -> not (skillNameCharacters.Contains character))
        then
            invalidArg "name" "Skill resource names must contain only lowercase letters, digits, '.', '_', '-', or '/'."

        normalized

    let validateSkillScriptName (value: string) =
        let normalized = requireNonBlank "name" value

        if normalized.Length > 128 then
            invalidArg "name" "Skill script names must be 128 characters or fewer."

        if
            normalized
            |> Seq.exists (fun character -> not (skillNameCharacters.Contains character))
        then
            invalidArg "name" "Skill script names must contain only lowercase letters, digits, '.', '_', '-', or '/'."

        normalized

    let canonicalizeSkillRoot (path: string) =
        try
            SkillPathSecurity.validateSkillRootPath path
        with _ ->
            invalidArg "fileRoots" "File skill roots must point to existing directories with a safe SKILL.md file."

    let copyCanonicalRoots (roots: seq<string>) =
        let snapshot = roots |> Seq.toArray

        if snapshot.Length = 0 then
            invalidArg "fileRoots" "At least one file skill root is required."

        let seen = HashSet<string>(SkillPathSecurity.PathComparer)
        let canonicalRoots = ResizeArray<string>()

        for root in snapshot do
            let canonicalRoot = canonicalizeSkillRoot root

            if not (seen.Add canonicalRoot) then
                invalidArg "fileRoots" "Duplicate file skill roots are not allowed."

            canonicalRoots.Add canonicalRoot

        Array.AsReadOnly(canonicalRoots.ToArray()) :> IReadOnlyList<string>


/// Provides the ambient context available while reading a dynamic skill resource.
[<Sealed>]
type SkillResourceContext
    internal (runId: RunId, tenantId: string voption, userId: string voption, services: IServiceProvider) =
    do
        if isNull services then
            nullArg "services"

    /// Gets the owning run identifier.
    member _.RunId = runId

    /// Gets the tenant identifier for the run, if any.
    member _.TenantId = tenantId

    /// Gets the user identifier for the run, if any.
    member _.UserId = userId

    /// Gets the ambient service provider.
    member _.Services = services

/// Describes a named resource exposed to a skill.
/// <remarks>
/// Dynamic resources execute inside the current process with access to ambient services. Treat them like code, not trusted data.
/// </remarks>
[<Sealed>]
type SkillResource
    internal
    (
        name: string,
        description: string,
        staticValue: obj voption,
        dynamicReader: Func<SkillResourceContext, CancellationToken, Task<obj>> voption
    ) =
    do
        SkillValidation.validateSkillResourceName name |> ignore
        SkillValidation.validateOptionalDescription "description" description |> ignore

        match staticValue, dynamicReader with
        | ValueSome _, ValueSome _ -> invalidArg "dynamicReader" "Skill resources cannot be both static and dynamic."
        | ValueNone, ValueNone -> invalidArg "staticValue" "Skill resources must be either static or dynamic."
        | ValueSome value, ValueNone when isNull value -> nullArg "staticValue"
        | ValueNone, ValueSome reader when isNull reader -> nullArg "dynamicReader"
        | _ -> ()

    /// Gets the resource name referenced by the skill.
    member _.Name = name

    /// Gets the human-readable resource description.
    member _.Description = description

    /// Gets whether the resource is computed on demand.
    member _.IsDynamic = dynamicReader.IsSome

    /// Gets the static resource value when the resource is not dynamic.
    /// <remarks>This property is ignored during JSON serialization.</remarks>
    [<JsonIgnore>]
    member _.StaticValue =
        match staticValue with
        | ValueSome value -> value
        | ValueNone -> null

    /// Gets the dynamic resource reader when the resource is computed on demand.
    /// <remarks>This property is ignored during JSON serialization.</remarks>
    [<JsonIgnore>]
    member _.DynamicReader =
        match dynamicReader with
        | ValueSome value -> value
        | ValueNone -> null

    member internal _.ReadAsync(context: SkillResourceContext, cancellationToken: CancellationToken) =
        match dynamicReader, staticValue with
        | ValueSome reader, _ -> reader.Invoke(context, cancellationToken)
        | ValueNone, ValueSome value -> Task.FromResult value
        | _ -> invalidOp "The skill resource is not configured correctly."

    /// Creates a static skill resource.
    static member Create(name: string, value: obj, description: string) =
        SkillResource(
            SkillValidation.validateSkillResourceName name,
            SkillValidation.validateOptionalDescription "description" description,
            ValueSome value,
            ValueNone
        )

    /// Creates a static skill resource without a description.
    static member Create(name: string, value: obj) =
        SkillResource.Create(name, value, String.Empty)

    /// Creates a dynamic skill resource.
    /// <remarks>The delegate may observe cancellation and may throw.</remarks>
    static member CreateDynamic
        (name: string, readAsync: Func<SkillResourceContext, CancellationToken, Task<obj>>, description: string)
        =
        SkillResource(
            SkillValidation.validateSkillResourceName name,
            SkillValidation.validateOptionalDescription "description" description,
            ValueNone,
            ValueSome readAsync
        )

    /// Creates a dynamic skill resource without a description.
    static member CreateDynamic(name: string, readAsync: Func<SkillResourceContext, CancellationToken, Task<obj>>) =
        SkillResource.CreateDynamic(name, readAsync, String.Empty)

/// Describes a named script entry point exposed by a skill.
[<Sealed>]
type SkillScriptDescriptor internal (name: string, description: string, metadata: IReadOnlyDictionary<string, string>) =
    do
        if isNull metadata then
            nullArg "metadata"

        SkillValidation.validateSkillScriptName name |> ignore
        SkillValidation.validateOptionalDescription "description" description |> ignore

    /// Gets the script name referenced by the skill.
    member _.Name = name

    /// Gets the human-readable script description.
    member _.Description = description

    /// Gets script metadata supplied by the skill author.
    member _.Metadata = metadata

    /// Creates a script descriptor.
    static member Create(name: string, description: string, metadata: IEnumerable<KeyValuePair<string, string>>) =
        if isNull metadata then
            nullArg "metadata"

        SkillScriptDescriptor(
            SkillValidation.validateSkillScriptName name,
            SkillValidation.validateOptionalDescription "description" description,
            SkillValidation.copyMetadata "metadata" metadata
        )

    /// Creates a script descriptor without metadata.
    static member Create(name: string, description: string) =
        SkillScriptDescriptor.Create(name, description, Seq.empty)

    /// Creates a script descriptor with only its name.
    static member Create(name: string) =
        SkillScriptDescriptor.Create(name, String.Empty, Seq.empty)

module private SkillCollections =
    let emptyResources = Array.empty<SkillResource> :> IReadOnlyList<SkillResource>

    let emptyScripts =
        Array.empty<SkillScriptDescriptor> :> IReadOnlyList<SkillScriptDescriptor>

    let copyResources (resources: seq<SkillResource>) =
        let seen = HashSet<string>(StringComparer.Ordinal)
        let snapshot = ResizeArray<SkillResource>()

        for resource in resources do
            if isNull (box resource) then
                invalidArg "resources" "Resources cannot contain null entries."

            if not (seen.Add resource.Name) then
                invalidArg "resources" "Duplicate skill resource names are not allowed."

            snapshot.Add resource

        if snapshot.Count = 0 then
            emptyResources
        else
            Array.AsReadOnly(snapshot.ToArray()) :> IReadOnlyList<SkillResource>

    let copyScripts (scripts: seq<SkillScriptDescriptor>) =
        let seen = HashSet<string>(StringComparer.Ordinal)
        let snapshot = ResizeArray<SkillScriptDescriptor>()

        for script in scripts do
            if isNull (box script) then
                invalidArg "scripts" "Scripts cannot contain null entries."

            if not (seen.Add script.Name) then
                invalidArg "scripts" "Duplicate skill script names are not allowed."

            snapshot.Add script

        if snapshot.Count = 0 then
            emptyScripts
        else
            Array.AsReadOnly(snapshot.ToArray()) :> IReadOnlyList<SkillScriptDescriptor>

/// Describes where a skill's content comes from.
/// <remarks>
/// File skills are restricted to validated roots that contain a <c>SKILL.md</c> entry point. Script execution is runtime-controlled and may be disabled entirely.
/// </remarks>
[<Sealed>]
type SkillSource
    internal
    (
        kind: SkillSourceKind,
        fileRoots: IReadOnlyList<string>,
        instructions: string,
        resources: IReadOnlyList<SkillResource>,
        scripts: IReadOnlyList<SkillScriptDescriptor>
    ) =
    do
        if isNull fileRoots then
            nullArg "fileRoots"

        if isNull resources then
            nullArg "resources"

        if isNull scripts then
            nullArg "scripts"

    /// Gets the source kind.
    member _.Kind = kind

    /// Gets the canonical file roots for file-backed skills.
    member _.FileRoots = fileRoots

    /// Gets the inline skill instructions.
    member _.Instructions = instructions

    /// Gets the inline skill resources.
    member _.Resources = resources

    /// Gets the inline skill scripts.
    member _.Scripts = scripts

    /// Creates a file-backed skill source.
    /// <exception cref="T:System.ArgumentException">Any root is missing, duplicated, unsafe, or lacks a <c>SKILL.md</c> entry point.</exception>
    static member CreateFile(fileRoots: IEnumerable<string>) =
        if isNull fileRoots then
            nullArg "fileRoots"

        SkillSource(
            SkillSourceKind.File,
            SkillValidation.copyCanonicalRoots fileRoots,
            String.Empty,
            SkillCollections.emptyResources,
            SkillCollections.emptyScripts
        )

    /// Creates a file-backed skill source from a single root.
    static member CreateFile(fileRoot: string) =
        SkillSource.CreateFile(seq { fileRoot })

    /// Creates an inline skill source.
    static member CreateInline
        (instructions: string, resources: IEnumerable<SkillResource>, scripts: IEnumerable<SkillScriptDescriptor>)
        =
        if isNull resources then
            nullArg "resources"

        if isNull scripts then
            nullArg "scripts"

        SkillSource(
            SkillSourceKind.Inline,
            SkillValidation.emptyStringList,
            SkillValidation.requireNonBlank "instructions" instructions,
            SkillCollections.copyResources resources,
            SkillCollections.copyScripts scripts
        )

    /// Creates an inline skill source without resources or scripts.
    static member CreateInline(instructions: string) =
        SkillSource.CreateInline(instructions, Seq.empty, Seq.empty)

    /// Creates a custom skill source placeholder for runtime-specific skill implementations.
    static member CreateCustom() =
        SkillSource(
            SkillSourceKind.Custom,
            SkillValidation.emptyStringList,
            String.Empty,
            SkillCollections.emptyResources,
            SkillCollections.emptyScripts
        )

/// Identifies a versioned skill and how to materialize it.
[<Sealed>]
type SkillReference
    internal
    (
        id: DefinitionId,
        version: SemanticVersion,
        description: string,
        source: SkillSource,
        metadata: IReadOnlyDictionary<string, string>
    ) =
    do
        if isNull (box source) then
            nullArg "source"

        if isNull metadata then
            nullArg "metadata"

    /// Gets the skill identifier.
    member _.Id = id

    /// Gets the skill version.
    member _.Version = version

    /// Gets the human-readable skill description.
    member _.Description = description

    /// Gets the source used to materialize the skill.
    member _.Source = source

    /// Gets arbitrary skill metadata.
    member _.Metadata = metadata

    /// Creates a skill reference.
    static member Create
        (
            id: string,
            version: string,
            description: string,
            source: SkillSource,
            metadata: IEnumerable<KeyValuePair<string, string>>
        ) =
        if isNull metadata then
            nullArg "metadata"

        SkillReference(
            DefinitionId.Create id,
            SemanticVersion.Parse version,
            (if isNull description then String.Empty else description),
            source,
            SkillValidation.copyMetadata "metadata" metadata
        )

    /// Creates a skill reference without metadata.
    static member Create(id: string, version: string, description: string, source: SkillSource) =
        SkillReference.Create(id, version, description, source, Seq.empty)

    /// Creates a custom skill reference with no inline description.
    static member Create(id: string, version: string) =
        SkillReference.Create(id, version, String.Empty, SkillSource.CreateCustom(), Seq.empty)

/// Represents a runtime-ready skill plus caller-supplied properties.
[<Sealed>]
type ResolvedSkill internal (reference: SkillReference, properties: IReadOnlyDictionary<string, obj>) =
    do
        if isNull (box reference) then
            nullArg "reference"

        if isNull properties then
            nullArg "properties"

    /// Gets the resolved skill reference.
    member _.Reference = reference

    /// Gets runtime-local properties attached to the skill.
    /// <remarks>This property is ignored during JSON serialization.</remarks>
    [<JsonIgnore>]
    member _.Properties = properties

    /// Creates a resolved skill with explicit properties.
    static member Create(reference: SkillReference, properties: IEnumerable<KeyValuePair<string, obj>>) =
        if isNull properties then
            nullArg "properties"

        ResolvedSkill(reference, SkillValidation.copyProperties properties)

    /// Creates a resolved skill without additional properties.
    static member Create(reference: SkillReference) =
        ResolvedSkill.Create(reference, Seq.empty)

    member internal _.TryGetProperty<'T>(key: string) =
        let mutable value = null

        if properties.TryGetValue(key, &value) && value :? 'T then
            ValueSome(value :?> 'T)
        else
            ValueNone

/// Provides the ambient context available while resolving skills for a run.
[<Sealed>]
type SkillResolutionContext
    internal (runId: RunId, tenantId: string voption, userId: string voption, services: IServiceProvider) =
    do
        if isNull services then
            nullArg "services"

    /// Gets the owning run identifier.
    member _.RunId = runId

    /// Gets the tenant identifier for the run, if any.
    member _.TenantId = tenantId

    /// Gets the user identifier for the run, if any.
    member _.UserId = userId

    /// Gets the ambient service provider.
    member _.Services = services

/// Describes a script invocation requested by a resolved skill.
/// <remarks>
/// File-backed skill roots and script paths are sanitized canonical locations inside approved roots; runtimes are not expected to expose original unresolved paths.
/// </remarks>
[<Sealed>]
type SkillScriptRequest
    internal
    (
        runId: RunId,
        tenantId: string voption,
        userId: string voption,
        services: IServiceProvider,
        skill: SkillReference,
        script: SkillScriptDescriptor,
        arguments: Nullable<JsonElement>,
        skillRoot: string voption,
        scriptPath: string voption
    ) =
    do
        if isNull services then
            nullArg "services"

        if isNull (box skill) then
            nullArg "skill"

        if isNull (box script) then
            nullArg "script"

    /// Gets the owning run identifier.
    member _.RunId = runId

    /// Gets the tenant identifier for the run, if any.
    member _.TenantId = tenantId

    /// Gets the user identifier for the run, if any.
    member _.UserId = userId

    /// Gets the ambient service provider.
    /// <remarks>This property is ignored during JSON serialization.</remarks>
    [<JsonIgnore>]
    member _.Services = services

    /// Gets the skill that requested the script invocation.
    member _.Skill = skill

    /// Gets the target script descriptor.
    member _.Script = script

    /// Gets the optional JSON arguments passed to the script.
    member _.Arguments = arguments

    /// Gets the canonical skill root for file-backed skills when one exists.
    member _.SkillRoot = skillRoot

    /// Gets the canonical script path for file-backed skills when one exists.
    member _.ScriptPath = scriptPath

/// Represents the output returned by a skill script runner.
[<Sealed>]
type SkillScriptResult internal (output: obj) =
    /// Gets the script output value.
    member _.Output = output

    /// Creates a script result.
    static member Create(output: obj) = SkillScriptResult(output)

/// Resolves the skills available to a run.
type ISkillResolver =
    /// Resolves skills for the supplied run context.
    abstract ResolveAsync:
        context: SkillResolutionContext * cancellationToken: CancellationToken ->
            ValueTask<IReadOnlyList<ResolvedSkill>>

/// Executes a resolved skill script request.
type ISkillScriptRunner =
    /// Executes a script request.
    /// <remarks>
    /// Script runners are trusted code. They should enforce their own process, file-system, and network isolation if needed.
    /// </remarks>
    abstract RunAsync: request: SkillScriptRequest * cancellationToken: CancellationToken -> Task<SkillScriptResult>

/// Returns a fixed skill list for every resolution request.
[<Sealed>]
type StaticSkillResolver(skills: IEnumerable<ResolvedSkill>) =
    let snapshot =
        if isNull skills then
            nullArg "skills"

        skills |> Seq.toArray

    interface ISkillResolver with
        member _.ResolveAsync(_context, _cancellationToken) =
            ValueTask<IReadOnlyList<ResolvedSkill>>(snapshot :> IReadOnlyList<ResolvedSkill>)

/// Resolves skills through a caller-supplied delegate.
[<Sealed>]
type DelegateSkillResolver
    (resolver: Func<SkillResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedSkill>>>) =
    do
        if isNull resolver then
            nullArg "resolver"

    interface ISkillResolver with
        member _.ResolveAsync(context, cancellationToken) =
            resolver.Invoke(context, cancellationToken)

module internal SkillResolution =
    let private emptySkills = Array.empty<ResolvedSkill> :> IReadOnlyList<ResolvedSkill>

    let private validateResolvedSkill (skill: ResolvedSkill) =
        if isNull (box skill) then
            invalidOp "Skill resolvers cannot return null skill entries."

        if isNull skill.Reference.Description then
            invalidOp "Resolved skill descriptions cannot be null."

    let private appendResolvedSkills
        (skills: ResizeArray<ResolvedSkill>)
        (identities: HashSet<string>)
        (resolvedSkills: IReadOnlyList<ResolvedSkill>)
        =
        if isNull resolvedSkills then
            invalidOp "Skill resolvers cannot return null skill lists."

        for skill in resolvedSkills do
            validateResolvedSkill skill

            let identity = $"{skill.Reference.Id.Value}@{skill.Reference.Version}"

            if not (identities.Add identity) then
                invalidOp $"Duplicate skill identity '{identity}' was resolved."

            skills.Add skill

    let resolveAllAsync
        (resolvers: IReadOnlyList<ISkillResolver>)
        (context: SkillResolutionContext)
        (cancellationToken: CancellationToken)
        =
        try
            if isNull resolvers then
                nullArg "resolvers"

            if isNull (box context) then
                nullArg "context"

            if resolvers.Count = 0 then
                Task.FromResult(emptySkills)
            else
                let tasks = Array.zeroCreate<Task<IReadOnlyList<ResolvedSkill>>> resolvers.Count

                for index = 0 to resolvers.Count - 1 do
                    let resolver = resolvers[index]

                    if isNull (box resolver) then
                        invalidOp "Skill resolvers cannot contain null entries."

                    tasks[index] <- resolver.ResolveAsync(context, cancellationToken).AsTask()

                (Task.WhenAll tasks)
                    .ContinueWith(
                        Func<Task<IReadOnlyList<ResolvedSkill>[]>, IReadOnlyList<ResolvedSkill>>(fun completed ->
                            let resolvedBatches = completed.GetAwaiter().GetResult()
                            let skills = ResizeArray<ResolvedSkill>()
                            let identities = HashSet<string>(StringComparer.Ordinal)

                            for resolvedSkills in resolvedBatches do
                                appendResolvedSkills skills identities resolvedSkills

                            skills.ToArray() :> IReadOnlyList<ResolvedSkill>),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default
                    )
        with ex ->
            Task.FromException<IReadOnlyList<ResolvedSkill>>(ex)
