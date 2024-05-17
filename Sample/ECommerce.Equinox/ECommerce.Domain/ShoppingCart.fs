module ECommerce.Domain.ShoppingCart

let [<Literal>] Category = "ShoppingCart"

let streamName = CartId.toString >> FsCodec.StreamName.create Category
let (|StreamName|_|) = function FsCodec.StreamName.CategoryAndId (Category, CartId.Parse cartId) -> Some cartId | _ -> None

module Events =

    type Event =
        | Initialized of {| clientId : ClientId |}
        | ItemAdded of {| productId : ProductId; quantity : int; unitPrice : decimal |}
        | ItemRemoved of {| productId : ProductId; (*; quantity : int;*) unitPrice : decimal |}
        | Confirmed of {| confirmedAt : System.DateTimeOffset |}
        | Registering of {| originEpoch : ConfirmedEpochId |}
        interface TypeShape.UnionContract.IUnionContract
    let codec = Config.EventCodec.forUnion<Event>

module Reactions =

    let (|Decode|) (span : Propulsion.Streams.StreamSpan<_>) = span.events |> Seq.choose Events.codec.TryDecode |> Array.ofSeq
    let (|Parse|_|) = function
        | StreamName sellerId, Decode events -> Some (sellerId, events)
        | _ -> None
    let chooseConfirmed = function
        | Events.Confirmed _ -> Some ()
        | _ -> None
    let (|Confirmed|_|) events = Seq.rev events |> Seq.tryPick chooseConfirmed
    let chooseNotRegistering = function
        | Events.Registering _ -> None
        | _ -> Some ()
    let (|StateChanged|_|) events = Seq.rev events |> Seq.tryPick chooseNotRegistering

module Fold =

    type Item = { productId : ProductId; quantity : int; unitPrice : decimal }

    type Status = Pending | Confirmed
    type State =
        {   clientId : ClientId option
            status : Status; items : Item array
            confirmedAt : System.DateTimeOffset option
            confirmedOriginEpoch : ConfirmedEpochId option }
    let initial = { clientId = None; status = Status.Pending; items = Array.empty; confirmedAt = None; confirmedOriginEpoch = None }
    let isClosed (s : State) = match s.status with Confirmed -> true | Pending -> false
    module ItemList =
        let keys (x : Item) = x.productId, x.unitPrice
        let add (productId, price, quantity) (current : Item seq) =
            let newItemKeys = productId, price
            let mkItem (productId, price, quantity) = { productId = productId; quantity = quantity; unitPrice = price }
            let mutable merged = false
            [|  for x in current do
                    if newItemKeys = keys x then
                        mkItem (productId, price, x.quantity + quantity)
                        merged <- true
                    else
                        x
                if not merged then
                    mkItem (productId, price, quantity) |]
        let remove (productId, price) (current : Item[]) =
            current |> Array.where (fun x -> keys x <> (productId, price))
    let private evolve s = function
        | Events.Initialized e ->   { s with clientId = Some e.clientId }
        | Events.ItemAdded e ->     { s with items = s.items |> ItemList.add (e.productId, e.unitPrice, e.quantity) }
        | Events.ItemRemoved e ->   { s with items = s.items |> ItemList.remove (e.productId, e.unitPrice) }
        | Events.Confirmed e ->     { s with status = Confirmed; confirmedAt = Some e.confirmedAt }
        | Events.Registering e ->   { s with confirmedOriginEpoch = Some e.originEpoch }
    let fold = Seq.fold evolve

let decideInitialize clientId (s : Fold.State) =
    if s.clientId <> None then []
    else [ Events.Initialized {| clientId = clientId |}]

let decideAdd calculatePrice productId quantity state = async {
    match state with
    | s when Fold.isClosed s -> return invalidOp $"Adding product item for cart in '%A{s.status}' status is not allowed."
    | _ ->
        let! price = calculatePrice (productId, quantity)
        return (), [ Events.ItemAdded {| productId = productId; unitPrice = price; quantity = quantity |} ] }

let decideRemove (productId, price) = function
    | s when Fold.isClosed s -> invalidOp $"Removing product item for cart in '%A{s.status}' status is not allowed."
    | _ ->
        [ Events.ItemRemoved {| productId = productId; unitPrice = price |} ]

