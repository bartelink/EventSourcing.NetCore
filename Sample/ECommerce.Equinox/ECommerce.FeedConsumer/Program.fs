﻿module ECommerce.FeedConsumer.Program

open ECommerce.Infrastructure // ConnectStore etc
open ECommerce.Domain // Config etc
open Serilog
open System

type Configuration(tryGet) =
    inherit Args.Configuration(tryGet)

    member _.BaseUri =                      base.get "API_BASE_URI"
    member _.Group =                        base.get "API_CONSUMER_GROUP"

let [<Literal>] AppName = "FeedConsumer"

module Args =

    open Argu
    open Args.Esdb

    [<NoEquality; NoComparison>]
    type Parameters =
        | [<AltCommandLine "-V"; Unique>]   Verbose
        | [<AltCommandLine "-p"; Unique>]   PrometheusPort of int

        | [<AltCommandLine "-g"; Unique>]   Group of string
        | [<AltCommandLine "-f"; Unique>]   BaseUri of string

        | [<AltCommandLine "-r"; Unique>]   MaxReadAhead of int
        | [<AltCommandLine "-w"; Unique>]   FcsDop of int
        | [<AltCommandLine "-t"; Unique>]   TicketsDop of int

        | [<CliPrefix(CliPrefix.None); Unique; Last>] Cosmos of ParseResults<Args.Cosmos.Parameters>
        | [<CliPrefix(CliPrefix.None); Unique; Last>] Dynamo of ParseResults<Args.Dynamo.Parameters>
        | [<CliPrefix(CliPrefix.None); Last>] Esdb of ParseResults<Args.Esdb.Parameters>
        interface IArgParserTemplate with
            member a.Usage = a |> function
                | Verbose _ ->              "request verbose logging."
                | PrometheusPort _ ->       "port from which to expose a Prometheus /metrics endpoint. Default: off (optional if environment variable PROMETHEUS_PORT specified)"
                | Group _ ->                "specify Api Consumer Group Id. (optional if environment variable API_CONSUMER_GROUP specified)"
                | BaseUri _ ->              "specify Api endpoint. (optional if environment variable API_BASE_URI specified)"
                | MaxReadAhead _ ->         "maximum number of batches to let processing get ahead of completion. Default: 8."
                | FcsDop _ ->               "maximum number of FCs to process in parallel. Default: 4"
                | TicketsDop _ ->           "maximum number of Tickets to process in parallel (per FC). Default: 4"
                | Cosmos _ ->               "specify CosmosDB input parameters"
                | Dynamo _ ->               "specify DynamoDB input parameters"
                | Esdb _ ->                 "specify EventStore input parameters"

    type Arguments(c : Configuration, a : ParseResults<Parameters>) =
        member val Verbose =                a.Contains Verbose
        member val PrometheusPort =         a.TryGetResult PrometheusPort |> Option.orElseWith (fun () -> c.PrometheusPort)
        member val SourceId =               a.TryGetResult Group        |> Option.defaultWith (fun () -> c.Group) |> Propulsion.Feed.SourceId.parse
        member val BaseUri =                a.TryGetResult BaseUri      |> Option.defaultWith (fun () -> c.BaseUri) |> Uri
        member val MaxReadAhead =           a.GetResult(MaxReadAhead,8)
        member val FcsDop =                 a.TryGetResult FcsDop       |> Option.defaultValue 4
        member val TicketsDop =             a.TryGetResult TicketsDop   |> Option.defaultValue 4
        member val StatsInterval =          TimeSpan.FromMinutes 1.
        member val StateInterval =          TimeSpan.FromMinutes 5.
        member val CheckpointInterval =     TimeSpan.FromHours 1.
        member val TailSleepInterval =      TimeSpan.FromSeconds 1.
        member val ConsumerGroupName =      "default"
        member val StoreArgs : Args.StoreArguments =
            match a.TryGetSubCommand() with
            | Some (Parameters.Cosmos cosmos) -> Args.StoreArguments.Cosmos (Args.Cosmos.Arguments (c, cosmos))
            | Some (Parameters.Dynamo dynamo) -> Args.StoreArguments.Dynamo (Args.Dynamo.Arguments (c, dynamo))
            | Some (Parameters.Esdb es) -> Args.StoreArguments.Esdb (Args.Esdb.Arguments (c, es))
            | _ -> Args.missingArg "Must specify one of cosmos, dynamo or esdb for store"
        member x.VerboseStore = Args.verboseRequested x.StoreArgs
        member x.DumpStoreMetrics = Args.dumpMetrics x.StoreArgs
        member x.Connect() : Config.Store<_> =
            let cache = Equinox.Cache (AppName, sizeMb = 10)
            match x.StoreArgs with
            | Args.StoreArguments.Cosmos a ->
                let context = a.Connect() |> Async.RunSynchronously |> CosmosStoreContext.create
                Config.Store.Cosmos (context, cache)
            | Args.StoreArguments.Dynamo a ->
                let context = a.Connect() |> DynamoStoreContext.create
                Config.Store.Dynamo (context, cache)
            | Args.StoreArguments.Esdb a ->
                let context = a.Connect(Log.Logger, AppName, EventStore.Client.NodePreference.Leader) |> EventStoreContext.create
                Config.Store.Esdb (context, cache)
        member x.CheckpointStoreConfig(mainStore) : CheckpointStore.Config =
            match mainStore with
            | Config.Store.Cosmos (context, cache) -> CheckpointStore.Config.Cosmos (context, cache)
            | Config.Store.Dynamo (context, cache) -> CheckpointStore.Config.Dynamo (context, cache)
            | Config.Store.Memory _ -> failwith "Unexpected"
            | Config.Store.Esdb (_, cache) ->
                match x.StoreArgs with
                | Args.StoreArguments.Esdb a ->
                    match a.CheckpointStoreArgs with
                    | CheckpointStoreArguments.Cosmos a ->
                        let context = a.Connect() |> Async.RunSynchronously |> CosmosStoreContext.create
                        CheckpointStore.Config.Cosmos (context, cache)
                    | CheckpointStoreArguments.Dynamo a ->
                        let context = a.Connect() |> DynamoStoreContext.create
                        CheckpointStore.Config.Dynamo (context, cache)
                 | _ -> failwith "unexpected"
        member x.CreateCheckpointStore(config) : Propulsion.Feed.IFeedCheckpointStore =
            CheckpointStore.create (x.ConsumerGroupName, x.CheckpointInterval) Config.log config

    /// Parse the commandline; can throw exceptions in response to missing arguments and/or `-h`/`--help` args
    let parse tryGetConfigValue argv =
        let programName = Reflection.Assembly.GetEntryAssembly().GetName().Name
        let parser = ArgumentParser.Create<Parameters>(programName = programName)
        Arguments(Configuration tryGetConfigValue, parser.ParseCommandLine argv)

