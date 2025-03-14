﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Completion.Providers;
using Microsoft.CodeAnalysis.Editor.Host;
using Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.QuickInfo;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using AsyncCompletionData = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using VSCompletionItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;
using VSUtilities = Microsoft.VisualStudio.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.AsyncCompletion
{
    internal sealed class CompletionSource : ForegroundThreadAffinitizedObject, IAsyncExpandingCompletionSource
    {
        internal const string RoslynItem = nameof(RoslynItem);
        internal const string TriggerLocation = nameof(TriggerLocation);
        internal const string ExpandedItemTriggerLocation = nameof(ExpandedItemTriggerLocation);
        internal const string CompletionListSpan = nameof(CompletionListSpan);
        internal const string InsertionText = nameof(InsertionText);
        internal const string HasSuggestionItemOptions = nameof(HasSuggestionItemOptions);
        internal const string Description = nameof(Description);
        internal const string PotentialCommitCharacters = nameof(PotentialCommitCharacters);
        internal const string ExcludedCommitCharacters = nameof(ExcludedCommitCharacters);
        internal const string NonBlockingCompletion = nameof(NonBlockingCompletion);
        internal const string TypeImportCompletionEnabled = nameof(TypeImportCompletionEnabled);
        internal const string TargetTypeFilterExperimentEnabled = nameof(TargetTypeFilterExperimentEnabled);

        private static readonly ImmutableArray<ImageElement> s_WarningImageAttributeImagesArray =
            ImmutableArray.Create(new ImageElement(Glyph.CompletionWarning.GetImageId(), EditorFeaturesResources.Warning_image_element));

        private static readonly EditorOptionKey<bool> NonBlockingCompletionEditorOption = new(NonBlockingCompletion);

        // Use CWT to cache data needed to create VSCompletionItem, so the table would be cleared when Roslyn completion item cache is cleared.
        private static readonly ConditionalWeakTable<RoslynCompletionItem, StrongBox<VSCompletionItemData>> s_roslynItemToVsItemData =
            new();

        private readonly ITextView _textView;
        private readonly bool _isDebuggerTextView;
        private readonly ImmutableHashSet<string> _roles;
        private readonly Lazy<IStreamingFindUsagesPresenter> _streamingPresenter;
        private readonly VSUtilities.IUIThreadOperationExecutor _operationExecutor;
        private readonly IAsynchronousOperationListener _asyncListener;
        private bool _snippetCompletionTriggeredIndirectly;

        internal CompletionSource(
            ITextView textView,
            Lazy<IStreamingFindUsagesPresenter> streamingPresenter,
            IThreadingContext threadingContext,
            VSUtilities.IUIThreadOperationExecutor operationExecutor,
            IAsynchronousOperationListener asyncListener)
            : base(threadingContext)
        {
            _textView = textView;
            _streamingPresenter = streamingPresenter;
            _operationExecutor = operationExecutor;
            _asyncListener = asyncListener;
            _isDebuggerTextView = textView is IDebuggerTextView;
            _roles = textView.Roles.ToImmutableHashSet();
        }

        public AsyncCompletionData.CompletionStartData InitializeCompletion(
            AsyncCompletionData.CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            CancellationToken cancellationToken)
        {
            // We take sourceText from document to get a snapshot span.
            // We would like to be sure that nobody changes buffers at the same time.
            AssertIsForeground();

            if (_textView.Selection.Mode == TextSelectionMode.Box)
            {
                // No completion with multiple selection
                return AsyncCompletionData.CompletionStartData.DoesNotParticipateInCompletion;
            }

            var document = triggerLocation.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
            {
                return AsyncCompletionData.CompletionStartData.DoesNotParticipateInCompletion;
            }

            var service = document.GetLanguageService<CompletionService>();
            if (service == null)
            {
                return AsyncCompletionData.CompletionStartData.DoesNotParticipateInCompletion;
            }

            // The Editor supports the option per textView.
            // There could be mixed desired behavior per textView and even per same completion session.
            // The right fix would be to send this information as a result of the method. 
            // Then, the Editor would choose the right behavior for mixed cases.
            _textView.Options.GlobalOptions.SetOptionValue(NonBlockingCompletionEditorOption, !document.Project.Solution.Workspace.Options.GetOption(CompletionOptions.BlockForCompletionItems2, service.Language));

            // In case of calls with multiple completion services for the same view (e.g. TypeScript and C#), those completion services must not be called simultaneously for the same session.
            // Therefore, in each completion session we use a list of commit character for a specific completion service and a specific content type.
            _textView.Properties[PotentialCommitCharacters] = service.GetRules().DefaultCommitCharacters;

            // Reset a flag which means a snippet triggered by ? + Tab.
            // Set it later if met the condition.
            _snippetCompletionTriggeredIndirectly = false;

            CheckForExperimentStatus(_textView, document);

            var sourceText = document.GetTextSynchronously(cancellationToken);

            return ShouldTriggerCompletion(trigger, triggerLocation, sourceText, document, service)
                ? new AsyncCompletionData.CompletionStartData(
                    participation: AsyncCompletionData.CompletionParticipation.ProvidesItems,
                    applicableToSpan: new SnapshotSpan(
                        triggerLocation.Snapshot,
                        service.GetDefaultCompletionListSpan(sourceText, triggerLocation.Position).ToSpan()))
                : AsyncCompletionData.CompletionStartData.DoesNotParticipateInCompletion;

            // For telemetry reporting purpose
            static void CheckForExperimentStatus(ITextView textView, Document document)
            {
                var options = document.Project.Solution.Options;

                textView.Properties[TargetTypeFilterExperimentEnabled] = options.GetOption(CompletionOptions.TargetTypedCompletionFilterFeatureFlag);

                var importCompletionOptionValue = options.GetOption(CompletionOptions.ShowItemsFromUnimportedNamespaces, document.Project.Language);
                var importCompletionExperimentValue = options.GetOption(CompletionOptions.TypeImportCompletionFeatureFlag);
                var isTypeImportEnababled = importCompletionOptionValue == true || (importCompletionOptionValue == null && importCompletionExperimentValue);
                textView.Properties[TypeImportCompletionEnabled] = isTypeImportEnababled;
            }
        }

        private bool ShouldTriggerCompletion(
            AsyncCompletionData.CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            SourceText sourceText,
            Document document,
            CompletionService completionService)
        {
            // The trigger reason guarantees that user wants a completion.
            if (trigger.Reason == AsyncCompletionData.CompletionTriggerReason.Invoke ||
                trigger.Reason == AsyncCompletionData.CompletionTriggerReason.InvokeAndCommitIfUnique)
            {
                return true;
            }

            // Enter does not trigger completion.
            if (trigger.Reason == AsyncCompletionData.CompletionTriggerReason.Insertion && trigger.Character == '\n')
            {
                return false;
            }

            //The user may be trying to invoke snippets through question-tab.
            // We may provide a completion after that.
            // Otherwise, tab should not be a completion trigger.
            if (trigger.Reason == AsyncCompletionData.CompletionTriggerReason.Insertion && trigger.Character == '\t')
            {
                return TryInvokeSnippetCompletion(completionService, document, sourceText, triggerLocation.Position);
            }

            var roslynTrigger = Helpers.GetRoslynTrigger(trigger, triggerLocation);

            // The completion service decides that user may want a completion.
            if (completionService.ShouldTriggerCompletion(document.Project, sourceText, triggerLocation.Position, roslynTrigger))
            {
                return true;
            }

            return false;
        }

        private bool TryInvokeSnippetCompletion(
            CompletionService completionService, Document document, SourceText text, int caretPoint)
        {
            var rules = completionService.GetRules();
            // Do not invoke snippet if the corresponding rule is not set in options.
            if (rules.SnippetsRule != SnippetsRule.IncludeAfterTypingIdentifierQuestionTab)
            {
                return false;
            }

            var syntaxFactsOpt = document.GetLanguageService<ISyntaxFactsService>();
            // Snippets are included if the user types: <quesiton><tab>
            // If at least one condition for snippets do not hold, bail out.
            if (syntaxFactsOpt == null ||
                caretPoint < 3 ||
                text[caretPoint - 2] != '?' ||
                !QuestionMarkIsPrecededByIdentifierAndWhitespace(text, caretPoint - 2, syntaxFactsOpt))
            {
                return false;
            }

            // Because <question><tab> is actually a command to bring up snippets,
            // we delete the last <question> that was typed.
            var textChange = new TextChange(TextSpan.FromBounds(caretPoint - 2, caretPoint), string.Empty);
            document.Project.Solution.Workspace.ApplyTextChanges(document.Id, textChange, CancellationToken.None);

            _snippetCompletionTriggeredIndirectly = true;
            return true;
        }

        public Task<AsyncCompletionData.CompletionContext> GetCompletionContextAsync(
            IAsyncCompletionSession session,
            AsyncCompletionData.CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            SnapshotSpan applicableToSpan,
            CancellationToken cancellationToken)
        {
            if (session is null)
                throw new ArgumentNullException(nameof(session));

            return GetCompletionContextWorkerAsync(session, trigger, triggerLocation, isExpanded: false, cancellationToken);
        }

        public async Task<AsyncCompletionData.CompletionContext> GetExpandedCompletionContextAsync(
            IAsyncCompletionSession session,
            AsyncCompletionData.CompletionExpander expander,
            AsyncCompletionData.CompletionTrigger intialTrigger,
            SnapshotSpan applicableToSpan,
            CancellationToken cancellationToken)
        {
            // We only want to provide expanded items for Roslyn's expander.
            if ((object)expander == FilterSet.Expander && session.Properties.TryGetProperty(ExpandedItemTriggerLocation, out SnapshotPoint initialTriggerLocation))
            {
                return await GetCompletionContextWorkerAsync(session, intialTrigger, initialTriggerLocation, isExpanded: true, cancellationToken).ConfigureAwait(false);
            }

            return AsyncCompletionData.CompletionContext.Empty;
        }

        private async Task<AsyncCompletionData.CompletionContext> GetCompletionContextWorkerAsync(
            IAsyncCompletionSession session,
            AsyncCompletionData.CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            bool isExpanded,
            CancellationToken cancellationToken)
        {
            var document = triggerLocation.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
            {
                return AsyncCompletionData.CompletionContext.Empty;
            }

            var completionService = document.GetRequiredLanguageService<CompletionService>();

            var roslynTrigger = Helpers.GetRoslynTrigger(trigger, triggerLocation);
            if (_snippetCompletionTriggeredIndirectly)
            {
                roslynTrigger = new CompletionTrigger(CompletionTriggerKind.Snippets);
            }

            var documentOptions = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
            var options = documentOptions
                .WithChangedOption(CompletionServiceOptions.IsExpandedCompletion, isExpanded);

            if (_isDebuggerTextView)
            {
                options = options
                    .WithChangedOption(CompletionControllerOptions.FilterOutOfScopeLocals, false)
                    .WithChangedOption(CompletionControllerOptions.ShowXmlDocCommentCompletion, false);
            }

            var (completionList, expandItemsAvailable) = await completionService.GetCompletionsInternalAsync(
                document,
                triggerLocation,
                roslynTrigger,
                _roles,
                options,
                cancellationToken).ConfigureAwait(false);

            ImmutableArray<VSCompletionItem> items;
            AsyncCompletionData.SuggestionItemOptions? suggestionItemOptions;
            var filterSet = new FilterSet();

            if (completionList == null)
            {
                items = ImmutableArray<VSCompletionItem>.Empty;
                suggestionItemOptions = null;
            }
            else
            {
                var itemsBuilder = new ArrayBuilder<VSCompletionItem>(completionList.Items.Length);
                foreach (var roslynItem in completionList.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = Convert(document, roslynItem, filterSet, triggerLocation);
                    itemsBuilder.Add(item);
                }

                items = itemsBuilder.ToImmutableAndFree();

                suggestionItemOptions = completionList.SuggestionModeItem != null
                        ? new AsyncCompletionData.SuggestionItemOptions(
                            completionList.SuggestionModeItem.DisplayText,
                            completionList.SuggestionModeItem.Properties.TryGetValue(Description, out var description)
                                ? description
                                : string.Empty)
                        : null;

                // Store around the span this completion list applies to.  We'll use this later
                // to pass this value in when we're committing a completion list item.
                // It's OK to overwrite this value when expanded items are requested.
                session.Properties[CompletionListSpan] = completionList.Span;

                // This is a code supporting original completion scenarios: 
                // Controller.Session_ComputeModel: if completionList.SuggestionModeItem != null, then suggestionMode = true
                // If there are suggestionItemOptions, then later HandleNormalFiltering should set selection to SoftSelection.
                if (!session.Properties.TryGetProperty(HasSuggestionItemOptions, out bool hasSuggestionItemOptionsBefore) || !hasSuggestionItemOptionsBefore)
                {
                    session.Properties[HasSuggestionItemOptions] = suggestionItemOptions != null;
                }

                var excludedCommitCharacters = GetExcludedCommitCharacters(completionList.Items);
                if (excludedCommitCharacters.Length > 0)
                {
                    if (session.Properties.TryGetProperty(ExcludedCommitCharacters, out ImmutableArray<char> excludedCommitCharactersBefore))
                    {
                        excludedCommitCharacters = excludedCommitCharacters.Union(excludedCommitCharactersBefore).ToImmutableArray();
                    }

                    session.Properties[ExcludedCommitCharacters] = excludedCommitCharacters;
                }
            }

            // We need to remember the trigger location for when a completion service claims expanded items are available
            // since the initial trigger we are able to get from IAsyncCompletionSession might not be the same (e.g. in projection scenarios)
            // so when they are requested via expander later, we can retrieve it.
            // Technically we should save the trigger location for each individual service that made such claim, but in reality only Roslyn's
            // completion service uses expander, so we can get away with not making such distinction.
            if (!isExpanded && expandItemsAvailable)
            {
                session.Properties[ExpandedItemTriggerLocation] = triggerLocation;
            }

            // It's possible that some providers can provide expanded items, in which case we will need to show expander as unselected.
            return new AsyncCompletionData.CompletionContext(
                items,
                suggestionItemOptions,
                suggestionItemOptions == null
                    ? AsyncCompletionData.InitialSelectionHint.RegularSelection
                    : AsyncCompletionData.InitialSelectionHint.SoftSelection,
                filterSet.GetFilterStatesInSet(addUnselectedExpander: expandItemsAvailable));
        }

        public async Task<object?> GetDescriptionAsync(IAsyncCompletionSession session, VSCompletionItem item, CancellationToken cancellationToken)
        {
            if (session is null)
                throw new ArgumentNullException(nameof(session));
            if (item is null)
                throw new ArgumentNullException(nameof(item));

            if (!item.Properties.TryGetProperty(RoslynItem, out RoslynCompletionItem roslynItem) ||
                !item.Properties.TryGetProperty(TriggerLocation, out SnapshotPoint triggerLocation))
            {
                return null;
            }

            var document = triggerLocation.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
                return null;

            var service = document.GetLanguageService<CompletionService>();
            if (service == null)
                return null;

            var description = await service.GetDescriptionAsync(document, roslynItem, cancellationToken).ConfigureAwait(false);

            var context = new IntellisenseQuickInfoBuilderContext(
                document, ThreadingContext, _operationExecutor, _asyncListener, _streamingPresenter);
            var elements = IntelliSense.Helpers.BuildInteractiveTextElements(description.TaggedParts, context).ToArray();
            if (elements.Length == 0)
            {
                return new ClassifiedTextElement();
            }
            else if (elements.Length == 1)
            {
                return elements[0];
            }
            else
            {
                return new ContainerElement(ContainerElementStyle.Stacked | ContainerElementStyle.VerticalPadding, elements);
            }
        }

        /// <summary>
        /// We'd like to cache VS Completion item directly to avoid allocation completely. However it holds references
        /// to transient objects, which would cause memory leak (among other potential issues) if cached. 
        /// So as a compromise,  we cache data that can be calculated from Roslyn completion item to avoid repeated 
        /// calculation cost for cached Roslyn completion items.
        /// </summary>
        private readonly struct VSCompletionItemData
        {
            public VSCompletionItemData(
                string displayText, ImageElement icon, ImmutableArray<AsyncCompletionData.CompletionFilter> filters,
                int filterSetData, ImmutableArray<ImageElement> attributeIcons, string insertionText)
            {
                DisplayText = displayText;
                Icon = icon;
                Filters = filters;
                FilterSetData = filterSetData;
                AttributeIcons = attributeIcons;
                InsertionText = insertionText;
            }

            public string DisplayText { get; }

            public ImageElement Icon { get; }

            public ImmutableArray<AsyncCompletionData.CompletionFilter> Filters { get; }

            /// <summary>
            /// This is the bit vector value from the FilterSet of this item.
            /// </summary>
            public int FilterSetData { get; }

            public ImmutableArray<ImageElement> AttributeIcons { get; }

            public string InsertionText { get; }
        }

        private VSCompletionItem Convert(
            Document document,
            RoslynCompletionItem roslynItem,
            FilterSet filterSet,
            SnapshotPoint initialTriggerLocation)
        {
            VSCompletionItemData itemData;

            if (roslynItem.Flags.IsCached() && s_roslynItemToVsItemData.TryGetValue(roslynItem, out var boxedItemData))
            {
                itemData = boxedItemData.Value;
                filterSet.CombineData(itemData.FilterSetData);
            }
            else
            {
                var imageId = roslynItem.Tags.GetFirstGlyph().GetImageId();
                var (filters, filterSetData) = filterSet.GetFiltersAndAddToSet(roslynItem);

                // roslynItem generated by providers can contain an insertionText in a property bag.
                // We will not use it but other providers may need it.
                // We actually will calculate the insertion text once again when called TryCommit.
                if (!roslynItem.Properties.TryGetValue(InsertionText, out var insertionText))
                {
                    insertionText = roslynItem.DisplayText;
                }

                var supportedPlatforms = SymbolCompletionItem.GetSupportedPlatforms(roslynItem, document.Project.Solution.Workspace);
                var attributeImages = supportedPlatforms != null ? s_WarningImageAttributeImagesArray : ImmutableArray<ImageElement>.Empty;

                itemData = new VSCompletionItemData(
                    displayText: roslynItem.GetEntireDisplayText(),
                    icon: new ImageElement(new ImageId(imageId.Guid, imageId.Id), roslynItem.DisplayText),
                    filters: filters,
                    filterSetData: filterSetData,
                    attributeIcons: attributeImages,
                    insertionText: insertionText);

                // It doesn't make sense to cache VS item data for those Roslyn items created from scratch for each session,
                // since CWT uses object identity for comparison.
                if (roslynItem.Flags.IsCached())
                {
                    s_roslynItemToVsItemData.Add(roslynItem, new StrongBox<VSCompletionItemData>(itemData));
                }
            }

            var item = new VSCompletionItem(
                displayText: itemData.DisplayText,
                source: this,
                icon: itemData.Icon,
                filters: itemData.Filters,
                suffix: roslynItem.InlineDescription, // InlineDescription will be right-aligned in the selection popup
                insertText: itemData.InsertionText,
                sortText: roslynItem.SortText,
                filterText: roslynItem.FilterText,
                automationText: roslynItem.AutomationText ?? roslynItem.DisplayText,
                attributeIcons: itemData.AttributeIcons);

            item.Properties.AddProperty(RoslynItem, roslynItem);
            item.Properties.AddProperty(TriggerLocation, initialTriggerLocation);

            return item;
        }

        private static ImmutableArray<char> GetExcludedCommitCharacters(ImmutableArray<RoslynCompletionItem> roslynItems)
        {
            var hashSet = new HashSet<char>();
            foreach (var roslynItem in roslynItems)
            {
                foreach (var rule in roslynItem.Rules.FilterCharacterRules)
                {
                    if (rule.Kind == CharacterSetModificationKind.Add)
                    {
                        foreach (var c in rule.Characters)
                        {
                            hashSet.Add(c);
                        }
                    }
                }
            }

            return hashSet.ToImmutableArray();
        }

        internal static bool QuestionMarkIsPrecededByIdentifierAndWhitespace(
            SourceText text, int questionPosition, ISyntaxFactsService syntaxFacts)
        {
            var startOfLine = text.Lines.GetLineFromPosition(questionPosition).Start;

            // First, skip all the whitespace.
            var current = startOfLine;
            while (current < questionPosition && char.IsWhiteSpace(text[current]))
            {
                current++;
            }

            if (current < questionPosition && syntaxFacts.IsIdentifierStartCharacter(text[current]))
            {
                current++;
            }
            else
            {
                return false;
            }

            while (current < questionPosition && syntaxFacts.IsIdentifierPartCharacter(text[current]))
            {
                current++;
            }

            return current == questionPosition;
        }
    }
}