let decideConfirm at = function
    | s when Fold.isClosed s -> []
    | _ -> [ Events.Confirmed {| confirmedAt = at |} ]

module Details =

    type View = { (* id *) clientId : ClientId; status : Fold.Status; items : Item[] }
     and Item = { productId : ProductId; unitPrice : decimal; quantity : int }

    let render = function
        | ({ clientId = None } : Fold.State) -> None
        | { clientId = Some clientId } as s ->
            let items = [| for { productId = productId; quantity = q; unitPrice = p } in s.items ->
                             { productId = productId; unitPrice = p; quantity = q } |]
            Some {  clientId = clientId; status = s.status; items = items }

let summarizeWithOriginEpoch getActiveEpochId state = async {
    match state with
    | s when not (Fold.isClosed s) -> return failwith "Unexpected"
    | { confirmedOriginEpoch = Some originEpoch } as s ->
        return (Details.render s |> Option.get, originEpoch), []
    | { confirmedOriginEpoch = None } as s ->
        let! originEpoch = getActiveEpochId ()
        return (Details.render s |> Option.get, originEpoch), [ Events.Registering {| originEpoch = originEpoch |} ] }

type Service(resolve : CartId -> Equinox.Decider<Events.Event, Fold.State>, calculatePrice : ProductId * int -> Async<decimal>) =

    member _.Initialize(cartId, clientId) =
        let decider = resolve cartId
        decider.Transact(decideInitialize clientId)

    member _.Add(cartId, productId, quantity) =
        let decider = resolve cartId
        decider.TransactAsync(decideAdd calculatePrice productId quantity)

    member _.Remove(cartId, productId, price) =
        let decider = resolve cartId
        decider.Transact(decideRemove (productId, price))

    member _.Confirm(cartId, at) =
        let decider = resolve cartId
        decider.Transact(decideConfirm at)

    // NOTE doing this does not fulfil the CQRS principle to the letter
    // However, its not unrealistic for this demo in that
    // a) it means you can read your writes immediately
    // b) it's not unreasonable in efficiency terms
    // - on Cosmos, you pay only 1RU to read through the cache with the etag
    // - on EventStoreDB you are reading forward from a cached stream and hence are typically doing a roundtrip that does not send any events
    member _.Read(cartId) : Async<Details.View option> =
        let decider = resolve cartId
        decider.Query(Details.render)

    /// Summarizes the contents of the cart
    /// Decides the tranche from where the insertion into the PoolTranches is to commence
    member _.SummarizeWithOriginEpoch(cartId, getActiveEpochId) : Async<Details.View * ConfirmedEpochId> =
        let decider = resolve cartId
        decider.TransactAsync(summarizeWithOriginEpoch getActiveEpochId)

    /// Render view (and emit version on which it was based) for Denormalizer to store
    member _.SummarizeWithVersion(cartId) : Async<Details.View option * int64> =
        let decider = resolve cartId
        decider.QueryEx(fun c -> Details.render c.State, c.Version)

module Config =

    // Adapts the external Pricing algorithm interface shape (see IProductPriceCalculator) to what's required by `type Service`
    let calculatePrice (pricer : ProductId -> Async<decimal>) (productId, _quantity) : Async<decimal> =
        pricer productId

    let private resolveStream = function
        | Config.Store.Memory store ->
            let cat = Config.Memory.create Events.codec Fold.initial Fold.fold store
            cat.Resolve
        | Config.Store.Esdb (context, cache) ->
            let cat = Config.Esdb.createUnoptimized Events.codec Fold.initial Fold.fold (context, cache)
            cat.Resolve
        | Config.Store.Cosmos (context, cache) ->
            let cat = Config.Cosmos.createUnoptimized Events.codec Fold.initial Fold.fold (context, cache)
            cat.Resolve
    let private resolveDecider store = streamName >> resolveStream store >> Config.createDecider
    let create_ pricer store =
        Service(resolveDecider store, calculatePrice pricer)
    let create =
        let defaultCalculator = RandomProductPriceCalculator()
        create_ defaultCalculator.Calculate
