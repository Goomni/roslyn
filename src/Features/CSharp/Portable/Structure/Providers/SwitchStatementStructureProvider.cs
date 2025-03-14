﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Shared.Collections;
using Microsoft.CodeAnalysis.Structure;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.Structure
{
    internal class SwitchStatementStructureProvider : AbstractSyntaxNodeStructureProvider<SwitchStatementSyntax>
    {
        protected override void CollectBlockSpans(
            SyntaxToken previousToken,
            SwitchStatementSyntax node,
            ref TemporaryArray<BlockSpan> spans,
            BlockStructureOptionProvider optionProvider,
            CancellationToken cancellationToken)
        {
            spans.Add(new BlockSpan(
                isCollapsible: true,
                textSpan: TextSpan.FromBounds((node.CloseParenToken != default) ? node.CloseParenToken.Span.End : node.Expression.Span.End, node.CloseBraceToken.Span.End),
                hintSpan: node.Span,
                type: BlockTypes.Conditional));
        }
    }
}
