namespace Circuit.Core

open System
open System.Collections
open System.Collections.Generic
open System.Collections.ObjectModel
open System.ComponentModel
open System.ComponentModel.DataAnnotations
open System.Reflection
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Schema
open System.Text.Json.Serialization

/// Wraps a JSON Schema document generated for a contract type.
[<Sealed>]
type SchemaDocument internal (node: JsonNode) =
    let json, valueType =
        if isNull node then
            nullArg "node"

        let snapshot = node.DeepClone()
        let json = snapshot.ToJsonString()

        use document = JsonDocument.Parse(json)
        let valueType = document.RootElement.ValueKind

        json, valueType

    /// Gets the schema root element.
    /// <remarks>A defensive clone is returned on each call.</remarks>
    member _.RootElement =
        use document = JsonDocument.Parse(json)
        document.RootElement.Clone()

    /// Serializes the schema to JSON.
    member _.ToJsonString() = json

    /// Gets the root JSON value kind for the schema document.
    member _.ValueType = valueType

/// Describes one validation failure discovered for a contract value.
type ValidationIssue =
    {
        /// Gets the JSON-style path of the failing value.
        Path: string
        /// Gets a stable, machine-readable validation code.
        Code: string
        /// Gets a human-readable explanation of the failure.
        Message: string
    }

/// Validates values supplied to or returned from a Circuit contract.
type IContractValidator<'T> =
    /// Validates a value and returns zero or more issues.
    abstract Validate: value: 'T -> IReadOnlyList<ValidationIssue>

