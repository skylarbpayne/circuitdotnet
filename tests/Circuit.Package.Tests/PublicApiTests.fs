namespace Circuit.Package.Tests

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Xml.Linq
open Circuit.Core
open Circuit.MicrosoftAgentFramework
open Xunit

module PublicApiTests =
    type private PublicApiProject =
        { Name: string
          Assembly: Assembly
          Directory: string }

    let private projectRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."))

    let private shippedPath directory =
        Path.Combine(projectRoot, directory, "PublicAPI.Shipped.txt")

    let private unshippedPath directory =
        Path.Combine(projectRoot, directory, "PublicAPI.Unshipped.txt")

    let private projects =
        [| { Name = "Circuit.Core"
             Assembly = typeof<AgentDefinition>.Assembly
             Directory = "src/Circuit.Core" }
           { Name = "Circuit.FSharp"
             Assembly = Assembly.Load("Circuit.FSharp")
             Directory = "src/Circuit.FSharp" }
           { Name = "Circuit.MicrosoftAgentFramework"
             Assembly = typeof<MafRuntime>.Assembly
             Directory = "src/Circuit.MicrosoftAgentFramework" }
           { Name = "Circuit.Testing"
             Assembly = Assembly.Load("Circuit.Testing")
             Directory = "src/Circuit.Testing" }
           { Name = "Circuit"
             Assembly = Assembly.Load("Circuit")
             Directory = "src/Circuit" } |]

    let private xmlDocProjects =
        projects
        |> Array.filter (fun project ->
            project.Name = "Circuit.Core"
            || project.Name = "Circuit.FSharp"
            || project.Name = "Circuit.MicrosoftAgentFramework"
            || project.Name = "Circuit.Testing")

    let private readApiLines path =
        if File.Exists path then
            File.ReadAllLines path
            |> Array.map _.Trim()
            |> Array.filter (fun line -> not (String.IsNullOrWhiteSpace line))
            |> Array.filter (fun line -> not (line.StartsWith("#", StringComparison.Ordinal)))
        else
            Array.empty

    let private hasUnreviewedEntries path =
        readApiLines path |> Array.isEmpty |> not

    let private formatDeclaringTypeName (typeInfo: Type) = typeInfo.FullName.Replace('+', '.')

    let rec private formatTypeReference (typeInfo: Type) =
        if typeInfo.IsByRef then
            formatTypeReference (typeInfo.GetElementType()) + "@"
        elif typeInfo.IsArray then
            formatTypeReference (typeInfo.GetElementType()) + "[]"
        elif typeInfo.IsPointer then
            formatTypeReference (typeInfo.GetElementType()) + "*"
        elif typeInfo.IsGenericParameter then
            if isNull typeInfo.DeclaringMethod then
                $"`{typeInfo.GenericParameterPosition}"
            else
                $"``{typeInfo.GenericParameterPosition}"
        elif typeInfo.IsGenericType then
            let genericTypeDefinition =
                if typeInfo.IsGenericTypeDefinition then
                    typeInfo
                else
                    typeInfo.GetGenericTypeDefinition()

            let fullName = genericTypeDefinition.FullName.Replace('+', '.')

            let baseName =
                match fullName.IndexOf('`') with
                | -1 -> fullName
                | index -> fullName[.. index - 1]

            let arguments =
                typeInfo.GetGenericArguments()
                |> Array.map formatTypeReference
                |> String.concat ","

            $"{baseName}{{{arguments}}}"
        else
            typeInfo.FullName.Replace('+', '.')

    let private isCompilerGenerated (provider: ICustomAttributeProvider) =
        provider.IsDefined(typeof<CompilerGeneratedAttribute>, false)

    let private isObjectLikeMethod (methodInfo: MethodInfo) =
        match methodInfo.Name, methodInfo.GetParameters().Length with
        | "ToString", 0
        | "GetHashCode", 0
        | "Equals", 1 -> true
        | _ -> false

    let private formatConstructor (constructorInfo: ConstructorInfo) =
        let parameters =
            constructorInfo.GetParameters()
            |> Array.map (fun parameter -> formatTypeReference parameter.ParameterType)
            |> String.concat ","

        $"M:{formatDeclaringTypeName constructorInfo.DeclaringType}.#ctor({parameters})"

    let private formatXmlConstructor (constructorInfo: ConstructorInfo) =
        let parameters =
            constructorInfo.GetParameters()
            |> Array.map (fun parameter -> formatTypeReference parameter.ParameterType)
            |> String.concat ","

        if String.IsNullOrEmpty parameters then
            $"M:{formatDeclaringTypeName constructorInfo.DeclaringType}.#ctor"
        else
            $"M:{formatDeclaringTypeName constructorInfo.DeclaringType}.#ctor({parameters})"

    let private formatMethod (methodInfo: MethodInfo) =
        let parameters =
            methodInfo.GetParameters()
            |> Array.map (fun parameter -> formatTypeReference parameter.ParameterType)
            |> String.concat ","

        $"M:{formatDeclaringTypeName methodInfo.DeclaringType}.{methodInfo.Name}({parameters})~{formatTypeReference methodInfo.ReturnType}"

    let private formatXmlMethod (methodInfo: MethodInfo) =
        let genericArity =
            if methodInfo.IsGenericMethodDefinition then
                methodInfo.GetGenericArguments().Length
            else
                0

        let methodName =
            if genericArity = 0 then
                methodInfo.Name
            else
                $"{methodInfo.Name}``{genericArity}"

        let parameters =
            methodInfo.GetParameters()
            |> Array.map (fun parameter -> formatTypeReference parameter.ParameterType)
            |> String.concat ","

        if String.IsNullOrEmpty parameters then
            $"M:{formatDeclaringTypeName methodInfo.DeclaringType}.{methodName}"
        else
            $"M:{formatDeclaringTypeName methodInfo.DeclaringType}.{methodName}({parameters})"

    let private formatProperty (propertyInfo: PropertyInfo) =
        $"P:{formatDeclaringTypeName propertyInfo.DeclaringType}.{propertyInfo.Name}~{formatTypeReference propertyInfo.PropertyType}"

    let private formatXmlProperty (propertyInfo: PropertyInfo) =
        $"P:{formatDeclaringTypeName propertyInfo.DeclaringType}.{propertyInfo.Name}"

    let private formatField (fieldInfo: FieldInfo) =
        $"F:{formatDeclaringTypeName fieldInfo.DeclaringType}.{fieldInfo.Name} = {fieldInfo.GetRawConstantValue()}"

    let private formatXmlField (fieldInfo: FieldInfo) =
        $"F:{formatDeclaringTypeName fieldInfo.DeclaringType}.{fieldInfo.Name}"

    let private generateApiLines (assembly: Assembly) =
        assembly.GetExportedTypes()
        |> Array.sortBy formatDeclaringTypeName
        |> Array.collect (fun typeInfo ->
            let constructors =
                typeInfo.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly)
                |> Array.map formatConstructor
                |> Array.sort

            let methods =
                typeInfo.GetMethods(
                    BindingFlags.Public
                    ||| BindingFlags.Instance
                    ||| BindingFlags.Static
                    ||| BindingFlags.DeclaredOnly
                )
                |> Array.filter (fun methodInfo -> not methodInfo.IsSpecialName)
                |> Array.filter (fun methodInfo -> not (isCompilerGenerated methodInfo))
                |> Array.filter (fun methodInfo -> not (isObjectLikeMethod methodInfo))
                |> Array.map formatMethod
                |> Array.sort

            let properties =
                typeInfo.GetProperties(
                    BindingFlags.Public
                    ||| BindingFlags.Instance
                    ||| BindingFlags.Static
                    ||| BindingFlags.DeclaredOnly
                )
                |> Array.map formatProperty
                |> Array.sort

            let fields =
                typeInfo.GetFields(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly)
                |> Array.filter (fun fieldInfo -> fieldInfo.IsLiteral && fieldInfo.Name <> "value__")
                |> Array.map formatField
                |> Array.sort

            Array.concat
                [ [| $"T:{formatDeclaringTypeName typeInfo}" |]
                  constructors
                  methods
                  properties
                  fields ])

    let private tryFindNestedTypeByName (owner: Type) (name: string) =
        owner.GetNestedType(name, BindingFlags.Public ||| BindingFlags.NonPublic)

    let private additionalDocTargetsForProperty (propertyInfo: PropertyInfo) =
        let declaringType = propertyInfo.DeclaringType
        let targets = ResizeArray<string>()

        if declaringType.IsNested && not (isNull declaringType.DeclaringType) then
            targets.Add($"T:{formatDeclaringTypeName declaringType}")
        else
            match tryFindNestedTypeByName declaringType propertyInfo.Name with
            | null -> ()
            | nestedType -> targets.Add($"T:{formatDeclaringTypeName nestedType}")

            targets.Add($"T:{formatDeclaringTypeName declaringType}.{propertyInfo.Name}")

            if propertyInfo.Name = "Tag" then
                targets.Add($"T:{formatDeclaringTypeName declaringType}")
            elif propertyInfo.Name.StartsWith("Is", StringComparison.Ordinal) then
                let caseName = propertyInfo.Name.Substring(2)

                match tryFindNestedTypeByName declaringType caseName with
                | null -> targets.Add($"T:{formatDeclaringTypeName declaringType}")
                | nestedType -> targets.Add($"T:{formatDeclaringTypeName nestedType}")

        targets.ToArray() |> Array.distinct

    let private additionalDocTargetsForField (fieldInfo: FieldInfo) =
        let declaringType = fieldInfo.DeclaringType
        let targets = ResizeArray<string>()

        if declaringType.Name = "Tags" && not (isNull declaringType.DeclaringType) then
            match tryFindNestedTypeByName declaringType.DeclaringType fieldInfo.Name with
            | null -> targets.Add($"T:{formatDeclaringTypeName declaringType.DeclaringType}")
            | nestedType -> targets.Add($"T:{formatDeclaringTypeName nestedType}")

        targets.ToArray()

    let private buildXmlDocExpectations (assembly: Assembly) =
        let expectations = Dictionary<string, string[]>(StringComparer.Ordinal)

        for typeInfo in assembly.GetExportedTypes() do
            expectations[$"T:{formatDeclaringTypeName typeInfo}"] <-
                if typeInfo.Name = "Tags" && not (isNull typeInfo.DeclaringType) then
                    [| $"T:{formatDeclaringTypeName typeInfo}"
                       $"T:{formatDeclaringTypeName typeInfo.DeclaringType}" |]
                else
                    [| $"T:{formatDeclaringTypeName typeInfo}" |]

            for constructorInfo in
                typeInfo.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly) do
                expectations[formatConstructor constructorInfo] <-
                    [| formatXmlConstructor constructorInfo
                       $"T:{formatDeclaringTypeName typeInfo}" |]

            for methodInfo in
                typeInfo.GetMethods(
                    BindingFlags.Public
                    ||| BindingFlags.Instance
                    ||| BindingFlags.Static
                    ||| BindingFlags.DeclaredOnly
                )
                |> Array.filter (fun methodInfo -> not methodInfo.IsSpecialName)
                |> Array.filter (fun methodInfo -> not (isCompilerGenerated methodInfo))
                |> Array.filter (fun methodInfo -> not (isObjectLikeMethod methodInfo)) do
                expectations[formatMethod methodInfo] <- [| formatXmlMethod methodInfo |]

            for propertyInfo in
                typeInfo.GetProperties(
                    BindingFlags.Public
                    ||| BindingFlags.Instance
                    ||| BindingFlags.Static
                    ||| BindingFlags.DeclaredOnly
                ) do
                expectations[formatProperty propertyInfo] <-
                    Array.append [| formatXmlProperty propertyInfo |] (additionalDocTargetsForProperty propertyInfo)

            for fieldInfo in
                typeInfo.GetFields(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly)
                |> Array.filter (fun fieldInfo -> fieldInfo.IsLiteral && fieldInfo.Name <> "value__") do
                expectations[formatField fieldInfo] <-
                    Array.append [| formatXmlField fieldInfo |] (additionalDocTargetsForField fieldInfo)

        expectations

    let private xmlDocsWithNonEmptySummary (assembly: Assembly) =
        let xmlPath = Path.ChangeExtension(assembly.Location, ".xml")

        Assert.True(File.Exists xmlPath, $"Expected XML documentation file at {xmlPath}.")

        let document = XDocument.Load xmlPath

        document.Root.Element(XName.Get "members").Elements(XName.Get "member")
        |> Seq.choose (fun memberElement ->
            let nameAttribute = memberElement.Attribute(XName.Get "name")
            let summaryElement = memberElement.Element(XName.Get "summary")

            if isNull nameAttribute || isNull summaryElement then
                None
            else
                let summaryText = summaryElement.Value.Trim()

                if String.IsNullOrWhiteSpace summaryText then
                    None
                else
                    Some nameAttribute.Value)
        |> HashSet

    [<Fact>]
    let ``public api files match reflected production metadata`` () =
        for project in projects do
            let expected = generateApiLines project.Assembly |> Array.sort

            let actual =
                Array.concat
                    [ readApiLines (shippedPath project.Directory)
                      readApiLines (unshippedPath project.Directory) ]
                |> Array.sort

            let missing = expected |> Array.except actual
            let extra = actual |> Array.except expected

            let failure =
                [ if missing.Length > 0 then
                      yield $"Missing from {project.Name}:\n{String.Join(Environment.NewLine, missing)}"
                  if extra.Length > 0 then
                      yield $"Extra in {project.Name}:\n{String.Join(Environment.NewLine, extra)}" ]
                |> String.concat "\n\n"

            Assert.True(String.IsNullOrEmpty failure, failure)

    [<Fact>]
    let ``public api files contain no unreviewed entries`` () =
        let failures =
            projects
            |> Array.choose (fun project ->
                let path = unshippedPath project.Directory

                if hasUnreviewedEntries path then
                    Some
                        $"{project.Name} still has unreviewed entries in {path}. Move reviewed changes into PublicAPI.Shipped.txt before release."
                else
                    None)

        Assert.True(failures.Length = 0, String.Join(Environment.NewLine, failures))

    [<Fact>]
    let ``public F# package api entries have nonempty xml summaries`` () =
        let failures = ResizeArray<string>()

        for project in xmlDocProjects do
            let documentedMembers = xmlDocsWithNonEmptySummary project.Assembly
            let expectations = buildXmlDocExpectations project.Assembly

            let missing =
                readApiLines (shippedPath project.Directory)
                |> Array.distinct
                |> Array.choose (fun apiLine ->
                    match expectations.TryGetValue apiLine with
                    | true, acceptableTargets when acceptableTargets |> Array.exists documentedMembers.Contains -> None
                    | true, acceptableTargets ->
                        let targetList = String.Join(" | ", acceptableTargets)
                        Some $"{apiLine} -> {targetList}"
                    | false, _ -> Some $"{apiLine} -> <no reflection mapping>")

            if missing.Length > 0 then
                failures.Add(
                    $"{project.Name} is missing XML documentation summaries for:\n{String.Join(Environment.NewLine, missing)}"
                )

        Assert.True(failures.Count = 0, String.Join(Environment.NewLine + Environment.NewLine, failures))

    [<Fact>]
    let ``ICircuitRuntime streaming cancellation token is marked for enumerator cancellation`` () =
        let streamingMethod = typeof<ICircuitRuntime>.GetMethod("RunStreamingAsync")

        let cancellationParameter = streamingMethod.GetParameters() |> Array.last

        Assert.True(
            cancellationParameter.IsDefined(typeof<EnumeratorCancellationAttribute>, false),
            "ICircuitRuntime.RunStreamingAsync must mark its cancellation token with [<EnumeratorCancellation>]."
        )
