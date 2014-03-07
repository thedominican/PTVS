﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Interpreter;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudioTools.Project;

namespace Microsoft.PythonTools.Project {
    static class Pip {
        private static readonly Regex PackageNameRegex = new Regex(
            "^(?<name>[a-z0-9_]+)(-.+)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly KeyValuePair<string, string>[] UnbufferedEnv = new[] { 
            new KeyValuePair<string, string>("PYTHONUNBUFFERED", "1")
        };

        // The relative path from PrefixPath, and true if it is a Python script
        // that needs to be run with the interpreter.
        private static readonly KeyValuePair<string, bool>[] PipLocations = new[] {
            new KeyValuePair<string, bool>(Path.Combine("Scripts", "pip-script.py"), true),
            new KeyValuePair<string, bool>("pip-script.py", true),
            new KeyValuePair<string, bool>(Path.Combine("Scripts", "pip.exe"), false),
            new KeyValuePair<string, bool>("pip.exe", false)
        };

        private static ProcessOutput Run(IPythonInterpreterFactory factory, Redirector output, bool elevate, params string[] cmd) {
            factory.ThrowIfNotRunnable("factory");

            var args = cmd.AsEnumerable();
            bool isScript = false;
            string pipPath = null;
            foreach (var path in PipLocations) {
                pipPath = Path.Combine(factory.Configuration.PrefixPath, path.Key);
                isScript = path.Value;
                if (File.Exists(pipPath)) {
                    break;
                }
                pipPath = null;
            }

            if (string.IsNullOrEmpty(pipPath)) {
                args = new[] { "-m", "pip" }.Concat(args);
                isScript = true;
                pipPath = factory.Configuration.InterpreterPath;
            } else if (isScript) {
                args = new[] { ProcessOutput.QuoteSingleArgument(pipPath) }.Concat(args);
                pipPath = factory.Configuration.InterpreterPath;
            }

            return ProcessOutput.Run(
                pipPath,
                args,
                factory.Configuration.PrefixPath,
                UnbufferedEnv,
                false,
                output,
                quoteArgs: false,
                elevate: elevate
            );
        }

        public static async Task<HashSet<string>> Freeze(IPythonInterpreterFactory factory) {
            var lines = new HashSet<string>();
            using (var proc = Run(factory, null, false, "--version")) {
                if (await proc == 0) {
                    lines.UnionWith(proc.StandardOutputLines
                        .Select(line => Regex.Match(line, "pip (?<version>[0-9.]+)"))
                        .Where(match => match.Success && match.Groups["version"].Success)
                        .Select(match => "pip==" + match.Groups["version"].Value));
                }
            }

            using (var proc = Run(factory, null, false, "freeze")) {
                if (await proc == 0) {
                    lines.UnionWith(proc.StandardOutputLines);
                    return lines;
                }
            }

            // Pip failed, so clear out any entries that may have appeared
            lines.Clear();

            try {
                var packagesPath = Path.Combine(factory.Configuration.LibraryPath, "site-packages");
                await Task.Run(() => {
                    lines.UnionWith(Directory.EnumerateDirectories(packagesPath)
                        .Select(name => Path.GetFileName(name))
                        .Select(name => PackageNameRegex.Match(name))
                        .Where(m => m.Success)
                        .Select(m => m.Groups["name"].Value));
                });
            } catch {
                lines.Clear();
            }

            return lines;
        }

        /// <summary>
        /// Returns true if installing a package will be secure.
        /// 
        /// This returns false for Python 2.5 and earlier because it does not
        /// include the required SSL support by default. No detection is done to
        /// determine whether the support has been added separately.
        /// </summary>
        public static bool IsSecureInstall(IPythonInterpreterFactory factory) {
            return factory.Configuration.Version > new Version(2, 5);
        }

        private static string GetInsecureArg(
            IPythonInterpreterFactory factory,
            Redirector output = null
        ) {
            if (!IsSecureInstall(factory)) {
                // Python 2.5 does not include ssl, and so the --insecure
                // option is required to use pip.
                if (output != null) {
                    output.WriteErrorLine("Using '--insecure' option for Python 2.5.");
                }
                return "--insecure";
            }
            return null;
        }

        public static async Task Install(
            IPythonInterpreterFactory factory,
            string package,
            bool elevate,
            Redirector output = null
        ) {
            using (var proc = Run(factory, output, elevate, "install", GetInsecureArg(factory, output), package)) {
                await proc;
            }
        }

        public static async Task<bool> Install(
            IPythonInterpreterFactory factory,
            string package,
            IServiceProvider site,
            bool elevate,
            Redirector output = null
        ) {
            if (site != null && !ModulePath.GetModulesInLib(factory).Any(mp => mp.ModuleName == "pip")) {
                try {
                    await QueryInstallPip(factory, site, SR.GetString(SR.InstallPip), elevate, output);
                } catch (OperationCanceledException) {
                    return false;
                }
            }

            if (output != null) {
                output.WriteLine(SR.GetString(SR.PackageInstalling, package));
                if (PythonToolsPackage.Instance != null && PythonToolsPackage.Instance.GeneralOptionsPage.ShowOutputWindowForPackageInstallation) {
                    output.ShowAndActivate();
                } else {
                    output.Show();
                }
            }

            using (var proc = Run(factory, output, elevate, "install", GetInsecureArg(factory, output), package)) {
                var exitCode = await proc;

                if (output != null) {
                    if (exitCode == 0) {
                        output.WriteLine(SR.GetString(SR.PackageInstallSucceeded, package));
                    } else {
                        output.WriteLine(SR.GetString(SR.PackageInstallFailedExitCode, package, exitCode));
                    }
                    if (PythonToolsPackage.Instance != null && PythonToolsPackage.Instance.GeneralOptionsPage.ShowOutputWindowForPackageInstallation) {
                        output.ShowAndActivate();
                    } else {
                        output.Show();
                    }
                }
                return exitCode == 0;
            }
        }

