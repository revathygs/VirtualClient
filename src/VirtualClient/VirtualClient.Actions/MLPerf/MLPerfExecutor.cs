// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Actions
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Abstractions;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using VirtualClient.Actions.NetworkPerformance;
    using VirtualClient.Common;
    using VirtualClient.Common.Extensions;
    using VirtualClient.Common.Platform;
    using VirtualClient.Common.Telemetry;
    using VirtualClient.Contracts;
    using VirtualClient.Dependencies.Packaging;

    /// <summary>
    /// The MLPerf workload executor.
    /// </summary>
    [UnixCompatible]
    public class MLPerfExecutor : VirtualClientComponent
    {
        private IFileSystem fileSystem;
        private IPackageManager packageManager;
        private IStateManager stateManager;
        private ISystemManagement systemManager;

        private IDiskManager diskManager;
        private string mlperfScratchSpace;

        private List<string> benchmarks;
        private Dictionary<string, string> scenarios;
        private Dictionary<string, List<string>> benchmarkConfigs;

        /// <summary>
        /// Constructor for <see cref="MLPerfExecutor"/>
        /// </summary>
        /// <param name="dependencies">Provides required dependencies to the component.</param>
        /// <param name="parameters">Parameters defined in the profile or supplied on the command line.</param>
        public MLPerfExecutor(IServiceCollection dependencies, IDictionary<string, IConvertible> parameters)
             : base(dependencies, parameters)
        {
            this.systemManager = this.Dependencies.GetService<ISystemManagement>();
            this.packageManager = this.systemManager.PackageManager;
            this.stateManager = this.systemManager.StateManager;

            this.fileSystem = this.systemManager.FileSystem;
            this.diskManager = this.systemManager.DiskManager;

            this.benchmarks = new List<string>
            {
                "bert",
                "rnnt",
                "ssd-mobilenet",
                "ssd-resnet34"
            };

            if (string.IsNullOrEmpty(this.DiskFilter))
            {
                this.DiskFilter = "SizeGreaterThan:1000gb&OSDisk:false";
            }
        }

        /// <summary>
        /// Disk filter string to filter disks to format.
        /// </summary>
        public string DiskFilter
        {
            get
            {
                string filter = this.Parameters.GetValue<string>(nameof(MLPerfExecutor.DiskFilter), "SizeGreaterThan:1000gb");
                // Enforce filter to remove OS disk.
                filter = $"{filter}&OSDisk:false";
                return filter;
            }

            set
            {
                this.Parameters[nameof(MLPerfExecutor.DiskFilter)] = value;
            }
        }

        /// <summary>
        /// The user who has the ssh identity registered for.
        /// </summary>
        public string Username => this.Parameters.GetValue<string>(nameof(MLPerfExecutor.Username));

        /// <summary>
        /// The MLPerf Nvidia code directory.
        /// </summary>
        protected string NvidiaDirectory
        {
            get
            {
                return this.PlatformSpecifics.Combine(this.PlatformSpecifics.PackagesDirectory, "mlperf", "closed", "NVIDIA");
            }
        }

        /// <summary>
        /// Export statement for scratch space
        /// </summary>
        protected string ExportScratchSpace { get; set; }

        /// <summary>
        /// The output directory of MLPerf.
        /// </summary>
        protected string OutputDirectory
        {
            get
            {
                return this.PlatformSpecifics.Combine(this.NvidiaDirectory, "build", "logs");
            }
        }

        /// <summary>
        /// Executes the MLPerf workload.
        /// </summary>
        protected override async Task ExecuteAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            DateTime startTime = DateTime.UtcNow;
            this.Logger.LogTraceMessage($"{this.Scenario}.ExecutionStarted", telemetryContext);

            this.PrepareBenchmarkConfigsAndScenarios();

            using (BackgroundOperations profiling = BackgroundOperations.BeginProfiling(this, cancellationToken))
            {
                foreach (string config in this.benchmarkConfigs[this.Scenario])
                {
                    string perfModeExecCommand = $"docker exec -u {this.Username} {this.GetContainerName()} " +
                        $"sudo bash -c \"{this.ExportScratchSpace} && " +
                        $"make run RUN_ARGS=\'--benchmarks={this.Scenario} --scenarios={this.scenarios[this.Scenario]} " +
                        $"--config_ver={config} --test_mode=PerformanceOnly --fast\'\"";

                    string accuracyModeExecCommand = $"docker exec -u {this.Username} {this.GetContainerName()} " +
                        $"sudo bash -c \"{this.ExportScratchSpace} && " +
                        $"make run RUN_ARGS=\'--benchmarks={this.Scenario} --scenarios={this.scenarios[this.Scenario]} " +
                        $"--config_ver={config} --test_mode=AccuracyOnly --fast\'\"";

                    await this.ExecuteCommandAsync("sudo", perfModeExecCommand, this.NvidiaDirectory, cancellationToken);
                    await this.ExecuteCommandAsync("sudo", accuracyModeExecCommand, this.NvidiaDirectory, cancellationToken);
                }
            }

            DateTime endTime = DateTime.UtcNow;
            this.LogOutput(startTime, endTime, telemetryContext);
            this.Logger.LogTraceMessage($"{this.Scenario}.ExecutionCompleted", telemetryContext);
        }

        /// <summary>
        /// Initializes the environment for execution of the MLPerf workload.
        /// </summary>
        protected override async Task InitializeAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            this.Logger.LogTraceMessage($"{this.TypeName}.InitializationStarted", telemetryContext);

            this.ThrowIfPlatformNotSupported();

            await this.ThrowIfUnixDistroNotSupportedAsync(cancellationToken)
                .ConfigureAwait(false);

            await this.CreateScratchSpace(cancellationToken);
            this.ExportScratchSpace = $"export MLPERF_SCRATCH_PATH={this.mlperfScratchSpace}";

            MLPerfState state = await this.stateManager.GetStateAsync<MLPerfState>($"{nameof(MLPerfState)}", cancellationToken)
                ?? new MLPerfState();

            if (!state.Initialized)
            {
                // add user in docker group and create scratch space
                await this.ExecuteCommandAsync("usermod", $"-aG docker {this.Username}", this.NvidiaDirectory, cancellationToken);
               
                // If GPUConfig is not included in the MLPerf code but is supported
                this.ReplaceGPUConfigFilesToSupportAdditionalGPUs();
                string makefileFilePath = this.PlatformSpecifics.Combine(this.NvidiaDirectory, "Makefile");

                // Update the docker flags in MLPerf docker file
                await this.fileSystem.File.ReplaceInFileAsync(
                    makefileFilePath, "DOCKER_INTERACTIVE_FLAGS = -it", "DOCKER_INTERACTIVE_FLAGS = -i -d", cancellationToken);

                await this.CreateSetUp(cancellationToken);
                state.Initialized = true;
                await this.stateManager.SaveStateAsync<MLPerfState>($"{nameof(MLPerfState)}", state, cancellationToken);
            }
        }

        /// <summary>
        /// Create a scratch directory to store downloaded data and models for MLPerf.
        /// </summary>
        /// <param name="cancellationToken">A token that can be used to cancel the operations.</param>
        /// <returns></returns>
        /// <exception cref="WorkloadException"></exception>
        protected async Task CreateScratchSpace(CancellationToken cancellationToken)
        {
            IEnumerable<Disk> disks = await this.diskManager.GetDisksAsync(cancellationToken).ConfigureAwait(false);

            if (disks?.Any() != true)
            {
                throw new WorkloadException(
                    "Unexpected scenario. The disks defined for the system could not be properly enumerated.",
                    ErrorReason.WorkloadUnexpectedAnomaly);
            }

            IEnumerable<Disk> filteredDisks = this.GetFilteredDisks(disks, this.DiskFilter);

            if (filteredDisks?.Any() != true)
            {
                throw new WorkloadException(
                    "Expected disks based on filter not found. Given the parameters defined for the profile action/step or those passed " +
                    "in on the command line, the requisite disks do not exist on the system or could not be identified based on the properties " +
                    "of the existing disks.",
                    ErrorReason.DependencyNotFound);
            }

            if (await this.diskManager.CreateMountPointsAsync(filteredDisks, this.systemManager, cancellationToken).ConfigureAwait(false))
            {
                // Refresh the disks to pickup the mount point changes.
                await Task.Delay(1000).ConfigureAwait(false);

                IEnumerable<Disk> updatedDisks = await this.diskManager.GetDisksAsync(cancellationToken)
                    .ConfigureAwait(false);

                filteredDisks = this.GetFilteredDisks(updatedDisks, this.DiskFilter);
            }

            filteredDisks.ToList().ForEach(disk => this.Logger.LogTraceMessage($"Disk Target: '{disk}'"));

            string accessPath = filteredDisks.OrderBy(d => d.Index).First().GetPreferredAccessPath(this.Platform);
            this.mlperfScratchSpace = this.PlatformSpecifics.Combine(accessPath, "scratch");

            if (!this.fileSystem.Directory.Exists(this.mlperfScratchSpace))
            {
                this.fileSystem.Directory.CreateDirectory(this.mlperfScratchSpace).Create();
            }
        }

        /// <summary>
        /// Gets the container name created by MLPerf.
        /// </summary>
        /// <returns>Container name created by MLPerf</returns>
        /// <exception cref="WorkloadException"></exception>
        protected string GetContainerName()
        {
            // Update this function to accomodate other architectures
            if (this.Platform == PlatformID.Unix && this.CpuArchitecture == Architecture.X64)
            {
                return $"mlperf-inference-{this.Username}-x86_64";
            }
            else if (this.Platform == PlatformID.Unix && this.CpuArchitecture == Architecture.Arm64)
            {
                return $"mlperf-inference-{this.Username}-arm64";
            }
            else
            {
                throw new WorkloadException(
                    $"The container name is not defined for the current platform/architecture " +
                    $"{PlatformSpecifics.GetPlatformArchitectureName(this.Platform, this.CpuArchitecture)}.",
                    ErrorReason.PlatformNotSupported);
            }
        }

        /// <summary>
        /// Creates setup for MLPerf workload.
        /// </summary>
        /// <param name="cancellationToken">A token that can be used to cancel the operations.</param>
        /// <returns></returns>
        protected async Task CreateSetUp(CancellationToken cancellationToken)
        {
            string dockerExecCommand = $"docker exec -u {this.Username} {this.GetContainerName()}";

            await this.ExecuteCommandAsync(
                "bash", 
                $"-c \"{this.ExportScratchSpace} && mkdir $MLPERF_SCRATCH_PATH/data $MLPERF_SCRATCH_PATH/models $MLPERF_SCRATCH_PATH/preprocessed_data\"", 
                this.NvidiaDirectory, 
                cancellationToken);

            await this.ExecuteCommandAsync(
                "sudo", 
                $" -u {this.Username} bash -c \"make prebuild MLPERF_SCRATCH_PATH={this.mlperfScratchSpace}\"", 
                this.NvidiaDirectory, 
                cancellationToken);

            await this.ExecuteCommandAsync(
                "sudo",
                $"docker ps",
                this.NvidiaDirectory,
                cancellationToken);

            await this.ExecuteCommandAsync(
                "sudo", 
                $"{dockerExecCommand} sudo bash -c \"{this.ExportScratchSpace} && make clean\"", 
                this.NvidiaDirectory, 
                cancellationToken);     
            
            await this.ExecuteCommandAsync(
                "sudo", 
                $"{dockerExecCommand} sudo bash -c \"{this.ExportScratchSpace} && make link_dirs\"", 
                this.NvidiaDirectory, 
                cancellationToken);
           
            foreach (string benchmark in this.benchmarks)
            {
                await this.ExecuteCommandAsync(
                    "sudo", 
                    $"{dockerExecCommand} sudo bash -c \"{this.ExportScratchSpace} && make download_data BENCHMARKS={benchmark}\"", 
                    this.NvidiaDirectory, 
                    cancellationToken);

                await this.ExecuteCommandAsync(
                    "sudo", 
                    $"{dockerExecCommand} sudo bash -c \"{this.ExportScratchSpace} && make download_model BENCHMARKS={benchmark}\"", 
                    this.NvidiaDirectory, 
                    cancellationToken);

                await this.ExecuteCommandAsync(
                    "sudo", 
                    $"{dockerExecCommand} sudo bash -c \"{this.ExportScratchSpace} && make preprocess_data BENCHMARKS={benchmark}\"", 
                    this.NvidiaDirectory, 
                    cancellationToken);

            }

            await this.ExecuteCommandAsync(
                "sudo", 
                $"{dockerExecCommand} sudo bash -c \"{this.ExportScratchSpace} && make build\"", 
                this.NvidiaDirectory, 
                cancellationToken);

        }

        private void ReplaceGPUConfigFilesToSupportAdditionalGPUs()
        {
            foreach (string file in this.fileSystem.Directory.GetFiles(this.PlatformSpecifics.GetScriptPath("mlperf", "GPUConfigFiles")))
            {
                this.fileSystem.File.Copy(
                    file,
                    this.Combine(this.NvidiaDirectory, "code", "common", "systems", Path.GetFileName(file)),
                    true);
            }

            foreach (string directory in this.fileSystem.Directory.GetDirectories(
                this.PlatformSpecifics.GetScriptPath("mlperf", "GPUConfigFiles"), "*", SearchOption.AllDirectories))
            {
                foreach (string subDirectory in this.fileSystem.Directory.GetDirectories(directory))
                {
                    if (this.fileSystem.File.Exists(this.Combine(subDirectory, "__init__.py")))
                    {
                        this.fileSystem.File.Copy(
                        this.Combine(subDirectory, "__init__.py"),
                        this.Combine(this.NvidiaDirectory, "configs", Path.GetFileName(directory), Path.GetFileName(subDirectory), "__init__.py"),
                        true);
                    }
                }
            }
        }

        private void ThrowIfPlatformNotSupported()
        {
            switch (this.Platform)
            {
                case PlatformID.Unix:
                    break;
                default:
                    throw new WorkloadException(
                        $"The MLPerf benchmark workload is not supported on the current platform/architecture " +
                        $"{PlatformSpecifics.GetPlatformArchitectureName(this.Platform, this.CpuArchitecture)}." +
                        $" Supported platform/architectures include: " +
                        $"{PlatformSpecifics.GetPlatformArchitectureName(PlatformID.Unix, Architecture.X64)}, " +
                        $"{PlatformSpecifics.GetPlatformArchitectureName(PlatformID.Unix, Architecture.Arm64)}",
                        ErrorReason.PlatformNotSupported);
            }
        }

        private async Task ThrowIfUnixDistroNotSupportedAsync(CancellationToken cancellationToken)
        {
            if (this.Platform == PlatformID.Unix)
            {
                var linuxDistributionInfo = await this.systemManager.GetLinuxDistributionAsync(cancellationToken)
                    .ConfigureAwait(false);

                switch (linuxDistributionInfo.LinuxDistribution)
                {
                    case LinuxDistribution.Ubuntu:
                    case LinuxDistribution.Debian:
                    case LinuxDistribution.CentOS7:
                    case LinuxDistribution.RHEL7:
                    case LinuxDistribution.SUSE:
                        break;
                    default:
                        throw new WorkloadException(
                            $"The MLPerf benchmark workload is not supported on the current Linux distro - " +
                            $"{linuxDistributionInfo.LinuxDistribution.ToString()}.  Supported distros include:" +
                            $" Ubuntu, Debian, CentOD7, RHEL7, SUSE. ",
                            ErrorReason.LinuxDistributionNotSupported);
                }
            }
        }

        private async Task ExecuteCommandAsync(string pathToExe, string commandLineArguments, string workingDirectory, CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                this.Logger.LogTraceMessage($"Executing process '{pathToExe}' '{commandLineArguments}' at directory '{workingDirectory}'.");

                EventContext telemetryContext = EventContext.Persisted()
                    .AddContext("command", pathToExe)
                    .AddContext("commandArguments", commandLineArguments);

                await this.Logger.LogMessageAsync($"{nameof(MLPerfExecutor)}.ExecuteProcess", telemetryContext, async () =>
                {
                    DateTime start = DateTime.Now;
                    using (IProcessProxy process = this.systemManager.ProcessManager.CreateElevatedProcess(
                        this.Platform, pathToExe, commandLineArguments, workingDirectory))
                    {
                        SystemManagement.CleanupTasks.Add(() => process.SafeKill());
                        await process.StartAndWaitAsync(cancellationToken).ConfigureAwait(false);

                        if (!cancellationToken.IsCancellationRequested)
                        {
                            this.Logger.LogProcessDetails<MLPerfExecutor>(process, telemetryContext);
                            process.ThrowIfErrored<WorkloadException>(ProcessProxy.DefaultSuccessCodes, errorReason: ErrorReason.WorkloadFailed);
                        }
                    }
                }).ConfigureAwait(false);
            }
        }

        private void PrepareBenchmarkConfigsAndScenarios()
        {
            this.scenarios = new Dictionary<string, string>();

            this.scenarios.Add("bert", "Offline,Server,SingleStream");
            this.scenarios.Add("rnnt", "Offline,Server,SingleStream");
            this.scenarios.Add("ssd-mobilenet", "Offline,MultiStream,SingleStream");
            this.scenarios.Add("ssd-resnet34", "Offline,Server,SingleStream,MultiStream");

            List<string> bertConfigs = new List<string>()
            {
                "default",
                "high_accuracy",
                "triton",
                "high_accuracy_triton"
            };

            List<string> rnntConfigs = new List<string>()
            {
                "default"
            };

            List<string> ssdConfigs = new List<string>()
            {
                "default",
                "triton"
            };

            this.benchmarkConfigs = new Dictionary<string, List<string>>();

            this.benchmarkConfigs.Add("bert", bertConfigs);
            this.benchmarkConfigs.Add("rnnt", rnntConfigs);
            this.benchmarkConfigs.Add("ssd-mobilenet", ssdConfigs);
            this.benchmarkConfigs.Add("ssd-resnet34", ssdConfigs);
        }

        private IEnumerable<Disk> GetFilteredDisks(IEnumerable<Disk> disks, string diskFilter)
        {
            List<Disk> filteredDisks = new List<Disk>();
            diskFilter = string.IsNullOrWhiteSpace(diskFilter) ? DiskFilters.DefaultDiskFilter : diskFilter;
            filteredDisks = DiskFilters.FilterDisks(disks, diskFilter, System.PlatformID.Unix).ToList();

            return filteredDisks;
        }

        private void LogOutput(DateTime startTime, DateTime endTime, EventContext telemetryContext)
        {
            string[] outputFiles = this.fileSystem.Directory.GetFiles(this.OutputDirectory, "accuracy_summary.json", SearchOption.AllDirectories);

            foreach (string file in outputFiles)
            {
                string text = this.fileSystem.File.ReadAllText(file);
                bool accuracyMode = true;

                MLPerfMetricsParser parser = new MLPerfMetricsParser(text, accuracyMode);
                IList<Metric> metrics = parser.Parse();

                this.Logger.LogMetrics(
                    "MLPerf",
                    this.Scenario,
                    startTime,
                    endTime,
                    metrics,
                    "AccuracyMode",
                    null,
                    this.Tags,
                    telemetryContext);

                this.fileSystem.File.Delete(file);
            }

            outputFiles = this.fileSystem.Directory.GetFiles(this.OutputDirectory, "perf_harness_summary.json", SearchOption.AllDirectories);

            foreach (string file in outputFiles)
            {
                string text = this.fileSystem.File.ReadAllText(file);
                bool accuracyMode = false;

                MLPerfMetricsParser parser = new MLPerfMetricsParser(text, accuracyMode);
                IList<Metric> metrics = parser.Parse();

                this.Logger.LogMetrics(
                    "MLPerf",
                    this.Scenario,
                    startTime,
                    endTime,
                    metrics,
                    "PerformanceMode",
                    null,
                    this.Tags,
                    telemetryContext);

                this.fileSystem.File.Delete(file);
            }
        }

        internal class MLPerfState : State
        {
            public MLPerfState(IDictionary<string, IConvertible> properties = null)
                : base(properties)
            {
            }

            public bool Initialized
            { 
                get
                {
                    return this.Properties.GetValue<bool>(nameof(MLPerfState.Initialized), false);
                }

                set
                {
                    this.Properties[nameof(MLPerfState.Initialized)] = value;
                }
            }
        }
    }
}