using Scalar.Common.FileSystem;
using Scalar.Common.Git;
using Scalar.Common.NuGetUpgrade;
using Scalar.Common.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;

namespace Scalar.Common
{
    /// <summary>
    /// Delegate to wrap install action steps in.
    /// This can be used to report the beginning / end of each install step.
    /// </summary>
    /// <param name="method">The method to run inside wrapper</param>
    /// <param name="message">The message to display</param>
    /// <returns>success or failure return from the method run.</returns>
    public delegate bool InstallActionWrapper(Func<bool> method, string message);

    public abstract class ProductUpgrader : IDisposable
    {
        protected readonly Version installedVersion;
        protected readonly ITracer tracer;
        protected readonly PhysicalFileSystem fileSystem;

        protected bool noVerify;
        protected bool dryRun;
        protected ProductUpgraderPlatformStrategy productUpgraderPlatformStrategy;

        protected ProductUpgrader(
            string currentVersion,
            ITracer tracer,
            bool dryRun,
            bool noVerify,
            PhysicalFileSystem fileSystem)
            : this(
                  currentVersion,
                  tracer,
                  dryRun,
                  noVerify,
                  fileSystem,
                  ScalarPlatform.Instance.CreateProductUpgraderPlatformInteractions(fileSystem, tracer))
        {
        }

        protected ProductUpgrader(
            string currentVersion,
            ITracer tracer,
            bool dryRun,
            bool noVerify,
            PhysicalFileSystem fileSystem,
            ProductUpgraderPlatformStrategy productUpgraderPlatformStrategy)
        {
            this.installedVersion = new Version(currentVersion);
            this.dryRun = dryRun;
            this.noVerify = noVerify;
            this.tracer = tracer;
            this.fileSystem = fileSystem;
            this.productUpgraderPlatformStrategy = productUpgraderPlatformStrategy;
        }

        /// <summary>
        /// For mocking purposes only
        /// </summary>
        protected ProductUpgrader()
        {
        }

        public abstract bool SupportsAnonymousVersionQuery { get; }

        public string UpgradeInstanceId { get; set; } = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        public static bool TryCreateUpgrader(
            ITracer tracer,
            PhysicalFileSystem fileSystem,
            LocalScalarConfig scalarConfig,
            ICredentialStore credentialStore,
            bool dryRun,
            bool noVerify,
            out ProductUpgrader newUpgrader,
            out string error)
        {
            Dictionary<string, string> entries;
            if (!scalarConfig.TryGetAllConfig(out entries, out error))
            {
                newUpgrader = null;
                return false;
            }

            bool containsUpgradeFeedUrl = entries.ContainsKey(ScalarConstants.LocalScalarConfig.UpgradeFeedUrl);
            bool containsUpgradePackageName = entries.ContainsKey(ScalarConstants.LocalScalarConfig.UpgradeFeedPackageName);
            bool containsOrgInfoServerUrl = entries.ContainsKey(ScalarConstants.LocalScalarConfig.OrgInfoServerUrl);

            if (!containsUpgradeFeedUrl && !containsUpgradePackageName)
            {
                error = "Custom upgrade feed is not configured";
                tracer.RelatedWarning(error);
                newUpgrader = null;
                return false;
            }

            // We are configured for NuGet - determine if we are using OrgNuGetUpgrader or not
            if (containsOrgInfoServerUrl)
            {
                if (OrgNuGetUpgrader.TryCreate(
                    tracer,
                    fileSystem,
                    scalarConfig,
                    new HttpClient(),
                    credentialStore,
                    dryRun,
                    noVerify,
                    out OrgNuGetUpgrader orgNuGetUpgrader,
                    out error))
                {
                    // We were successfully able to load a NuGetUpgrader - use that.
                    newUpgrader = orgNuGetUpgrader;
                    return true;
                }
                else
                {
                    tracer.RelatedError($"{nameof(TryCreateUpgrader)}: Could not create organization based upgrader. {error}");
                    newUpgrader = null;
                    return false;
                }
            }
            else
            {
                if (NuGetUpgrader.TryCreate(
                    tracer,
                    fileSystem,
                    scalarConfig,
                    credentialStore,
                    dryRun,
                    noVerify,
                    out NuGetUpgrader nuGetUpgrader,
                    out bool isConfigured,
                    out error))
                {
                    // We were successfully able to load a NuGetUpgrader - use that.
                    newUpgrader = nuGetUpgrader;
                    return true;
                }
                else
                {
                    tracer.RelatedError($"{nameof(TryCreateUpgrader)}: Could not create NuGet based upgrader. {error}");
                    newUpgrader = null;
                    return false;
                }
            }
        }

        public abstract bool UpgradeAllowed(out string message);

        public abstract bool TryQueryNewestVersion(out Version newVersion, out string message);

        public abstract bool TryDownloadNewestVersion(out string errorMessage);

        public abstract bool TryRunInstaller(InstallActionWrapper installActionWrapper, out string error);

        public virtual bool TrySetupUpgradeApplicationDirectory(out string upgradeApplicationPath, out string error)
        {
            string upgradeApplicationDirectory = ProductUpgraderInfo.GetUpgradeApplicationDirectory();

            if (!this.productUpgraderPlatformStrategy.TryPrepareApplicationDirectory(out error))
            {
                upgradeApplicationPath = null;
                return false;
            }

            string currentPath = ProcessHelper.GetCurrentProcessLocation();
            error = null;
            try
            {
                this.fileSystem.CopyDirectoryRecursive(currentPath, upgradeApplicationDirectory);
            }
            catch (UnauthorizedAccessException e)
            {
                error = string.Join(
                    Environment.NewLine,
                    "File copy error - " + e.Message,
                    $"Make sure you have write permissions to directory {upgradeApplicationDirectory} and run {ScalarConstants.UpgradeVerbMessages.ScalarUpgradeConfirm} again.");
            }
            catch (IOException e)
            {
                error = "File copy error - " + e.Message;
                this.TraceException(e, nameof(this.TrySetupUpgradeApplicationDirectory), $"Error copying {currentPath} to {upgradeApplicationDirectory}.");
            }

            if (string.IsNullOrEmpty(error))
            {
                // There was no error - set upgradeToolPath and return success.
                upgradeApplicationPath = Path.Combine(
                    upgradeApplicationDirectory,
                    ScalarPlatform.Instance.Constants.ScalarUpgraderExecutableName);
                return true;
            }
            else
            {
                // Encountered error - do not set upgrade tool path and return failure.
                upgradeApplicationPath = null;
                return false;
            }
        }

        public abstract bool TryCleanup(out string error);

        public void TraceException(Exception exception, string method, string message)
        {
            this.TraceException(this.tracer, exception, method, message);
        }

        public void TraceException(ITracer tracer, Exception exception, string method, string message)
        {
            EventMetadata metadata = new EventMetadata();
            metadata.Add("Method", method);
            metadata.Add("Exception", exception.ToString());
            tracer.RelatedError(metadata, message);
        }

        public virtual void Dispose()
        {
        }

        protected virtual bool TryCreateAndConfigureDownloadDirectory(ITracer tracer, out string error)
        {
            return this.productUpgraderPlatformStrategy.TryPrepareDownloadDirectory(out error);
        }

        protected virtual void RunInstaller(string path, string args, out int exitCode, out string error)
        {
            ProcessResult processResult = ProcessHelper.Run(path, args);

            exitCode = processResult.ExitCode;
            error = processResult.Errors;
        }
    }
}
