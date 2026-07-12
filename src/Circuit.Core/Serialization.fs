namespace Circuit.Core

open System
open System.Globalization
open System.Reflection
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Json.Serialization.Metadata

module CircuitJson =
    let createOptions () =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
        options.PropertyNameCaseInsensitive <- false
        options.UnmappedMemberHandling <- JsonUnmappedMemberHandling.Disallow
        options.TypeInfoResolver <- DefaultJsonTypeInfoResolver()
        options.Converters.Add(JsonStringEnumConverter(JsonNamingPolicy.CamelCase))
        options

module internal SerializationPolicy =
    let private encode (text: string) =
        if isNull text then "-1:" else $"{text.Length}:{text}"

    let private tryFingerprintJsonNamingPolicy (policy: JsonNamingPolicy) =
        if isNull policy then
            ValueSome "namingPolicy:null"
        elif obj.ReferenceEquals(policy, JsonNamingPolicy.CamelCase) then
            ValueSome "namingPolicy:camelCase"
        else
            ValueNone

    let rec private tryFingerprintValue (value: obj) =
        if isNull value then
            ValueSome "null"
        elif value :? string then
            let text = value :?> string
            ValueSome $"string:{encode text}"
        elif value :? JsonNamingPolicy then
            match tryFingerprintJsonNamingPolicy (value :?> JsonNamingPolicy) with
            | ValueSome fingerprint -> ValueSome $"jsonNamingPolicy:{fingerprint}"
            | ValueNone -> ValueNone
        elif value :? JsonStringEnumConverter then
            tryFingerprintJsonStringEnumConverter (value :?> JsonStringEnumConverter)
        elif value :? IJsonTypeInfoResolver then
            tryFingerprintResolver (value :?> IJsonTypeInfoResolver)
        elif value :? Type then
            let valueType = value :?> Type
            ValueSome $"type:{encode valueType.AssemblyQualifiedName}"
        elif value :? System.Text.Encodings.Web.JavaScriptEncoder then
            if obj.ReferenceEquals(value, JavaScriptEncoder.Default) then
                ValueSome "encoder:default"
            elif obj.ReferenceEquals(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) then
                ValueSome "encoder:unsafeRelaxed"
            else
                ValueNone
        elif value :? System.Collections.IEnumerable then
            let items = ResizeArray<string>()
            let mutable stable = true

            for item in value :?> System.Collections.IEnumerable do
                match tryFingerprintValue item with
                | ValueSome fingerprint -> items.Add(encode fingerprint)
                | ValueNone -> stable <- false

            if stable then
                let joined = String.concat "" items
                ValueSome $"seq:{encode joined}"
            else
                ValueNone
        elif
            value.GetType().IsPrimitive
            || value.GetType().IsEnum
            || value :? decimal
            || value :? DateTime
            || value :? DateTimeOffset
            || value :? TimeSpan
            || value :? Guid
            || value :? Uri
        then
            ValueSome
                $"{value.GetType().AssemblyQualifiedName}:{encode (Convert.ToString(value, CultureInfo.InvariantCulture))}"
        else
            ValueNone

    and private tryFingerprintJsonStringEnumConverter (converter: JsonStringEnumConverter) =
        if converter.GetType() <> typeof<JsonStringEnumConverter> then
            ValueNone
        else
            let fieldFingerprints = ResizeArray<string>()
            let mutable stable = true

            for field in
                converter.GetType().GetFields(BindingFlags.Instance ||| BindingFlags.NonPublic)
                |> Array.sortBy _.Name do
                let value = field.GetValue converter

                match value with
                | null -> fieldFingerprints.Add(encode field.Name + encode "null")
                | value when typeof<JsonNamingPolicy>.IsAssignableFrom field.FieldType ->
                    match tryFingerprintJsonNamingPolicy (value :?> JsonNamingPolicy) with
                    | ValueSome fingerprint -> fieldFingerprints.Add(encode field.Name + encode fingerprint)
                    | ValueNone -> stable <- false
                | value when field.FieldType = typeof<string> ->
                    fieldFingerprints.Add(encode field.Name + encode $"string:{encode (value :?> string)}")
                | value when field.FieldType.IsEnum ->
                    fieldFingerprints.Add(encode field.Name + encode $"{field.FieldType.AssemblyQualifiedName}:{value}")
                | value when field.FieldType = typeof<bool> ->
                    fieldFingerprints.Add(encode field.Name + encode (if value :?> bool then "true" else "false"))
                | value when field.FieldType.IsPrimitive ->
                    fieldFingerprints.Add(
                        encode field.Name
                        + encode
                            $"{field.FieldType.AssemblyQualifiedName}:{encode (Convert.ToString(value, CultureInfo.InvariantCulture))}"
                    )
                | value when field.FieldType = typeof<decimal> ->
                    fieldFingerprints.Add(
                        encode field.Name
                        + encode
                            $"{field.FieldType.AssemblyQualifiedName}:{encode (Convert.ToString(value, CultureInfo.InvariantCulture))}"
                    )
                | _ -> stable <- false

            if stable then
                let fingerprints = String.concat "" fieldFingerprints
                ValueSome $"converter:{converter.GetType().AssemblyQualifiedName}:{fingerprints}"
            else
                ValueNone

    and private tryFingerprintResolver (resolver: IJsonTypeInfoResolver) =
        if isNull resolver then
            ValueNone
        elif resolver.GetType() = typeof<DefaultJsonTypeInfoResolver> then
            let defaultResolver = resolver :?> DefaultJsonTypeInfoResolver

            if defaultResolver.Modifiers.Count = 0 then
                ValueSome "resolver:DefaultJsonTypeInfoResolver:default"
            else
                ValueNone
        elif
            resolver.GetType().FullName = "System.Text.Json.JsonSerializerOptions+OptionsBoundJsonTypeInfoResolverChain"
        then
            let items = ResizeArray<string>()
            let mutable stable = true

            for item in resolver :?> System.Collections.IEnumerable do
                match tryFingerprintValue item with
                | ValueSome fingerprint -> items.Add(encode fingerprint)
                | ValueNone -> stable <- false

            if stable && items.Count > 0 then
                let joined = String.concat "" items
                ValueSome $"resolverChain:{encode joined}"
            else
                ValueNone
        else
            ValueNone

    let tryGetSemanticFingerprint (options: JsonSerializerOptions) =
        if isNull options then
            nullArg "options"

        typeof<JsonSerializerOptions>.GetProperties(BindingFlags.Instance ||| BindingFlags.Public)
        |> Array.filter (fun propertyInfo ->
            propertyInfo.CanRead
            && propertyInfo.GetIndexParameters().Length = 0
            && propertyInfo.Name <> "IsReadOnly")
        |> Array.sortBy _.Name
        |> Array.fold
            (fun fingerprint propertyInfo ->
                match fingerprint with
                | ValueNone -> ValueNone
                | ValueSome accumulated ->
                    match tryFingerprintValue (propertyInfo.GetValue options) with
                    | ValueSome valueFingerprint ->
                        ValueSome(accumulated + encode propertyInfo.Name + encode valueFingerprint)
                    | ValueNone -> ValueNone)
            (ValueSome "")
