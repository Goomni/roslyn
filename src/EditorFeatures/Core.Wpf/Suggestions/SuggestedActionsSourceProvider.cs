﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Editor.Tags;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.Suggestions
{
    [Export(typeof(ISuggestedActionsSourceProvider))]
    [Export(typeof(SuggestedActionsSourceProvider))]
    [ContentType(ContentTypeNames.RoslynContentType)]
    [ContentType(ContentTypeNames.XamlContentType)]
    [Name("Roslyn Code Fix")]
    [Order]
    internal partial class SuggestedActionsSourceProvider : ISuggestedActionsSourceProvider
    {
        private static readonly Guid s_CSharpSourceGuid = new Guid("b967fea8-e2c3-4984-87d4-71a38f49e16a");
        private static readonly Guid s_visualBasicSourceGuid = new Guid("4de30e93-3e0c-40c2-a4ba-1124da4539f6");
        private static readonly Guid s_xamlSourceGuid = new Guid("a0572245-2eab-4c39-9f61-06a6d8c5ddda");

        private const int InvalidSolutionVersion = -1;

        private readonly IThreadingContext _threadingContext;
        private readonly ICodeRefactoringService _codeRefactoringService;
        private readonly IDiagnosticAnalyzerService _diagnosticService;
        private readonly ICodeFixService _codeFixService;
        private readonly ISuggestedActionCategoryRegistryService _suggestedActionCategoryRegistry;
        private readonly IGlobalOptionService _optionService;
        public readonly ICodeActionEditHandlerService EditHandler;
        public readonly IAsynchronousOperationListener OperationListener;
        public readonly IUIThreadOperationExecutor UIThreadOperationExecutor;

        public readonly ImmutableArray<Lazy<IImageIdService, OrderableMetadata>> ImageIdServices;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public SuggestedActionsSourceProvider(
            IThreadingContext threadingContext,
            ICodeRefactoringService codeRefactoringService,
            IDiagnosticAnalyzerService diagnosticService,
            ICodeFixService codeFixService,
            ICodeActionEditHandlerService editHandler,
            IUIThreadOperationExecutor uiThreadOperationExecutor,
            ISuggestedActionCategoryRegistryService suggestedActionCategoryRegistry,
            IAsynchronousOperationListenerProvider listenerProvider,
            IGlobalOptionService optionService,
            [ImportMany] IEnumerable<Lazy<IImageIdService, OrderableMetadata>> imageIdServices)
        {
            _threadingContext = threadingContext;
            _codeRefactoringService = codeRefactoringService;
            _diagnosticService = diagnosticService;
            _codeFixService = codeFixService;
            _suggestedActionCategoryRegistry = suggestedActionCategoryRegistry;
            _optionService = optionService;
            EditHandler = editHandler;
            UIThreadOperationExecutor = uiThreadOperationExecutor;
            OperationListener = listenerProvider.GetListener(FeatureAttribute.LightBulb);

            ImageIdServices = ExtensionOrderer.Order(imageIdServices).ToImmutableArray();
        }

        public ISuggestedActionsSource? CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer)
        {
            Contract.ThrowIfNull(textView);
            Contract.ThrowIfNull(textBuffer);

            // Disable lightbulb points when running under the LSP editor.
            // The LSP client will interface with the editor to display our code actions.
            if (textBuffer.IsInLspEditorContext())
                return null;

            var asyncEnabled = _optionService.GetOption(SuggestionsOptions.Asynchronous) ??
                               _optionService.GetOption(SuggestionsOptions.AsynchronousFeatureFlag);

            return asyncEnabled == true
                ? new AsyncSuggestedActionsSource(_threadingContext, this, textView, textBuffer, _suggestedActionCategoryRegistry)
                : new SyncSuggestedActionsSource(_threadingContext, this, textView, textBuffer, _suggestedActionCategoryRegistry);
        }
    }
}