        public static async Task<bool> Uninstall(
            IPythonInterpreterFactory factory,
            string package,
            bool elevate,
            Redirector output = null
        ) {
            factory.ThrowIfNotRunnable("factory");

            if (output != null) {
                output.WriteLine(SR.GetString(SR.PackageUninstalling, package));
                if (PythonToolsPackage.Instance != null && PythonToolsPackage.Instance.GeneralOptionsPage.ShowOutputWindowForPackageInstallation) {
                    output.ShowAndActivate();
                } else {
                    output.Show();
                }
            }

            using (var proc = Run(factory, output, elevate, "uninstall", "-y", package)) {
                var exitCode = await proc;

                if (output != null) {
                    if (exitCode == 0) {
                        output.WriteLine(SR.GetString(SR.PackageUninstallSucceeded, package));
                    } else {
                        output.WriteLine(SR.GetString(SR.PackageUninstallFailedExitCode, package, exitCode));
                    }
                    if (PythonToolsPackage.Instance != null && PythonToolsPackage.Instance.GeneralOptionsPage.ShowOutputWindowForPackageInstallation) {
                        output.ShowAndActivate();
                    } else {
                        output.Show();
                    }
                }
                return exitCode == 0;
            }
        }

        public static async Task InstallPip(IPythonInterpreterFactory factory, bool elevate, Redirector output = null) {
            factory.ThrowIfNotRunnable("factory");

            var pipDownloaderPath = PythonToolsInstallPath.GetFile("pip_downloader.py");

            if (output != null) {
                output.WriteLine(SR.GetString(SR.PipInstalling));
                if (PythonToolsPackage.Instance != null && PythonToolsPackage.Instance.GeneralOptionsPage.ShowOutputWindowForPackageInstallation) {
                    output.ShowAndActivate();
                } else {
                    output.Show();
                }
            }
            using (var proc = ProcessOutput.Run(
                factory.Configuration.InterpreterPath,
                new[] { pipDownloaderPath },
                factory.Configuration.PrefixPath,
                null,
                false,
                output,
                elevate: PythonToolsPackage.Instance != null && PythonToolsPackage.Instance.GeneralOptionsPage.ElevatePip
            )) {
                var exitCode = await proc;
                if (output != null) {
                    if (exitCode == 0) {
                        output.WriteLine(SR.GetString(SR.PipInstallSucceeded));
                    } else {
                        output.WriteLine(SR.GetString(SR.PipInstallFailedExitCode, exitCode));
                    }
                    if (PythonToolsPackage.Instance != null && PythonToolsPackage.Instance.GeneralOptionsPage.ShowOutputWindowForPackageInstallation) {
                        output.ShowAndActivate();
                    } else {
                        output.Show();
                    }
                }
            }
        }

        public static async Task QueryInstall(
            IPythonInterpreterFactory factory,
            string package,
            IServiceProvider site,
            string message,
            bool elevate,
            Redirector output = null
        ) {
            factory.ThrowIfNotRunnable("factory");

            if (Microsoft.VisualStudio.Shell.VsShellUtilities.ShowMessageBox(
                site,
                message,
                null,
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST
            ) == 2) {
                throw new OperationCanceledException();
            }

            await Install(factory, package, elevate, output);
        }

        public static async Task QueryInstallPip(
            IPythonInterpreterFactory factory,
            IServiceProvider site,
            string message,
            bool elevate,
            Redirector output = null
        ) {
            factory.ThrowIfNotRunnable();

            if (Microsoft.VisualStudio.Shell.VsShellUtilities.ShowMessageBox(
                site,
                message,
                null,
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST
            ) == 2) {
                throw new OperationCanceledException();
            }

            await InstallPip(factory, elevate, output);
        }

        /// <summary>
        /// Checks whether a given package is installed and satisfies the version specification.
        /// </summary>
        /// <param name="package">Name, and optionally the version of the package to install, in setuptools format.</param>
        /// <remarks>
        /// This method requires setuptools to be installed to correctly detect packages and verify their versions. If setuptools
        /// is not available, the method will always return <c>false</c> for any package name.
        /// </remarks>
        public static async Task<bool> IsInstalled(IPythonInterpreterFactory factory, string package) {
            if (!factory.IsRunnable()) {
                return false;
            }

            var code = string.Format("import pkg_resources; pkg_resources.require('{0}')", package);
            using (var proc = ProcessOutput.Run(
                factory.Configuration.InterpreterPath,
                new[] { "-c", code  },
                factory.Configuration.PrefixPath,
                UnbufferedEnv,
                visible: false,
                redirector: null,
                quoteArgs: true)
            ) {
                try {
                    return await proc == 0;
                } catch (NoInterpretersException) {
                    return false;
                }
            }
        }
    }
}
