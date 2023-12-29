﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Actions
{
    using System;
    using System.Collections.Generic;
    using System.IO.Abstractions;
    using System.IO.Packaging;
    using System.Reflection.Metadata.Ecma335;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Identity.Client;
    using Polly;
    using VirtualClient.Common;
    using VirtualClient.Common.Extensions;
    using VirtualClient.Common.Telemetry;
    using VirtualClient.Contracts;
    using VirtualClient.Contracts.Metadata;

    /// <summary>
    /// The MongoDB workload executor.
    /// </summary>
    public class MongoDBExecutor : VirtualClientComponent
    {
        private IPackageManager packageManager;
        private ISystemManagement systemManager;

        /// <summary>
        /// Constructor for <see cref="MongoDBExecutor"/>
        /// </summary>
        /// <param name="dependencies">Provides required dependencies to the component.</param>
        /// <param name="parameters">Parameters defined in the profile or supplied on the command line.</param>
        public MongoDBExecutor(IServiceCollection dependencies, IDictionary<string, IConvertible> parameters)
             : base(dependencies, parameters)
        {
            this.packageManager = dependencies.GetService<IPackageManager>();
            this.systemManager = this.Dependencies.GetService<ISystemManagement>();
        }

        /// <summary>
        /// The retry policy to apply to package install for handling transient errors.
        /// </summary>
        public IAsyncPolicy InstallRetryPolicy { get; set; } = Policy
            .Handle<WorkloadException>(exc => exc.Reason == ErrorReason.DependencyInstallationFailed)
            .WaitAndRetryAsync(5, (retries) => TimeSpan.FromSeconds(retries * 2));

        /// <summary>
        /// Defines the name of the YCSB package.
        /// </summary>
        public string YCSBPackageName
        {
            get
            {
                return this.Parameters.GetValue<string>(nameof(this.YCSBPackageName));
            }
        }

        /// <summary>
        /// Java Development Kit package name.
        /// </summary>
        public string JdkPackageName
        {
            get
            {
                return this.Parameters.GetValue<string>(nameof(MongoDBExecutor.JdkPackageName));
            }
        }

        /// <summary>
        /// The file path for YCSB workloads.
        /// </summary>
        protected string YcsbPackagePath { get; set; }

        /// <summary>
        /// The file path for MongoDB
        /// </summary>
        protected string MongoDBPackagePath { get; set; }

        /// <summary>
        /// The file path for YCSB workloads.
        /// </summary>
        protected string JDKPackagePath { get; set; }

        /// <summary>
        /// MongoDB mount path
        /// </summary>
        protected string DBPath { get; set; }

        /// <summary>
        /// Initializes the environment for execution of the MongoDB YCSB workload.
        /// </summary>
        protected override async Task InitializeAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            DependencyPath javaPackage = await this.GetPackageAsync(
               this.JdkPackageName, cancellationToken);
            if (javaPackage == null)
            {
                throw new DependencyException(
                   $"The expected package '{this.PackageName}' does not exist on the system or is not registered.",
                   ErrorReason.WorkloadDependencyMissing);
            }

            this.JDKPackagePath = javaPackage.Path; 

            DependencyPath mongoDBPackage = await this.packageManager.GetPackageAsync(this.PackageName, CancellationToken.None);

            if (mongoDBPackage == null)
            {
                throw new DependencyException(
                    $"The expected package '{this.PackageName}' does not exist on the system or is not registered.",
                    ErrorReason.WorkloadDependencyMissing);
            }

            this.MongoDBPackagePath = mongoDBPackage.Path;

            DependencyPath ycsbPackage = await this.packageManager.GetPackageAsync(this.YCSBPackageName, CancellationToken.None);

            if (mongoDBPackage == null)
            {
                throw new DependencyException(
                    $"The expected package '{this.PackageName}' does not exist on the system or is not registered.",
                    ErrorReason.WorkloadDependencyMissing);
            }

            this.YcsbPackagePath = ycsbPackage.Path;

            // string pathtobin = this.PlatformSpecifics.Combine(this.PackageDirectory, "bin");

        }

        /// <summary>
        /// Executes the MongoDB YCSB workload.
        /// </summary>
        protected override async Task ExecuteAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            this.DBPath = this.PlatformSpecifics.Combine(this.MongoDBPackagePath, "mongodb-linux-x86_64-ubuntu2004-5.0.15", "bin", "mongod");

            try
            {
                string ycsbPath = this.PlatformSpecifics.Combine(this.YcsbPackagePath, "ycsb-0.5.0", "bin", "ycsb");
                string javaExecutablePath = this.PlatformSpecifics.Combine(this.JDKPackagePath, "bin", "java");
                string file_to_make_executablePath = this.PlatformSpecifics.Combine(this.YcsbPackagePath, "ycsb-0.5.0", "bin");

                string loadArguments = this.GetCommandLineArguments("load");
                string runArguments = this.GetCommandLineArguments("run");
                string file_to_make_executable = "ycsb";

                Console.WriteLine($"javaExecutablePath - {javaExecutablePath}");
                Console.WriteLine($"mountPath - {this.DBPath}");
                Console.WriteLine($"ycsbPath - {ycsbPath}");
                Console.WriteLine($"ycsbPath1 - {this.YcsbPackagePath}");
                Console.WriteLine($"created  db location ");

                this.SetEnvironmentVariable(EnvironmentVariable.JAVA_HOME, javaExecutablePath, EnvironmentVariableTarget.Process);

                await this.ExecuteCommandAsync("chmod", $"+x {file_to_make_executable}", file_to_make_executablePath, telemetryContext, cancellationToken).ConfigureAwait(false);
                await this.ExecuteCommandAsync("mkdir", " -p /tmp/mongodb", this.MongoDBPackagePath, telemetryContext, cancellationToken)
                    .ConfigureAwait(false);
                await this.ExecuteCommandAsync($"{this.DBPath}", " --fork --dbpath /tmp/mongodb --logpath /tmp/mongod.log", this.MongoDBPackagePath, telemetryContext, cancellationToken)
                           .ConfigureAwait(false);

                /* ./ycsb load mongodb -s -P /home/azureuser/vc/content/linux-x64/packages/ycsb/ycsb-0.5.0/workloads/workloada -p recordcount=1000000 -threads 16
                */
                DateTime loadStartTime = DateTime.UtcNow;
                var loadOutput = await this.ExecuteCommandAsync($"{ycsbPath}", loadArguments, this.YcsbPackagePath, telemetryContext, cancellationToken)
                         .ConfigureAwait(false);
                DateTime loadFinishTime = DateTime.UtcNow;
                Console.WriteLine($"loadOutput - {loadOutput}");

                await this.CaptureMetricsAsync(loadOutput, loadArguments, loadStartTime, loadFinishTime, telemetryContext, cancellationToken)
                    .ConfigureAwait(false);

                /*./ycsb run mongodb -s -P /home/azureuser/vc/content/linux-x64/packages/ycsb/ycsb-0.5.0/workloads/workloada -p operationcount=1000000 -p recordcount=1000000 -threads 16
                */
                DateTime runStartTime = DateTime.UtcNow;
                var runOutput = await this.ExecuteCommandAsync($"{ycsbPath}", runArguments, this.YcsbPackagePath, telemetryContext, cancellationToken)
                         .ConfigureAwait(false);
                DateTime runFinishTime = DateTime.UtcNow;

                Console.WriteLine($"runOutput - {runOutput}");

                await this.CaptureMetricsAsync(runOutput, runArguments, runStartTime, runFinishTime, telemetryContext, cancellationToken)
                    .ConfigureAwait(false);

                await this.ShutDownMongoDB(telemetryContext, cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                await this.ShutDownMongoDB(telemetryContext, cancellationToken);
                telemetryContext.AddError(ex);
                this.Logger.LogTraceMessage($"{nameof(ExampleWorkloadExecutor)}.Exception", telemetryContext);
            }
        }

        private async Task<string> ExecuteCommandAsync(string command, string commandArguments, string workingDirectory, EventContext telemetryContext, CancellationToken cancellationToken)
        {
            EventContext relatedContext = EventContext.Persisted()
                .AddContext(nameof(command), command)
                .AddContext(nameof(commandArguments), commandArguments);

            using (IProcessProxy process = this.systemManager.ProcessManager.CreateElevatedProcess(this.Platform, command, commandArguments, workingDirectory))
            {
                this.CleanupTasks.Add(() => process.SafeKill());
                this.LogProcessTrace(process);

                await process.StartAndWaitAsync(cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                {
                    if (process.IsErrored())
                    {
                        await this.LogProcessDetailsAsync(process, relatedContext, logToFile: true);
                        process.ThrowIfWorkloadFailed();
                    }
                }

                return process.StandardOutput.ToString();
            }
        }

        private string GetCommandLineArguments(string commandType)
        {
            string workloadPath = this.PlatformSpecifics.Combine(this.YcsbPackagePath, "ycsb-0.5.0", "workloads", "workloada");
            if (commandType == "load")
            {
                return $"load mongodb -s -P {workloadPath} -p recordcount={this.Parameters["RecordCount"]} -threads {this.Parameters["ThreadCount"]}";
            }
            else
            {
                return $"run mongodb -s -P {workloadPath} -p operationcount={this.Parameters["OperationCount"]} -p recordcount={this.Parameters["RecordCount"]} -threads {this.Parameters["ThreadCount"]}";
            }
        }

        private async Task CaptureMetricsAsync(string results, string commandArguments, DateTime startTime, DateTime endtime, EventContext telemetryContext, CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    this.MetadataContract.AddForScenario(
                     "MongoDB",
                     commandArguments,
                     toolVersion: null);

                    this.MetadataContract.Apply(telemetryContext);

                    results.ThrowIfNullOrWhiteSpace(nameof(results));
                    this.Logger.LogMessage($"{nameof(MongoDBExecutor)}.CaptureMetrics", telemetryContext.Clone()
                        .AddContext("results", results));

                    MongoDBMetricsParser resultsParser = new MongoDBMetricsParser(results);
                    IList<Metric> workloadMetrics = resultsParser.Parse();
                    foreach (Metric metric in workloadMetrics)
                    {
                        Console.WriteLine($"metric Name {metric.Name}, unit {metric.Unit} value {metric.Value} {metric.Description} {metric.Relativity}");
                    }

                    this.Logger.LogMetrics(
                        toolName: nameof(MongoDBExecutor),
                        scenarioName: this.Scenario,
                        scenarioStartTime: startTime,
                        scenarioEndTime: endtime,
                        metrics: workloadMetrics,
                        metricCategorization: null,
                        scenarioArguments: commandArguments,
                        this.Tags,
                        telemetryContext);
                }
                catch (SchemaException ex)
                {
                    await this.ShutDownMongoDB(telemetryContext, cancellationToken);
                    telemetryContext.AddError(ex);
                    throw new WorkloadResultsException($"Failed to parse workload results.", ex, ErrorReason.WorkloadResultsParsingFailed);
                }

            }
        }

        private async Task ShutDownMongoDB(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            /*  sudo /home/azureuser/vc/content/linux-x64/packages/mongodb/mongodb-linux-x86_64-ubuntu2004-5.0.15/bin/mongod--dbpath /tmp/mongodb--shutdown
                */
            await this.ExecuteCommandAsync($"{this.DBPath}", "--dbpath /tmp/mongodb --shutdown", this.MongoDBPackagePath, telemetryContext, cancellationToken).ConfigureAwait(false);

            await this.ExecuteCommandAsync("rm", "-rf /tmp/*", this.MongoDBPackagePath, telemetryContext, cancellationToken).ConfigureAwait(false);

            /* Adding delay of 1minute for graceful shutdown of mongodb */
            await Task.Delay(60000).ConfigureAwait(false);

        }
    }
}