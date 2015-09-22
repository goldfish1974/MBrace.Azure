﻿namespace MBrace.Azure

#nowarn "40"
#nowarn "444"
#nowarn "445"

open System
open System.Diagnostics
open System.IO
open MBrace.Core
open MBrace.Core.Internals
open MBrace.Azure
open MBrace.Azure.Runtime
open MBrace.Azure.Runtime.Arguments
open MBrace.Runtime

/// A system logger that writes entries to stdout
type ConsoleLogger = MBrace.Runtime.ConsoleLogger
/// Struct that specifies a single system log entry
type SystemLogEntry = MBrace.Runtime.SystemLogEntry
/// Struct that specifies a single cloud log entry
type CloudLogEntry = MBrace.Runtime.CloudLogEntry
/// Log level used by the MBrace runtime implementation
type LogLevel = MBrace.Runtime.LogLevel
/// A Serializable object used to identify a specific worker in a cluster.
/// Can be used to point computations for execution at specific machines.
type WorkerRef = MBrace.Runtime.WorkerRef
/// Represents a distributed computation that is being executed by an MBrace runtime
type CloudProcess = MBrace.Runtime.CloudProcess
/// Represents a distributed computation that is being executed by an MBrace runtime
type CloudProcess<'T> = MBrace.Runtime.CloudProcess<'T>

