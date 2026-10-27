namespace Nyrwvy.Tools.AuthoringSession

open System.Text
open Microsoft.CodeAnalysis.CSharp
open Nyrwvy.Core

/// A facade name that cannot be emitted, with the declarations that would carry it.
type CSharpFacadeProblem =
    { Code: string
      Name: string
      Declarations: (string * string) list }

/// Readable C# names for a snapshot's exported declarations, emitted beside the generated source.
/// Generated identifiers stay name-independent; only this file depends on display names, so it is
/// bound to the snapshot it was emitted from. See docs/decisions/csharp-facade.md.
module CSharpFacade =
    let private ns = CSharpBackendNames.generatedNamespace

    // Record members an extension property could not shadow.
    let private reservedMembers = set [ "Equals"; "GetHashCode"; "ToString"; "GetType"; "Deconstruct"; "EqualityContract" ]

    let private identifier (name: string) =
        if SyntaxFacts.GetKeywordKind name <> SyntaxKind.None then "@" + name else name

    let private invalid (name: string) =
        // "__" is kept for the facade's own receiver name.
        not (SyntaxFacts.IsValidIdentifier name) || name.StartsWith "@" || name.StartsWith "__"

    /// Types and capabilities that an exported signature or an included type reaches.
    let private reachable (declarations: AuthoringDeclarations) (roots: TypeRef list) =
        let typesById = declarations.Types |> List.map (fun t -> t.Id, t) |> Map.ofList
        let capabilitiesById = declarations.Capabilities |> List.map (fun c -> c.Id, c) |> Map.ofList
        let rec visit (types: Set<TypeId>, capabilities: Set<CapabilityId>) typeRef =
            match typeRef with
            | TypeRef.Named(typeId, arguments) ->
                let state = arguments |> List.fold visit (types, capabilities)
                match Map.tryFind typeId typesById with
                | Some t when not (Set.contains typeId (fst state)) ->
                    let fields =
                        match t.Kind with
                        | SemanticTypeKind.Product fields -> fields
                        | SemanticTypeKind.Union variants -> variants |> List.collect (fun v -> v.Fields)
                    fields |> List.map (fun f -> f.Type) |> List.fold visit (Set.add typeId (fst state), snd state)
                | _ -> state
            | TypeRef.Capability capabilityId ->
                match Map.tryFind capabilityId capabilitiesById with
                | Some c when not (Set.contains capabilityId capabilities) ->
                    c.Operations
                    |> List.collect (fun op -> op.ResultType :: (op.Parameters |> List.map (fun p -> p.Type)))
                    |> List.fold visit (types, Set.add capabilityId capabilities)
                | _ -> types, capabilities
            | TypeRef.BuiltinGeneric(_, arguments) -> arguments |> List.fold visit (types, capabilities)
            | TypeRef.Function(arguments, result) -> (result :: arguments) |> List.fold visit (types, capabilities)
            | TypeRef.Primitive _
            | TypeRef.TypeParameter _ -> types, capabilities
        roots |> List.fold visit (Set.empty, Set.empty)

    let private genericList (parameters: (TypeParameterId * string) list) =
        if List.isEmpty parameters then ""
        else "<" + (parameters |> List.map (fst >> CSharpBackendNames.typeParameterName) |> String.concat ", ") + ">"


    /// Emits the facade, or the names that are invalid or carried by more than one declaration.
    /// `hostOperationIds` are the operations the host implements: their capabilities get a base class even
    /// when no exported signature reaches them, as an imported library's capabilities often are not.
    /// `tableCapabilityIds` are mapping-table capabilities with a public generated adapter: each gets a
    /// `New<Name>()` constructor, so a host need not spell the adapter's generated name.
    let emit (declarations: AuthoringDeclarations) (hostOperationIds: CapabilityOperationId list) (tableCapabilityIds: CapabilityId list) : Result<string, CSharpFacadeProblem list> =
        let isLocal kind (id: string) =
            declarations.Provenance |> List.exists (fun p -> p.Kind = kind && p.Id = id && p.IsLocal)
        let functions =
            declarations.Functions
            |> List.filter (fun f -> f.Exported && isLocal "function" (StableId.value f.Id))
            |> List.sortBy (fun f -> StableId.value f.Id)
        let localTypes = declarations.Types |> List.filter (fun t -> t.Exported && isLocal "type" (StableId.value t.Id))
        let localCapabilities =
            declarations.Capabilities |> List.filter (fun c -> c.Exported && isLocal "capability" (StableId.value c.Id))
        let roots =
            (functions |> List.collect (fun f -> f.ResultType :: (f.Parameters |> List.map (fun p -> p.Type))))
            @ (localTypes |> List.map (fun t -> TypeRef.Named(t.Id, t.TypeParameters |> List.map (fst >> TypeRef.TypeParameter))))
            @ (localCapabilities |> List.map (fun c -> TypeRef.Capability c.Id))
            @ (declarations.Capabilities
               |> List.filter (fun c -> c.Operations |> List.exists (fun op -> List.contains op.Id hostOperationIds))
               |> List.map (fun c -> TypeRef.Capability c.Id))
            @ (tableCapabilityIds |> List.map TypeRef.Capability)
        let typeIds, capabilityIds = reachable declarations roots
        let types = declarations.Types |> List.filter (fun t -> Set.contains t.Id typeIds) |> List.sortBy (fun t -> StableId.value t.Id)
        let capabilities =
            declarations.Capabilities |> List.filter (fun c -> Set.contains c.Id capabilityIds) |> List.sortBy (fun c -> StableId.value c.Id)
        let variantsOf (t: SemanticType) =
            match t.Kind with
            | SemanticTypeKind.Union variants -> variants |> List.sortBy (fun v -> StableId.value v.Id)
            | SemanticTypeKind.Product _ -> []
        // Every name in the facade's shared scope: type, variant and capability aliases, capability
        // base classes, constructors and functions. One display name may not serve two of them.
        let scoped =
            [ for t in types do
                  if List.isEmpty t.TypeParameters then yield t.DisplayName, ("type", StableId.value t.Id)
                  match t.Kind with
                  | SemanticTypeKind.Product _ -> yield "New" + t.DisplayName, ("type", StableId.value t.Id)
                  | SemanticTypeKind.Union _ ->
                      for v in variantsOf t do
                          if List.isEmpty t.TypeParameters then yield t.DisplayName + v.DisplayName, ("variant", StableId.value v.Id)
                          yield "New" + t.DisplayName + v.DisplayName, ("variant", StableId.value v.Id)
              for c in capabilities do
                  yield c.DisplayName, ("capability", StableId.value c.Id)
                  yield c.DisplayName + "Base", ("capability", StableId.value c.Id)
                  if List.contains c.Id tableCapabilityIds then yield "New" + c.DisplayName, ("capability", StableId.value c.Id)
              for f in functions do
                  yield f.DisplayName, ("function", StableId.value f.Id) ]
        let members =
            [ for t in types do
                  let fields =
                      match t.Kind with
                      | SemanticTypeKind.Product fields -> fields
                      | SemanticTypeKind.Union variants -> variants |> List.collect (fun v -> v.Fields)
                  for f in fields do yield f.DisplayName, ("field", StableId.value f.Id)
              for c in capabilities do
                  for op in c.Operations do
                      yield op.DisplayName, ("capabilityOperation", StableId.value op.Id)
                      for p in op.Parameters do yield p.DisplayName, ("capabilityParameter", StableId.value p.Id)
              for f in functions do
                  for p in f.Parameters do yield p.DisplayName, ("parameter", StableId.value p.Id) ]
        let invalidNames =
            (scoped @ members)
            |> List.filter (fun (name, _) -> invalid name || (name.Length > 2 && name.StartsWith "Ny" && (System.Char.IsUpper name[2] || name[2] = '_')))
            |> List.groupBy fst
            |> List.map (fun (name, items) -> { Code = "FacadeNameInvalid"; Name = name; Declarations = List.map snd items })
        let reserved =
            members
            |> List.filter (fun (name, (kind, _)) -> kind = "field" && Set.contains name reservedMembers)
            |> List.map (fun (name, declaration) -> { Code = "FacadeNameInvalid"; Name = name; Declarations = [ declaration ] })
        let conflicts =
            scoped
            |> List.groupBy fst
            |> List.filter (fun (_, items) -> (items |> List.map snd |> List.distinct).Length > 1)
            |> List.map (fun (name, items) -> { Code = "FacadeNameConflict"; Name = name; Declarations = items |> List.map snd |> List.distinct })
        match invalidNames @ reserved @ conflicts with
        | _ :: _ as problems -> Error problems
        | [] ->
            // Signatures name aliased types by their aliases. Generated names have one fixed length, so
            // replacing whole generated names cannot touch part of another.
            let aliases =
                (types |> List.filter (fun t -> List.isEmpty t.TypeParameters)
                 |> List.map (fun t -> CSharpBackendNames.typeName t.Id, identifier t.DisplayName))
                @ (capabilities |> List.map (fun c -> CSharpBackendNames.capabilityName c.Id, identifier c.DisplayName))
            let typeText (t: TypeRef) =
                aliases |> List.fold (fun (text: string) (generated, alias) -> text.Replace(generated, alias)) (CSharpBackend.formatType t)
            let parameterList (items: (string * TypeRef) list) =
                items |> List.map (fun (name, t) -> typeText t + " " + identifier name) |> String.concat ", "
            let out = StringBuilder()
            let line (text: string) = out.Append(text).Append('\n') |> ignore
            line "// Generated by Nyrwvy."
            line "// Readable names for the exported declarations of one snapshot; regenerate it with the program."
            line "// A renamed declaration renames its facade name, so a host that uses the old name stops compiling."
            line "// Requires C# 14 (extension members). Hosts import it with: using static Nyrwvy.Generated.NyFacade;"
            for t in types do
                if List.isEmpty t.TypeParameters then
                    line $"global using {identifier t.DisplayName} = {ns}.{CSharpBackendNames.typeName t.Id};"
                    for v in variantsOf t do
                        line $"global using {t.DisplayName}{v.DisplayName} = {ns}.{CSharpBackendNames.variantName v.Id};"
            for c in capabilities do
                line $"global using {identifier c.DisplayName} = {ns}.{CSharpBackendNames.capabilityName c.Id};"
            line ""
            line $"namespace {ns};"
            line ""
            line "public static class NyFacade"
            line "{"
            let fieldBlock receiver generics (fields: SemanticField list) =
                if not (List.isEmpty fields) then
                    line $"    extension{generics}({receiver} __value)"
                    line "    {"
                    for f in fields do
                        line $"        public {typeText f.Type} {identifier f.DisplayName} => __value.{CSharpBackendNames.fieldName f.Id};"
                    line "    }"
            let construct (typeName: string) (fields: SemanticField list) =
                fields |> List.map (fun f -> $"{CSharpBackendNames.fieldName f.Id}: {identifier f.DisplayName}") |> String.concat ", "
                |> fun arguments -> $"new {typeName}({arguments})"
            for t in types do
                let generics = genericList t.TypeParameters
                let typeName = CSharpBackendNames.typeName t.Id + generics
                let shown = if List.isEmpty t.TypeParameters then identifier t.DisplayName else typeName
                match t.Kind with
                | SemanticTypeKind.Product fields ->
                    fieldBlock typeName generics fields
                    let parameters = parameterList (fields |> List.map (fun f -> f.DisplayName, f.Type))
                    line $"    public static {shown} New{t.DisplayName}{generics}({parameters}) => {construct typeName fields};"
                | SemanticTypeKind.Union _ ->
                    for v in variantsOf t do
                        let variantName = CSharpBackendNames.variantName v.Id + generics
                        fieldBlock variantName generics v.Fields
                        let parameters = parameterList (v.Fields |> List.map (fun f -> f.DisplayName, f.Type))
                        line $"    public static {shown} New{t.DisplayName}{v.DisplayName}{generics}({parameters}) => {construct variantName v.Fields};"
            for c in capabilities do
                if List.contains c.Id tableCapabilityIds then
                    line $"    public static {identifier c.DisplayName} New{c.DisplayName}() => new {CSharpBackendNames.capabilityAdapterName c.Id}();"
            for f in functions do
                let generics = genericList f.TypeParameters
                let parameters = parameterList (f.Parameters |> List.map (fun p -> p.DisplayName, p.Type))
                let arguments =
                    f.Parameters |> List.map (fun p -> $"{CSharpBackendNames.parameterName p.Id}: {identifier p.DisplayName}") |> String.concat ", "
                line $"    public static {typeText f.ResultType} {identifier f.DisplayName}{generics}({parameters}) => NyFunctions.{CSharpBackendNames.functionName f.Id}{generics}({arguments});"
            line "}"
            // A host implements a capability through readable abstract methods; the explicit interface
            // members forward the generated names to them.
            for c in capabilities do
                let interfaceName = CSharpBackendNames.capabilityName c.Id
                line ""
                line $"public abstract class {c.DisplayName}Base : {interfaceName}"
                line "{"
                for op in c.Operations |> List.sortBy (fun op -> StableId.value op.Id) do
                    let resultType = CSharpBackend.formatType op.ResultType
                    line $"    public abstract {typeText op.ResultType} {identifier op.DisplayName}({parameterList (op.Parameters |> List.map (fun p -> p.DisplayName, p.Type))});"
                    let generated =
                        op.Parameters |> List.map (fun p -> CSharpBackend.formatType p.Type + " " + CSharpBackendNames.capabilityParameterName p.Id) |> String.concat ", "
                    let forwarded = op.Parameters |> List.map (fun p -> CSharpBackendNames.capabilityParameterName p.Id) |> String.concat ", "
                    line $"    {resultType} {interfaceName}.{CSharpBackendNames.capabilityOperationName op.Id}({generated}) => {identifier op.DisplayName}({forwarded});"
                line "}"
            Ok(out.ToString())
