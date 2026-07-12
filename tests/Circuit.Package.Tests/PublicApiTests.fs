namespace Circuit.Package.Tests

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
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

    let private readApiLines path =
        if File.Exists path then
            File.ReadAllLines path
            |> Array.map _.Trim()
            |> Array.filter (fun line -> not (String.IsNullOrWhiteSpace line))
        else
            Array.empty

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

    let private formatMethod (methodInfo: MethodInfo) =
        let parameters =
            methodInfo.GetParameters()
            |> Array.map (fun parameter -> formatTypeReference parameter.ParameterType)
            |> String.concat ","

        $"M:{formatDeclaringTypeName methodInfo.DeclaringType}.{methodInfo.Name}({parameters})~{formatTypeReference methodInfo.ReturnType}"

    let private formatProperty (propertyInfo: PropertyInfo) =
        $"P:{formatDeclaringTypeName propertyInfo.DeclaringType}.{propertyInfo.Name}~{formatTypeReference propertyInfo.PropertyType}"

    let private formatField (fieldInfo: FieldInfo) =
        $"F:{formatDeclaringTypeName fieldInfo.DeclaringType}.{fieldInfo.Name} = {fieldInfo.GetRawConstantValue()}"

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
    let ``ICircuitRuntime streaming cancellation token is marked for enumerator cancellation`` () =
        let streamingMethod = typeof<ICircuitRuntime>.GetMethod("RunStreamingAsync")

        let cancellationParameter = streamingMethod.GetParameters() |> Array.last

        Assert.True(
            cancellationParameter.IsDefined(typeof<EnumeratorCancellationAttribute>, false),
            "ICircuitRuntime.RunStreamingAsync must mark its cancellation token with [<EnumeratorCancellation>]."
        )