type private BoundedCache<'K, 'V when 'K: equality>(capacity: int) =
    let gate = obj ()
    let entries = Dictionary<'K, 'V>()
    let order = Queue<'K>()

    do
        if capacity < 1 then
            invalidArg "capacity" "Cache capacity must be greater than zero."

    member _.GetOrAdd(key: 'K, factory: 'K -> 'V) =
        lock gate (fun () ->
            match entries.TryGetValue key with
            | true, value -> value
            | _ ->
                let value = factory key
                entries[key] <- value
                order.Enqueue key

                while entries.Count > capacity do
                    let evictedKey = order.Dequeue()
                    entries.Remove evictedKey |> ignore

                value)

module private SchemaGeneration =
    // Keep the shared cache intentionally small so the process cannot retain an
    // unbounded number of type/policy combinations forever.
    let private schemaCache = BoundedCache<struct (Type * string), SchemaDocument>(16)

    let private tryGetDescriptionFromProvider (provider: ICustomAttributeProvider) =
        if isNull provider then
            ValueNone
        else
            match provider.GetCustomAttributes(typeof<DescriptionAttribute>, true) with
            | [| :? DescriptionAttribute as description |] when not (String.IsNullOrWhiteSpace description.Description) ->
                ValueSome description.Description
            | _ -> ValueNone

    let private tryGetDescription (context: JsonSchemaExporterContext) =
        if not (isNull context.PropertyInfo) then
            tryGetDescriptionFromProvider context.PropertyInfo.AttributeProvider
        else
            match context.TypeInfo.Type.GetCustomAttribute<DescriptionAttribute>() with
            | null -> ValueNone
            | description when String.IsNullOrWhiteSpace description.Description -> ValueNone
            | description -> ValueSome description.Description

    let private applyDescription (description: string) (node: JsonNode) =
        match node with
        | :? JsonObject as schemaObject ->
            schemaObject["description"] <- JsonValue.Create description
            schemaObject :> JsonNode
        | _ -> node

    let private createSchema (contractType: Type) (jsonOptions: JsonSerializerOptions) =
        let exporterOptions =
            JsonSchemaExporterOptions(
                TreatNullObliviousAsNonNullable = true,
                TransformSchemaNode =
                    Func<JsonSchemaExporterContext, JsonNode, JsonNode>(fun context node ->
                        match tryGetDescription context with
                        | ValueSome description -> applyDescription description node
                        | ValueNone -> node)
            )

        JsonSchemaExporter.GetJsonSchemaAsNode(jsonOptions, contractType, exporterOptions)
        |> SchemaDocument

    let getOrCreateSchema<'T> (jsonOptions: JsonSerializerOptions) =
        match SerializationPolicy.tryGetSemanticFingerprint jsonOptions with
        | ValueSome fingerprint ->
            let key = struct (typeof<'T>, fingerprint)
            schemaCache.GetOrAdd(key, fun _ -> createSchema typeof<'T> jsonOptions)
        | ValueNone -> createSchema typeof<'T> jsonOptions

module private ValidationTraversal =
    let private scalarTypes =
        HashSet<Type>(
            [| typeof<string>
               typeof<JsonElement>
               typeof<JsonDocument>
               typeof<DateTime>
               typeof<DateTimeOffset>
               typeof<DateOnly>
               typeof<TimeOnly>
               typeof<TimeSpan>
               typeof<Guid>
               typeof<Uri>
               typeof<decimal> |],
            HashIdentity.Reference
        )

    let private isEnumerableType (valueType: Type) =
        valueType <> typeof<string> && typeof<IEnumerable>.IsAssignableFrom valueType

    let rec private isScalarType (valueType: Type) =
        valueType.IsPrimitive
        || valueType.IsEnum
        || scalarTypes.Contains valueType
        || (valueType.IsGenericType
            && valueType.GetGenericTypeDefinition() = typedefof<Nullable<_>>
            && isScalarType (Nullable.GetUnderlyingType valueType))

    let private convertName (namingPolicy: JsonNamingPolicy) (name: string) =
        if isNull namingPolicy then
            name
        else
            namingPolicy.ConvertName name

    let private jsonNameFor (namingPolicy: JsonNamingPolicy) (memberInfo: MemberInfo) =
        match memberInfo.GetCustomAttribute<JsonPropertyNameAttribute>() with
        | null -> convertName namingPolicy memberInfo.Name
        | attribute -> attribute.Name

    let private jsonNameForMemberName (namingPolicy: JsonNamingPolicy) (memberName: string) =
        convertName namingPolicy memberName

    let private appendPropertyPath namingPolicy path (propertyInfo: PropertyInfo) =
        $"{path}.{jsonNameFor namingPolicy propertyInfo}"

    let private issue path message =
        { Path = path
          Code = "validation"
          Message =
            if String.IsNullOrWhiteSpace message then
                "Validation failed."
            else
                message }

    let private tryResolveProperty (valueType: Type) (memberName: string) =
        valueType.GetProperty(memberName, BindingFlags.Instance ||| BindingFlags.Public)
        |> function
            | null ->
                valueType.GetProperty(
                    memberName,
                    BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.IgnoreCase
                )
            | propertyInfo -> propertyInfo

    let private collectValidationResults
        (namingPolicy: JsonNamingPolicy)
        path
        (value: obj)
        (valueType: Type)
        (issues: ResizeArray<ValidationIssue>)
        =
        let results = ResizeArray<ValidationResult>()
        let context = ValidationContext(value)
        Validator.TryValidateObject(value, context, results, true) |> ignore

        for result in results do
            let memberNames = result.MemberNames |> Seq.toArray

            if memberNames.Length = 0 then
                issues.Add(issue path result.ErrorMessage)
            else
                for memberName in memberNames do
                    let memberPath =
                        match tryResolveProperty valueType memberName with
                        | null -> $"{path}.{jsonNameForMemberName namingPolicy memberName}"
                        | propertyInfo -> appendPropertyPath namingPolicy path propertyInfo

                    issues.Add(issue memberPath result.ErrorMessage)

    let private readableProperties (valueType: Type) =
        valueType.GetProperties(BindingFlags.Instance ||| BindingFlags.Public)
        |> Array.filter (fun propertyInfo -> propertyInfo.CanRead && propertyInfo.GetIndexParameters().Length = 0)

    let validate<'T> (namingPolicy: JsonNamingPolicy) (value: 'T) =
        let issues = ResizeArray<ValidationIssue>()
        let active = HashSet<obj>(ReferenceEqualityComparer.Instance)

        let rec visit path (node: obj) =
            if isNull node then
                ()
            else
                let valueType = node.GetType()

                if isScalarType valueType then
                    ()
                elif isEnumerableType valueType then
                    if valueType.IsValueType || active.Add node then
                        try
                            let mutable index = 0

                            for item in node :?> IEnumerable do
                                visit $"{path}[{index}]" item
                                index <- index + 1
                        finally
                            if not valueType.IsValueType then
                                active.Remove node |> ignore
                else if valueType.IsValueType || active.Add node then
                    try
                        collectValidationResults namingPolicy path node valueType issues

                        for propertyInfo in readableProperties valueType do
                            let propertyValue = propertyInfo.GetValue(node, null)
                            let propertyPath = appendPropertyPath namingPolicy path propertyInfo
                            visit propertyPath propertyValue
                    finally
                        if not valueType.IsValueType then
                            active.Remove node |> ignore

        visit "$" (box value)
        issues.ToArray() :> IReadOnlyList<ValidationIssue>

type internal DataAnnotationsValidator<'T>(namingPolicy: JsonNamingPolicy) =
    interface IContractValidator<'T> with
        member _.Validate(value: 'T) =
            ValidationTraversal.validate namingPolicy value

/// Describes the serialized shape and validators for a public contract type.
/// <remarks>
/// Every contract automatically includes a DataAnnotations-based validator derived from the configured JSON naming policy.
/// </remarks>
[<Sealed>]
type Contract<'T> internal (schema: SchemaDocument, validators: IContractValidator<'T>[]) =
    let validatorSnapshot =
        if isNull validators then
            nullArg "validators"

        validators |> Array.copy

    let validatorView = ReadOnlyCollection(validatorSnapshot)

    /// Gets the CLR type represented by the contract.
    member _.ValueType = typeof<'T>

    /// Gets the generated JSON Schema for the contract.
    member _.Schema = schema

    /// Gets the validators that will run for this contract.
    member _.Validators = validatorView :> IReadOnlyList<IContractValidator<'T>>

    /// Validates a value against all configured validators.
    /// <param name="value">The value to validate.</param>
    /// <returns>The collected validation issues, or an empty list when validation succeeds.</returns>
    member _.Validate(value: 'T) =
        let issues = ResizeArray<ValidationIssue>()

        if isNull (box value) then
            issues.Add(
                { Path = "$"
                  Code = "required"
                  Message = "Value must not be null." }
            )
        else
            for validator in validatorSnapshot do
                let validationIssues = validator.Validate value

                if not (isNull validationIssues) then
                    issues.AddRange validationIssues

        issues.ToArray() :> IReadOnlyList<ValidationIssue>

    /// Creates a contract using the supplied serializer options and custom validators.
    /// <param name="jsonOptions">The JSON serializer options that define the public wire shape.</param>
    /// <param name="validators">Additional validators to append after Circuit's built-in DataAnnotations validator.</param>
    /// <returns>The created contract.</returns>
    /// <exception cref="T:System.ArgumentNullException"><paramref name="jsonOptions" /> or <paramref name="validators" /> is <see langword="null" />.</exception>
    /// <exception cref="T:System.ArgumentException"><paramref name="validators" /> contains a <see langword="null" /> entry.</exception>
    static member Create(jsonOptions: JsonSerializerOptions, validators: IEnumerable<IContractValidator<'T>>) =
        if isNull jsonOptions then
            nullArg "jsonOptions"

        if isNull validators then
            nullArg "validators"

        let customValidators = validators |> Seq.toArray

        if customValidators |> Array.exists (fun validator -> isNull (box validator)) then
            invalidArg "validators" "Validators cannot contain null entries."

        let schema = SchemaGeneration.getOrCreateSchema<'T> jsonOptions

        let allValidators =
            Array.append
                [| DataAnnotationsValidator<'T>(jsonOptions.PropertyNamingPolicy) :> IContractValidator<'T> |]
                customValidators

        Contract<'T>(schema, allValidators)