/// <summary>
///     Windows Azure Cluster management client. Provides methods for management, execution and debugging of MBrace tasks in Azure.
/// </summary>
[<AutoSerializable(false)>]
type AzureCluster private (manager : ClusterManager) =
    inherit MBraceClient(manager)

    static let lockObj = obj()
    static let mutable localWorkerExecutable : string option = None
    static do Config.InitClientGlobalState()

    /// Current client instance identifier.
    member this.UUID = manager.RuntimeManagerId

    /// <summary>
    /// Kill all local worker processes.
    /// </summary>
    member this.KillAllLocalWorkers() =
        let workers = this.Workers
        let exists id hostname =
            if Net.Dns.GetHostName() = hostname then
                let ps = try Some <| Process.GetProcessById(id) with :? ArgumentException -> None
                ps.IsSome
            else false
        workers |> Seq.filter (fun w -> exists w.ProcessId w.Hostname)    
                |> Seq.iter this.KillLocalWorker

    /// <summary>
    /// Kill local worker process.
    /// </summary>
    /// <param name="worker">Local worker to kill.</param>
    member this.KillLocalWorker(worker : WorkerRef) =
        let hostname = System.Net.Dns.GetHostName()
        if worker.Hostname <> hostname then
            failwith "WorkerRef hostname %A does not match local hostname %A" worker.Hostname hostname
        else
            let ps = try Some <| Process.GetProcessById(worker.ProcessId) with :? ArgumentException -> None
            match ps with
            | Some p ->
                (manager :> IRuntimeManager).WorkerManager.DeclareWorkerStatus(worker.WorkerId, CloudWorkItemExecutionStatus.Stopped)
                |> Async.RunSync
                p.Kill()
            | _ ->
                failwithf "No local process with Id = %d found." worker.ProcessId

    /// Delete and reactivate runtime state (queues, containers, tables and logs but not user folders).
    /// Using 'Reset' may cause unexpected behavior in clients and workers.
    [<CompilerMessage("Using 'Reset' may cause unexpected behavior in clients and workers.", 445)>]
    member this.Reset () = 
        (manager :> IRuntimeManager).ResetClusterState()
        |> Async.RunSync

    /// <summary>
    /// Delete and re-activate runtime state.
    /// Using 'Reset' may cause unexpected behavior in clients and workers.
    /// Workers should be restarted manually.</summary>
    /// <param name="deleteQueues">Delete Configuration queue and topic.</param>
    /// <param name="deleteState">Delete Configuration table and containers.</param>
    /// <param name="deleteLogs">Delete Configuration logs table.</param>
    /// <param name="deleteUserData">Delete Configuration UserData table and container.</param>
    /// <param name="deleteVagabondData">Delete Vagabond assembly data container.</param>
    /// <param name="force">Ignore active workers.</param>
    /// <param name="reactivate">Reactivate configuration.</param>
    [<CompilerMessage("Using 'Reset' may cause unexpected behavior in clients and workers.", 445)>]
    member this.Reset(deleteQueues, deleteState, deleteLogs, deleteUserData, deleteVagabondData, force, reactivate) =
        manager.ResetCluster(deleteQueues, deleteState, deleteLogs, deleteUserData, deleteVagabondData, force, reactivate)
        |> Async.RunSync

    /// <summary>
    ///     Spawns a worker instance in the local machine, subscribed to the current cluster configuration.
    /// </summary>
    /// <param name="maxWorkItems">Maximum number of concurrent jobs in the spawned worker.</param>
    /// <param name="logLevel">LogLevel used by the worker.</param>
    member this.AttachLocalWorker(?maxWorkItems:int, ?logLevel:LogLevel) =
        AzureCluster.SpawnOnCurrentMachine(manager.Configuration, 1, ?maxWorkItems = maxWorkItems, ?logLevel = logLevel)

    /// Gets or sets the path for a local standalone worker executable.
    static member LocalWorkerExecutable
        with get () = match localWorkerExecutable with None -> invalidOp "unset executable path." | Some e -> e
        and set path = 
            lock lockObj (fun () ->
                let path = Path.GetFullPath path
                if File.Exists path then localWorkerExecutable <- Some path
                else raise <| FileNotFoundException(path))

    /// <summary>
    ///     Initialize a new local runtime instance with supplied worker count and return a handle.
    /// </summary>
    /// <param name="config">Azure runtime configuration.</param>
    /// <param name="workerCount">Number of local workers to spawn.</param>
    /// <param name="maxWorkItems">Maximum number of concurrent jobs per worker.</param>
    /// <param name="workerNameF">Worker name factory.</param>
    /// <param name="logLevel">Client and local worker logger verbosity level.</param>
    static member SpawnOnCurrentMachine(config : Configuration, workerCount, ?maxWorkItems : int, ?workerNameF : int -> string, ?logLevel : LogLevel) =
        let exe = AzureCluster.LocalWorkerExecutable
        if workerCount < 1 then invalidArg "workerCount" "must be positive."  
        let cfg = { Arguments.Configuration = config; Arguments.MaxWorkItems = defaultArg maxWorkItems Environment.ProcessorCount; Name = None; LogLevel = logLevel }
        do Config.Activate config
        let _ = Array.Parallel.init workerCount (fun i -> 
            let conf =
                match workerNameF with
                | None -> cfg
                | Some f -> {cfg with Name = Some(f(i)) }
            let args = Config.ToBase64Pickle conf
            let psi = new ProcessStartInfo(exe, args)
            psi.WorkingDirectory <- Path.GetDirectoryName exe
            psi.UseShellExecute <- true
            Process.Start psi)
        ()

    /// <summary>
    ///     Initialize a new local runtime instance with supplied worker count and return a handle.
    /// </summary>
    /// <param name="config">Azure runtime configuration.</param>
    /// <param name="workerCount">Number of local workers to spawn.</param>
    /// <param name="maxWorkItems">Maximum number of concurrent jobs per worker.</param>
    /// <param name="workerNameF">Worker name factory.</param>
    /// <param name="clientId">Client instance identifier.</param>
    /// <param name="logger">Client logger to attach.</param>
    /// <param name="logLevel">Client and local worker logger verbosity level.</param>
    static member InitOnCurrentMachine(config : Configuration, workerCount : int, ?maxWorkItems : int, ?workerNameF, ?clientId : string, ?logger : ISystemLogger, ?logLevel : LogLevel) : AzureCluster =
        AzureCluster.SpawnOnCurrentMachine(config, workerCount, ?maxWorkItems = maxWorkItems, ?workerNameF = workerNameF, ?logLevel = logLevel)
        AzureCluster.Connect(config, ?clientId = clientId, ?logger = logger, ?logLevel = logLevel)

    /// <summary>
    ///     Connects to an MBrace-on-Azure cluster as identified by provided store and service bus connection strings.
    ///     If successful returns a management handle object to the cluster.
    /// </summary>
    /// <param name="storageConnectionString">Azure Storage connection string.</param>
    /// <param name="serviceBusConnectionString">Azure Service Bus connection string.</param>
    /// <param name="clientId">Custom client id for this instance.</param>
    /// <param name="logger">Custom logger to attach in client.</param>
    /// <param name="logLevel">Logger verbosity level.</param>
    static member Connect(storageConnectionString : string, serviceBusConnectionString : string,  ?clientId : string, ?logger : ISystemLogger, ?logLevel : LogLevel) : AzureCluster = 
        AzureCluster.Connect(new Configuration(storageConnectionString, serviceBusConnectionString), ?clientId = clientId, ?logger = logger, ?logLevel = logLevel)

    /// <summary>
    ///     Connects to an MBrace-on-Azure cluster as identified by provided configuration object.
    ///     If successful returns a management handle object to the cluster.
    /// </summary>
    /// <param name="config">Runtime configuration.</param>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="clientId">Custom client id for this instance.</param>
    /// <param name="logger">Custom logger to attach in client.</param>
    /// <param name="logLevel">Logger verbosity level.</param>
    static member Connect(config : Configuration, ?clientId : string, ?logger : ISystemLogger, ?logLevel : LogLevel) : AzureCluster = 
        let hostProc = Diagnostics.Process.GetCurrentProcess()
        let clientId = defaultArg clientId <| sprintf "%s-%s-%05d" (System.Net.Dns.GetHostName()) hostProc.ProcessName hostProc.Id
        let attachableLogger = AttacheableLogger.Create(makeAsynchronous = true, ?logLevel = logLevel)
        let storageLogger = TableSystemLogManager(config).CreateLogWriter(clientId) |> Async.RunSync
        let _ = attachableLogger.AttachLogger(storageLogger)
        let _ = logger |> Option.map attachableLogger.AttachLogger
        let manager = ClusterManager.CreateForClient(config, clientId, attachableLogger, ResourceRegistry.Empty)
        let cluster = new AzureCluster(manager)
        cluster