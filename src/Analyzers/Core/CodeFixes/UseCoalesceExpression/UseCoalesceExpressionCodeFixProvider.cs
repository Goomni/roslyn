﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.UseCoalesceExpression
{
    [ExportCodeFixProvider(LanguageNames.CSharp, LanguageNames.VisualBasic, Name = PredefinedCodeFixProviderNames.UseCoalesceExpression), Shared]
    internal class UseCoalesceExpressionCodeFixProvider : SyntaxEditorBasedCodeFixProvider
    {
        [ImportingConstructor]
        [SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
        public UseCoalesceExpressionCodeFixProvider()
        {
        }

        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(IDEDiagnosticIds.UseCoalesceExpressionDiagnosticId);

        internal sealed override CodeFixCategory CodeFixCategory => CodeFixCategory.CodeStyle;

        protected override bool IncludeDiagnosticDuringFixAll(Diagnostic diagnostic)
            => !diagnostic.Descriptor.ImmutableCustomTags().Contains(WellKnownDiagnosticTags.Unnecessary);

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            context.RegisterCodeFix(new MyCodeAction(
                c => FixAsync(context.Document, context.Diagnostics[0], c)),
                context.Diagnostics);
            return Task.CompletedTask;
        }

        protected override async Task FixAllAsync(
            Document document, ImmutableArray<Diagnostic> diagnostics,
            SyntaxEditor editor, CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var expressionTypeOpt = semanticModel.Compilation.GetTypeByMetadataName("System.Linq.Expressions.Expression`1");
            var syntaxFacts = document.GetLanguageService<ISyntaxFactsService>();
            var semanticFacts = document.GetLanguageService<ISemanticFactsService>();

            foreach (var diagnostic in diagnostics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyEdit(
                    editor, semanticModel, expressionTypeOpt,
                    syntaxFacts, semanticFacts,
                    diagnostic, cancellationToken);
            }
        }

        private static void ApplyEdit(
            SyntaxEditor editor, SemanticModel semanticModel, INamedTypeSymbol expressionTypeOpt,
            ISyntaxFactsService syntaxFacts, ISemanticFactsService semanticFacts,
            Diagnostic diagnostic, CancellationToken cancellationToken)
        {
            var root = editor.OriginalRoot;
            var conditionalExpression = root.FindNode(diagnostic.AdditionalLocations[0].SourceSpan, getInnermostNodeForTie: true);
            var conditionalPartHigh = root.FindNode(diagnostic.AdditionalLocations[1].SourceSpan);
            var whenPart = root.FindNode(diagnostic.AdditionalLocations[2].SourceSpan);
            syntaxFacts.GetPartsOfConditionalExpression(
                conditionalExpression, out var condition, out var whenTrue, out var whenFalse);

            var conditionalPartLow = syntaxFacts.WalkDownParentheses(conditionalPartHigh);
            editor.ReplaceNode(conditionalExpression,
                (c, g) =>
                {
                    syntaxFacts.GetPartsOfConditionalExpression(
                        c, out var currentCondition, out var currentWhenTrue, out var currentWhenFalse);

                    var coalesceExpression = GetCoalesceExpression(
                        syntaxFacts, g, whenPart, whenTrue, conditionalPartLow,
                        currentWhenTrue, currentWhenFalse)
                        .WithTrailingTrivia(conditionalExpression.GetTrailingTrivia());

                    if (semanticFacts.IsInExpressionTree(
                            semanticModel, conditionalExpression, expressionTypeOpt, cancellationToken))
                    {
                        coalesceExpression = coalesceExpression.WithAdditionalAnnotations(
                            WarningAnnotation.Create(AnalyzersResources.Changes_to_expression_trees_may_result_in_behavior_changes_at_runtime));
                    }

                    return coalesceExpression.WithAdditionalAnnotations(Formatter.Annotation);
                });
        }

        private static SyntaxNode GetCoalesceExpression(
            ISyntaxFactsService syntaxFacts, SyntaxGenerator generator,
            SyntaxNode whenPart, SyntaxNode whenTrue,
            SyntaxNode conditionalPartLow,
            SyntaxNode currentWhenTrue, SyntaxNode currentWhenFalse)
        {
            return whenPart == whenTrue
                ? generator.CoalesceExpression(conditionalPartLow, syntaxFacts.WalkDownParentheses(currentWhenTrue))
                : generator.CoalesceExpression(conditionalPartLow, syntaxFacts.WalkDownParentheses(currentWhenFalse));
        }

        private class MyCodeAction : CustomCodeActions.DocumentChangeAction
        {
            public MyCodeAction(Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(AnalyzersResources.Use_coalesce_expression, createChangedDocument, nameof(AnalyzersResources.Use_coalesce_expression))
            {

            }
        }
    }
}
