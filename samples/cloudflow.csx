﻿#r "../../packages/FSharp.Core/lib/net40/FSharp.Core.dll"
#r "../../packages/System.Runtime.Loader/lib/DNXCore50/System.Runtime.Loader.dll"
#r "../../bin/FsPickler.dll"
#r "../../bin/Vagabond.dll"
#r "../../bin/Argu.dll"
#r "System.Net.dll"
#r "../../bin/Newtonsoft.Json.dll"
#r "../../bin/Hyak.Common.dll"
#r "../../bin/Microsoft.Data.Edm.dll"
#r "../../bin/Microsoft.Data.OData.dll"
#r "../../bin/Microsoft.Threading.Tasks.dll"
#r "../../bin/Microsoft.Azure.Common.dll"
#r "../../bin/Microsoft.WindowsAzure.Common.dll"
#r "../../bin/Microsoft.WindowsAzure.Common.NetFramework.dll"
#r "../../bin/Microsoft.WindowsAzure.Configuration.dll"
#r "../../bin/Microsoft.WindowsAzure.Management.dll"
#r "../../bin/Microsoft.WindowsAzure.Management.Compute.dll"
#r "../../bin/Microsoft.WindowsAzure.Management.Storage.dll"
#r "../../bin/Microsoft.WindowsAzure.Management.ServiceBus.dll"
#r "../../bin/MBrace.Core.dll"
#r "../../bin/MBrace.Runtime.dll"
#r "../../bin/MBrace.Azure.dll"
#r "../../bin/MBrace.Azure.Management.dll"
#r "../../bin/Streams.dll"
#r "../../bin/MBrace.Flow.dll"
#r "../../packages/MBrace.CSharp/lib/net472/MBrace.CSharp.dll"

// before running sample, don't forget to set binding redirects to FSharp.Core in InteractiveHost.exe

using System;
using System.IO;
using System.Linq;
using Microsoft.FSharp.Core;
using MBrace.Core;
using MBrace.Core.CSharp;
using MBrace.Flow.CSharp;
using MBrace.Library;
using MBrace.Azure;
using MBrace.Azure.Management;

AzureWorker.LocalExecutable = "../../bin/mbrace.azureworker.exe";
var pubSettings = "Replace with path to your local .publishsettings file";
var subscriptionId = "subscription name";
var region = Region.North_Europe;
var logger = (MBrace.Runtime.ISystemLogger)new MBrace.Runtime.ConsoleLogger();

/// <summary>
///     Resolves a locally built CS package
/// </summary>
/// <returns></returns>
string GetLocalCsPkg()
{
    var path = Path.GetFullPath("../../bin/cspkg/app.publish/MBrace.Azure.CloudService.cspkg");
    if (!File.Exists(path))
        throw new InvalidOperationException("Right click on the 'MBrace.Azure.CloudService' project and hit 'Package...'.");
    return path;
}

var manager = SubscriptionManager.FromPublishSettingsFile(pubSettings, 
                                                            defaultRegion: region, 
                                                            subscriptionId: subscriptionId.ToOption(),
                                                            logger: logger.ToOption());


var deployment = manager.Provision(vmCount: 4, 
                                    serviceName: "mbraceTests".ToOption(), 
                                    vmSize: VMSize.A3.ToOption(),
                                    cloudServicePackage: GetLocalCsPkg().ToOption());
// var deployment = manager.GetDeployment("mbraceTests");

deployment.ShowInfo();

var cluster = AzureCluster.Connect(deployment.Configuration, logger: logger.ToOption());

// 1. Hello, World
var getMachineName = CloudBuilder.FromFunc(() => Environment.MachineName);
cluster.Run(getMachineName);

cluster.ShowProcesses();

cluster.Workers;

// 2. Parallel workflow
var inputs = Enumerable.Range(1, 10000000);
var pworkflow =
    CloudBuilder
        .ParallelMap(inputs, x => (2 * x + 1) % 100)
        .OnSuccess(xs => xs.Sum());

cluster.Run(pworkflow);

// 3. CloudFlow.CSharp tests
var url = "http://prod.publicdata.landregistry.gov.uk.s3-website-eu-west-1.amazonaws.com/pp-2015.csv";

string trim(string input) { return input.Trim(new char[] { '\"' }); }

var cacheF = CloudFlow.OfHttpFileByLine(url)
                    .Select(line => line.Split(','))
                    .Select(arr => new { TransactionId = Guid.Parse(trim(arr[0])), Price = Double.Parse(trim(arr[1])), City = trim(arr[12]) })
                    .Cache();

var cacheFlowProc = cluster.CreateProcess(cacheF); // Start caching process

cacheFlowProc.ShowInfo();


var cachedFlow = cacheFlowProc.Result; // get the cached CloudFlow

var top10London =
    cachedFlow
        .Where(trans => trans.City.ToLower() == "london")
        .OrderByDescending(trans => trans.Price, 10)
        .ToArray();

cluster.Run(top10London);

var maxAverageCity =
    cachedFlow
        .GroupBy(trans => trans.City.ToLower())
        .Select(gp => new { City = gp.Key, Average = gp.Value.Select(t => t.Price).Average() }) 
        .MaxBy(city => city.Average);

cluster.Run(maxAverageCity);
