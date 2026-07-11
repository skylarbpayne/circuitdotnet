namespace Circuit.Core

open System
open System.Collections.Generic
open System.Text.Json

module private SignatureValidation =
    let requireNonBlank name (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg name $"{name} cannot be blank."

        value

[<Sealed>]
type Signature<'Input, 'Output>
    internal
    (
        id: DefinitionId,
        version: SemanticVersion,
        description: string,
        instructions: string,
        jsonOptions: JsonSerializerOptions,
        input: Contract<'Input>,
        output: Contract<'Output>
    ) =
    member _.Id = id
    member _.Version = version
    member _.Description = description
    member _.Input = input
    member _.Output = output
    member _.Instructions = instructions
    member internal _.JsonSerializerOptions = jsonOptions

    static member Create
        (
            id: string,
            version: string,
            description: string,
            instructions: string,
            jsonOptions: JsonSerializerOptions,
            inputValidators: IEnumerable<IContractValidator<'Input>>,
            outputValidators: IEnumerable<IContractValidator<'Output>>
        ) =
        if isNull jsonOptions then
            nullArg "jsonOptions"

        if isNull inputValidators then
            nullArg "inputValidators"

        if isNull outputValidators then
            nullArg "outputValidators"

        let normalizedDescription =
            SignatureValidation.requireNonBlank "description" description

        let normalizedInstructions =
            SignatureValidation.requireNonBlank "instructions" instructions

        let jsonOptionsSnapshot = JsonSerializerOptions(jsonOptions)
        jsonOptionsSnapshot.MakeReadOnly()

        Signature(
            DefinitionId.Create id,
            SemanticVersion.Parse version,
            normalizedDescription,
            normalizedInstructions,
            jsonOptionsSnapshot,
            Contract<'Input>.Create(jsonOptionsSnapshot, inputValidators),
            Contract<'Output>.Create(jsonOptionsSnapshot, outputValidators)
        )
