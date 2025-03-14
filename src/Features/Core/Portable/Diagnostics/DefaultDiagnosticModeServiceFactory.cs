﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Composition;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.Diagnostics
{
    [ExportWorkspaceServiceFactory(typeof(IDiagnosticModeService), ServiceLayer.Default), Shared]
    internal class DefaultDiagnosticModeServiceFactory : IWorkspaceServiceFactory
    {
        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public DefaultDiagnosticModeServiceFactory()
        {
        }

        public IWorkspaceService CreateService(HostWorkspaceServices workspaceServices)
            => new DefaultDiagnosticModeService(workspaceServices.Workspace);

        private class DefaultDiagnosticModeService : IDiagnosticModeService
        {
            private readonly Workspace _workspace;
            private readonly Dictionary<Option2<DiagnosticMode>, Lazy<DiagnosticMode>> _optionToMode = new();

            public DefaultDiagnosticModeService(Workspace workspace)
            {
                _workspace = workspace;
            }

            public DiagnosticMode GetDiagnosticMode(Option2<DiagnosticMode> option)
            {
                var lazy = GetLazy(option);
                return lazy.Value;
            }

            private Lazy<DiagnosticMode> GetLazy(Option2<DiagnosticMode> option)
            {
                lock (_optionToMode)
                {
                    if (!_optionToMode.TryGetValue(option, out var lazy))
                    {
                        lazy = new Lazy<DiagnosticMode>(() => ComputeDiagnosticMode(option), isThreadSafe: true);
                        _optionToMode.Add(option, lazy);
                    }

                    return lazy;
                }
            }

            private DiagnosticMode ComputeDiagnosticMode(Option2<DiagnosticMode> option)
            {
                var inCodeSpacesServer = IsInCodeSpacesServer();

                // If we're in the code-spaces server, we only support pull diagnostics.  This is because the only way
                // for diagnostics to make it through from the  server to the client is through the codespaces LSP
                // channel, which is only pull based.
                if (inCodeSpacesServer)
                    return DiagnosticMode.Pull;

                var diagnosticModeOption = _workspace.Options.GetOption(option);

                // If the workspace diagnostic mode is set to Default, defer to the feature flag service.
                if (diagnosticModeOption == DiagnosticMode.Default)
                {
                    return _workspace.Options.GetOption(DiagnosticOptions.LspPullDiagnosticsFeatureFlag) ? DiagnosticMode.Pull : DiagnosticMode.Push;
                }

                // Otherwise, defer to the workspace+option to determine what mode we're in.
                return diagnosticModeOption;
            }

            private static bool IsInCodeSpacesServer()
            {
                // hack until there is an officially supported free-threaded synchronous platform API to ask this question.
                return Environment.GetEnvironmentVariable("VisualStudioServerMode") == "1";
            }
        }
    }
}
