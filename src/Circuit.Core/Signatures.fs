namespace Circuit.Core

open System
open System.Collections.Generic
open System.Text.Json

module private SignatureValidation =
    let requireNonBlank name (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg name $"{name} cannot be blank."

        value

/// Describes an agent contract: input type, output type, and execution instructions.
/// <remarks>
/// Signatures snapshot their JSON serializer options at creation time so schemas, validation, and runtime decoding stay aligned.
/// </remarks>
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
    /// Gets the signature identifier.
    member _.Id = id

    /// Gets the signature version.
    member _.Version = version

    /// Gets the short human-readable description of the capability.
    member _.Description = description

    /// Gets the validated input contract.
    member _.Input = input

    /// Gets the validated output contract.
    member _.Output = output

    /// Gets the additional execution instructions appended to the agent prompt.
    member _.Instructions = instructions
    member internal _.JsonSerializerOptions = jsonOptions

    /// Creates a signature from its public metadata and validators.
    /// <param name="id">The signature identifier.</param>
    /// <param name="version">The semantic version in <c>major.minor.patch</c> form.</param>
    /// <param name="description">A short human-readable description.</param>
    /// <param name="instructions">The runtime instructions paired with the signature.</param>
    /// <param name="jsonOptions">The serializer options that define the input and output wire contracts.</param>
    /// <param name="inputValidators">Additional validators for the input contract.</param>
    /// <param name="outputValidators">Additional validators for the output contract.</param>
    /// <returns>The created signature.</returns>
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
