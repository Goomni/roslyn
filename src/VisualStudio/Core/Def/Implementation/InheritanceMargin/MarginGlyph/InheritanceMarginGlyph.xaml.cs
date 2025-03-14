﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.GoToDefinition;
using Microsoft.CodeAnalysis.Editor.Host;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.FindUsages;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.InheritanceMargin.MarginGlyph
{
    internal partial class InheritanceMarginGlyph
    {
        private readonly IThreadingContext _threadingContext;
        private readonly IStreamingFindUsagesPresenter _streamingFindUsagesPresenter;
        private readonly IUIThreadOperationExecutor _operationExecutor;
        private readonly Workspace _workspace;
        private readonly IWpfTextView _textView;
        private readonly IAsynchronousOperationListener _listener;

        public InheritanceMarginGlyph(
            IThreadingContext threadingContext,
            IStreamingFindUsagesPresenter streamingFindUsagesPresenter,
            ClassificationTypeMap classificationTypeMap,
            IClassificationFormatMap classificationFormatMap,
            IUIThreadOperationExecutor operationExecutor,
            InheritanceMarginTag tag,
            IWpfTextView textView,
            IAsynchronousOperationListener listener)
        {
            _threadingContext = threadingContext;
            _streamingFindUsagesPresenter = streamingFindUsagesPresenter;
            _workspace = tag.Workspace;
            _operationExecutor = operationExecutor;
            _textView = textView;
            _listener = listener;
            InitializeComponent();

            var viewModel = InheritanceMarginGlyphViewModel.Create(classificationTypeMap, classificationFormatMap, tag, textView.ZoomLevel);
            DataContext = viewModel;
            ContextMenu.DataContext = viewModel;
            ToolTip = new ToolTip { Content = viewModel.ToolTipTextBlock, Style = (Style)FindResource("ToolTipStyle") };
        }

        private void InheritanceMargin_OnClick(object sender, RoutedEventArgs e)
        {
            if (this.ContextMenu != null)
            {
                this.ContextMenu.IsOpen = true;
                e.Handled = true;
            }
        }

        private void TargetMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is MenuItem { DataContext: TargetMenuItemViewModel viewModel })
            {
                Logger.Log(FunctionId.InheritanceMargin_NavigateToTarget, KeyValueLogMessage.Create(LogType.UserAction));

                var token = _listener.BeginAsyncOperation(nameof(TargetMenuItem_OnClick));
                TargetMenuItem_OnClickAsync(viewModel).CompletesAsyncOperation(token);
            }
        }

        private async Task TargetMenuItem_OnClickAsync(TargetMenuItemViewModel viewModel)
        {
            using var context = _operationExecutor.BeginExecute(
                title: EditorFeaturesResources.Navigating,
                defaultDescription: string.Format(ServicesVSResources.Navigate_to_0, viewModel.DisplayContent),
                allowCancellation: true,
                showProgress: false);

            var cancellationToken = context.UserCancellationToken;
            var rehydrated = await viewModel.DefinitionItem.TryRehydrateAsync(cancellationToken).ConfigureAwait(false);
            if (rehydrated == null)
                return;

            await _streamingFindUsagesPresenter.TryNavigateToOrPresentItemsAsync(
                _threadingContext,
                _workspace,
                string.Format(EditorFeaturesResources._0_declarations, viewModel.DisplayContent),
                ImmutableArray.Create<DefinitionItem>(rehydrated),
                cancellationToken).ConfigureAwait(false);
        }

        private void ChangeBorderToHoveringColor()
        {
            SetResourceReference(BackgroundProperty, VsBrushes.CommandBarMenuBackgroundGradientKey);
            SetResourceReference(BorderBrushProperty, VsBrushes.CommandBarMenuBorderKey);
        }

        private void InheritanceMargin_OnMouseEnter(object sender, MouseEventArgs e)
        {
            ChangeBorderToHoveringColor();
        }

        private void InheritanceMargin_OnMouseLeave(object sender, MouseEventArgs e)
        {
            // If the context menu is open, then don't reset the color of the button because we need
            // the margin looks like being pressed.
            if (!ContextMenu.IsOpen)
            {
                ResetBorderToInitialColor();
            }
        }

        private void ContextMenu_OnClose(object sender, RoutedEventArgs e)
        {
            // If mouse is still hovering. Don't reset the color. The context menu might be closed because user clicks within the margin
            if (!IsMouseOver)
            {
                ResetBorderToInitialColor();
            }
            // Move the focus back to textView when the context menu is closed.
            // It ensures the focus won't be left at the margin
            ResetFocus();
        }

        private void ContextMenu_OnOpen(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is ContextMenu { DataContext: InheritanceMarginGlyphViewModel inheritanceMarginViewModel }
                && inheritanceMarginViewModel.MenuItemViewModels.Any(vm => vm is TargetMenuItemViewModel))
            {
                // We have two kinds of context menu. e.g.
                // 1. [margin] -> Header
                //                Target1
                //                Target2
                //                Target3
                //
                // 2. [margin] -> method Bar -> Header
                //                           -> Target1
                //                           -> Target2
                //             -> method Foo -> Header
                //                           -> Target3
                //                           -> Target4
                // If the first level of the context menu contains a TargetMenuItemViewModel, it means here it is case 1,
                // user is viewing the targets menu.
                Logger.Log(FunctionId.InheritanceMargin_TargetsMenuOpen, KeyValueLogMessage.Create(LogType.UserAction));
            }
        }

        private void TargetsSubmenu_OnOpen(object sender, RoutedEventArgs e)
        {
            Logger.Log(FunctionId.InheritanceMargin_TargetsMenuOpen, KeyValueLogMessage.Create(LogType.UserAction));
        }

        private void ResetBorderToInitialColor()
        {
            this.Background = Brushes.Transparent;
            this.BorderBrush = Brushes.Transparent;
        }

        private void ResetFocus()
        {
            if (!_textView.HasAggregateFocus)
            {
                var visualElement = _textView.VisualElement;
                if (visualElement.Focusable)
                {
                    Keyboard.Focus(visualElement);
                }
            }
        }
    }
}