let build (args : Args.Arguments) =
    let store = args.Connect()

    let log = Log.forGroup args.SourceId // needs to have a `group` tag for Propulsion.Streams Prometheus metrics
    let sink =
        let handle = Ingester.handle args.TicketsDop
        let stats = Ingester.Stats(log, args.StatsInterval, args.StateInterval, logExternalStats = args.DumpStoreMetrics)
        Propulsion.Streams.StreamsProjector.Start(log, args.MaxReadAhead, args.FcsDop, handle, stats, args.StatsInterval)
    let pumpSource =
        let checkpoints = args.CreateCheckpointStore(args.CheckpointStoreConfig store)
        let feed = ApiClient.TicketsFeed args.BaseUri
        let source =
            Propulsion.Feed.FeedSource(
                log, args.StatsInterval, args.SourceId, args.TailSleepInterval,
                checkpoints, feed.Poll, sink)
        source.Pump feed.ReadTranches
    sink, pumpSource

let run args = async {
    let sink, pumpSource = build args
    do! Async.Parallel [ pumpSource; sink.AwaitWithStopOnCancellation() ] |> Async.Ignore<unit[]>
    return if sink.RanToCompletion then 0 else 3
}

[<EntryPoint>]
let main argv =
    try let args = Args.parse EnvVar.tryGet argv
        try let metrics = Sinks.equinoxAndPropulsionFeedConsumerMetrics (Sinks.tags AppName)
            Log.Logger <- LoggerConfiguration().Configure(args.Verbose).Sinks(metrics, args.VerboseStore).CreateLogger()
            try run args |> Async.RunSynchronously
            with e when not (e :? Args.MissingArg) -> Log.Fatal(e, "Exiting"); 2
        finally Log.CloseAndFlush()
    with Args.MissingArg msg -> eprintfn "%s" msg; 1
        | :? Argu.ArguParseException as e -> eprintfn "%s" e.Message; 1
        | e -> eprintf "Exception %s" e.Message; 1
