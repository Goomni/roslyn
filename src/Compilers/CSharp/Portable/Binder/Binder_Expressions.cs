﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// This portion of the binder converts an <see cref="ExpressionSyntax"/> into a <see cref="BoundExpression"/>.
    /// </summary>
    internal partial class Binder
    {
        /// <summary>
        /// Determines whether "this" reference is available within the current context.
        /// </summary>
        /// <param name="isExplicit">The reference was explicitly specified in syntax.</param>
        /// <param name="inStaticContext">True if "this" is not available due to the current method/property/field initializer being static.</param>
        /// <returns>True if a reference to "this" is available.</returns>
        internal bool HasThis(bool isExplicit, out bool inStaticContext)
        {
            var memberOpt = this.ContainingMemberOrLambda?.ContainingNonLambdaMember();
            if (memberOpt?.IsStatic == true)
            {
                inStaticContext = memberOpt.Kind == SymbolKind.Field || memberOpt.Kind == SymbolKind.Method || memberOpt.Kind == SymbolKind.Property;
                return false;
            }

            inStaticContext = false;

            if (InConstructorInitializer || InAttributeArgument)
            {
                return false;
            }

            var containingType = memberOpt?.ContainingType;
            bool inTopLevelScriptMember = (object)containingType != null && containingType.IsScriptClass;

            // "this" is not allowed in field initializers (that are not script variable initializers):
            if (InFieldInitializer && !inTopLevelScriptMember)
            {
                return false;
            }

            // top-level script code only allows implicit "this" reference:
            return !inTopLevelScriptMember || !isExplicit;
        }

        internal bool InFieldInitializer
        {
            get { return this.Flags.Includes(BinderFlags.FieldInitializer); }
        }

        internal bool InParameterDefaultValue
        {
            get { return this.Flags.Includes(BinderFlags.ParameterDefaultValue); }
        }

        protected bool InConstructorInitializer
        {
            get { return this.Flags.Includes(BinderFlags.ConstructorInitializer); }
        }

        internal bool InAttributeArgument
        {
            get { return this.Flags.Includes(BinderFlags.AttributeArgument); }
        }

        internal bool InCref
        {
            get { return this.Flags.Includes(BinderFlags.Cref); }
        }

        protected bool InCrefButNotParameterOrReturnType
        {
            get { return InCref && !this.Flags.Includes(BinderFlags.CrefParameterOrReturnType); }
        }

        /// <summary>
        /// Returns true if the node is in a position where an unbound type
        /// such as (C&lt;,&gt;) is allowed.
        /// </summary>
        protected virtual bool IsUnboundTypeAllowed(GenericNameSyntax syntax)
        {
            return Next.IsUnboundTypeAllowed(syntax);
        }

        /// <summary>
        /// Generates a new <see cref="BoundBadExpression"/> with no known type
        /// </summary>
        private BoundBadExpression BadExpression(SyntaxNode syntax)
        {
            return BadExpression(syntax, LookupResultKind.Empty, ImmutableArray<Symbol>.Empty);
        }

        /// <summary>
        /// Generates a new <see cref="BoundBadExpression"/> with no known type, and the given bound child.
        /// </summary>
        private BoundBadExpression BadExpression(SyntaxNode syntax, BoundExpression childNode)
        {
            return BadExpression(syntax, LookupResultKind.Empty, ImmutableArray<Symbol>.Empty, childNode);
        }

        /// <summary>
        /// Generates a new <see cref="BoundBadExpression"/> with no known type, and the given bound children.
        /// </summary>
        private BoundBadExpression BadExpression(SyntaxNode syntax, ImmutableArray<BoundExpression> childNodes)
        {
            return BadExpression(syntax, LookupResultKind.Empty, ImmutableArray<Symbol>.Empty, childNodes);
        }

        /// <summary>
        /// Generates a new <see cref="BoundBadExpression"/> with no known type, given lookup resultKind.
        /// </summary>
        protected BoundBadExpression BadExpression(SyntaxNode syntax, LookupResultKind lookupResultKind)
        {
            return BadExpression(syntax, lookupResultKind, ImmutableArray<Symbol>.Empty);
        }

        /// <summary>
        /// Generates a new <see cref="BoundBadExpression"/> with no known type, given lookup resultKind and the given bound child.
        /// </summary>
        protected BoundBadExpression BadExpression(SyntaxNode syntax, LookupResultKind lookupResultKind, BoundExpression childNode)
        {
            return BadExpression(syntax, lookupResultKind, ImmutableArray<Symbol>.Empty, childNode);
        }

        /// <summary>
        /// Generates a new <see cref="BoundBadExpression"/> with no known type, given lookupResultKind and given symbols for GetSemanticInfo API.
        /// </summary>
        private BoundBadExpression BadExpression(SyntaxNode syntax, LookupResultKind resultKind, ImmutableArray<Symbol> symbols)
        {
            return new BoundBadExpression(syntax,
                resultKind,
                symbols,
                ImmutableArray<BoundExpression>.Empty,
                CreateErrorType());
        }

        /// <summary>
        /// Generates a new <see cref="BoundBadExpression"/> with no known type, given lookupResultKind and given symbols for GetSemanticInfo API,
        /// and the given bound child.
        /// </summary>
        private BoundBadExpression BadExpression(SyntaxNode syntax, LookupResultKind resultKind, ImmutableArray<Symbol> symbols, BoundExpression childNode)
        {
            return new BoundBadExpression(syntax,
                resultKind,
                symbols,
                ImmutableArray.Create(BindToTypeForErrorRecovery(childNode)),
                CreateErrorType());
        }

        /// <summary>
        /// Generates a new <see cref="BoundBadExpression"/> with no known type, given lookupResultKind and given symbols for GetSemanticInfo API,
        /// and the given bound children.
        /// </summary>
        private BoundBadExpression BadExpression(SyntaxNode syntax, LookupResultKind resultKind, ImmutableArray<Symbol> symbols, ImmutableArray<BoundExpression> childNodes, bool wasCompilerGenerated = false)
        {
            return new BoundBadExpression(syntax,
                resultKind,
                symbols,
                childNodes.SelectAsArray((e, self) => self.BindToTypeForErrorRecovery(e), this),
                CreateErrorType())
            { WasCompilerGenerated = wasCompilerGenerated };
        }

        /// <summary>
        /// Helper method to generate a bound expression with HasErrors set to true.
        /// Returned bound expression is guaranteed to have a non-null type, except when <paramref name="expr"/> is an unbound lambda.
        /// If <paramref name="expr"/> already has errors and meets the above type requirements, then it is returned unchanged.
        /// Otherwise, if <paramref name="expr"/> is a BoundBadExpression, then it is updated with the <paramref name="resultKind"/> and non-null type.
        /// Otherwise, a new <see cref="BoundBadExpression"/> wrapping <paramref name="expr"/> is returned. 
        /// </summary>
        /// <remarks>
        /// Returned expression need not be a <see cref="BoundBadExpression"/>, but is guaranteed to have HasErrors set to true.
        /// </remarks>
        private BoundExpression ToBadExpression(BoundExpression expr, LookupResultKind resultKind = LookupResultKind.Empty)
        {
            Debug.Assert(expr != null);
            Debug.Assert(resultKind != LookupResultKind.Viable);

            TypeSymbol resultType = expr.Type;
            BoundKind exprKind = expr.Kind;

            if (expr.HasAnyErrors && ((object)resultType != null || exprKind == BoundKind.UnboundLambda || exprKind == BoundKind.DefaultLiteral))
            {
                return expr;
            }

            if (exprKind == BoundKind.BadExpression)
            {
                var badExpression = (BoundBadExpression)expr;
                return badExpression.Update(resultKind, badExpression.Symbols, badExpression.ChildBoundNodes, resultType);
            }
            else
            {
                ArrayBuilder<Symbol> symbols = ArrayBuilder<Symbol>.GetInstance();
                expr.GetExpressionSymbols(symbols, parent: null, binder: this);
                return new BoundBadExpression(
                    expr.Syntax,
                    resultKind,
                    symbols.ToImmutableAndFree(),
                    ImmutableArray.Create(BindToTypeForErrorRecovery(expr)),
                    resultType ?? CreateErrorType());
            }
        }

        internal NamedTypeSymbol CreateErrorType(string name = "")
        {
            return new ExtendedErrorTypeSymbol(this.Compilation, name, arity: 0, errorInfo: null, unreported: false);
        }

        /// <summary>
        /// Bind the expression and verify the expression matches the combination of lvalue and
        /// rvalue requirements given by valueKind. If the expression was bound successfully, but
        /// did not meet the requirements, the return value will be a <see cref="BoundBadExpression"/> that
        /// (typically) wraps the subexpression.
        /// </summary>
        internal BoundExpression BindValue(ExpressionSyntax node, BindingDiagnosticBag diagnostics, BindValueKind valueKind)
        {
            var result = this.BindExpression(node, diagnostics: diagnostics, invoked: false, indexed: false);
            return CheckValue(result, valueKind, diagnostics);
        }

        internal BoundExpression BindRValueWithoutTargetType(ExpressionSyntax node, BindingDiagnosticBag diagnostics, bool reportNoTargetType = true)
        {
            return BindToNaturalType(BindValue(node, diagnostics, BindValueKind.RValue), diagnostics, reportNoTargetType);
        }

        /// <summary>
        /// When binding a switch case's expression, it is possible that it resolves to a type (technically, a type pattern).
        /// This implementation permits either an rvalue or a BoundTypeExpression.
        /// </summary>
        internal BoundExpression BindTypeOrRValue(ExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            var valueOrType = BindExpression(node, diagnostics: diagnostics, invoked: false, indexed: false);
            if (valueOrType.Kind == BoundKind.TypeExpression)
            {
                // In the Color Color case (Kind == BoundKind.TypeOrValueExpression), we treat it as a value
                // by not entering this if statement
                return valueOrType;
            }

            return CheckValue(valueOrType, BindValueKind.RValue, diagnostics);
        }

        internal BoundExpression BindToTypeForErrorRecovery(BoundExpression expression, TypeSymbol type = null)
        {
            if (expression is null)
                return null;
            var result =
                !expression.NeedsToBeConverted() ? expression :
                type is null ? BindToNaturalType(expression, BindingDiagnosticBag.Discarded, reportNoTargetType: false) :
                GenerateConversionForAssignment(type, expression, BindingDiagnosticBag.Discarded);
            return result;
        }

        /// <summary>
        /// Bind an rvalue expression to its natural type.  For example, a switch expression that has not been
        /// converted to another type has to be converted to its own natural type by applying a conversion to
        /// that type to each of the arms of the switch expression.  This method is a bottleneck for ensuring
        /// that such a conversion occurs when needed.  It also handles tuple expressions which need to be
        /// converted to their own natural type because they may contain switch expressions.
        /// </summary>
        internal BoundExpression BindToNaturalType(BoundExpression expression, BindingDiagnosticBag diagnostics, bool reportNoTargetType = true)
        {
            if (!expression.NeedsToBeConverted())
                return expression;

            BoundExpression result;
            switch (expression)
            {
                case BoundUnconvertedSwitchExpression expr:
                    {
                        var commonType = expr.Type;
                        var exprSyntax = (SwitchExpressionSyntax)expr.Syntax;
                        bool hasErrors = expression.HasErrors;
                        if (commonType is null)
                        {
                            diagnostics.Add(ErrorCode.ERR_SwitchExpressionNoBestType, exprSyntax.SwitchKeyword.GetLocation());
                            commonType = CreateErrorType();
                            hasErrors = true;
                        }
                        result = ConvertSwitchExpression(expr, commonType, conversionIfTargetTyped: null, diagnostics, hasErrors);
                    }
                    break;
                case BoundUnconvertedConditionalOperator op:
                    {
                        TypeSymbol type = op.Type;
                        bool hasErrors = op.HasErrors;
                        if (type is null)
                        {
                            Debug.Assert(op.NoCommonTypeError != 0);
                            type = CreateErrorType();
                            hasErrors = true;
                            object trueArg = op.Consequence.Display;
                            object falseArg = op.Alternative.Display;
                            if (op.NoCommonTypeError == ErrorCode.ERR_InvalidQM && trueArg is Symbol trueSymbol && falseArg is Symbol falseSymbol)
                            {
                                // ERR_InvalidQM is an error that there is no conversion between the two types. They might be the same
                                // type name from different assemblies, so we disambiguate the display.
                                SymbolDistinguisher distinguisher = new SymbolDistinguisher(this.Compilation, trueSymbol, falseSymbol);
                                trueArg = distinguisher.First;
                                falseArg = distinguisher.Second;
                            }

                            diagnostics.Add(op.NoCommonTypeError, op.Syntax.Location, trueArg, falseArg);
                        }

                        result = ConvertConditionalExpression(op, type, conversionIfTargetTyped: null, diagnostics, hasErrors);
                    }
                    break;
                case BoundTupleLiteral sourceTuple:
                    {
                        var boundArgs = ArrayBuilder<BoundExpression>.GetInstance(sourceTuple.Arguments.Length);
                        foreach (var arg in sourceTuple.Arguments)
                        {
                            boundArgs.Add(BindToNaturalType(arg, diagnostics, reportNoTargetType));
                        }
                        result = new BoundConvertedTupleLiteral(
                            sourceTuple.Syntax,
                            sourceTuple,
                            wasTargetTyped: false,
                            boundArgs.ToImmutableAndFree(),
                            sourceTuple.ArgumentNamesOpt,
                            sourceTuple.InferredNamesOpt,
                            sourceTuple.Type, // same type to keep original element names
                            sourceTuple.HasErrors).WithSuppression(sourceTuple.IsSuppressed);
                    }
                    break;
                case BoundDefaultLiteral defaultExpr:
                    {
                        if (reportNoTargetType)
                        {
                            // In some cases, we let the caller report the error
                            diagnostics.Add(ErrorCode.ERR_DefaultLiteralNoTargetType, defaultExpr.Syntax.GetLocation());
                        }

                        result = new BoundDefaultExpression(
                            defaultExpr.Syntax,
                            targetType: null,
                            defaultExpr.ConstantValue,
                            CreateErrorType(),
                            hasErrors: true).WithSuppression(defaultExpr.IsSuppressed);
                    }
                    break;
                case BoundStackAllocArrayCreation { Type: null } boundStackAlloc:
                    {
                        // This is a context in which the stackalloc could be either a pointer
                        // or a span.  For backward compatibility we treat it as a pointer.
                        var type = new PointerTypeSymbol(TypeWithAnnotations.Create(boundStackAlloc.ElementType));
                        result = GenerateConversionForAssignment(type, boundStackAlloc, diagnostics);
                    }
                    break;
                case BoundUnconvertedObjectCreationExpression expr:
                    {
                        if (reportNoTargetType && !expr.HasAnyErrors)
                        {
                            diagnostics.Add(ErrorCode.ERR_ImplicitObjectCreationNoTargetType, expr.Syntax.GetLocation(), expr.Display);
                        }

                        result = BindObjectCreationForErrorRecovery(expr, diagnostics);
                    }
                    break;
                case BoundUnconvertedInterpolatedString unconvertedInterpolatedString:
                    {
                        result = BindUnconvertedInterpolatedStringToString(unconvertedInterpolatedString, diagnostics);
                    }
                    break;
                case BoundBinaryOperator unconvertedBinaryOperator:
                    {
                        result = RebindSimpleBinaryOperatorAsConverted(unconvertedBinaryOperator, diagnostics);
                    }
                    break;
                default:
                    result = expression;
                    break;
            }

            return result?.WithWasConverted();
        }

        private BoundExpression BindToInferredDelegateType(BoundExpression expr, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(expr.Kind is BoundKind.UnboundLambda or BoundKind.MethodGroup);

            var syntax = expr.Syntax;
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            var delegateType = expr.GetInferredDelegateType(ref useSiteInfo);
            diagnostics.Add(syntax, useSiteInfo);

            if (delegateType is null)
            {
                if (CheckFeatureAvailability(syntax, MessageID.IDS_FeatureInferredDelegateType, diagnostics))
                {
                    diagnostics.Add(ErrorCode.ERR_CannotInferDelegateType, syntax.GetLocation());
                }
                delegateType = CreateErrorType();
            }

            return GenerateConversionForAssignment(delegateType, expr, diagnostics);
        }

        internal BoundExpression BindValueAllowArgList(ExpressionSyntax node, BindingDiagnosticBag diagnostics, BindValueKind valueKind)
        {
            var result = this.BindExpressionAllowArgList(node, diagnostics: diagnostics);
            return CheckValue(result, valueKind, diagnostics);
        }

        internal BoundFieldEqualsValue BindFieldInitializer(
            FieldSymbol field,
            EqualsValueClauseSyntax initializerOpt,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert((object)this.ContainingMemberOrLambda == field);

            if (initializerOpt == null)
            {
                return null;
            }

            Binder initializerBinder = this.GetBinder(initializerOpt);
            Debug.Assert(initializerBinder != null);

            BoundExpression result = initializerBinder.BindVariableOrAutoPropInitializerValue(initializerOpt, RefKind.None,
                                                           field.GetFieldType(initializerBinder.FieldsBeingBound).Type, diagnostics);

            return new BoundFieldEqualsValue(initializerOpt, field, initializerBinder.GetDeclaredLocalsForScope(initializerOpt), result);
        }

        internal BoundExpression BindVariableOrAutoPropInitializerValue(
            EqualsValueClauseSyntax initializerOpt,
            RefKind refKind,
            TypeSymbol varType,
            BindingDiagnosticBag diagnostics)
        {
            if (initializerOpt == null)
            {
                return null;
            }

            BindValueKind valueKind;
            ExpressionSyntax value;
            IsInitializerRefKindValid(initializerOpt, initializerOpt, refKind, diagnostics, out valueKind, out value);
            BoundExpression initializer = BindPossibleArrayInitializer(value, varType, valueKind, diagnostics);
            initializer = GenerateConversionForAssignment(varType, initializer, diagnostics);
            return initializer;
        }

        internal Binder CreateBinderForParameterDefaultValue(
            ParameterSymbol parameter,
            EqualsValueClauseSyntax defaultValueSyntax)
        {
            var binder = new LocalScopeBinder(this.WithContainingMemberOrLambda(parameter.ContainingSymbol).WithAdditionalFlags(BinderFlags.ParameterDefaultValue));
            return new ExecutableCodeBinder(defaultValueSyntax,
                                            parameter.ContainingSymbol,
                                            binder);
        }

        internal BoundParameterEqualsValue BindParameterDefaultValue(
            EqualsValueClauseSyntax defaultValueSyntax,
            ParameterSymbol parameter,
            BindingDiagnosticBag diagnostics,
            out BoundExpression valueBeforeConversion)
        {
            Debug.Assert(this.InParameterDefaultValue);
            Debug.Assert(this.ContainingMemberOrLambda.Kind == SymbolKind.Method || this.ContainingMemberOrLambda.Kind == SymbolKind.Property);

            // UNDONE: The binding and conversion has to be executed in a checked context.
            Binder defaultValueBinder = this.GetBinder(defaultValueSyntax);
            Debug.Assert(defaultValueBinder != null);

            valueBeforeConversion = defaultValueBinder.BindValue(defaultValueSyntax.Value, diagnostics, BindValueKind.RValue);

            // Always generate the conversion, even if the expression is not convertible to the given type.
            // We want the erroneous conversion in the tree.
            var result = new BoundParameterEqualsValue(defaultValueSyntax, parameter, defaultValueBinder.GetDeclaredLocalsForScope(defaultValueSyntax),
                              defaultValueBinder.GenerateConversionForAssignment(parameter.Type, valueBeforeConversion, diagnostics, isDefaultParameter: true));

            return result;
        }

        internal BoundFieldEqualsValue BindEnumConstantInitializer(
            SourceEnumConstantSymbol symbol,
            EqualsValueClauseSyntax equalsValueSyntax,
            BindingDiagnosticBag diagnostics)
        {
            Binder initializerBinder = this.GetBinder(equalsValueSyntax);
            Debug.Assert(initializerBinder != null);

            var initializer = initializerBinder.BindValue(equalsValueSyntax.Value, diagnostics, BindValueKind.RValue);
            initializer = initializerBinder.GenerateConversionForAssignment(symbol.ContainingType.EnumUnderlyingType, initializer, diagnostics);
            return new BoundFieldEqualsValue(equalsValueSyntax, symbol, initializerBinder.GetDeclaredLocalsForScope(equalsValueSyntax), initializer);
        }

        public BoundExpression BindExpression(ExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            return BindExpression(node, diagnostics: diagnostics, invoked: false, indexed: false);
        }

        protected BoundExpression BindExpression(ExpressionSyntax node, BindingDiagnosticBag diagnostics, bool invoked, bool indexed)
        {
            BoundExpression expr = BindExpressionInternal(node, diagnostics, invoked, indexed);
            VerifyUnchecked(node, diagnostics, expr);

            if (expr.Kind == BoundKind.ArgListOperator)
            {
                // CS0226: An __arglist expression may only appear inside of a call or new expression
                Error(diagnostics, ErrorCode.ERR_IllegalArglist, node);
                expr = ToBadExpression(expr);
            }

            return expr;
        }

        // PERF: allowArgList is not a parameter because it is fairly uncommon case where arglists are allowed
        //       so we do not want to pass that argument to every BindExpression which is often recursive 
        //       and extra arguments contribute to the stack size.
        protected BoundExpression BindExpressionAllowArgList(ExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            BoundExpression expr = BindExpressionInternal(node, diagnostics, invoked: false, indexed: false);
            VerifyUnchecked(node, diagnostics, expr);
            return expr;
        }

        private void VerifyUnchecked(ExpressionSyntax node, BindingDiagnosticBag diagnostics, BoundExpression expr)
        {
            if (!expr.HasAnyErrors && !IsInsideNameof)
            {
                TypeSymbol exprType = expr.Type;
                if ((object)exprType != null && exprType.IsUnsafe())
                {
                    ReportUnsafeIfNotAllowed(node, diagnostics);
                    //CONSIDER: Return a bad expression so that HasErrors is true?
                }
            }
        }

        private BoundExpression BindExpressionInternal(ExpressionSyntax node, BindingDiagnosticBag diagnostics, bool invoked, bool indexed)
        {
            if (IsEarlyAttributeBinder && !EarlyWellKnownAttributeBinder.CanBeValidAttributeArgument(node, this))
            {
                return BadExpression(node, LookupResultKind.NotAValue);
            }

            Debug.Assert(node != null);
            switch (node.Kind())
            {
                case SyntaxKind.AnonymousMethodExpression:
                case SyntaxKind.ParenthesizedLambdaExpression:
                case SyntaxKind.SimpleLambdaExpression:
                    return BindAnonymousFunction((AnonymousFunctionExpressionSyntax)node, diagnostics);
                case SyntaxKind.ThisExpression:
                    return BindThis((ThisExpressionSyntax)node, diagnostics);
                case SyntaxKind.BaseExpression:
                    return BindBase((BaseExpressionSyntax)node, diagnostics);
                case SyntaxKind.InvocationExpression:
                    return BindInvocationExpression((InvocationExpressionSyntax)node, diagnostics);
                case SyntaxKind.ArrayInitializerExpression:
                    return BindUnexpectedArrayInitializer((InitializerExpressionSyntax)node, diagnostics, ErrorCode.ERR_ArrayInitInBadPlace);
                case SyntaxKind.ArrayCreationExpression:
                    return BindArrayCreationExpression((ArrayCreationExpressionSyntax)node, diagnostics);
                case SyntaxKind.ImplicitArrayCreationExpression:
                    return BindImplicitArrayCreationExpression((ImplicitArrayCreationExpressionSyntax)node, diagnostics);
                case SyntaxKind.StackAllocArrayCreationExpression:
                    return BindStackAllocArrayCreationExpression((StackAllocArrayCreationExpressionSyntax)node, diagnostics);
                case SyntaxKind.ImplicitStackAllocArrayCreationExpression:
                    return BindImplicitStackAllocArrayCreationExpression((ImplicitStackAllocArrayCreationExpressionSyntax)node, diagnostics);
                case SyntaxKind.ObjectCreationExpression:
                    return BindObjectCreationExpression((ObjectCreationExpressionSyntax)node, diagnostics);
                case SyntaxKind.ImplicitObjectCreationExpression:
                    return BindImplicitObjectCreationExpression((ImplicitObjectCreationExpressionSyntax)node, diagnostics);
                case SyntaxKind.IdentifierName:
                case SyntaxKind.GenericName:
                    return BindIdentifier((SimpleNameSyntax)node, invoked, indexed, diagnostics);
                case SyntaxKind.SimpleMemberAccessExpression:
                case SyntaxKind.PointerMemberAccessExpression:
                    return BindMemberAccess((MemberAccessExpressionSyntax)node, invoked, indexed, diagnostics: diagnostics);
                case SyntaxKind.SimpleAssignmentExpression:
                    return BindAssignment((AssignmentExpressionSyntax)node, diagnostics);
                case SyntaxKind.CastExpression:
                    return BindCast((CastExpressionSyntax)node, diagnostics);
                case SyntaxKind.ElementAccessExpression:
                    return BindElementAccess((ElementAccessExpressionSyntax)node, diagnostics);
                case SyntaxKind.AddExpression:
                case SyntaxKind.MultiplyExpression:
                case SyntaxKind.SubtractExpression:
                case SyntaxKind.DivideExpression:
                case SyntaxKind.ModuloExpression:
                case SyntaxKind.EqualsExpression:
                case SyntaxKind.NotEqualsExpression:
                case SyntaxKind.GreaterThanExpression:
                case SyntaxKind.LessThanExpression:
                case SyntaxKind.GreaterThanOrEqualExpression:
                case SyntaxKind.LessThanOrEqualExpression:
                case SyntaxKind.BitwiseAndExpression:
                case SyntaxKind.BitwiseOrExpression:
                case SyntaxKind.ExclusiveOrExpression:
                case SyntaxKind.LeftShiftExpression:
                case SyntaxKind.RightShiftExpression:
                    return BindSimpleBinaryOperator((BinaryExpressionSyntax)node, diagnostics);
                case SyntaxKind.LogicalAndExpression:
                case SyntaxKind.LogicalOrExpression:
                    return BindConditionalLogicalOperator((BinaryExpressionSyntax)node, diagnostics);
                case SyntaxKind.CoalesceExpression:
                    return BindNullCoalescingOperator((BinaryExpressionSyntax)node, diagnostics);
                case SyntaxKind.ConditionalAccessExpression:
                    return BindConditionalAccessExpression((ConditionalAccessExpressionSyntax)node, diagnostics);

                case SyntaxKind.MemberBindingExpression:
                    return BindMemberBindingExpression((MemberBindingExpressionSyntax)node, invoked, indexed, diagnostics);

                case SyntaxKind.ElementBindingExpression:
                    return BindElementBindingExpression((ElementBindingExpressionSyntax)node, diagnostics);

                case SyntaxKind.IsExpression:
                    return BindIsOperator((BinaryExpressionSyntax)node, diagnostics);

                case SyntaxKind.AsExpression:
                    return BindAsOperator((BinaryExpressionSyntax)node, diagnostics);

                case SyntaxKind.UnaryPlusExpression:
                case SyntaxKind.UnaryMinusExpression:
                case SyntaxKind.LogicalNotExpression:
                case SyntaxKind.BitwiseNotExpression:
                    return BindUnaryOperator((PrefixUnaryExpressionSyntax)node, diagnostics);

                case SyntaxKind.IndexExpression:
                    return BindFromEndIndexExpression((PrefixUnaryExpressionSyntax)node, diagnostics);

                case SyntaxKind.RangeExpression:
                    return BindRangeExpression((RangeExpressionSyntax)node, diagnostics);

                case SyntaxKind.AddressOfExpression:
                    return BindAddressOfExpression((PrefixUnaryExpressionSyntax)node, diagnostics);

                case SyntaxKind.PointerIndirectionExpression:
                    return BindPointerIndirectionExpression((PrefixUnaryExpressionSyntax)node, diagnostics);

                case SyntaxKind.PostIncrementExpression:
                case SyntaxKind.PostDecrementExpression:
                    return BindIncrementOperator(node, ((PostfixUnaryExpressionSyntax)node).Operand, ((PostfixUnaryExpressionSyntax)node).OperatorToken, diagnostics);

                case SyntaxKind.PreIncrementExpression:
                case SyntaxKind.PreDecrementExpression:
                    return BindIncrementOperator(node, ((PrefixUnaryExpressionSyntax)node).Operand, ((PrefixUnaryExpressionSyntax)node).OperatorToken, diagnostics);

                case SyntaxKind.ConditionalExpression:
                    return BindConditionalOperator((ConditionalExpressionSyntax)node, diagnostics);

                case SyntaxKind.SwitchExpression:
                    return BindSwitchExpression((SwitchExpressionSyntax)node, diagnostics);

                case SyntaxKind.NumericLiteralExpression:
                case SyntaxKind.StringLiteralExpression:
                case SyntaxKind.CharacterLiteralExpression:
                case SyntaxKind.TrueLiteralExpression:
                case SyntaxKind.FalseLiteralExpression:
                case SyntaxKind.NullLiteralExpression:
                    return BindLiteralConstant((LiteralExpressionSyntax)node, diagnostics);

                case SyntaxKind.DefaultLiteralExpression:
                    return new BoundDefaultLiteral(node);

                case SyntaxKind.ParenthesizedExpression:
                    // Parenthesis tokens are ignored, and operand is bound in the context of parent
                    // expression.
                    return BindParenthesizedExpression(((ParenthesizedExpressionSyntax)node).Expression, diagnostics);

                case SyntaxKind.UncheckedExpression:
                case SyntaxKind.CheckedExpression:
                    return BindCheckedExpression((CheckedExpressionSyntax)node, diagnostics);

                case SyntaxKind.DefaultExpression:
                    return BindDefaultExpression((DefaultExpressionSyntax)node, diagnostics);

                case SyntaxKind.TypeOfExpression:
                    return BindTypeOf((TypeOfExpressionSyntax)node, diagnostics);

                case SyntaxKind.SizeOfExpression:
                    return BindSizeOf((SizeOfExpressionSyntax)node, diagnostics);

                case SyntaxKind.AddAssignmentExpression:
                case SyntaxKind.AndAssignmentExpression:
                case SyntaxKind.DivideAssignmentExpression:
                case SyntaxKind.ExclusiveOrAssignmentExpression:
                case SyntaxKind.LeftShiftAssignmentExpression:
                case SyntaxKind.ModuloAssignmentExpression:
                case SyntaxKind.MultiplyAssignmentExpression:
                case SyntaxKind.OrAssignmentExpression:
                case SyntaxKind.RightShiftAssignmentExpression:
                case SyntaxKind.SubtractAssignmentExpression:
                    return BindCompoundAssignment((AssignmentExpressionSyntax)node, diagnostics);

                case SyntaxKind.CoalesceAssignmentExpression:
                    return BindNullCoalescingAssignmentOperator((AssignmentExpressionSyntax)node, diagnostics);

                case SyntaxKind.AliasQualifiedName:
                case SyntaxKind.PredefinedType:
                    return this.BindNamespaceOrType(node, diagnostics);

                case SyntaxKind.QueryExpression:
                    return this.BindQuery((QueryExpressionSyntax)node, diagnostics);

                case SyntaxKind.AnonymousObjectCreationExpression:
                    return BindAnonymousObjectCreation((AnonymousObjectCreationExpressionSyntax)node, diagnostics);

                case SyntaxKind.QualifiedName:
                    return BindQualifiedName((QualifiedNameSyntax)node, diagnostics);

                case SyntaxKind.ComplexElementInitializerExpression:
                    return BindUnexpectedComplexElementInitializer((InitializerExpressionSyntax)node, diagnostics);

                case SyntaxKind.ArgListExpression:
                    return BindArgList(node, diagnostics);

                case SyntaxKind.RefTypeExpression:
                    return BindRefType((RefTypeExpressionSyntax)node, diagnostics);

                case SyntaxKind.MakeRefExpression:
                    return BindMakeRef((MakeRefExpressionSyntax)node, diagnostics);

                case SyntaxKind.RefValueExpression:
                    return BindRefValue((RefValueExpressionSyntax)node, diagnostics);

                case SyntaxKind.AwaitExpression:
                    return BindAwait((AwaitExpressionSyntax)node, diagnostics);

                case SyntaxKind.OmittedArraySizeExpression:
                case SyntaxKind.OmittedTypeArgument:
                case SyntaxKind.ObjectInitializerExpression:
                    // Not reachable during method body binding, but
                    // may be used by SemanticModel for error cases.
                    return BadExpression(node);

                case SyntaxKind.NullableType:
                    // Not reachable during method body binding, but
                    // may be used by SemanticModel for error cases.
                    // NOTE: This happens when there's a problem with the Nullable<T> type (e.g. it's missing).
                    // There is no corresponding problem for array or pointer types (which seem analogous), since
                    // they are not constructed types; the element type can be an error type, but the array/pointer 
                    // type cannot.
                    return BadExpression(node);

                case SyntaxKind.InterpolatedStringExpression:
                    return BindInterpolatedString((InterpolatedStringExpressionSyntax)node, diagnostics);

                case SyntaxKind.IsPatternExpression:
                    return BindIsPatternExpression((IsPatternExpressionSyntax)node, diagnostics);

                case SyntaxKind.TupleExpression:
                    return BindTupleExpression((TupleExpressionSyntax)node, diagnostics);

                case SyntaxKind.ThrowExpression:
                    return BindThrowExpression((ThrowExpressionSyntax)node, diagnostics);

                case SyntaxKind.RefType:
                    return BindRefType(node, diagnostics);

                case SyntaxKind.RefExpression:
                    return BindRefExpression(node, diagnostics);

                case SyntaxKind.DeclarationExpression:
                    return BindDeclarationExpressionAsError((DeclarationExpressionSyntax)node, diagnostics);

                case SyntaxKind.SuppressNullableWarningExpression:
                    return BindSuppressNullableWarningExpression((PostfixUnaryExpressionSyntax)node, diagnostics);

                case SyntaxKind.WithExpression:
                    return BindWithExpression((WithExpressionSyntax)node, diagnostics);

                default:
                    // NOTE: We could probably throw an exception here, but it's conceivable
                    // that a non-parser syntax tree could reach this point with an unexpected
                    // SyntaxKind and we don't want to throw if that occurs.
                    Debug.Assert(false, "Unexpected SyntaxKind " + node.Kind());
                    diagnostics.Add(ErrorCode.ERR_InternalError, node.Location);
                    return BadExpression(node);
            }
        }

#nullable enable
        internal virtual BoundSwitchExpressionArm BindSwitchExpressionArm(SwitchExpressionArmSyntax node, TypeSymbol switchGoverningType, uint switchGoverningValEscape, BindingDiagnosticBag diagnostics)
        {
            return this.NextRequired.BindSwitchExpressionArm(node, switchGoverningType, switchGoverningValEscape, diagnostics);
        }
#nullable disable

        private BoundExpression BindRefExpression(ExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            var firstToken = node.GetFirstToken();
            diagnostics.Add(ErrorCode.ERR_UnexpectedToken, firstToken.GetLocation(), firstToken.ValueText);
            return new BoundBadExpression(
                node, LookupResultKind.Empty, ImmutableArray<Symbol>.Empty, ImmutableArray<BoundExpression>.Empty,
                CreateErrorType("ref"));
        }

        private BoundExpression BindRefType(ExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            var firstToken = node.GetFirstToken();
            diagnostics.Add(ErrorCode.ERR_UnexpectedToken, firstToken.GetLocation(), firstToken.ValueText);
            return new BoundTypeExpression(node, null, CreateErrorType("ref"));
        }

        private BoundExpression BindThrowExpression(ThrowExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            bool hasErrors = node.HasErrors;
            if (!IsThrowExpressionInProperContext(node))
            {
                diagnostics.Add(ErrorCode.ERR_ThrowMisplaced, node.ThrowKeyword.GetLocation());
                hasErrors = true;
            }

            var thrownExpression = BindThrownExpression(node.Expression, diagnostics, ref hasErrors);
            return new BoundThrowExpression(node, thrownExpression, null, hasErrors);
        }

        private static bool IsThrowExpressionInProperContext(ThrowExpressionSyntax node)
        {
            var parent = node.Parent;
            if (parent == null || node.HasErrors)
            {
                return true;
            }

            switch (parent.Kind())
            {
                case SyntaxKind.ConditionalExpression: // ?:
                    {
                        var conditionalParent = (ConditionalExpressionSyntax)parent;
                        return node == conditionalParent.WhenTrue || node == conditionalParent.WhenFalse;
                    }
                case SyntaxKind.CoalesceExpression: // ??
                    {
                        var binaryParent = (BinaryExpressionSyntax)parent;
                        return node == binaryParent.Right;
                    }
                case SyntaxKind.SwitchExpressionArm:
                case SyntaxKind.ArrowExpressionClause:
                case SyntaxKind.ParenthesizedLambdaExpression:
                case SyntaxKind.SimpleLambdaExpression:
                    return true;
                // We do not support && and || because
                // 1. The precedence would not syntactically allow it
                // 2. It isn't clear what the semantics should be
                // 3. It isn't clear what use cases would motivate us to change the precedence to support it
                default:
                    return false;
            }
        }

        // Bind a declaration expression where it isn't permitted.
        private BoundExpression BindDeclarationExpressionAsError(DeclarationExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            // This is an error, as declaration expressions are handled specially in every context in which
            // they are permitted. So we have a context in which they are *not* permitted. Nevertheless, we
            // bind it and then give one nice message.

            bool isVar;
            bool isConst = false;
            AliasSymbol alias;
            var declType = BindVariableTypeWithAnnotations(node.Designation, diagnostics, node.Type, ref isConst, out isVar, out alias);
            Error(diagnostics, ErrorCode.ERR_DeclarationExpressionNotPermitted, node);
            return BindDeclarationVariablesForErrorRecovery(declType, node.Designation, node, diagnostics);
        }

        /// <summary>
        /// Bind a declaration variable where it isn't permitted. The caller is expected to produce a diagnostic.
        /// </summary>
        private BoundExpression BindDeclarationVariablesForErrorRecovery(TypeWithAnnotations declTypeWithAnnotations, VariableDesignationSyntax node, CSharpSyntaxNode syntax, BindingDiagnosticBag diagnostics)
        {
            declTypeWithAnnotations = declTypeWithAnnotations.HasType ? declTypeWithAnnotations : TypeWithAnnotations.Create(CreateErrorType("var"));
            switch (node.Kind())
            {
                case SyntaxKind.SingleVariableDesignation:
                    {
                        var single = (SingleVariableDesignationSyntax)node;
                        var result = BindDeconstructionVariable(declTypeWithAnnotations, single, syntax, diagnostics);
                        return BindToTypeForErrorRecovery(result);
                    }
                case SyntaxKind.DiscardDesignation:
                    {
                        return BindDiscardExpression(syntax, declTypeWithAnnotations);
                    }
                case SyntaxKind.ParenthesizedVariableDesignation:
                    {
                        var tuple = (ParenthesizedVariableDesignationSyntax)node;
                        int count = tuple.Variables.Count;
                        var builder = ArrayBuilder<BoundExpression>.GetInstance(count);
                        var namesBuilder = ArrayBuilder<string>.GetInstance(count);

                        foreach (var n in tuple.Variables)
                        {
                            builder.Add(BindDeclarationVariablesForErrorRecovery(declTypeWithAnnotations, n, n, diagnostics));
                            namesBuilder.Add(InferTupleElementName(n));
                        }
                        ImmutableArray<BoundExpression> subExpressions = builder.ToImmutableAndFree();

                        var uniqueFieldNames = PooledHashSet<string>.GetInstance();
                        RemoveDuplicateInferredTupleNamesAndFreeIfEmptied(ref namesBuilder, uniqueFieldNames);
                        uniqueFieldNames.Free();

                        ImmutableArray<string> tupleNames = namesBuilder is null ? default : namesBuilder.ToImmutableAndFree();
                        ImmutableArray<bool> inferredPositions = tupleNames.IsDefault ? default : tupleNames.SelectAsArray(n => n != null);
                        bool disallowInferredNames = this.Compilation.LanguageVersion.DisallowInferredTupleElementNames();

                        // We will not check constraints at this point as this code path
                        // is failure-only and the caller is expected to produce a diagnostic.
                        var tupleType = NamedTypeSymbol.CreateTuple(
                            locationOpt: null,
                            subExpressions.SelectAsArray(e => TypeWithAnnotations.Create(e.Type)),
                            elementLocations: default,
                            tupleNames,
                            Compilation,
                            shouldCheckConstraints: false,
                            includeNullability: false,
                            errorPositions: disallowInferredNames ? inferredPositions : default);

                        return new BoundConvertedTupleLiteral(syntax, sourceTuple: null, wasTargetTyped: true, subExpressions, tupleNames, inferredPositions, tupleType);
                    }
                default:
                    throw ExceptionUtilities.UnexpectedValue(node.Kind());
            }
        }

        private BoundExpression BindTupleExpression(TupleExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            SeparatedSyntaxList<ArgumentSyntax> arguments = node.Arguments;
            int numElements = arguments.Count;

            if (numElements < 2)
            {
                // this should be a parse error already.
                var args = numElements == 1 ?
                    ImmutableArray.Create(BindValue(arguments[0].Expression, diagnostics, BindValueKind.RValue)) :
                    ImmutableArray<BoundExpression>.Empty;

                return BadExpression(node, args);
            }

            bool hasNaturalType = true;

            var boundArguments = ArrayBuilder<BoundExpression>.GetInstance(arguments.Count);
            var elementTypesWithAnnotations = ArrayBuilder<TypeWithAnnotations>.GetInstance(arguments.Count);
            var elementLocations = ArrayBuilder<Location>.GetInstance(arguments.Count);

            // prepare names
            var (elementNames, inferredPositions, hasErrors) = ExtractTupleElementNames(arguments, diagnostics);

            // prepare types and locations
            for (int i = 0; i < numElements; i++)
            {
                ArgumentSyntax argumentSyntax = arguments[i];
                IdentifierNameSyntax nameSyntax = argumentSyntax.NameColon?.Name;

                if (nameSyntax != null)
                {
                    elementLocations.Add(nameSyntax.Location);
                }
                else
                {
                    elementLocations.Add(argumentSyntax.Location);
                }

                BoundExpression boundArgument = BindValue(argumentSyntax.Expression, diagnostics, BindValueKind.RValue);
                if (boundArgument.Type?.SpecialType == SpecialType.System_Void)
                {
                    diagnostics.Add(ErrorCode.ERR_VoidInTuple, argumentSyntax.Location);
                    boundArgument = new BoundBadExpression(
                        argumentSyntax, LookupResultKind.Empty, ImmutableArray<Symbol>.Empty,
                        ImmutableArray.Create<BoundExpression>(boundArgument), CreateErrorType("void"));
                }

                boundArguments.Add(boundArgument);

                var elementTypeWithAnnotations = TypeWithAnnotations.Create(boundArgument.Type);
                elementTypesWithAnnotations.Add(elementTypeWithAnnotations);

                if (!elementTypeWithAnnotations.HasType)
                {
                    hasNaturalType = false;
                }
            }

            NamedTypeSymbol tupleTypeOpt = null;
            var elements = elementTypesWithAnnotations.ToImmutableAndFree();
            var locations = elementLocations.ToImmutableAndFree();

            if (hasNaturalType)
            {
                bool disallowInferredNames = this.Compilation.LanguageVersion.DisallowInferredTupleElementNames();

                tupleTypeOpt = NamedTypeSymbol.CreateTuple(node.Location, elements, locations, elementNames,
                    this.Compilation, syntax: node, diagnostics: diagnostics, shouldCheckConstraints: true,
                    includeNullability: false, errorPositions: disallowInferredNames ? inferredPositions : default(ImmutableArray<bool>));
            }
            else
            {
                NamedTypeSymbol.VerifyTupleTypePresent(elements.Length, node, this.Compilation, diagnostics);
            }

            // Always track the inferred positions in the bound node, so that conversions don't produce a warning
            // for "dropped names" on tuple literal when the name was inferred.
            return new BoundTupleLiteral(node, boundArguments.ToImmutableAndFree(), elementNames, inferredPositions, tupleTypeOpt, hasErrors);
        }

        private static (ImmutableArray<string> elementNamesArray, ImmutableArray<bool> inferredArray, bool hasErrors) ExtractTupleElementNames(
            SeparatedSyntaxList<ArgumentSyntax> arguments, BindingDiagnosticBag diagnostics)
        {
            bool hasErrors = false;
            int numElements = arguments.Count;
            var uniqueFieldNames = PooledHashSet<string>.GetInstance();
            ArrayBuilder<string> elementNames = null;
            ArrayBuilder<string> inferredElementNames = null;

            for (int i = 0; i < numElements; i++)
            {
                ArgumentSyntax argumentSyntax = arguments[i];
                IdentifierNameSyntax nameSyntax = argumentSyntax.NameColon?.Name;

                string name = null;
                string inferredName = null;

                if (nameSyntax != null)
                {
                    name = nameSyntax.Identifier.ValueText;

                    if (diagnostics != null && !CheckTupleMemberName(name, i, argumentSyntax.NameColon.Name, diagnostics, uniqueFieldNames))
                    {
                        hasErrors = true;
                    }
                }
                else
                {
                    inferredName = InferTupleElementName(argumentSyntax.Expression);
                }

                CollectTupleFieldMemberName(name, i, numElements, ref elementNames);
                CollectTupleFieldMemberName(inferredName, i, numElements, ref inferredElementNames);
            }

            RemoveDuplicateInferredTupleNamesAndFreeIfEmptied(ref inferredElementNames, uniqueFieldNames);
            uniqueFieldNames.Free();

            var result = MergeTupleElementNames(elementNames, inferredElementNames);
            elementNames?.Free();
            inferredElementNames?.Free();
            return (result.names, result.inferred, hasErrors);
        }

        private static (ImmutableArray<string> names, ImmutableArray<bool> inferred) MergeTupleElementNames(
            ArrayBuilder<string> elementNames, ArrayBuilder<string> inferredElementNames)
        {
            if (elementNames == null)
            {
                if (inferredElementNames == null)
                {
                    return (default(ImmutableArray<string>), default(ImmutableArray<bool>));
                }
                else
                {
                    var finalNames = inferredElementNames.ToImmutable();
                    return (finalNames, finalNames.SelectAsArray(n => n != null));
                }
            }

            if (inferredElementNames == null)
            {
                return (elementNames.ToImmutable(), default(ImmutableArray<bool>));
            }

            Debug.Assert(elementNames.Count == inferredElementNames.Count);
            var builder = ArrayBuilder<bool>.GetInstance(elementNames.Count);
            for (int i = 0; i < elementNames.Count; i++)
            {
                string inferredName = inferredElementNames[i];
                if (elementNames[i] == null && inferredName != null)
                {
                    elementNames[i] = inferredName;
                    builder.Add(true);
                }
                else
                {
                    builder.Add(false);
                }
            }

            return (elementNames.ToImmutable(), builder.ToImmutableAndFree());
        }

        /// <summary>
        /// Removes duplicate entries in <paramref name="inferredElementNames"/> and frees it if only nulls remain.
        /// </summary>
        private static void RemoveDuplicateInferredTupleNamesAndFreeIfEmptied(ref ArrayBuilder<string> inferredElementNames, HashSet<string> uniqueFieldNames)
        {
            if (inferredElementNames == null)
            {
                return;
            }

            // Inferred names that duplicate an explicit name or a previous inferred name are tagged for removal
            var toRemove = PooledHashSet<string>.GetInstance();
            foreach (var name in inferredElementNames)
            {
                if (name != null && !uniqueFieldNames.Add(name))
                {
                    toRemove.Add(name);
                }
            }

            for (int i = 0; i < inferredElementNames.Count; i++)
            {
                var inferredName = inferredElementNames[i];
                if (inferredName != null && toRemove.Contains(inferredName))
                {
                    inferredElementNames[i] = null;
                }
            }
            toRemove.Free();

            if (inferredElementNames.All(n => n is null))
            {
                inferredElementNames.Free();
                inferredElementNames = null;
            }
        }

        private static string InferTupleElementName(SyntaxNode syntax)
        {
            string name = syntax.TryGetInferredMemberName();

            // Reserved names are never candidates to be inferred names, at any position
            if (name == null || NamedTypeSymbol.IsTupleElementNameReserved(name) != -1)
            {
                return null;
            }

            return name;
        }

        private BoundExpression BindRefValue(RefValueExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            // __refvalue(tr, T) requires that tr be a TypedReference and T be a type.
            // The result is a *variable* of type T.

            BoundExpression argument = BindValue(node.Expression, diagnostics, BindValueKind.RValue);
            bool hasErrors = argument.HasAnyErrors;

            TypeSymbol typedReferenceType = this.Compilation.GetSpecialType(SpecialType.System_TypedReference);
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            Conversion conversion = this.Conversions.ClassifyConversionFromExpression(argument, typedReferenceType, ref useSiteInfo);
            diagnostics.Add(node, useSiteInfo);
            if (!conversion.IsImplicit || !conversion.IsValid)
            {
                hasErrors = true;
                GenerateImplicitConversionError(diagnostics, node, conversion, argument, typedReferenceType);
            }

            argument = CreateConversion(argument, conversion, typedReferenceType, diagnostics);

            TypeWithAnnotations typeWithAnnotations = BindType(node.Type, diagnostics);

            return new BoundRefValueOperator(node, typeWithAnnotations.NullableAnnotation, argument, typeWithAnnotations.Type, hasErrors);
        }

        private BoundExpression BindMakeRef(MakeRefExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            // __makeref(x) requires that x be a variable, and not be of a restricted type.
            BoundExpression argument = this.BindValue(node.Expression, diagnostics, BindValueKind.RefOrOut);

            bool hasErrors = argument.HasAnyErrors;

            TypeSymbol typedReferenceType = GetSpecialType(SpecialType.System_TypedReference, diagnostics, node);

            if ((object)argument.Type != null && argument.Type.IsRestrictedType())
            {
                // CS1601: Cannot make reference to variable of type '{0}'
                Error(diagnostics, ErrorCode.ERR_MethodArgCantBeRefAny, node, argument.Type);
                hasErrors = true;
            }

            // UNDONE: We do not yet implement warnings anywhere for:
            // UNDONE: * taking a ref to a volatile field
            // UNDONE: * taking a ref to a "non-agile" field
            // UNDONE: We should do so here when we implement this feature for regular out/ref parameters.

            return new BoundMakeRefOperator(node, argument, typedReferenceType, hasErrors);
        }

        private BoundExpression BindRefType(RefTypeExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            // __reftype(x) requires that x be implicitly convertible to TypedReference.

            BoundExpression argument = BindValue(node.Expression, diagnostics, BindValueKind.RValue);
            bool hasErrors = argument.HasAnyErrors;

            TypeSymbol typedReferenceType = this.Compilation.GetSpecialType(SpecialType.System_TypedReference);
            TypeSymbol typeType = this.GetWellKnownType(WellKnownType.System_Type, diagnostics, node);
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            Conversion conversion = this.Conversions.ClassifyConversionFromExpression(argument, typedReferenceType, ref useSiteInfo);
            diagnostics.Add(node, useSiteInfo);
            if (!conversion.IsImplicit || !conversion.IsValid)
            {
                hasErrors = true;
                GenerateImplicitConversionError(diagnostics, node, conversion, argument, typedReferenceType);
            }

            argument = CreateConversion(argument, conversion, typedReferenceType, diagnostics);
            return new BoundRefTypeOperator(node, argument, null, typeType, hasErrors);
        }

        private BoundExpression BindArgList(CSharpSyntaxNode node, BindingDiagnosticBag diagnostics)
        {
            // There are two forms of __arglist expression. In a method with an __arglist parameter,
            // it is legal to use __arglist as an expression of type RuntimeArgumentHandle. In 
            // a call to such a method, it is legal to use __arglist(x, y, z) as the final argument.
            // This method only handles the first usage; the second usage is parsed as a call syntax.

            // The native compiler allows __arglist in a lambda:
            //
            // class C
            // {
            //   delegate int D(RuntimeArgumentHandle r);
            //   static void M(__arglist)
            //   {
            //     D f = null;
            //     f = r=>f(__arglist);
            //   }
            // }
            //
            // This is clearly wrong. Either the developer intends __arglist to refer to the 
            // arg list of the *lambda*, or to the arg list of *M*. The former makes no sense;
            // lambdas cannot have an arg list. The latter we have no way to generate code for;
            // you cannot hoist the arg list to a field of a closure class.
            //
            // The native compiler allows this and generates code as though the developer
            // was attempting to access the arg list of the lambda! We should simply disallow it.

            TypeSymbol runtimeArgumentHandleType = GetSpecialType(SpecialType.System_RuntimeArgumentHandle, diagnostics, node);

            MethodSymbol method = this.ContainingMember() as MethodSymbol;

            bool hasError = false;

            if ((object)method == null || !method.IsVararg)
            {
                // CS0190: The __arglist construct is valid only within a variable argument method
                Error(diagnostics, ErrorCode.ERR_ArgsInvalid, node);
                hasError = true;
            }
            else
            {
                // We're in a varargs method; are we also inside a lambda?
                Symbol container = this.ContainingMemberOrLambda;
                if (container != method)
                {
                    // We also need to report this any time a local variable of a restricted type
                    // would be hoisted into a closure for an anonymous function, iterator or async method.
                    // We do that during the actual rewrites.

                    // CS4013: Instance of type '{0}' cannot be used inside an anonymous function, query expression, iterator block or async method
                    Error(diagnostics, ErrorCode.ERR_SpecialByRefInLambda, node, runtimeArgumentHandleType);

                    hasError = true;
                }
            }

            return new BoundArgList(node, runtimeArgumentHandleType, hasError);
        }

        /// <summary>
        /// This can be reached for the qualified name on the right-hand-side of an `is` operator.
        /// For compatibility we parse it as a qualified name, as the is-type expression only permitted
        /// a type on the right-hand-side in C# 6. But the same syntax now, in C# 7 and later, can
        /// refer to a constant, which would normally be represented as a *simple member access expression*.
        /// Since the parser cannot distinguish, it parses it as before and depends on the binder
        /// to handle a qualified name appearing as an expression.
        /// </summary>
        private BoundExpression BindQualifiedName(QualifiedNameSyntax node, BindingDiagnosticBag diagnostics)
        {
            return BindMemberAccessWithBoundLeft(node, this.BindLeftOfPotentialColorColorMemberAccess(node.Left, diagnostics), node.Right, node.DotToken, invoked: false, indexed: false, diagnostics: diagnostics);
        }

        private BoundExpression BindParenthesizedExpression(ExpressionSyntax innerExpression, BindingDiagnosticBag diagnostics)
        {
            var result = BindExpression(innerExpression, diagnostics);

            // A parenthesized expression may not be a namespace or a type. If it is a parenthesized
            // namespace or type then report the error but let it go; we'll just ignore the
            // parenthesis and keep on trucking.
            CheckNotNamespaceOrType(result, diagnostics);
            return result;
        }

#nullable enable
        private BoundExpression BindTypeOf(TypeOfExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            ExpressionSyntax typeSyntax = node.Type;

            TypeofBinder typeofBinder = new TypeofBinder(typeSyntax, this); //has special handling for unbound types
            AliasSymbol alias;
            TypeWithAnnotations typeWithAnnotations = typeofBinder.BindType(typeSyntax, diagnostics, out alias);
            TypeSymbol type = typeWithAnnotations.Type;

            bool hasError = false;

            // NB: Dev10 has an error for typeof(dynamic), but allows typeof(dynamic[]),
            // typeof(C<dynamic>), etc.
            if (type.IsDynamic())
            {
                diagnostics.Add(ErrorCode.ERR_BadDynamicTypeof, node.Location);
                hasError = true;
            }
            else if (typeWithAnnotations.NullableAnnotation.IsAnnotated() && type.IsReferenceType)
            {
                // error: cannot take the `typeof` a nullable reference type.
                diagnostics.Add(ErrorCode.ERR_BadNullableTypeof, node.Location);
                hasError = true;
            }
            else if (this.InAttributeArgument && type.ContainsFunctionPointer())
            {
                // https://github.com/dotnet/roslyn/issues/48765 tracks removing this error and properly supporting function
                // pointers in attribute types. Until then, we don't know how serialize them, so error instead of crashing
                // during emit.
                diagnostics.Add(ErrorCode.ERR_FunctionPointerTypesInAttributeNotSupported, node.Location);
                hasError = true;
            }

            BoundTypeExpression boundType = new BoundTypeExpression(typeSyntax, alias, typeWithAnnotations, type.IsErrorType());
            return new BoundTypeOfOperator(node, boundType, null, this.GetWellKnownType(WellKnownType.System_Type, diagnostics, node), hasError);
        }

        /// <summary>Called when an "attribute-dependent" type such as 'dynamic', 'string?', etc. is not permitted.</summary>
        private void CheckDisallowedAttributeDependentType(TypeWithAnnotations typeArgument, Location errorLocation, BindingDiagnosticBag diagnostics)
        {
            typeArgument.VisitType(type: null, static (typeWithAnnotations, arg, _) =>
            {
                var (topLevelType, errorLocation, diagnostics) = arg;
                var type = typeWithAnnotations.Type;
                if (type.IsDynamic()
                    || (typeWithAnnotations.NullableAnnotation.IsAnnotated() && !type.IsValueType)
                    || type.IsNativeIntegerType
                    || (type.IsTupleType && !type.TupleElementNames.IsDefault))
                {
                    diagnostics.Add(ErrorCode.ERR_AttrDependentTypeNotAllowed, errorLocation, topLevelType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat));
                    return true;
                }

                return false;
            }, typePredicate: null, arg: (typeArgument, errorLocation, diagnostics));
        }

        private BoundExpression BindSizeOf(SizeOfExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            ExpressionSyntax typeSyntax = node.Type;
            AliasSymbol alias;
            TypeWithAnnotations typeWithAnnotations = this.BindType(typeSyntax, diagnostics, out alias);
            TypeSymbol type = typeWithAnnotations.Type;

            bool typeHasErrors = type.IsErrorType() || CheckManagedAddr(Compilation, type, node.Location, diagnostics);

            BoundTypeExpression boundType = new BoundTypeExpression(typeSyntax, alias, typeWithAnnotations, typeHasErrors);
            ConstantValue constantValue = GetConstantSizeOf(type);
            bool hasErrors = constantValue is null && ReportUnsafeIfNotAllowed(node, diagnostics, type);
            return new BoundSizeOfOperator(node, boundType, constantValue,
                this.GetSpecialType(SpecialType.System_Int32, diagnostics, node), hasErrors);
        }

        /// <returns>true if managed type-related errors were found, otherwise false.</returns>
        internal static bool CheckManagedAddr(CSharpCompilation compilation, TypeSymbol type, Location location, BindingDiagnosticBag diagnostics)
        {
            var useSiteInfo = new CompoundUseSiteInfo<AssemblySymbol>(diagnostics, compilation.Assembly);
            var managedKind = type.GetManagedKind(ref useSiteInfo);
            diagnostics.Add(location, useSiteInfo);

            return CheckManagedAddr(compilation, type, managedKind, location, diagnostics);
        }

        /// <returns>true if managed type-related errors were found, otherwise false.</returns>
        internal static bool CheckManagedAddr(CSharpCompilation compilation, TypeSymbol type, ManagedKind managedKind, Location location, BindingDiagnosticBag diagnostics)
        {
            switch (managedKind)
            {
                case ManagedKind.Managed:
                    diagnostics.Add(ErrorCode.ERR_ManagedAddr, location, type);
                    return true;
                case ManagedKind.UnmanagedWithGenerics when MessageID.IDS_FeatureUnmanagedConstructedTypes.GetFeatureAvailabilityDiagnosticInfo(compilation) is CSDiagnosticInfo diagnosticInfo:
                    diagnostics.Add(diagnosticInfo, location);
                    return true;
                case ManagedKind.Unknown:
                    throw ExceptionUtilities.UnexpectedValue(managedKind);
                default:
                    return false;
            }
        }
#nullable disable

        internal static ConstantValue GetConstantSizeOf(TypeSymbol type)
        {
            return ConstantValue.CreateSizeOf((type.GetEnumUnderlyingType() ?? type).SpecialType);
        }

        private BoundExpression BindDefaultExpression(DefaultExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            TypeWithAnnotations typeWithAnnotations = this.BindType(node.Type, diagnostics, out AliasSymbol alias);
            var typeExpression = new BoundTypeExpression(node.Type, aliasOpt: alias, typeWithAnnotations);
            TypeSymbol type = typeWithAnnotations.Type;
            return new BoundDefaultExpression(node, typeExpression, constantValueOpt: type.GetDefaultValue(), type);
        }

        /// <summary>
        /// Binds a simple identifier.
        /// </summary>
        private BoundExpression BindIdentifier(
            SimpleNameSyntax node,
            bool invoked,
            bool indexed,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node != null);

            // If the syntax tree is ill-formed and the identifier is missing then we've already
            // given a parse error. Just return an error local and continue with analysis.
            if (node.IsMissing)
            {
                return BadExpression(node);
            }

            // A simple-name is either of the form I or of the form I<A1, ..., AK>, where I is a
            // single identifier and <A1, ..., AK> is an optional type-argument-list. When no
            // type-argument-list is specified, consider K to be zero. The simple-name is evaluated
            // and classified as follows:

            // If K is zero and the simple-name appears within a block and if the block's (or an
            // enclosing block's) local variable declaration space contains a local variable,
            // parameter or constant with name I, then the simple-name refers to that local
            // variable, parameter or constant and is classified as a variable or value.

            // If K is zero and the simple-name appears within the body of a generic method
            // declaration and if that declaration includes a type parameter with name I, then the
            // simple-name refers to that type parameter.

            BoundExpression expression;

            // It's possible that the argument list is malformed; if so, do not attempt to bind it;
            // just use the null array.

            int arity = node.Arity;
            bool hasTypeArguments = arity > 0;

            SeparatedSyntaxList<TypeSyntax> typeArgumentList = node.Kind() == SyntaxKind.GenericName
                ? ((GenericNameSyntax)node).TypeArgumentList.Arguments
                : default(SeparatedSyntaxList<TypeSyntax>);

            Debug.Assert(arity == typeArgumentList.Count);

            var typeArgumentsWithAnnotations = hasTypeArguments ?
                BindTypeArguments(typeArgumentList, diagnostics) :
                default(ImmutableArray<TypeWithAnnotations>);

            var lookupResult = LookupResult.GetInstance();
            LookupOptions options = LookupOptions.AllMethodsOnArityZero;
            if (invoked)
            {
                options |= LookupOptions.MustBeInvocableIfMember;
            }

            if (!IsInMethodBody && !IsInsideNameof)
            {
                Debug.Assert((options & LookupOptions.NamespacesOrTypesOnly) == 0);
                options |= LookupOptions.MustNotBeMethodTypeParameter;
            }

            var name = node.Identifier.ValueText;
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            this.LookupSymbolsWithFallback(lookupResult, name, arity: arity, useSiteInfo: ref useSiteInfo, options: options);
            diagnostics.Add(node, useSiteInfo);

            if (lookupResult.Kind != LookupResultKind.Empty)
            {
                // have we detected an error with the current node?
                bool isError;
                var members = ArrayBuilder<Symbol>.GetInstance();
                Symbol symbol = GetSymbolOrMethodOrPropertyGroup(lookupResult, node, name, node.Arity, members, diagnostics, out isError, qualifierOpt: null);  // reports diagnostics in result.

                if ((object)symbol == null)
                {
                    Debug.Assert(members.Count > 0);

                    var receiver = SynthesizeMethodGroupReceiver(node, members);
                    expression = ConstructBoundMemberGroupAndReportOmittedTypeArguments(
                        node,
                        typeArgumentList,
                        typeArgumentsWithAnnotations,
                        receiver,
                        name,
                        members,
                        lookupResult,
                        receiver != null ? BoundMethodGroupFlags.HasImplicitReceiver : BoundMethodGroupFlags.None,
                        isError,
                        diagnostics);

                    ReportSimpleProgramLocalReferencedOutsideOfTopLevelStatement(node, members[0], diagnostics);
                }
                else
                {
                    bool isNamedType = (symbol.Kind == SymbolKind.NamedType) || (symbol.Kind == SymbolKind.ErrorType);

                    if (hasTypeArguments && isNamedType)
                    {
                        symbol = ConstructNamedTypeUnlessTypeArgumentOmitted(node, (NamedTypeSymbol)symbol, typeArgumentList, typeArgumentsWithAnnotations, diagnostics);
                    }

                    expression = BindNonMethod(node, symbol, diagnostics, lookupResult.Kind, indexed, isError);

                    if (!isNamedType && (hasTypeArguments || node.Kind() == SyntaxKind.GenericName))
                    {
                        Debug.Assert(isError); // Should have been reported by GetSymbolOrMethodOrPropertyGroup.
                        expression = new BoundBadExpression(
                            syntax: node,
                            resultKind: LookupResultKind.WrongArity,
                            symbols: ImmutableArray.Create(symbol),
                            childBoundNodes: ImmutableArray.Create(BindToTypeForErrorRecovery(expression)),
                            type: expression.Type,
                            hasErrors: isError);
                    }
                }

                members.Free();
            }
            else
            {
                expression = null;
                if (node is IdentifierNameSyntax identifier)
                {
                    var type = BindNativeIntegerSymbolIfAny(identifier, diagnostics);
                    if (type is { })
                    {
                        expression = new BoundTypeExpression(node, null, type);
                    }
                    else if (FallBackOnDiscard(identifier, diagnostics))
                    {
                        expression = new BoundDiscardExpression(node, type: null);
                    }
                }

                // Otherwise, the simple-name is undefined and a compile-time error occurs.
                if (expression is null)
                {
                    expression = BadExpression(node);
                    if (lookupResult.Error != null)
                    {
                        Error(diagnostics, lookupResult.Error, node);
                    }
                    else if (IsJoinRangeVariableInLeftKey(node))
                    {
                        Error(diagnostics, ErrorCode.ERR_QueryOuterKey, node, name);
                    }
                    else if (IsInJoinRightKey(node))
                    {
                        Error(diagnostics, ErrorCode.ERR_QueryInnerKey, node, name);
                    }
                    else
                    {
                        Error(diagnostics, ErrorCode.ERR_NameNotInContext, node, name);
                    }
                }
            }

            lookupResult.Free();
            return expression;
        }

        /// <summary>
        /// Is this is an _ identifier in a context where discards are allowed?
        /// </summary>
        private static bool FallBackOnDiscard(IdentifierNameSyntax node, BindingDiagnosticBag diagnostics)
        {
            if (!node.Identifier.IsUnderscoreToken())
            {
                return false;
            }

            CSharpSyntaxNode containingDeconstruction = node.GetContainingDeconstruction();
            bool isDiscard = containingDeconstruction != null || IsOutVarDiscardIdentifier(node);
            if (isDiscard)
            {
                CheckFeatureAvailability(node, MessageID.IDS_FeatureDiscards, diagnostics);
            }

            return isDiscard;
        }

        private static bool IsOutVarDiscardIdentifier(SimpleNameSyntax node)
        {
            Debug.Assert(node.Identifier.IsUnderscoreToken());

            CSharpSyntaxNode parent = node.Parent;
            return (parent?.Kind() == SyntaxKind.Argument &&
                ((ArgumentSyntax)parent).RefOrOutKeyword.Kind() == SyntaxKind.OutKeyword);
        }

        private BoundExpression SynthesizeMethodGroupReceiver(CSharpSyntaxNode syntax, ArrayBuilder<Symbol> members)
        {
            // SPEC: For each instance type T starting with the instance type of the immediately
            // SPEC: enclosing type declaration, and continuing with the instance type of each
            // SPEC: enclosing class or struct declaration, [do a lot of things to find a match].
            // SPEC: ...
            // SPEC: If T is the instance type of the immediately enclosing class or struct type 
            // SPEC: and the lookup identifies one or more methods, the result is a method group 
            // SPEC: with an associated instance expression of this. 

            // Explanation of spec:
            //
            // We are looping over a set of types, from inner to outer, attempting to resolve the
            // meaning of a simple name; for example "M(123)".
            //
            // There are a number of possibilities:
            // 
            // If the lookup finds M in an outer class:
            //
            // class Outer { 
            //     static void M(int x) {}
            //     class Inner {
            //         void X() { M(123); }
            //     }
            // }
            //
            // or the base class of an outer class:
            //
            // class Base { 
            //     public static void M(int x) {}
            // }
            // class Outer : Base {
            //     class Inner {
            //         void X() { M(123); }
            //     }
            // }
            //
            // Then there is no "associated instance expression" of the method group.  That is, there
            // is no possibility of there being an "implicit this".
            //
            // If the lookup finds M on the class that triggered the lookup on the other hand, or
            // one of its base classes:
            //
            // class Base { 
            //     public static void M(int x) {}
            // }
            // class Derived : Base {
            //   void X() { M(123); }
            // }
            //
            // Then the associated instance expression is "this" *even if one or more methods in the
            // method group are static*. If it turns out that the method was static, then we'll
            // check later to determine if there was a receiver actually present in the source code
            // or not.  (That happens during the "final validation" phase of overload resolution.

            // Implementation explanation:
            //
            // If we're here, then lookup has identified one or more methods.  
            Debug.Assert(members.Count > 0);

            // The lookup implementation loops over the set of types from inner to outer, and stops 
            // when it makes a match. (This is correct because any matches found on more-outer types
            // would be hidden, and discarded.) This means that we only find members associated with 
            // one containing class or struct. The method is possibly on that type directly, or via 
            // inheritance from a base type of the type.
            //
            // The question then is what the "associated instance expression" is; is it "this" or
            // nothing at all? If the type that we found the method on is the current type, or is a
            // base type of the current type, then there should be a "this" associated with the
            // method group. Otherwise, it should be null.

            var currentType = this.ContainingType;
            if ((object)currentType == null)
            {
                // This may happen if there is no containing type, 
                // e.g. we are binding an expression in an assembly-level attribute
                return null;
            }

            var declaringType = members[0].ContainingType;

            var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
            if (currentType.IsEqualToOrDerivedFrom(declaringType, TypeCompareKind.ConsiderEverything, useSiteInfo: ref discardedUseSiteInfo) ||
                (currentType.IsInterface && (declaringType.IsObjectType() || currentType.AllInterfacesNoUseSiteDiagnostics.Contains(declaringType))))
            {
                return ThisReference(syntax, currentType, wasCompilerGenerated: true);
            }
            else
            {
                return TryBindInteractiveReceiver(syntax, declaringType);
            }
        }

        private bool IsBadLocalOrParameterCapture(Symbol symbol, TypeSymbol type, RefKind refKind)
        {
            if (refKind != RefKind.None || type.IsRefLikeType)
            {
                var containingMethod = this.ContainingMemberOrLambda as MethodSymbol;
                if ((object)containingMethod != null && (object)symbol.ContainingSymbol != (object)containingMethod)
                {
                    // Not expecting symbol from constructed method.
                    Debug.Assert(!symbol.ContainingSymbol.Equals(containingMethod));

                    // Captured in a lambda.
                    return (containingMethod.MethodKind == MethodKind.AnonymousFunction || containingMethod.MethodKind == MethodKind.LocalFunction) && !IsInsideNameof; // false in EE evaluation method
                }
            }
            return false;
        }

        private BoundExpression BindNonMethod(SimpleNameSyntax node, Symbol symbol, BindingDiagnosticBag diagnostics, LookupResultKind resultKind, bool indexed, bool isError)
        {
            // Events are handled later as we don't know yet if we are binding to the event or it's backing field.
            if (symbol.Kind != SymbolKind.Event)
            {
                ReportDiagnosticsIfObsolete(diagnostics, symbol, node, hasBaseReceiver: false);
            }

            switch (symbol.Kind)
            {
                case SymbolKind.Local:
                    {
                        var localSymbol = (LocalSymbol)symbol;
                        TypeSymbol type;
                        bool isNullableUnknown;

                        if (ReportSimpleProgramLocalReferencedOutsideOfTopLevelStatement(node, localSymbol, diagnostics))
                        {
                            type = new ExtendedErrorTypeSymbol(
                                this.Compilation, name: "var", arity: 0, errorInfo: null, variableUsedBeforeDeclaration: true);
                            isNullableUnknown = true;
                        }
                        else if (isUsedBeforeDeclaration(node, localSymbol))
                        {
                            // Here we report a local variable being used before its declaration
                            //
                            // There are two possible diagnostics for this:
                            //
                            // CS0841: ERR_VariableUsedBeforeDeclaration
                            // Cannot use local variable 'x' before it is declared
                            //
                            // CS0844: ERR_VariableUsedBeforeDeclarationAndHidesField
                            // Cannot use local variable 'x' before it is declared. The 
                            // declaration of the local variable hides the field 'C.x'.
                            //
                            // There are two situations in which we give these errors.
                            //
                            // First, the scope of a local variable -- that is, the region of program 
                            // text in which it can be looked up by name -- is throughout the entire
                            // block which declares it. It is therefore possible to use a local
                            // before it is declared, which is an error.
                            //
                            // As an additional help to the user, we give a special error for this
                            // scenario:
                            //
                            // class C { 
                            //  int x; 
                            //  void M() { 
                            //    Print(x); 
                            //    int x = 5;
                            //  } }
                            //
                            // Because a too-clever C++ user might be attempting to deliberately
                            // bind to "this.x" in the "Print". (In C++ the local does not come
                            // into scope until its declaration.)
                            //
                            FieldSymbol possibleField = null;
                            var lookupResult = LookupResult.GetInstance();
                            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                            this.LookupMembersInType(
                                lookupResult,
                                ContainingType,
                                localSymbol.Name,
                                arity: 0,
                                basesBeingResolved: null,
                                options: LookupOptions.Default,
                                originalBinder: this,
                                diagnose: false,
                                useSiteInfo: ref useSiteInfo);
                            diagnostics.Add(node, useSiteInfo);
                            possibleField = lookupResult.SingleSymbolOrDefault as FieldSymbol;
                            lookupResult.Free();
                            if ((object)possibleField != null)
                            {
                                Error(diagnostics, ErrorCode.ERR_VariableUsedBeforeDeclarationAndHidesField, node, node, possibleField);
                            }
                            else
                            {
                                Error(diagnostics, ErrorCode.ERR_VariableUsedBeforeDeclaration, node, node);
                            }

                            type = new ExtendedErrorTypeSymbol(
                                this.Compilation, name: "var", arity: 0, errorInfo: null, variableUsedBeforeDeclaration: true);
                            isNullableUnknown = true;
                        }
                        else if ((localSymbol as SourceLocalSymbol)?.IsVar == true && localSymbol.ForbiddenZone?.Contains(node) == true)
                        {
                            // A var (type-inferred) local variable has been used in its own initialization (the "forbidden zone").
                            // There are many cases where this occurs, including:
                            //
                            // 1. var x = M(out x);
                            // 2. M(out var x, out x);
                            // 3. var (x, y) = (y, x);
                            //
                            // localSymbol.ForbiddenDiagnostic provides a suitable diagnostic for whichever case applies.
                            //
                            diagnostics.Add(localSymbol.ForbiddenDiagnostic, node.Location, node);
                            type = new ExtendedErrorTypeSymbol(
                                this.Compilation, name: "var", arity: 0, errorInfo: null, variableUsedBeforeDeclaration: true);
                            isNullableUnknown = true;
                        }
                        else
                        {
                            type = localSymbol.Type;
                            isNullableUnknown = false;
                            if (IsBadLocalOrParameterCapture(localSymbol, type, localSymbol.RefKind))
                            {
                                isError = true;
                                Error(diagnostics, ErrorCode.ERR_AnonDelegateCantUseLocal, node, localSymbol);
                            }
                        }

                        var constantValueOpt = localSymbol.IsConst && !IsInsideNameof && !type.IsErrorType()
                            ? localSymbol.GetConstantValue(node, this.LocalInProgress, diagnostics) : null;
                        return new BoundLocal(node, localSymbol, BoundLocalDeclarationKind.None, constantValueOpt: constantValueOpt, isNullableUnknown: isNullableUnknown, type: type, hasErrors: isError);
                    }

                case SymbolKind.Parameter:
                    {
                        var parameter = (ParameterSymbol)symbol;
                        if (IsBadLocalOrParameterCapture(parameter, parameter.Type, parameter.RefKind))
                        {
                            isError = true;
                            Error(diagnostics, ErrorCode.ERR_AnonDelegateCantUse, node, parameter.Name);
                        }
                        return new BoundParameter(node, parameter, hasErrors: isError);
                    }

                case SymbolKind.NamedType:
                case SymbolKind.ErrorType:
                case SymbolKind.TypeParameter:
                    // If I identifies a type, then the result is that type constructed with the
                    // given type arguments. UNDONE: Construct the child type if it is generic!
                    return new BoundTypeExpression(node, null, (TypeSymbol)symbol, hasErrors: isError);

                case SymbolKind.Property:
                    {
                        BoundExpression receiver = SynthesizeReceiver(node, symbol, diagnostics);
                        return BindPropertyAccess(node, receiver, (PropertySymbol)symbol, diagnostics, resultKind, hasErrors: isError);
                    }

                case SymbolKind.Event:
                    {
                        BoundExpression receiver = SynthesizeReceiver(node, symbol, diagnostics);
                        return BindEventAccess(node, receiver, (EventSymbol)symbol, diagnostics, resultKind, hasErrors: isError);
                    }

                case SymbolKind.Field:
                    {
                        BoundExpression receiver = SynthesizeReceiver(node, symbol, diagnostics);
                        return BindFieldAccess(node, receiver, (FieldSymbol)symbol, diagnostics, resultKind, indexed, hasErrors: isError);
                    }

                case SymbolKind.Namespace:
                    return new BoundNamespaceExpression(node, (NamespaceSymbol)symbol, hasErrors: isError);

                case SymbolKind.Alias:
                    {
                        var alias = (AliasSymbol)symbol;
                        symbol = alias.Target;
                        switch (symbol.Kind)
                        {
                            case SymbolKind.NamedType:
                            case SymbolKind.ErrorType:
                                return new BoundTypeExpression(node, alias, (NamedTypeSymbol)symbol, hasErrors: isError);
                            case SymbolKind.Namespace:
                                return new BoundNamespaceExpression(node, (NamespaceSymbol)symbol, alias, hasErrors: isError);
                            default:
                                throw ExceptionUtilities.UnexpectedValue(symbol.Kind);
                        }
                    }

                case SymbolKind.RangeVariable:
                    return BindRangeVariable(node, (RangeVariableSymbol)symbol, diagnostics);

                default:
                    throw ExceptionUtilities.UnexpectedValue(symbol.Kind);
            }

            bool isUsedBeforeDeclaration(SimpleNameSyntax node, LocalSymbol localSymbol)
            {
                Location localSymbolLocation = localSymbol.Locations[0];

                if (node.SyntaxTree == localSymbolLocation.SourceTree)
                {
                    return node.SpanStart < localSymbolLocation.SourceSpan.Start;
                }

                return false;
            }
        }

        private static bool ReportSimpleProgramLocalReferencedOutsideOfTopLevelStatement(SimpleNameSyntax node, Symbol symbol, BindingDiagnosticBag diagnostics)
        {
            if (symbol.ContainingSymbol is SynthesizedSimpleProgramEntryPointSymbol)
            {
                if (!SyntaxFacts.IsTopLevelStatement(node.Ancestors(ascendOutOfTrivia: false).OfType<GlobalStatementSyntax>().FirstOrDefault()))
                {
                    Error(diagnostics, ErrorCode.ERR_SimpleProgramLocalIsReferencedOutsideOfTopLevelStatement, node, node);
                    return true;
                }
            }

            return false;
        }

        protected virtual BoundExpression BindRangeVariable(SimpleNameSyntax node, RangeVariableSymbol qv, BindingDiagnosticBag diagnostics)
        {
            return Next.BindRangeVariable(node, qv, diagnostics);
        }

        private BoundExpression SynthesizeReceiver(SyntaxNode node, Symbol member, BindingDiagnosticBag diagnostics)
        {
            // SPEC: Otherwise, if T is the instance type of the immediately enclosing class or
            // struct type, if the lookup identifies an instance member, and if the reference occurs
            // within the block of an instance constructor, an instance method, or an instance
            // accessor, the result is the same as a member access of the form this.I. This can only
            // happen when K is zero.

            if (!member.RequiresInstanceReceiver())
            {
                return null;
            }

            var currentType = this.ContainingType;
            var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
            NamedTypeSymbol declaringType = member.ContainingType;
            if (currentType.IsEqualToOrDerivedFrom(declaringType, TypeCompareKind.ConsiderEverything, useSiteInfo: ref discardedUseSiteInfo) ||
                (currentType.IsInterface && (declaringType.IsObjectType() || currentType.AllInterfacesNoUseSiteDiagnostics.Contains(declaringType))))
            {
                bool hasErrors = false;
                if (EnclosingNameofArgument != node)
                {
                    if (InFieldInitializer && !currentType.IsScriptClass)
                    {
                        //can't access "this" in field initializers
                        Error(diagnostics, ErrorCode.ERR_FieldInitRefNonstatic, node, member);
                        hasErrors = true;
                    }
                    else if (InConstructorInitializer || InAttributeArgument)
                    {
                        //can't access "this" in constructor initializers or attribute arguments
                        Error(diagnostics, ErrorCode.ERR_ObjectRequired, node, member);
                        hasErrors = true;
                    }
                    else
                    {
                        // not an instance member if the container is a type, like when binding default parameter values.
                        var containingMember = ContainingMember();
                        bool locationIsInstanceMember = !containingMember.IsStatic &&
                            (containingMember.Kind != SymbolKind.NamedType || currentType.IsScriptClass);

                        if (!locationIsInstanceMember)
                        {
                            // error CS0120: An object reference is required for the non-static field, method, or property '{0}'
                            Error(diagnostics, ErrorCode.ERR_ObjectRequired, node, member);
                            hasErrors = true;
                        }
                    }

                    hasErrors = hasErrors || IsRefOrOutThisParameterCaptured(node, diagnostics);
                }

                return ThisReference(node, currentType, hasErrors, wasCompilerGenerated: true);
            }
            else
            {
                return TryBindInteractiveReceiver(node, declaringType);
            }
        }

        internal Symbol ContainingMember()
        {
            return this.ContainingMemberOrLambda.ContainingNonLambdaMember();
        }

        private BoundExpression TryBindInteractiveReceiver(SyntaxNode syntax, NamedTypeSymbol memberDeclaringType)
        {
            if (this.ContainingType.TypeKind == TypeKind.Submission
                // check we have access to `this`
                && isInstanceContext())
            {
                if (memberDeclaringType.TypeKind == TypeKind.Submission)
                {
                    return new BoundPreviousSubmissionReference(syntax, memberDeclaringType) { WasCompilerGenerated = true };
                }
                else
                {
                    TypeSymbol hostObjectType = Compilation.GetHostObjectTypeSymbol();
                    var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                    if ((object)hostObjectType != null && hostObjectType.IsEqualToOrDerivedFrom(memberDeclaringType, TypeCompareKind.ConsiderEverything, useSiteInfo: ref discardedUseSiteInfo))
                    {
                        return new BoundHostObjectMemberReference(syntax, hostObjectType) { WasCompilerGenerated = true };
                    }
                }
            }

            return null;

            bool isInstanceContext()
            {
                var containingMember = this.ContainingMemberOrLambda;
                do
                {
                    if (containingMember.IsStatic)
                    {
                        return false;
                    }
                    if (containingMember.Kind == SymbolKind.NamedType)
                    {
                        break;
                    }
                    containingMember = containingMember.ContainingSymbol;
                } while ((object)containingMember != null);
                return true;
            }
        }

        public BoundExpression BindNamespaceOrTypeOrExpression(ExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            if (node.Kind() == SyntaxKind.PredefinedType)
            {
                return this.BindNamespaceOrType(node, diagnostics);
            }

            if (SyntaxFacts.IsName(node.Kind()))
            {
                if (SyntaxFacts.IsNamespaceAliasQualifier(node))
                {
                    return this.BindNamespaceAlias((IdentifierNameSyntax)node, diagnostics);
                }
                else if (SyntaxFacts.IsInNamespaceOrTypeContext(node))
                {
                    return this.BindNamespaceOrType(node, diagnostics);
                }
            }
            else if (SyntaxFacts.IsTypeSyntax(node.Kind()))
            {
                return this.BindNamespaceOrType(node, diagnostics);
            }

            return this.BindExpression(node, diagnostics, SyntaxFacts.IsInvoked(node), SyntaxFacts.IsIndexed(node));
        }

        public BoundExpression BindLabel(ExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            var name = node as IdentifierNameSyntax;
            if (name == null)
            {
                Debug.Assert(node.ContainsDiagnostics);
                return BadExpression(node, LookupResultKind.NotLabel);
            }

            var result = LookupResult.GetInstance();
            string labelName = name.Identifier.ValueText;
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            this.LookupSymbolsWithFallback(result, labelName, arity: 0, useSiteInfo: ref useSiteInfo, options: LookupOptions.LabelsOnly);
            diagnostics.Add(node, useSiteInfo);

            if (!result.IsMultiViable)
            {
                Error(diagnostics, ErrorCode.ERR_LabelNotFound, node, labelName);
                result.Free();
                return BadExpression(node, result.Kind);
            }

            Debug.Assert(result.IsSingleViable, "If this happens, we need to deal with multiple label definitions.");
            var symbol = (LabelSymbol)result.Symbols.First();
            result.Free();
            return new BoundLabel(node, symbol, null);
        }

        public BoundExpression BindNamespaceOrType(ExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            var symbol = this.BindNamespaceOrTypeOrAliasSymbol(node, diagnostics, null, false);
            return CreateBoundNamespaceOrTypeExpression(node, symbol.Symbol);
        }

        public BoundExpression BindNamespaceAlias(IdentifierNameSyntax node, BindingDiagnosticBag diagnostics)
        {
            var symbol = this.BindNamespaceAliasSymbol(node, diagnostics);
            return CreateBoundNamespaceOrTypeExpression(node, symbol);
        }

        private static BoundExpression CreateBoundNamespaceOrTypeExpression(ExpressionSyntax node, Symbol symbol)
        {
            var alias = symbol as AliasSymbol;

            if ((object)alias != null)
            {
                symbol = alias.Target;
            }

            var type = symbol as TypeSymbol;
            if ((object)type != null)
            {
                return new BoundTypeExpression(node, alias, type);
            }

            var namespaceSymbol = symbol as NamespaceSymbol;
            if ((object)namespaceSymbol != null)
            {
                return new BoundNamespaceExpression(node, namespaceSymbol, alias);
            }

            throw ExceptionUtilities.UnexpectedValue(symbol);
        }

        private BoundThisReference BindThis(ThisExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node != null);
            bool hasErrors = true;

            bool inStaticContext;
            if (!HasThis(isExplicit: true, inStaticContext: out inStaticContext))
            {
                //this error is returned in the field initializer case
                Error(diagnostics, inStaticContext ? ErrorCode.ERR_ThisInStaticMeth : ErrorCode.ERR_ThisInBadContext, node);
            }
            else
            {
                hasErrors = IsRefOrOutThisParameterCaptured(node.Token, diagnostics);
            }

            return ThisReference(node, this.ContainingType, hasErrors);
        }

        private BoundThisReference ThisReference(SyntaxNode node, NamedTypeSymbol thisTypeOpt, bool hasErrors = false, bool wasCompilerGenerated = false)
        {
            return new BoundThisReference(node, thisTypeOpt ?? CreateErrorType(), hasErrors) { WasCompilerGenerated = wasCompilerGenerated };
        }

        private bool IsRefOrOutThisParameterCaptured(SyntaxNodeOrToken thisOrBaseToken, BindingDiagnosticBag diagnostics)
        {
            ParameterSymbol thisSymbol = this.ContainingMemberOrLambda.EnclosingThisSymbol();
            // If there is no this parameter, then it is definitely not captured and 
            // any diagnostic would be cascading.
            if ((object)thisSymbol != null && thisSymbol.ContainingSymbol != ContainingMemberOrLambda && thisSymbol.RefKind != RefKind.None)
            {
                Error(diagnostics, ErrorCode.ERR_ThisStructNotInAnonMeth, thisOrBaseToken);
                return true;
            }

            return false;
        }

        private BoundBaseReference BindBase(BaseExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            bool hasErrors = false;
            TypeSymbol baseType = this.ContainingType is null ? null : this.ContainingType.BaseTypeNoUseSiteDiagnostics;
            bool inStaticContext;

            if (!HasThis(isExplicit: true, inStaticContext: out inStaticContext))
            {
                //this error is returned in the field initializer case
                Error(diagnostics, inStaticContext ? ErrorCode.ERR_BaseInStaticMeth : ErrorCode.ERR_BaseInBadContext, node.Token);
                hasErrors = true;
            }
            else if ((object)baseType == null) // e.g. in System.Object
            {
                Error(diagnostics, ErrorCode.ERR_NoBaseClass, node);
                hasErrors = true;
            }
            else if (this.ContainingType is null || node.Parent is null || (node.Parent.Kind() != SyntaxKind.SimpleMemberAccessExpression && node.Parent.Kind() != SyntaxKind.ElementAccessExpression))
            {
                Error(diagnostics, ErrorCode.ERR_BaseIllegal, node.Token);
                hasErrors = true;
            }
            else if (IsRefOrOutThisParameterCaptured(node.Token, diagnostics))
            {
                // error has been reported by IsRefOrOutThisParameterCaptured
                hasErrors = true;
            }

            return new BoundBaseReference(node, baseType, hasErrors);
        }

        private BoundExpression BindCast(CastExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            BoundExpression operand = this.BindValue(node.Expression, diagnostics, BindValueKind.RValue);
            TypeWithAnnotations targetTypeWithAnnotations = this.BindType(node.Type, diagnostics);
            TypeSymbol targetType = targetTypeWithAnnotations.Type;

            if (targetType.IsNullableType() &&
                !operand.HasAnyErrors &&
                (object)operand.Type != null &&
                !operand.Type.IsNullableType() &&
                !TypeSymbol.Equals(targetType.GetNullableUnderlyingType(), operand.Type, TypeCompareKind.ConsiderEverything2))
            {
                return BindExplicitNullableCastFromNonNullable(node, operand, targetTypeWithAnnotations, diagnostics);
            }

            return BindCastCore(node, operand, targetTypeWithAnnotations, wasCompilerGenerated: operand.WasCompilerGenerated, diagnostics: diagnostics);
        }

        private BoundExpression BindFromEndIndexExpression(PrefixUnaryExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node.OperatorToken.IsKind(SyntaxKind.CaretToken));

            CheckFeatureAvailability(node, MessageID.IDS_FeatureIndexOperator, diagnostics);

            // Used in lowering as the second argument to the constructor. Example: new Index(value, fromEnd: true)
            GetSpecialType(SpecialType.System_Boolean, diagnostics, node);

            BoundExpression boundOperand = BindValue(node.Operand, diagnostics, BindValueKind.RValue);
            TypeSymbol intType = GetSpecialType(SpecialType.System_Int32, diagnostics, node);
            TypeSymbol indexType = GetWellKnownType(WellKnownType.System_Index, diagnostics, node);

            if ((object)boundOperand.Type != null && boundOperand.Type.IsNullableType())
            {
                // Used in lowering to construct the nullable
                GetSpecialTypeMember(SpecialMember.System_Nullable_T__ctor, diagnostics, node);
                NamedTypeSymbol nullableType = GetSpecialType(SpecialType.System_Nullable_T, diagnostics, node);

                if (!indexType.IsNonNullableValueType())
                {
                    Error(diagnostics, ErrorCode.ERR_ValConstraintNotSatisfied, node, nullableType, nullableType.TypeParameters.Single(), indexType);
                }

                intType = nullableType.Construct(intType);
                indexType = nullableType.Construct(indexType);
            }

            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            Conversion conversion = this.Conversions.ClassifyImplicitConversionFromExpression(boundOperand, intType, ref useSiteInfo);
            diagnostics.Add(node, useSiteInfo);

            if (!conversion.IsValid)
            {
                GenerateImplicitConversionError(diagnostics, node, conversion, boundOperand, intType);
            }

            BoundExpression boundConversion = CreateConversion(boundOperand, conversion, intType, diagnostics);
            MethodSymbol symbolOpt = GetWellKnownTypeMember(WellKnownMember.System_Index__ctor, diagnostics, syntax: node) as MethodSymbol;

            return new BoundFromEndIndexExpression(node, boundConversion, symbolOpt, indexType);
        }

        private BoundExpression BindRangeExpression(RangeExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            CheckFeatureAvailability(node, MessageID.IDS_FeatureRangeOperator, diagnostics);

            TypeSymbol rangeType = GetWellKnownType(WellKnownType.System_Range, diagnostics, node);
            MethodSymbol symbolOpt = null;

            if (!rangeType.IsErrorType())
            {
                // Depending on the available arguments to the range expression, there are four
                // possible well-known members we could bind to. The constructor is always the
                // fallback member, usable in any situation. However, if any of the other members
                // are available and applicable, we will prefer that.

                WellKnownMember? memberOpt = null;
                if (node.LeftOperand is null && node.RightOperand is null)
                {
                    memberOpt = WellKnownMember.System_Range__get_All;
                }
                else if (node.LeftOperand is null)
                {
                    memberOpt = WellKnownMember.System_Range__EndAt;
                }
                else if (node.RightOperand is null)
                {
                    memberOpt = WellKnownMember.System_Range__StartAt;
                }

                if (memberOpt is object)
                {
                    symbolOpt = (MethodSymbol)GetWellKnownTypeMember(
                        memberOpt.GetValueOrDefault(),
                        diagnostics,
                        syntax: node,
                        isOptional: true);
                }

                if (symbolOpt is null)
                {
                    symbolOpt = (MethodSymbol)GetWellKnownTypeMember(
                        WellKnownMember.System_Range__ctor,
                        diagnostics,
                        syntax: node);
                }
            }

            BoundExpression left = BindRangeExpressionOperand(node.LeftOperand, diagnostics);
            BoundExpression right = BindRangeExpressionOperand(node.RightOperand, diagnostics);

            if (left?.Type.IsNullableType() == true || right?.Type.IsNullableType() == true)
            {
                // Used in lowering to construct the nullable
                GetSpecialType(SpecialType.System_Boolean, diagnostics, node);
                GetSpecialTypeMember(SpecialMember.System_Nullable_T__ctor, diagnostics, node);
                NamedTypeSymbol nullableType = GetSpecialType(SpecialType.System_Nullable_T, diagnostics, node);

                if (!rangeType.IsNonNullableValueType())
                {
                    Error(diagnostics, ErrorCode.ERR_ValConstraintNotSatisfied, node, nullableType, nullableType.TypeParameters.Single(), rangeType);
                }

                rangeType = nullableType.Construct(rangeType);
            }

            return new BoundRangeExpression(node, left, right, symbolOpt, rangeType);
        }

        private BoundExpression BindRangeExpressionOperand(ExpressionSyntax operand, BindingDiagnosticBag diagnostics)
        {
            if (operand is null)
            {
                return null;
            }

            BoundExpression boundOperand = BindValue(operand, diagnostics, BindValueKind.RValue);
            TypeSymbol indexType = GetWellKnownType(WellKnownType.System_Index, diagnostics, operand);

            if (boundOperand.Type?.IsNullableType() == true)
            {
                // Used in lowering to construct the nullable
                GetSpecialTypeMember(SpecialMember.System_Nullable_T__ctor, diagnostics, operand);
                NamedTypeSymbol nullableType = GetSpecialType(SpecialType.System_Nullable_T, diagnostics, operand);

                if (!indexType.IsNonNullableValueType())
                {
                    Error(diagnostics, ErrorCode.ERR_ValConstraintNotSatisfied, operand, nullableType, nullableType.TypeParameters.Single(), indexType);
                }

                indexType = nullableType.Construct(indexType);
            }

            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            Conversion conversion = this.Conversions.ClassifyImplicitConversionFromExpression(boundOperand, indexType, ref useSiteInfo);
            diagnostics.Add(operand, useSiteInfo);

            if (!conversion.IsValid)
            {
                GenerateImplicitConversionError(diagnostics, operand, conversion, boundOperand, indexType);
            }

            return CreateConversion(boundOperand, conversion, indexType, diagnostics);
        }

        private BoundExpression BindCastCore(ExpressionSyntax node, BoundExpression operand, TypeWithAnnotations targetTypeWithAnnotations, bool wasCompilerGenerated, BindingDiagnosticBag diagnostics)
        {
            TypeSymbol targetType = targetTypeWithAnnotations.Type;
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            Conversion conversion = this.Conversions.ClassifyConversionFromExpression(operand, targetType, ref useSiteInfo, forCast: true);
            diagnostics.Add(node, useSiteInfo);

            var conversionGroup = new ConversionGroup(conversion, targetTypeWithAnnotations);
            bool suppressErrors = operand.HasAnyErrors || targetType.IsErrorType();
            bool hasErrors = !conversion.IsValid || targetType.IsStatic;
            if (hasErrors && !suppressErrors)
            {
                GenerateExplicitConversionErrors(diagnostics, node, conversion, operand, targetType);
            }

            return CreateConversion(node, operand, conversion, isCast: true, conversionGroupOpt: conversionGroup, wasCompilerGenerated: wasCompilerGenerated, destination: targetType, diagnostics: diagnostics, hasErrors: hasErrors | suppressErrors);
        }

        private void GenerateExplicitConversionErrors(
            BindingDiagnosticBag diagnostics,
            SyntaxNode syntax,
            Conversion conversion,
            BoundExpression operand,
            TypeSymbol targetType)
        {
            // Make sure that errors within the unbound lambda don't get lost.
            if (operand.Kind == BoundKind.UnboundLambda)
            {
                GenerateAnonymousFunctionConversionError(diagnostics, operand.Syntax, (UnboundLambda)operand, targetType);
                return;
            }

            if (operand.HasAnyErrors || targetType.IsErrorType())
            {
                // an error has already been reported elsewhere
                return;
            }

            if (targetType.IsStatic)
            {
                // The specification states in the section titled "Referencing Static
                // Class Types" that it is always illegal to have a static class in a
                // cast operator.
                diagnostics.Add(ErrorCode.ERR_ConvertToStaticClass, syntax.Location, targetType);
                return;
            }

            if (!targetType.IsReferenceType && !targetType.IsNullableType() && operand.IsLiteralNull())
            {
                diagnostics.Add(ErrorCode.ERR_ValueCantBeNull, syntax.Location, targetType);
                return;
            }

            if (conversion.ResultKind == LookupResultKind.OverloadResolutionFailure)
            {
                Debug.Assert(conversion.IsUserDefined);

                ImmutableArray<MethodSymbol> originalUserDefinedConversions = conversion.OriginalUserDefinedConversions;
                if (originalUserDefinedConversions.Length > 1)
                {
                    diagnostics.Add(ErrorCode.ERR_AmbigUDConv, syntax.Location, originalUserDefinedConversions[0], originalUserDefinedConversions[1], operand.Display, targetType);
                }
                else
                {
                    Debug.Assert(originalUserDefinedConversions.Length == 0,
                        "How can there be exactly one applicable user-defined conversion if the conversion doesn't exist?");
                    SymbolDistinguisher distinguisher1 = new SymbolDistinguisher(this.Compilation, operand.Type, targetType);
                    diagnostics.Add(ErrorCode.ERR_NoExplicitConv, syntax.Location, distinguisher1.First, distinguisher1.Second);
                }

                return;
            }

            switch (operand.Kind)
            {
                case BoundKind.MethodGroup:
                    {
                        if (targetType.TypeKind != TypeKind.Delegate ||
                            !MethodGroupConversionDoesNotExistOrHasErrors((BoundMethodGroup)operand, (NamedTypeSymbol)targetType, syntax.Location, diagnostics, out _))
                        {
                            diagnostics.Add(ErrorCode.ERR_NoExplicitConv, syntax.Location, MessageID.IDS_SK_METHOD.Localize(), targetType);
                        }

                        return;
                    }
                case BoundKind.TupleLiteral:
                    {
                        var tuple = (BoundTupleLiteral)operand;
                        var targetElementTypesWithAnnotations = default(ImmutableArray<TypeWithAnnotations>);

                        // If target is a tuple or compatible type with the same number of elements,
                        // report errors for tuple arguments that failed to convert, which would be more useful.
                        if (targetType.TryGetElementTypesWithAnnotationsIfTupleType(out targetElementTypesWithAnnotations) &&
                            targetElementTypesWithAnnotations.Length == tuple.Arguments.Length)
                        {
                            GenerateExplicitConversionErrorsForTupleLiteralArguments(diagnostics, tuple.Arguments, targetElementTypesWithAnnotations);
                            return;
                        }

                        // target is not compatible with source and source does not have a type
                        if ((object)tuple.Type == null)
                        {
                            Error(diagnostics, ErrorCode.ERR_ConversionNotTupleCompatible, syntax, tuple.Arguments.Length, targetType);
                            return;
                        }

                        // Otherwise it is just a regular conversion failure from T1 to T2.
                        break;
                    }
                case BoundKind.StackAllocArrayCreation:
                    {
                        var stackAllocExpression = (BoundStackAllocArrayCreation)operand;
                        Error(diagnostics, ErrorCode.ERR_StackAllocConversionNotPossible, syntax, stackAllocExpression.ElementType, targetType);
                        return;
                    }
                case BoundKind.UnconvertedConditionalOperator when operand.Type is null:
                case BoundKind.UnconvertedSwitchExpression when operand.Type is null:
                    {
                        GenerateImplicitConversionError(diagnostics, operand.Syntax, conversion, operand, targetType);
                        return;
                    }
                case BoundKind.UnconvertedAddressOfOperator:
                    {
                        var errorCode = targetType.TypeKind switch
                        {
                            TypeKind.FunctionPointer => ErrorCode.ERR_MethFuncPtrMismatch,
                            TypeKind.Delegate => ErrorCode.ERR_CannotConvertAddressOfToDelegate,
                            _ => ErrorCode.ERR_AddressOfToNonFunctionPointer
                        };

                        diagnostics.Add(errorCode, syntax.Location, ((BoundUnconvertedAddressOfOperator)operand).Operand.Name, targetType);
                        return;
                    }
            }

            Debug.Assert((object)operand.Type != null);
            SymbolDistinguisher distinguisher = new SymbolDistinguisher(this.Compilation, operand.Type, targetType);
            diagnostics.Add(ErrorCode.ERR_NoExplicitConv, syntax.Location, distinguisher.First, distinguisher.Second);
        }

        private void GenerateExplicitConversionErrorsForTupleLiteralArguments(
            BindingDiagnosticBag diagnostics,
            ImmutableArray<BoundExpression> tupleArguments,
            ImmutableArray<TypeWithAnnotations> targetElementTypesWithAnnotations)
        {
            // report all leaf elements of the tuple literal that failed to convert
            // NOTE: we are not responsible for reporting use site errors here, just the failed leaf conversions.
            // By the time we get here we have done analysis and know we have failed the cast in general, and diagnostics collected in the process is already in the bag. 
            // The only thing left is to form a diagnostics about the actually failing conversion(s).
            // This whole method does not itself collect any usesite diagnostics. Its only purpose is to produce an error better than "conversion failed here"           
            var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;

            for (int i = 0; i < targetElementTypesWithAnnotations.Length; i++)
            {
                var argument = tupleArguments[i];
                var targetElementType = targetElementTypesWithAnnotations[i].Type;

                var elementConversion = Conversions.ClassifyConversionFromExpression(argument, targetElementType, ref discardedUseSiteInfo);
                if (!elementConversion.IsValid)
                {
                    GenerateExplicitConversionErrors(diagnostics, argument.Syntax, elementConversion, argument, targetElementType);
                }
            }
        }

        /// <summary>
        /// This implements the casting behavior described in section 6.2.3 of the spec:
        /// 
        /// - If the nullable conversion is from S to T?, the conversion is evaluated as the underlying conversion 
        ///   from S to T followed by a wrapping from T to T?.
        ///
        /// This particular check is done in the binder because it involves conversion processing rules (like overflow
        /// checking and constant folding) which are not handled by Conversions.
        /// </summary>
        private BoundExpression BindExplicitNullableCastFromNonNullable(ExpressionSyntax node, BoundExpression operand, TypeWithAnnotations targetTypeWithAnnotations, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(targetTypeWithAnnotations.HasType && targetTypeWithAnnotations.IsNullableType());
            Debug.Assert((object)operand.Type != null && !operand.Type.IsNullableType());

            // Section 6.2.3 of the spec only applies when the non-null version of the types involved have a
            // built in conversion.
            var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
            TypeWithAnnotations underlyingTargetTypeWithAnnotations = targetTypeWithAnnotations.Type.GetNullableUnderlyingTypeWithAnnotations();
            var underlyingConversion = Conversions.ClassifyBuiltInConversion(operand.Type, underlyingTargetTypeWithAnnotations.Type, ref discardedUseSiteInfo);
            if (!underlyingConversion.Exists)
            {
                return BindCastCore(node, operand, targetTypeWithAnnotations, wasCompilerGenerated: operand.WasCompilerGenerated, diagnostics: diagnostics);
            }

            var bag = new BindingDiagnosticBag(DiagnosticBag.GetInstance(), diagnostics.DependenciesBag);
            try
            {
                var underlyingExpr = BindCastCore(node, operand, underlyingTargetTypeWithAnnotations, wasCompilerGenerated: false, diagnostics: bag);
                if (underlyingExpr.HasErrors || bag.HasAnyErrors())
                {
                    Error(diagnostics, ErrorCode.ERR_NoExplicitConv, node, operand.Type, targetTypeWithAnnotations.Type);

                    return new BoundConversion(
                        node,
                        operand,
                        Conversion.NoConversion,
                        @checked: CheckOverflowAtRuntime,
                        explicitCastInCode: true,
                        conversionGroupOpt: new ConversionGroup(Conversion.NoConversion, explicitType: targetTypeWithAnnotations),
                        constantValueOpt: ConstantValue.NotAvailable,
                        type: targetTypeWithAnnotations.Type,
                        hasErrors: true);
                }

                // It's possible for the S -> T conversion to produce a 'better' constant value.  If this 
                // constant value is produced place it in the tree so that it gets emitted.  This maintains 
                // parity with the native compiler which also evaluated the conversion at compile time. 
                if (underlyingExpr.ConstantValue != null)
                {
                    underlyingExpr.WasCompilerGenerated = true;
                    return BindCastCore(node, underlyingExpr, targetTypeWithAnnotations, wasCompilerGenerated: operand.WasCompilerGenerated, diagnostics: diagnostics);
                }

                return BindCastCore(node, operand, targetTypeWithAnnotations, wasCompilerGenerated: operand.WasCompilerGenerated, diagnostics: diagnostics);
            }
            finally
            {
                bag.DiagnosticBag.Free();
            }
        }

        private static NameSyntax GetNameSyntax(SyntaxNode syntax)
        {
            string nameString;
            return GetNameSyntax(syntax, out nameString);
        }

        /// <summary>
        /// Gets the NameSyntax associated with the syntax node
        /// If no syntax is attached it sets the nameString to plain text
        /// name and returns a null NameSyntax
        /// </summary>
        /// <param name="syntax">Syntax node</param>
        /// <param name="nameString">Plain text name</param>
        internal static NameSyntax GetNameSyntax(SyntaxNode syntax, out string nameString)
        {
            nameString = string.Empty;
            while (true)
            {
                switch (syntax.Kind())
                {
                    case SyntaxKind.PredefinedType:
                        nameString = ((PredefinedTypeSyntax)syntax).Keyword.ValueText;
                        return null;
                    case SyntaxKind.SimpleLambdaExpression:
                        nameString = MessageID.IDS_Lambda.Localize().ToString();
                        return null;
                    case SyntaxKind.ParenthesizedExpression:
                        syntax = ((ParenthesizedExpressionSyntax)syntax).Expression;
                        continue;
                    case SyntaxKind.CastExpression:
                        syntax = ((CastExpressionSyntax)syntax).Expression;
                        continue;
                    case SyntaxKind.SimpleMemberAccessExpression:
                    case SyntaxKind.PointerMemberAccessExpression:
                        return ((MemberAccessExpressionSyntax)syntax).Name;
                    case SyntaxKind.MemberBindingExpression:
                        return ((MemberBindingExpressionSyntax)syntax).Name;
                    default:
                        return syntax as NameSyntax;
                }
            }
        }

        /// <summary>
        /// Gets the plain text name associated with the expression syntax node
        /// </summary>
        /// <param name="syntax">Expression syntax node</param>
        /// <returns>Plain text name</returns>
        private static string GetName(ExpressionSyntax syntax)
        {
            string nameString;
            var nameSyntax = GetNameSyntax(syntax, out nameString);
            if (nameSyntax != null)
            {
                return nameSyntax.GetUnqualifiedName().Identifier.ValueText;
            }
            return nameString;
        }

        // Given a list of arguments, create arrays of the bound arguments and the names of those
        // arguments.
        private void BindArgumentsAndNames(BaseArgumentListSyntax argumentListOpt, BindingDiagnosticBag diagnostics, AnalyzedArguments result, bool allowArglist = false, bool isDelegateCreation = false)
        {
            if (argumentListOpt is null)
            {
                return;
            }

            // Only report the first "duplicate name" or "named before positional" error,
            // so as to avoid "cascading" errors.
            bool hadError = false;

            // Only report the first "non-trailing named args required C# 7.2" error,
            // so as to avoid "cascading" errors.
            bool hadLangVersionError = false;

            foreach (var argumentSyntax in argumentListOpt.Arguments)
            {
                BindArgumentAndName(result, diagnostics, ref hadError, ref hadLangVersionError,
                    argumentSyntax, allowArglist, isDelegateCreation: isDelegateCreation);
            }
        }

        private bool RefMustBeObeyed(bool isDelegateCreation, ArgumentSyntax argumentSyntax)
        {
            if (Compilation.FeatureStrictEnabled || !isDelegateCreation)
            {
                return true;
            }

            switch (argumentSyntax.Expression.Kind())
            {
                // The next 3 cases should never be allowed as they cannot be ref/out. Assuming a bug in legacy compiler.
                case SyntaxKind.ParenthesizedLambdaExpression:
                case SyntaxKind.SimpleLambdaExpression:
                case SyntaxKind.AnonymousMethodExpression:
                case SyntaxKind.InvocationExpression:
                case SyntaxKind.ObjectCreationExpression:
                case SyntaxKind.ImplicitObjectCreationExpression:
                case SyntaxKind.ParenthesizedExpression: // this is never allowed in legacy compiler
                case SyntaxKind.DeclarationExpression:
                    // A property/indexer is also invalid as it cannot be ref/out, but cannot be checked here. Assuming a bug in legacy compiler.
                    return true;
                default:
                    // The only ones that concern us here for compat is: locals, params, fields
                    // BindArgumentAndName correctly rejects all other cases, except for properties and indexers.
                    // They are handled after BindArgumentAndName returns and the binding can be checked.
                    return false;
            }
        }

        private void BindArgumentAndName(
            AnalyzedArguments result,
            BindingDiagnosticBag diagnostics,
            ref bool hadError,
            ref bool hadLangVersionError,
            ArgumentSyntax argumentSyntax,
            bool allowArglist,
            bool isDelegateCreation)
        {
            RefKind origRefKind = argumentSyntax.RefOrOutKeyword.Kind().GetRefKind();
            // The old native compiler ignores ref/out in a delegate creation expression.
            // For compatibility we implement the same bug except in strict mode.
            // Note: Some others should still be rejected when ref/out present. See RefMustBeObeyed.
            RefKind refKind = origRefKind == RefKind.None || RefMustBeObeyed(isDelegateCreation, argumentSyntax) ? origRefKind : RefKind.None;

            BoundExpression boundArgument = BindArgumentValue(diagnostics, argumentSyntax, allowArglist, refKind);

            BindArgumentAndName(
                result,
                diagnostics,
                ref hadLangVersionError,
                argumentSyntax,
                boundArgument,
                argumentSyntax.NameColon,
                refKind);

            // check for ref/out property/indexer, only needed for 1 parameter version
            if (!hadError && isDelegateCreation && origRefKind != RefKind.None && result.Arguments.Count == 1)
            {
                var arg = result.Argument(0);
                switch (arg.Kind)
                {
                    case BoundKind.PropertyAccess:
                    case BoundKind.IndexerAccess:
                        var requiredValueKind = origRefKind == RefKind.In ? BindValueKind.ReadonlyRef : BindValueKind.RefOrOut;
                        hadError = !CheckValueKind(argumentSyntax, arg, requiredValueKind, false, diagnostics);
                        return;
                }
            }

            if (argumentSyntax.RefOrOutKeyword.Kind() != SyntaxKind.None)
            {
                argumentSyntax.Expression.CheckDeconstructionCompatibleArgument(diagnostics);
            }
        }

        private BoundExpression BindArgumentValue(BindingDiagnosticBag diagnostics, ArgumentSyntax argumentSyntax, bool allowArglist, RefKind refKind)
        {
            if (argumentSyntax.Expression.Kind() == SyntaxKind.DeclarationExpression)
            {
                var declarationExpression = (DeclarationExpressionSyntax)argumentSyntax.Expression;
                if (declarationExpression.IsOutDeclaration())
                {
                    return BindOutDeclarationArgument(declarationExpression, diagnostics);
                }
            }

            return BindArgumentExpression(diagnostics, argumentSyntax.Expression, refKind, allowArglist);
        }

        private BoundExpression BindOutDeclarationArgument(DeclarationExpressionSyntax declarationExpression, BindingDiagnosticBag diagnostics)
        {
            TypeSyntax typeSyntax = declarationExpression.Type;
            VariableDesignationSyntax designation = declarationExpression.Designation;

            if (typeSyntax.GetRefKind() != RefKind.None)
            {
                diagnostics.Add(ErrorCode.ERR_OutVariableCannotBeByRef, declarationExpression.Type.Location);
            }

            switch (designation.Kind())
            {
                case SyntaxKind.DiscardDesignation:
                    {
                        bool isVar;
                        bool isConst = false;
                        AliasSymbol alias;
                        var declType = BindVariableTypeWithAnnotations(designation, diagnostics, typeSyntax, ref isConst, out isVar, out alias);
                        Debug.Assert(isVar != declType.HasType);

                        return new BoundDiscardExpression(declarationExpression, declType.Type);
                    }
                case SyntaxKind.SingleVariableDesignation:
                    return BindOutVariableDeclarationArgument(declarationExpression, diagnostics);
                default:
                    throw ExceptionUtilities.UnexpectedValue(designation.Kind());
            }
        }

        private BoundExpression BindOutVariableDeclarationArgument(
             DeclarationExpressionSyntax declarationExpression,
             BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(declarationExpression.IsOutVarDeclaration());
            bool isVar;
            var designation = (SingleVariableDesignationSyntax)declarationExpression.Designation;
            TypeSyntax typeSyntax = declarationExpression.Type;

            // Is this a local?
            SourceLocalSymbol localSymbol = this.LookupLocal(designation.Identifier);
            if ((object)localSymbol != null)
            {
                Debug.Assert(localSymbol.DeclarationKind == LocalDeclarationKind.OutVariable);
                if ((InConstructorInitializer || InFieldInitializer) && ContainingMemberOrLambda.ContainingSymbol.Kind == SymbolKind.NamedType)
                {
                    CheckFeatureAvailability(declarationExpression, MessageID.IDS_FeatureExpressionVariablesInQueriesAndInitializers, diagnostics);
                }

                bool isConst = false;
                AliasSymbol alias;
                var declType = BindVariableTypeWithAnnotations(declarationExpression, diagnostics, typeSyntax, ref isConst, out isVar, out alias);

                localSymbol.ScopeBinder.ValidateDeclarationNameConflictsInScope(localSymbol, diagnostics);

                if (isVar)
                {
                    return new OutVariablePendingInference(declarationExpression, localSymbol, null);
                }

                CheckRestrictedTypeInAsyncMethod(this.ContainingMemberOrLambda, declType.Type, diagnostics, typeSyntax);

                return new BoundLocal(declarationExpression, localSymbol, BoundLocalDeclarationKind.WithExplicitType, constantValueOpt: null, isNullableUnknown: false, type: declType.Type);
            }

            // Is this a field?
            GlobalExpressionVariable expressionVariableField = LookupDeclaredField(designation);

            if ((object)expressionVariableField == null)
            {
                // We should have the right binder in the chain, cannot continue otherwise.
                throw ExceptionUtilities.Unreachable;
            }

            BoundExpression receiver = SynthesizeReceiver(designation, expressionVariableField, diagnostics);

            if (typeSyntax.IsVar)
            {
                BindTypeOrAliasOrVarKeyword(typeSyntax, BindingDiagnosticBag.Discarded, out isVar);

                if (isVar)
                {
                    return new OutVariablePendingInference(declarationExpression, expressionVariableField, receiver);
                }
            }

            TypeSymbol fieldType = expressionVariableField.GetFieldType(this.FieldsBeingBound).Type;
            return new BoundFieldAccess(declarationExpression,
                                        receiver,
                                        expressionVariableField,
                                        null,
                                        LookupResultKind.Viable,
                                        isDeclaration: true,
                                        type: fieldType);
        }

        /// <summary>
        /// Returns true if a bad special by ref local was found.
        /// </summary>
        internal static bool CheckRestrictedTypeInAsyncMethod(Symbol containingSymbol, TypeSymbol type, BindingDiagnosticBag diagnostics, SyntaxNode syntax)
        {
            if (containingSymbol.Kind == SymbolKind.Method
                && ((MethodSymbol)containingSymbol).IsAsync
                && type.IsRestrictedType())
            {
                Error(diagnostics, ErrorCode.ERR_BadSpecialByRefLocal, syntax, type);
                return true;
            }
            return false;
        }

        internal GlobalExpressionVariable LookupDeclaredField(SingleVariableDesignationSyntax variableDesignator)
        {
            return LookupDeclaredField(variableDesignator, variableDesignator.Identifier.ValueText);
        }

        internal GlobalExpressionVariable LookupDeclaredField(SyntaxNode node, string identifier)
        {
            foreach (Symbol member in ContainingType?.GetMembers(identifier) ?? ImmutableArray<Symbol>.Empty)
            {
                GlobalExpressionVariable field;
                if (member.Kind == SymbolKind.Field &&
                    (field = member as GlobalExpressionVariable)?.SyntaxTree == node.SyntaxTree &&
                    field.SyntaxNode == node)
                {
                    return field;
                }
            }

            return null;
        }

        // Bind a named/positional argument.
        // Prevent cascading diagnostic by considering the previous
        // error state and returning the updated error state.
        private void BindArgumentAndName(
            AnalyzedArguments result,
            BindingDiagnosticBag diagnostics,
            ref bool hadLangVersionError,
            CSharpSyntaxNode argumentSyntax,
            BoundExpression boundArgumentExpression,
            NameColonSyntax nameColonSyntax,
            RefKind refKind)
        {
            Debug.Assert(argumentSyntax is ArgumentSyntax || argumentSyntax is AttributeArgumentSyntax);

            bool hasRefKinds = result.RefKinds.Any();
            if (refKind != RefKind.None)
            {
                // The common case is no ref or out arguments. So we defer all work until the first one is seen.
                if (!hasRefKinds)
                {
                    hasRefKinds = true;

                    int argCount = result.Arguments.Count;
                    for (int i = 0; i < argCount; ++i)
                    {
                        result.RefKinds.Add(RefKind.None);
                    }
                }
            }

            if (hasRefKinds)
            {
                result.RefKinds.Add(refKind);
            }

            bool hasNames = result.Names.Any();
            if (nameColonSyntax != null)
            {
                // The common case is no named arguments. So we defer all work until the first named argument is seen.
                if (!hasNames)
                {
                    hasNames = true;

                    int argCount = result.Arguments.Count;
                    for (int i = 0; i < argCount; ++i)
                    {
                        result.Names.Add(null);
                    }
                }

                result.AddName(nameColonSyntax.Name);
            }
            else if (hasNames)
            {
                // We just saw a fixed-position argument after a named argument.
                if (!hadLangVersionError && !Compilation.LanguageVersion.AllowNonTrailingNamedArguments())
                {
                    // CS1738: Named argument specifications must appear after all fixed arguments have been specified
                    Error(diagnostics, ErrorCode.ERR_NamedArgumentSpecificationBeforeFixedArgument, argumentSyntax,
                        new CSharpRequiredLanguageVersion(MessageID.IDS_FeatureNonTrailingNamedArguments.RequiredVersion()));

                    hadLangVersionError = true;
                }

                result.Names.Add(null);
            }

            result.Arguments.Add(boundArgumentExpression);
        }

        /// <summary>
        /// Bind argument and verify argument matches rvalue or out param requirements.
        /// </summary>
        private BoundExpression BindArgumentExpression(BindingDiagnosticBag diagnostics, ExpressionSyntax argumentExpression, RefKind refKind, bool allowArglist)
        {
            BindValueKind valueKind =
                refKind == RefKind.None ?
                        BindValueKind.RValue :
                        refKind == RefKind.In ?
                            BindValueKind.ReadonlyRef :
                            BindValueKind.RefOrOut;

            BoundExpression argument;
            if (allowArglist)
            {
                argument = this.BindValueAllowArgList(argumentExpression, diagnostics, valueKind);
            }
            else
            {
                argument = this.BindValue(argumentExpression, diagnostics, valueKind);
            }

            return argument;
        }

#nullable enable
        private void CoerceArguments<TMember>(
            MemberResolutionResult<TMember> methodResult,
            ArrayBuilder<BoundExpression> arguments,
            BindingDiagnosticBag diagnostics,
            TypeSymbol? receiverType,
            RefKind? receiverRefKind,
            uint receiverEscapeScope)
            where TMember : Symbol
        {
            var result = methodResult.Result;

            // Parameter types should be taken from the least overridden member:
            var parameters = methodResult.LeastOverriddenMember.GetParameters();

            for (int arg = 0; arg < arguments.Count; ++arg)
            {
                var kind = result.ConversionForArg(arg);
                BoundExpression argument = arguments[arg];

                if (kind.IsInterpolatedStringHandler)
                {
                    Debug.Assert(argument is BoundUnconvertedInterpolatedString or BoundBinaryOperator { IsUnconvertedInterpolatedStringAddition: true });
                    TypeWithAnnotations parameterTypeWithAnnotations = GetCorrespondingParameterTypeWithAnnotations(ref result, parameters, arg);
                    reportUnsafeIfNeeded(methodResult, diagnostics, argument, parameterTypeWithAnnotations);
                    arguments[arg] = BindInterpolatedStringHandlerInMemberCall(argument, arguments, parameters, ref result, arg, receiverType, receiverRefKind, receiverEscapeScope, diagnostics);
                }
                // https://github.com/dotnet/roslyn/issues/37119 : should we create an (Identity) conversion when the kind is Identity but the types differ?
                else if (!kind.IsIdentity)
                {
                    TypeWithAnnotations parameterTypeWithAnnotations = GetCorrespondingParameterTypeWithAnnotations(ref result, parameters, arg);
                    reportUnsafeIfNeeded(methodResult, diagnostics, argument, parameterTypeWithAnnotations);

                    arguments[arg] = CreateConversion(argument.Syntax, argument, kind, isCast: false, conversionGroupOpt: null, parameterTypeWithAnnotations.Type, diagnostics);
                }
                else if (argument.Kind == BoundKind.OutVariablePendingInference)
                {
                    TypeWithAnnotations parameterTypeWithAnnotations = GetCorrespondingParameterTypeWithAnnotations(ref result, parameters, arg);
                    arguments[arg] = ((OutVariablePendingInference)argument).SetInferredTypeWithAnnotations(parameterTypeWithAnnotations, diagnostics);
                }
                else if (argument.Kind == BoundKind.OutDeconstructVarPendingInference)
                {
                    TypeWithAnnotations parameterTypeWithAnnotations = GetCorrespondingParameterTypeWithAnnotations(ref result, parameters, arg);
                    arguments[arg] = ((OutDeconstructVarPendingInference)argument).SetInferredTypeWithAnnotations(parameterTypeWithAnnotations, this, success: true);
                }
                else if (argument.Kind == BoundKind.DiscardExpression && !argument.HasExpressionType())
                {
                    TypeWithAnnotations parameterTypeWithAnnotations = GetCorrespondingParameterTypeWithAnnotations(ref result, parameters, arg);
                    Debug.Assert(parameterTypeWithAnnotations.HasType);
                    arguments[arg] = ((BoundDiscardExpression)argument).SetInferredTypeWithAnnotations(parameterTypeWithAnnotations);
                }
                else if (argument.NeedsToBeConverted())
                {
                    Debug.Assert(kind.IsIdentity);
                    if (argument is BoundTupleLiteral)
                    {
                        TypeWithAnnotations parameterTypeWithAnnotations = GetCorrespondingParameterTypeWithAnnotations(ref result, parameters, arg);
                        // CreateConversion reports tuple literal name mismatches, and constructs the expected pattern of bound nodes.
                        arguments[arg] = CreateConversion(argument.Syntax, argument, kind, isCast: false, conversionGroupOpt: null, parameterTypeWithAnnotations.Type, diagnostics);
                    }
                    else
                    {
                        arguments[arg] = BindToNaturalType(argument, diagnostics);
                    }
                }
            }

            void reportUnsafeIfNeeded(MemberResolutionResult<TMember> methodResult, BindingDiagnosticBag diagnostics, BoundExpression argument, TypeWithAnnotations parameterTypeWithAnnotations)
            {
                // NOTE: for some reason, dev10 doesn't report this for indexer accesses.
                if (!methodResult.Member.IsIndexer() && !argument.HasAnyErrors && parameterTypeWithAnnotations.Type.IsUnsafe())
                {
                    // CONSIDER: dev10 uses the call syntax, but this seems clearer.
                    ReportUnsafeIfNotAllowed(argument.Syntax, diagnostics);
                    //CONSIDER: Return a bad expression so that HasErrors is true?
                }
            }
        }

        private static ParameterSymbol GetCorrespondingParameter(ref MemberAnalysisResult result, ImmutableArray<ParameterSymbol> parameters, int arg)
        {
            int paramNum = result.ParameterFromArgument(arg);
            return parameters[paramNum];
        }
#nullable disable

        private static TypeWithAnnotations GetCorrespondingParameterTypeWithAnnotations(ref MemberAnalysisResult result, ImmutableArray<ParameterSymbol> parameters, int arg)
        {
            int paramNum = result.ParameterFromArgument(arg);
            var type = parameters[paramNum].TypeWithAnnotations;

            if (paramNum == parameters.Length - 1 && result.Kind == MemberResolutionKind.ApplicableInExpandedForm)
            {
                type = ((ArrayTypeSymbol)type.Type).ElementTypeWithAnnotations;
            }

            return type;
        }

        private BoundExpression BindArrayCreationExpression(ArrayCreationExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            // SPEC begins
            //
            // An array-creation-expression is used to create a new instance of an array-type.
            //
            // array-creation-expression:
            //     new non-array-type[expression-list] rank-specifiersopt array-initializeropt
            //     new array-type array-initializer 
            //     new rank-specifier array-initializer
            //
            // An array creation expression of the first form allocates an array instance of the
            // type that results from deleting each of the individual expressions from the 
            // expression list. For example, the array creation expression new int[10, 20] produces
            // an array instance of type int[,], and the array creation expression new int[10][,]
            // produces an array of type int[][,]. Each expression in the expression list must be of
            // type int, uint, long, or ulong, or implicitly convertible to one or more of these
            // types. The value of each expression determines the length of the corresponding
            // dimension in the newly allocated array instance. Since the length of an array
            // dimension must be nonnegative, it is a compile-time error to have a 
            // constant-expression with a negative value in the expression list.
            //
            // If an array creation expression of the first form includes an array initializer, each
            // expression in the expression list must be a constant and the rank and dimension 
            // lengths specified by the expression list must match those of the array initializer.
            //
            // In an array creation expression of the second or third form, the rank of the
            // specified array type or rank specifier must match that of the array initializer. The
            // individual dimension lengths are inferred from the number of elements in each of the
            // corresponding nesting levels of the array initializer. Thus, the expression new
            // int[,] {{0, 1}, {2, 3}, {4, 5}} exactly corresponds to new int[3, 2] {{0, 1}, {2, 3},
            // {4, 5}}
            //
            // An array creation expression of the third form is referred to as an implicitly typed
            // array creation expression. It is similar to the second form, except that the element
            // type of the array is not explicitly given, but determined as the best common type
            // (7.5.2.14) of the set of expressions in the array initializer. For a multidimensional
            // array, i.e., one where the rank-specifier contains at least one comma, this set
            // comprises all expressions found in nested array-initializers.
            //
            // An array creation expression permits instantiation of an array with elements of an
            // array type, but the elements of such an array must be manually initialized. For
            // example, the statement
            //
            // int[][] a = new int[100][];
            //
            // creates a single-dimensional array with 100 elements of type int[]. The initial value
            // of each element is null. It is not possible for the same array creation expression to
            // also instantiate the sub-arrays, and the statement
            //
            // int[][] a = new int[100][5];		// Error
            //
            // results in a compile-time error. 
            //
            // The following are examples of implicitly typed array creation expressions:
            //
            // var a = new[] { 1, 10, 100, 1000 };                     // int[]
            // var b = new[] { 1, 1.5, 2, 2.5 };                       // double[]
            // var c = new[,] { { "hello", null }, { "world", "!" } }; // string[,]
            // var d = new[] { 1, "one", 2, "two" };                   // Error
            //
            // The last expression causes a compile-time error because neither int nor string is 
            // implicitly convertible to the other, and so there is no best common type. An
            // explicitly typed array creation expression must be used in this case, for example
            // specifying the type to be object[]. Alternatively, one of the elements can be cast to
            // a common base type, which would then become the inferred element type.
            //
            // SPEC ends

            var type = (ArrayTypeSymbol)BindArrayType(node.Type, diagnostics, permitDimensions: true, basesBeingResolved: null, disallowRestrictedTypes: true).Type;

            // CONSIDER: 
            //
            // There may be erroneous rank specifiers in the source code, for example:
            //
            // int y = 123; 
            // int[][] z = new int[10][y];
            //
            // The "10" is legal but the "y" is not. If we are in such a situation we do have the
            // "y" expression syntax stashed away in the syntax tree. However, we do *not* perform
            // semantic analysis. This means that "go to definition" on "y" does not work, and so
            // on. We might consider doing a semantic analysis here (with error suppression; a parse
            // error has already been reported) so that "go to definition" works.

            ArrayBuilder<BoundExpression> sizes = ArrayBuilder<BoundExpression>.GetInstance();
            ArrayRankSpecifierSyntax firstRankSpecifier = node.Type.RankSpecifiers[0];
            bool hasErrors = false;
            foreach (var arg in firstRankSpecifier.Sizes)
            {
                var size = BindArrayDimension(arg, diagnostics, ref hasErrors);
                if (size != null)
                {
                    sizes.Add(size);
                }
                else if (node.Initializer is null && arg == firstRankSpecifier.Sizes[0])
                {
                    Error(diagnostics, ErrorCode.ERR_MissingArraySize, firstRankSpecifier);
                    hasErrors = true;
                }
            }

            // produce errors for additional sizes in the ranks
            for (int additionalRankIndex = 1; additionalRankIndex < node.Type.RankSpecifiers.Count; additionalRankIndex++)
            {
                var rank = node.Type.RankSpecifiers[additionalRankIndex];
                var dimension = rank.Sizes;
                foreach (var arg in dimension)
                {
                    if (arg.Kind() != SyntaxKind.OmittedArraySizeExpression)
                    {
                        var size = BindRValueWithoutTargetType(arg, diagnostics);
                        Error(diagnostics, ErrorCode.ERR_InvalidArray, arg);
                        hasErrors = true;
                        // Capture the invalid sizes for `SemanticModel` and `IOperation`
                        sizes.Add(size);
                    }
                }
            }

            ImmutableArray<BoundExpression> arraySizes = sizes.ToImmutableAndFree();

            return node.Initializer == null
                ? new BoundArrayCreation(node, arraySizes, null, type, hasErrors)
                : BindArrayCreationWithInitializer(diagnostics, node, node.Initializer, type, arraySizes, hasErrors: hasErrors);
        }

        private BoundExpression BindArrayDimension(ExpressionSyntax dimension, BindingDiagnosticBag diagnostics, ref bool hasErrors)
        {
            // These make the parse tree nicer, but they shouldn't actually appear in the bound tree.
            if (dimension.Kind() != SyntaxKind.OmittedArraySizeExpression)
            {
                var size = BindValue(dimension, diagnostics, BindValueKind.RValue);
                if (!size.HasAnyErrors)
                {
                    size = ConvertToArrayIndex(size, diagnostics, allowIndexAndRange: false);
                    if (IsNegativeConstantForArraySize(size))
                    {
                        Error(diagnostics, ErrorCode.ERR_NegativeArraySize, dimension);
                        hasErrors = true;
                    }
                }
                else
                {
                    size = BindToTypeForErrorRecovery(size);
                }

                return size;
            }
            return null;
        }

        private BoundExpression BindImplicitArrayCreationExpression(ImplicitArrayCreationExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            // See BindArrayCreationExpression method above for implicitly typed array creation SPEC.

            InitializerExpressionSyntax initializer = node.Initializer;
            int rank = node.Commas.Count + 1;

            ImmutableArray<BoundExpression> boundInitializerExpressions = BindArrayInitializerExpressions(initializer, diagnostics, dimension: 1, rank: rank);

            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            TypeSymbol bestType = BestTypeInferrer.InferBestType(boundInitializerExpressions, this.Conversions, ref useSiteInfo);
            diagnostics.Add(node, useSiteInfo);

            if ((object)bestType == null || bestType.IsVoidType()) // Dev10 also reports ERR_ImplicitlyTypedArrayNoBestType for void.
            {
                Error(diagnostics, ErrorCode.ERR_ImplicitlyTypedArrayNoBestType, node);
                bestType = CreateErrorType();
            }

            if (bestType.IsRestrictedType())
            {
                // CS0611: Array elements cannot be of type '{0}'
                Error(diagnostics, ErrorCode.ERR_ArrayElementCantBeRefAny, node, bestType);
            }

            // Element type nullability will be inferred in flow analysis and does not need to be set here.
            var arrayType = ArrayTypeSymbol.CreateCSharpArray(Compilation.Assembly, TypeWithAnnotations.Create(bestType), rank);
            return BindArrayCreationWithInitializer(diagnostics, node, initializer, arrayType,
                sizes: ImmutableArray<BoundExpression>.Empty, boundInitExprOpt: boundInitializerExpressions);
        }

        private BoundExpression BindImplicitStackAllocArrayCreationExpression(ImplicitStackAllocArrayCreationExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            InitializerExpressionSyntax initializer = node.Initializer;
            ImmutableArray<BoundExpression> boundInitializerExpressions = BindArrayInitializerExpressions(initializer, diagnostics, dimension: 1, rank: 1);

            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            TypeSymbol bestType = BestTypeInferrer.InferBestType(boundInitializerExpressions, this.Conversions, ref useSiteInfo);
            diagnostics.Add(node, useSiteInfo);

            if ((object)bestType == null || bestType.IsVoidType())
            {
                Error(diagnostics, ErrorCode.ERR_ImplicitlyTypedArrayNoBestType, node);
                bestType = CreateErrorType();
            }

            if (!bestType.IsErrorType())
            {
                CheckManagedAddr(Compilation, bestType, node.Location, diagnostics);
            }

            return BindStackAllocWithInitializer(
                node,
                initializer,
                type: GetStackAllocType(node, TypeWithAnnotations.Create(bestType), diagnostics, out bool hasErrors),
                elementType: bestType,
                sizeOpt: null,
                diagnostics,
                hasErrors: hasErrors,
                boundInitializerExpressions);
        }

        // This method binds all the array initializer expressions.
        // NOTE: It doesn't convert the bound initializer expressions to array's element type.
        // NOTE: This is done separately in ConvertAndBindArrayInitialization method below.
        private ImmutableArray<BoundExpression> BindArrayInitializerExpressions(InitializerExpressionSyntax initializer, BindingDiagnosticBag diagnostics, int dimension, int rank)
        {
            var exprBuilder = ArrayBuilder<BoundExpression>.GetInstance();
            BindArrayInitializerExpressions(initializer, exprBuilder, diagnostics, dimension, rank);
            return exprBuilder.ToImmutableAndFree();
        }

        /// <summary>
        /// This method walks through the array's InitializerExpressionSyntax and binds all the initializer expressions recursively.
        /// NOTE: It doesn't convert the bound initializer expressions to array's element type.
        /// NOTE: This is done separately in ConvertAndBindArrayInitialization method below.
        /// </summary>
        /// <param name="initializer">Initializer Syntax.</param>
        /// <param name="exprBuilder">Bound expression builder.</param>
        /// <param name="diagnostics">Diagnostics.</param>
        /// <param name="dimension">Current array dimension being processed.</param>
        /// <param name="rank">Rank of the array type.</param>
        private void BindArrayInitializerExpressions(InitializerExpressionSyntax initializer, ArrayBuilder<BoundExpression> exprBuilder, BindingDiagnosticBag diagnostics, int dimension, int rank)
        {
            Debug.Assert(rank > 0);
            Debug.Assert(dimension > 0 && dimension <= rank);
            Debug.Assert(exprBuilder != null);

            if (dimension == rank)
            {
                // We are processing the nth dimension of a rank-n array. We expect that these will
                // only be values, not array initializers.
                foreach (var expression in initializer.Expressions)
                {
                    var boundExpression = BindValue(expression, diagnostics, BindValueKind.RValue);
                    exprBuilder.Add(boundExpression);
                }
            }
            else
            {
                // Inductive case; we'd better have another array initializer
                foreach (var expression in initializer.Expressions)
                {
                    if (expression.Kind() == SyntaxKind.ArrayInitializerExpression)
                    {
                        BindArrayInitializerExpressions((InitializerExpressionSyntax)expression, exprBuilder, diagnostics, dimension + 1, rank);
                    }
                    else
                    {
                        // We have non-array initializer expression, but we expected an array initializer expression.

                        var boundExpression = BindValue(expression, diagnostics, BindValueKind.RValue);
                        if ((object)boundExpression.Type == null || !boundExpression.Type.IsErrorType())
                        {
                            if (!boundExpression.HasAnyErrors)
                            {
                                Error(diagnostics, ErrorCode.ERR_ArrayInitializerExpected, expression);
                            }

                            // Wrap the expression with a bound bad expression with error type.
                            boundExpression = BadExpression(
                                expression,
                                LookupResultKind.Empty,
                                ImmutableArray.Create(boundExpression.ExpressionSymbol),
                                ImmutableArray.Create(boundExpression));
                        }

                        exprBuilder.Add(boundExpression);
                    }
                }
            }
        }

        /// <summary>
        /// Given an array of bound initializer expressions, this method converts these bound expressions
        /// to array's element type and generates a BoundArrayInitialization with the converted initializers.
        /// </summary>
        /// <param name="diagnostics">Diagnostics.</param>
        /// <param name="node">Initializer Syntax.</param>
        /// <param name="type">Array type.</param>
        /// <param name="knownSizes">Known array bounds.</param>
        /// <param name="dimension">Current array dimension being processed.</param>
        /// <param name="boundInitExpr">Array of bound initializer expressions.</param>
        /// <param name="boundInitExprIndex">
        /// Index into the array of bound initializer expressions to fetch the next bound expression.
        /// </param>
        /// <returns></returns>
        private BoundArrayInitialization ConvertAndBindArrayInitialization(
            BindingDiagnosticBag diagnostics,
            InitializerExpressionSyntax node,
            ArrayTypeSymbol type,
            int?[] knownSizes,
            int dimension,
            ImmutableArray<BoundExpression> boundInitExpr,
            ref int boundInitExprIndex)
        {
            Debug.Assert(!boundInitExpr.IsDefault);

            ArrayBuilder<BoundExpression> initializers = ArrayBuilder<BoundExpression>.GetInstance();
            if (dimension == type.Rank)
            {
                // We are processing the nth dimension of a rank-n array. We expect that these will
                // only be values, not array initializers.
                TypeSymbol elemType = type.ElementType;
                foreach (var expressionSyntax in node.Expressions)
                {
                    Debug.Assert(boundInitExprIndex >= 0 && boundInitExprIndex < boundInitExpr.Length);

                    BoundExpression boundExpression = boundInitExpr[boundInitExprIndex];
                    boundInitExprIndex++;

                    BoundExpression convertedExpression = GenerateConversionForAssignment(elemType, boundExpression, diagnostics);
                    initializers.Add(convertedExpression);
                }
            }
            else
            {
                // Inductive case; we'd better have another array initializer
                foreach (var expr in node.Expressions)
                {
                    BoundExpression init = null;
                    if (expr.Kind() == SyntaxKind.ArrayInitializerExpression)
                    {
                        init = ConvertAndBindArrayInitialization(diagnostics, (InitializerExpressionSyntax)expr,
                             type, knownSizes, dimension + 1, boundInitExpr, ref boundInitExprIndex);
                    }
                    else
                    {
                        // We have non-array initializer expression, but we expected an array initializer expression.
                        // We have already generated the diagnostics during binding, so just fetch the bound expression.

                        Debug.Assert(boundInitExprIndex >= 0 && boundInitExprIndex < boundInitExpr.Length);

                        init = boundInitExpr[boundInitExprIndex];
                        Debug.Assert(init.HasAnyErrors);
                        Debug.Assert(init.Type.IsErrorType());

                        boundInitExprIndex++;
                    }

                    initializers.Add(init);
                }
            }

            bool hasErrors = false;
            var knownSizeOpt = knownSizes[dimension - 1];

            if (knownSizeOpt == null)
            {
                knownSizes[dimension - 1] = initializers.Count;
            }
            else if (knownSizeOpt != initializers.Count)
            {
                // No need to report an error if the known size is negative
                // since we've already reported CS0248 earlier and it's
                // expected that the number of initializers won't match.
                if (knownSizeOpt >= 0)
                {
                    Error(diagnostics, ErrorCode.ERR_ArrayInitializerIncorrectLength, node, knownSizeOpt.Value);
                    hasErrors = true;
                }
            }

            return new BoundArrayInitialization(node, initializers.ToImmutableAndFree(), hasErrors: hasErrors);
        }

        private BoundArrayInitialization BindArrayInitializerList(
           BindingDiagnosticBag diagnostics,
           InitializerExpressionSyntax node,
           ArrayTypeSymbol type,
           int?[] knownSizes,
           int dimension,
           ImmutableArray<BoundExpression> boundInitExprOpt = default(ImmutableArray<BoundExpression>))
        {
            // Bind the array initializer expressions, if not already bound.
            // NOTE: Initializer expressions might already be bound for implicitly type array creation
            // NOTE: during array's element type inference.
            if (boundInitExprOpt.IsDefault)
            {
                boundInitExprOpt = BindArrayInitializerExpressions(node, diagnostics, dimension, type.Rank);
            }

            // Convert the bound array initializer expressions to array's element type and
            // generate BoundArrayInitialization with the converted initializers.
            int boundInitExprIndex = 0;
            return ConvertAndBindArrayInitialization(diagnostics, node, type, knownSizes, dimension, boundInitExprOpt, ref boundInitExprIndex);
        }

        private BoundArrayInitialization BindUnexpectedArrayInitializer(
            InitializerExpressionSyntax node,
            BindingDiagnosticBag diagnostics,
            ErrorCode errorCode,
            CSharpSyntaxNode errorNode = null)
        {
            var result = BindArrayInitializerList(
                diagnostics,
                node,
                this.Compilation.CreateArrayTypeSymbol(GetSpecialType(SpecialType.System_Object, diagnostics, node)),
                new int?[1],
                dimension: 1);

            if (!result.HasAnyErrors)
            {
                result = new BoundArrayInitialization(node, result.Initializers, hasErrors: true);
            }

            Error(diagnostics, errorCode, errorNode ?? node);
            return result;
        }

        // We could be in the cases
        //
        // (1) int[] x = { a, b }
        // (2) new int[] { a, b }
        // (3) new int[2] { a, b }
        // (4) new [] { a, b }
        //
        // In case (1) there is no creation syntax.
        // In cases (2) and (3) creation syntax is an ArrayCreationExpression.
        // In case (4) creation syntax is an ImplicitArrayCreationExpression.
        //
        // In cases (1), (2) and (4) there are no sizes.
        //
        // The initializer syntax is always provided.
        //
        // If we are in case (3) and sizes are provided then the number of sizes must match the rank
        // of the array type passed in.

        // For case (4), i.e. ImplicitArrayCreationExpression, we must have already bound the
        // initializer expressions for best type inference.
        // These bound expressions are stored in boundInitExprOpt and reused in creating
        // BoundArrayInitialization to avoid binding them twice.

        private BoundArrayCreation BindArrayCreationWithInitializer(
            BindingDiagnosticBag diagnostics,
            ExpressionSyntax creationSyntax,
            InitializerExpressionSyntax initSyntax,
            ArrayTypeSymbol type,
            ImmutableArray<BoundExpression> sizes,
            ImmutableArray<BoundExpression> boundInitExprOpt = default(ImmutableArray<BoundExpression>),
            bool hasErrors = false)
        {
            Debug.Assert(creationSyntax == null ||
                creationSyntax.Kind() == SyntaxKind.ArrayCreationExpression ||
                creationSyntax.Kind() == SyntaxKind.ImplicitArrayCreationExpression);
            Debug.Assert(initSyntax != null);
            Debug.Assert((object)type != null);
            Debug.Assert(boundInitExprOpt.IsDefault || creationSyntax.Kind() == SyntaxKind.ImplicitArrayCreationExpression);

            // NOTE: In error scenarios, it may be the case sizes.Count > type.Rank.
            // For example, new int[1 2] has 2 sizes, but rank 1 (since there are 0 commas).
            int rank = type.Rank;
            int numSizes = sizes.Length;
            int?[] knownSizes = new int?[Math.Max(rank, numSizes)];

            // If there are sizes given and there is an array initializer, then every size must be a
            // constant. (We'll check later that it matches)
            for (int i = 0; i < numSizes; ++i)
            {
                // Here we are being bug-for-bug compatible with C# 4. When you have code like
                // byte[] b = new[uint.MaxValue] { 2 };
                // you might expect an error that says that the number of elements in the initializer does
                // not match the size of the array. But in C# 4 if the constant does not fit into an integer
                // then we confusingly give the error "that's not a constant".
                // NOTE: in the example above, GetIntegerConstantForArraySize is returning null because the
                // size doesn't fit in an int - not because it doesn't match the initializer length.
                var size = sizes[i];
                knownSizes[i] = GetIntegerConstantForArraySize(size);
                if (!size.HasAnyErrors && knownSizes[i] == null)
                {
                    Error(diagnostics, ErrorCode.ERR_ConstantExpected, size.Syntax);
                    hasErrors = true;
                }
            }

            // KnownSizes is further mutated by BindArrayInitializerList as it works out more
            // information about the sizes.
            BoundArrayInitialization initializer = BindArrayInitializerList(diagnostics, initSyntax, type, knownSizes, 1, boundInitExprOpt);

            hasErrors = hasErrors || initializer.HasAnyErrors;

            bool hasCreationSyntax = creationSyntax != null;
            CSharpSyntaxNode nonNullSyntax = (CSharpSyntaxNode)creationSyntax ?? initSyntax;

            // Construct a set of size expressions if we were not given any.
            //
            // It is possible in error scenarios that some of the bounds were not determined. Substitute
            // zeroes for those.
            if (numSizes == 0)
            {
                BoundExpression[] sizeArray = new BoundExpression[rank];
                for (int i = 0; i < rank; i++)
                {
                    sizeArray[i] = new BoundLiteral(
                        nonNullSyntax,
                        ConstantValue.Create(knownSizes[i] ?? 0),
                        GetSpecialType(SpecialType.System_Int32, diagnostics, nonNullSyntax))
                    { WasCompilerGenerated = true };
                }
                sizes = sizeArray.AsImmutableOrNull();
            }
            else if (!hasErrors && rank != numSizes)
            {
                Error(diagnostics, ErrorCode.ERR_BadIndexCount, nonNullSyntax, type.Rank);
                hasErrors = true;
            }

            return new BoundArrayCreation(nonNullSyntax, sizes, initializer, type, hasErrors: hasErrors)
            {
                WasCompilerGenerated = !hasCreationSyntax &&
                    (initSyntax.Parent == null ||
                    initSyntax.Parent.Kind() != SyntaxKind.EqualsValueClause ||
                    ((EqualsValueClauseSyntax)initSyntax.Parent).Value != initSyntax)
            };
        }

        private BoundExpression BindStackAllocArrayCreationExpression(
            StackAllocArrayCreationExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            TypeSyntax typeSyntax = node.Type;

            if (typeSyntax.Kind() != SyntaxKind.ArrayType)
            {
                Error(diagnostics, ErrorCode.ERR_BadStackAllocExpr, typeSyntax);

                return new BoundBadExpression(
                    node,
                    LookupResultKind.NotCreatable, //in this context, anyway
                    ImmutableArray<Symbol>.Empty,
                    ImmutableArray<BoundExpression>.Empty,
                    new PointerTypeSymbol(BindType(typeSyntax, diagnostics)));
            }

            ArrayTypeSyntax arrayTypeSyntax = (ArrayTypeSyntax)typeSyntax;
            var elementTypeSyntax = arrayTypeSyntax.ElementType;
            var arrayType = (ArrayTypeSymbol)BindArrayType(arrayTypeSyntax, diagnostics, permitDimensions: true, basesBeingResolved: null, disallowRestrictedTypes: false).Type;
            var elementType = arrayType.ElementTypeWithAnnotations;

            TypeSymbol type = GetStackAllocType(node, elementType, diagnostics, out bool hasErrors);
            if (!elementType.Type.IsErrorType())
            {
                hasErrors = hasErrors || CheckManagedAddr(Compilation, elementType.Type, elementTypeSyntax.Location, diagnostics);
            }

            SyntaxList<ArrayRankSpecifierSyntax> rankSpecifiers = arrayTypeSyntax.RankSpecifiers;

            if (rankSpecifiers.Count != 1 ||
                rankSpecifiers[0].Sizes.Count != 1)
            {
                // NOTE: Dev10 reported several parse errors here.
                Error(diagnostics, ErrorCode.ERR_BadStackAllocExpr, typeSyntax);

                var builder = ArrayBuilder<BoundExpression>.GetInstance();
                foreach (ArrayRankSpecifierSyntax rankSpecifier in rankSpecifiers)
                {
                    foreach (ExpressionSyntax size in rankSpecifier.Sizes)
                    {
                        if (size.Kind() != SyntaxKind.OmittedArraySizeExpression)
                        {
                            builder.Add(BindExpression(size, BindingDiagnosticBag.Discarded));
                        }
                    }
                }

                return new BoundBadExpression(
                    node,
                    LookupResultKind.Empty,
                    ImmutableArray<Symbol>.Empty,
                    builder.ToImmutableAndFree(),
                    new PointerTypeSymbol(elementType));
            }

            ExpressionSyntax countSyntax = rankSpecifiers[0].Sizes[0];
            BoundExpression count = null;
            if (countSyntax.Kind() != SyntaxKind.OmittedArraySizeExpression)
            {
                count = BindValue(countSyntax, diagnostics, BindValueKind.RValue);
                count = GenerateConversionForAssignment(GetSpecialType(SpecialType.System_Int32, diagnostics, node), count, diagnostics);
                if (IsNegativeConstantForArraySize(count))
                {
                    Error(diagnostics, ErrorCode.ERR_NegativeStackAllocSize, countSyntax);
                    hasErrors = true;
                }
            }
            else if (node.Initializer == null)
            {
                Error(diagnostics, ErrorCode.ERR_MissingArraySize, rankSpecifiers[0]);
                count = BadExpression(countSyntax);
                hasErrors = true;
            }

            return node.Initializer == null
                ? new BoundStackAllocArrayCreation(node, elementType.Type, count, initializerOpt: null, type, hasErrors: hasErrors)
                : BindStackAllocWithInitializer(node, node.Initializer, type, elementType.Type, count, diagnostics, hasErrors);
        }

        private bool ReportBadStackAllocPosition(SyntaxNode node, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node is StackAllocArrayCreationExpressionSyntax || node is ImplicitStackAllocArrayCreationExpressionSyntax);
            bool inLegalPosition = true;

            // If we are using a language version that does not restrict the position of a stackalloc expression, skip that test.
            LanguageVersion requiredVersion = MessageID.IDS_FeatureNestedStackalloc.RequiredVersion();
            if (requiredVersion > Compilation.LanguageVersion)
            {
                inLegalPosition = (IsInMethodBody || IsLocalFunctionsScopeBinder) && node.IsLegalCSharp73SpanStackAllocPosition();
                if (!inLegalPosition)
                {
                    MessageID.IDS_FeatureNestedStackalloc.CheckFeatureAvailability(diagnostics, node, node.GetFirstToken().GetLocation());
                }
            }

            // Check if we're syntactically within a catch or finally clause.
            if (this.Flags.IncludesAny(BinderFlags.InCatchBlock | BinderFlags.InCatchFilter | BinderFlags.InFinallyBlock))
            {
                Error(diagnostics, ErrorCode.ERR_StackallocInCatchFinally, node);
            }

            return inLegalPosition;
        }

        private TypeSymbol GetStackAllocType(SyntaxNode node, TypeWithAnnotations elementTypeWithAnnotations, BindingDiagnosticBag diagnostics, out bool hasErrors)
        {
            var inLegalPosition = ReportBadStackAllocPosition(node, diagnostics);
            hasErrors = !inLegalPosition;
            if (inLegalPosition && !isStackallocTargetTyped(node))
            {
                CheckFeatureAvailability(node, MessageID.IDS_FeatureRefStructs, diagnostics);

                var spanType = GetWellKnownType(WellKnownType.System_Span_T, diagnostics, node);
                return ConstructNamedType(
                    type: spanType,
                    typeSyntax: node.Kind() == SyntaxKind.StackAllocArrayCreationExpression
                        ? ((StackAllocArrayCreationExpressionSyntax)node).Type
                        : node,
                    typeArgumentsSyntax: default,
                    typeArguments: ImmutableArray.Create(elementTypeWithAnnotations),
                    basesBeingResolved: null,
                    diagnostics: diagnostics);
            }

            // We treat the stackalloc as target-typed, so we give it a null type for now.
            return null;

            // Is this a context in which a stackalloc expression could be converted to the corresponding pointer
            // type? The only context that permits it is the initialization of a local variable declaration (when
            // the declaration appears as a statement or as the first part of a for loop).
            static bool isStackallocTargetTyped(SyntaxNode node)
            {
                Debug.Assert(node != null);

                SyntaxNode equalsValueClause = node.Parent;

                if (!equalsValueClause.IsKind(SyntaxKind.EqualsValueClause))
                {
                    return false;
                }

                SyntaxNode variableDeclarator = equalsValueClause.Parent;

                if (!variableDeclarator.IsKind(SyntaxKind.VariableDeclarator))
                {
                    return false;
                }

                SyntaxNode variableDeclaration = variableDeclarator.Parent;
                if (!variableDeclaration.IsKind(SyntaxKind.VariableDeclaration))
                {
                    return false;
                }

                return
                    variableDeclaration.Parent.IsKind(SyntaxKind.LocalDeclarationStatement) ||
                    variableDeclaration.Parent.IsKind(SyntaxKind.ForStatement);
            }
        }

        private BoundExpression BindStackAllocWithInitializer(
            SyntaxNode node,
            InitializerExpressionSyntax initSyntax,
            TypeSymbol type,
            TypeSymbol elementType,
            BoundExpression sizeOpt,
            BindingDiagnosticBag diagnostics,
            bool hasErrors,
            ImmutableArray<BoundExpression> boundInitExprOpt = default)
        {
            Debug.Assert(node.IsKind(SyntaxKind.ImplicitStackAllocArrayCreationExpression) || node.IsKind(SyntaxKind.StackAllocArrayCreationExpression));

            if (boundInitExprOpt.IsDefault)
            {
                boundInitExprOpt = BindArrayInitializerExpressions(initSyntax, diagnostics, dimension: 1, rank: 1);
            }

            boundInitExprOpt = boundInitExprOpt.SelectAsArray((expr, t) => GenerateConversionForAssignment(t.elementType, expr, t.diagnostics), (elementType, diagnostics));

            if (sizeOpt != null)
            {
                if (!sizeOpt.HasAnyErrors)
                {
                    int? constantSizeOpt = GetIntegerConstantForArraySize(sizeOpt);
                    if (constantSizeOpt == null)
                    {
                        Error(diagnostics, ErrorCode.ERR_ConstantExpected, sizeOpt.Syntax);
                        hasErrors = true;
                    }
                    else if (boundInitExprOpt.Length != constantSizeOpt)
                    {
                        Error(diagnostics, ErrorCode.ERR_ArrayInitializerIncorrectLength, node, constantSizeOpt.Value);
                        hasErrors = true;
                    }
                }
            }
            else
            {
                sizeOpt = new BoundLiteral(
                        node,
                        ConstantValue.Create(boundInitExprOpt.Length),
                        GetSpecialType(SpecialType.System_Int32, diagnostics, node))
                { WasCompilerGenerated = true };
            }

            return new BoundStackAllocArrayCreation(node, elementType, sizeOpt, new BoundArrayInitialization(initSyntax, boundInitExprOpt), type, hasErrors);
        }

        private static int? GetIntegerConstantForArraySize(BoundExpression expression)
        {
            // If the bound could have been converted to int, then it was.  If it could not have been
            // converted to int, and it was a constant, then it was out of range.

            Debug.Assert(expression != null);
            if (expression.HasAnyErrors)
            {
                return null;
            }

            var constantValue = expression.ConstantValue;

            if (constantValue == null || constantValue.IsBad || expression.Type.SpecialType != SpecialType.System_Int32)
            {
                return null;
            }

            return constantValue.Int32Value;
        }

        private static bool IsNegativeConstantForArraySize(BoundExpression expression)
        {
            Debug.Assert(expression != null);

            if (expression.HasAnyErrors)
            {
                return false;
            }

            var constantValue = expression.ConstantValue;
            if (constantValue == null || constantValue.IsBad)
            {
                return false;
            }

            var type = expression.Type.SpecialType;
            if (type == SpecialType.System_Int32)
            {
                return constantValue.Int32Value < 0;
            }

            if (type == SpecialType.System_Int64)
            {
                return constantValue.Int64Value < 0;
            }

            // By the time we get here we definitely have int, long, uint or ulong.  Obviously the
            // latter two are never negative.
            Debug.Assert(type == SpecialType.System_UInt32 || type == SpecialType.System_UInt64);

            return false;
        }

        /// <summary>
        /// Bind the (implicit or explicit) constructor initializer of a constructor symbol (in source).
        /// </summary>
        /// <param name="initializerArgumentListOpt">
        /// Null for implicit, 
        /// <see cref="ConstructorInitializerSyntax.ArgumentList"/>, or 
        /// <see cref="PrimaryConstructorBaseTypeSyntax.ArgumentList"/> for explicit.</param>
        /// <param name="constructor">Constructor containing the initializer.</param>
        /// <param name="diagnostics">Accumulates errors (e.g. unable to find constructor to invoke).</param>
        /// <returns>A bound expression for the constructor initializer call.</returns>
        /// <remarks>
        /// This method should be kept consistent with Compiler.BindConstructorInitializer (e.g. same error codes).
        /// </remarks>
        internal BoundExpression BindConstructorInitializer(
            ArgumentListSyntax initializerArgumentListOpt,
            MethodSymbol constructor,
            BindingDiagnosticBag diagnostics)
        {
            Binder argumentListBinder = null;

            if (initializerArgumentListOpt != null)
            {
                argumentListBinder = this.GetBinder(initializerArgumentListOpt);
            }

            var result = (argumentListBinder ?? this).BindConstructorInitializerCore(initializerArgumentListOpt, constructor, diagnostics);

            if (argumentListBinder != null)
            {
                // This code is reachable only for speculative SemanticModel.
                Debug.Assert(argumentListBinder.IsSemanticModelBinder);
                result = argumentListBinder.WrapWithVariablesIfAny(initializerArgumentListOpt, result);
            }

            return result;
        }

        private BoundExpression BindConstructorInitializerCore(
            ArgumentListSyntax initializerArgumentListOpt,
            MethodSymbol constructor,
            BindingDiagnosticBag diagnostics)
        {
            // Either our base type is not object, or we have an initializer syntax, or both. We're going to
            // need to do overload resolution on the set of constructors of the base type, either on
            // the provided initializer syntax, or on an implicit ": base()" syntax.

            // SPEC ERROR: The specification states that if you have the situation 
            // SPEC ERROR: class B { ... } class D1 : B {} then the default constructor
            // SPEC ERROR: generated for D1 must call an accessible *parameterless* constructor
            // SPEC ERROR: in B. However, it also states that if you have 
            // SPEC ERROR: class B { ... } class D2 : B { D2() {} }  or
            // SPEC ERROR: class B { ... } class D3 : B { D3() : base() {} }  then
            // SPEC ERROR: the compiler performs *overload resolution* to determine
            // SPEC ERROR: which accessible constructor of B is called. Since B might have
            // SPEC ERROR: a ctor with all optional parameters, overload resolution might
            // SPEC ERROR: succeed even if there is no parameterless constructor. This
            // SPEC ERROR: is unintentionally inconsistent, and the native compiler does not
            // SPEC ERROR: implement this behavior. Rather, we should say in the spec that
            // SPEC ERROR: if there is no ctor in D1, then a ctor is created for you exactly
            // SPEC ERROR: as though you'd said "D1() : base() {}". 
            // SPEC ERROR: This is what we now do in Roslyn.

            Debug.Assert((object)constructor != null);
            Debug.Assert(constructor.MethodKind == MethodKind.Constructor ||
                constructor.MethodKind == MethodKind.StaticConstructor); // error scenario: constructor initializer on static constructor
            Debug.Assert(diagnostics != null);

            NamedTypeSymbol containingType = constructor.ContainingType;

            // Structs and enums do not have implicit constructor initializers.
            if ((containingType.TypeKind == TypeKind.Enum || containingType.TypeKind == TypeKind.Struct) && initializerArgumentListOpt == null)
            {
                return null;
            }

            AnalyzedArguments analyzedArguments = AnalyzedArguments.GetInstance();
            try
            {
                TypeSymbol constructorReturnType = constructor.ReturnType;
                Debug.Assert(constructorReturnType.IsVoidType()); //true of all constructors
                NamedTypeSymbol baseType = containingType.BaseTypeNoUseSiteDiagnostics;

                // Get the bound arguments and the argument names.
                // : this(__arglist()) is legal
                if (initializerArgumentListOpt != null)
                {
                    this.BindArgumentsAndNames(initializerArgumentListOpt, diagnostics, analyzedArguments, allowArglist: true);
                }

                NamedTypeSymbol initializerType = containingType;

                bool isBaseConstructorInitializer = initializerArgumentListOpt == null ||
                                                    initializerArgumentListOpt.Parent.Kind() != SyntaxKind.ThisConstructorInitializer;

                if (isBaseConstructorInitializer)
                {
                    initializerType = initializerType.BaseTypeNoUseSiteDiagnostics;

                    // Soft assert: we think this is the case, and we're asserting to catch scenarios that violate our expectations
                    Debug.Assert((object)initializerType != null ||
                    containingType.SpecialType == SpecialType.System_Object ||
                        containingType.IsInterface);

                    if ((object)initializerType == null || containingType.SpecialType == SpecialType.System_Object) //e.g. when defining System.Object in source
                    {
                        // If the constructor initializer is implicit and there is no base type, we're done.
                        // Otherwise, if the constructor initializer is explicit, we're in an error state.
                        if (initializerArgumentListOpt == null)
                        {
                            return null;
                        }
                        else
                        {
                            diagnostics.Add(ErrorCode.ERR_ObjectCallingBaseConstructor, constructor.Locations[0], containingType);
                            return new BoundBadExpression(
                                syntax: initializerArgumentListOpt.Parent,
                                resultKind: LookupResultKind.Empty,
                                symbols: ImmutableArray<Symbol>.Empty,
                                childBoundNodes: BuildArgumentsForErrorRecovery(analyzedArguments),
                                type: constructorReturnType);
                        }
                    }
                    else if (initializerArgumentListOpt != null && containingType.TypeKind == TypeKind.Struct)
                    {
                        diagnostics.Add(ErrorCode.ERR_StructWithBaseConstructorCall, constructor.Locations[0], containingType);
                        return new BoundBadExpression(
                            syntax: initializerArgumentListOpt.Parent,
                            resultKind: LookupResultKind.Empty,
                            symbols: ImmutableArray<Symbol>.Empty, //CONSIDER: we could look for a matching constructor on System.ValueType
                            childBoundNodes: BuildArgumentsForErrorRecovery(analyzedArguments),
                            type: constructorReturnType);
                    }
                }
                else
                {
                    Debug.Assert(initializerArgumentListOpt.Parent.Kind() == SyntaxKind.ThisConstructorInitializer);
                }

                CSharpSyntaxNode nonNullSyntax;
                Location errorLocation;
                bool enableCallerInfo;

                switch (initializerArgumentListOpt?.Parent)
                {
                    case ConstructorInitializerSyntax initializerSyntax:
                        nonNullSyntax = initializerSyntax;
                        errorLocation = initializerSyntax.ThisOrBaseKeyword.GetLocation();
                        enableCallerInfo = true;
                        break;

                    case PrimaryConstructorBaseTypeSyntax baseWithArguments:
                        nonNullSyntax = baseWithArguments;
                        errorLocation = initializerArgumentListOpt.GetLocation();
                        enableCallerInfo = true;
                        break;

                    default:
                        // Note: use syntax node of constructor with initializer, not constructor invoked by initializer (i.e. methodResolutionResult).
                        nonNullSyntax = constructor.GetNonNullSyntaxNode();
                        errorLocation = constructor.Locations[0];
                        enableCallerInfo = false;
                        break;
                }

                if (initializerArgumentListOpt != null && analyzedArguments.HasDynamicArgument)
                {
                    diagnostics.Add(ErrorCode.ERR_NoDynamicPhantomOnBaseCtor, errorLocation);

                    return new BoundBadExpression(
                            syntax: initializerArgumentListOpt.Parent,
                            resultKind: LookupResultKind.Empty,
                            symbols: ImmutableArray<Symbol>.Empty, //CONSIDER: we could look for a matching constructor on System.ValueType
                            childBoundNodes: BuildArgumentsForErrorRecovery(analyzedArguments),
                            type: constructorReturnType);
                }

                BoundExpression receiver = ThisReference(nonNullSyntax, initializerType, wasCompilerGenerated: true);

                MemberResolutionResult<MethodSymbol> memberResolutionResult;
                ImmutableArray<MethodSymbol> candidateConstructors;
                bool found = TryPerformConstructorOverloadResolution(
                                    initializerType,
                                    analyzedArguments,
                                    WellKnownMemberNames.InstanceConstructorName,
                                    errorLocation,
                                    false, // Don't suppress result diagnostics
                                    diagnostics,
                                    out memberResolutionResult,
                                    out candidateConstructors,
                                    allowProtectedConstructorsOfBaseType: true);
                MethodSymbol resultMember = memberResolutionResult.Member;

                validateRecordCopyConstructor(constructor, baseType, resultMember, errorLocation, diagnostics);

                if (found)
                {
                    bool hasErrors = false;

                    if (resultMember == constructor)
                    {
                        Debug.Assert(initializerType.IsErrorType() ||
                            (initializerArgumentListOpt != null && initializerArgumentListOpt.Parent.Kind() == SyntaxKind.ThisConstructorInitializer));
                        diagnostics.Add(ErrorCode.ERR_RecursiveConstructorCall,
                                        errorLocation,
                                        constructor);

                        hasErrors = true; // prevent recursive constructor from being emitted
                    }
                    else if (resultMember.HasUnsafeParameter())
                    {
                        // What if some of the arguments are implicit?  Dev10 reports unsafe errors
                        // if the implied argument would have an unsafe type.  We need to check
                        // the parameters explicitly, since there won't be bound nodes for the implied
                        // arguments until lowering.

                        // Don't worry about double reporting (i.e. for both the argument and the parameter)
                        // because only one unsafe diagnostic is allowed per scope - the others are suppressed.
                        hasErrors = ReportUnsafeIfNotAllowed(errorLocation, diagnostics);
                    }

                    ReportDiagnosticsIfObsolete(diagnostics, resultMember, nonNullSyntax, hasBaseReceiver: isBaseConstructorInitializer);

                    var expanded = memberResolutionResult.Result.Kind == MemberResolutionKind.ApplicableInExpandedForm;
                    var argsToParamsOpt = memberResolutionResult.Result.ArgsToParamsOpt;
                    BindDefaultArguments(nonNullSyntax, resultMember.Parameters, analyzedArguments.Arguments, analyzedArguments.RefKinds, ref argsToParamsOpt, out var defaultArguments, expanded, enableCallerInfo, diagnostics);

                    var arguments = analyzedArguments.Arguments.ToImmutable();
                    var refKinds = analyzedArguments.RefKinds.ToImmutableOrNull();
                    if (!hasErrors)
                    {
                        hasErrors = !CheckInvocationArgMixing(
                            nonNullSyntax,
                            resultMember,
                            receiver,
                            resultMember.Parameters,
                            arguments,
                            argsToParamsOpt,
                            this.LocalScopeDepth,
                            diagnostics);
                    }

                    return new BoundCall(
                        nonNullSyntax,
                        receiver,
                        resultMember,
                        arguments,
                        analyzedArguments.GetNames(),
                        refKinds,
                        isDelegateCall: false,
                        expanded,
                        invokedAsExtensionMethod: false,
                        argsToParamsOpt: argsToParamsOpt,
                        defaultArguments: defaultArguments,
                        resultKind: LookupResultKind.Viable,
                        type: constructorReturnType,
                        hasErrors: hasErrors)
                    { WasCompilerGenerated = initializerArgumentListOpt == null };
                }
                else
                {
                    var result = CreateBadCall(
                        node: nonNullSyntax,
                        name: WellKnownMemberNames.InstanceConstructorName,
                        receiver: receiver,
                        methods: candidateConstructors,
                        resultKind: LookupResultKind.OverloadResolutionFailure,
                        typeArgumentsWithAnnotations: ImmutableArray<TypeWithAnnotations>.Empty,
                        analyzedArguments: analyzedArguments,
                        invokedAsExtensionMethod: false,
                        isDelegate: false);
                    result.WasCompilerGenerated = initializerArgumentListOpt == null;
                    return result;
                }
            }
            finally
            {
                analyzedArguments.Free();
            }

            static void validateRecordCopyConstructor(MethodSymbol constructor, NamedTypeSymbol baseType, MethodSymbol resultMember, Location errorLocation, BindingDiagnosticBag diagnostics)
            {
                if (IsUserDefinedRecordCopyConstructor(constructor))
                {
                    if (baseType.SpecialType == SpecialType.System_Object)
                    {
                        if (resultMember is null || resultMember.ContainingType.SpecialType != SpecialType.System_Object)
                        {
                            // Record deriving from object must use `base()`, not `this()`
                            diagnostics.Add(ErrorCode.ERR_CopyConstructorMustInvokeBaseCopyConstructor, errorLocation);
                        }

                        return;
                    }

                    // Unless the base type is 'object', the constructor should invoke a base type copy constructor
                    if (resultMember is null || !SynthesizedRecordCopyCtor.HasCopyConstructorSignature(resultMember))
                    {
                        diagnostics.Add(ErrorCode.ERR_CopyConstructorMustInvokeBaseCopyConstructor, errorLocation);
                    }
                }
            }
        }

        internal static bool IsUserDefinedRecordCopyConstructor(MethodSymbol constructor)
        {
            return constructor.ContainingType is SourceNamedTypeSymbol sourceType &&
                sourceType.IsRecord &&
                constructor is not SynthesizedRecordConstructor &&
                SynthesizedRecordCopyCtor.HasCopyConstructorSignature(constructor);
        }

        private BoundExpression BindImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            var arguments = AnalyzedArguments.GetInstance();
            BindArgumentsAndNames(node.ArgumentList, diagnostics, arguments, allowArglist: true);
            var result = new BoundUnconvertedObjectCreationExpression(
                node,
                arguments.Arguments.ToImmutable(),
                arguments.Names.ToImmutableOrNull(),
                arguments.RefKinds.ToImmutableOrNull(),
                node.Initializer);
            arguments.Free();
            return result;
        }

        protected BoundExpression BindObjectCreationExpression(ObjectCreationExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            var typeWithAnnotations = BindType(node.Type, diagnostics);
            var type = typeWithAnnotations.Type;
            var originalType = type;

            if (typeWithAnnotations.NullableAnnotation.IsAnnotated() && !type.IsNullableType())
            {
                diagnostics.Add(ErrorCode.ERR_AnnotationDisallowedInObjectCreation, node.Location, type);
            }

            switch (type.TypeKind)
            {
                case TypeKind.Struct:
                case TypeKind.Class:
                case TypeKind.Enum:
                case TypeKind.Error:
                    return BindClassCreationExpression(node, (NamedTypeSymbol)type, GetName(node.Type), diagnostics, originalType);

                case TypeKind.Delegate:
                    return BindDelegateCreationExpression(node, (NamedTypeSymbol)type, diagnostics);

                case TypeKind.Interface:
                    return BindInterfaceCreationExpression(node, (NamedTypeSymbol)type, diagnostics);

                case TypeKind.TypeParameter:
                    return BindTypeParameterCreationExpression(node, (TypeParameterSymbol)type, diagnostics);

                case TypeKind.Submission:
                    // script class is synthesized and should not be used as a type of a new expression:
                    throw ExceptionUtilities.UnexpectedValue(type.TypeKind);

                case TypeKind.Pointer:
                case TypeKind.FunctionPointer:
                    type = new ExtendedErrorTypeSymbol(type, LookupResultKind.NotCreatable,
                        diagnostics.Add(ErrorCode.ERR_UnsafeTypeInObjectCreation, node.Location, type));
                    goto case TypeKind.Class;

                case TypeKind.Dynamic:
                // we didn't find any type called "dynamic" so we are using the builtin dynamic type, which has no constructors:
                case TypeKind.Array:
                    // ex: new ref[]
                    type = new ExtendedErrorTypeSymbol(type, LookupResultKind.NotCreatable,
                        diagnostics.Add(ErrorCode.ERR_InvalidObjectCreation, node.Type.Location, type));
                    goto case TypeKind.Class;

                default:
                    throw ExceptionUtilities.UnexpectedValue(type.TypeKind);
            }
        }

        private BoundExpression BindDelegateCreationExpression(ObjectCreationExpressionSyntax node, NamedTypeSymbol type, BindingDiagnosticBag diagnostics)
        {
            AnalyzedArguments analyzedArguments = AnalyzedArguments.GetInstance();
            BindArgumentsAndNames(node.ArgumentList, diagnostics, analyzedArguments, isDelegateCreation: true);
            var result = BindDelegateCreationExpression(node, type, analyzedArguments, node.Initializer, diagnostics);
            analyzedArguments.Free();
            return result;
        }

        private BoundExpression BindDelegateCreationExpression(SyntaxNode node, NamedTypeSymbol type, AnalyzedArguments analyzedArguments, InitializerExpressionSyntax initializerOpt, BindingDiagnosticBag diagnostics)
        {
            bool hasErrors = false;
            if (analyzedArguments.HasErrors)
            {
                // Let's skip this part of further error checking without marking hasErrors = true here,
                // as the argument could be an unbound lambda, and the error could come from inside.
                // We'll check analyzedArguments.HasErrors again after we find if this is not the case.
            }
            else if (analyzedArguments.Arguments.Count == 0)
            {
                diagnostics.Add(ErrorCode.ERR_BadCtorArgCount, node.Location, type, 0);
                hasErrors = true;
            }
            else if (analyzedArguments.Names.Count != 0 || analyzedArguments.RefKinds.Count != 0 || analyzedArguments.Arguments.Count != 1)
            {
                // Use a smaller span that excludes the parens.
                var argSyntax = analyzedArguments.Arguments[0].Syntax;
                var start = argSyntax.SpanStart;
                var end = analyzedArguments.Arguments[analyzedArguments.Arguments.Count - 1].Syntax.Span.End;
                var errorSpan = new TextSpan(start, end - start);

                var loc = new SourceLocation(argSyntax.SyntaxTree, errorSpan);

                diagnostics.Add(ErrorCode.ERR_MethodNameExpected, loc);
                hasErrors = true;
            }

            if (initializerOpt != null)
            {
                Error(diagnostics, ErrorCode.ERR_ObjectOrCollectionInitializerWithDelegateCreation, node);
                hasErrors = true;
            }

            BoundExpression argument = analyzedArguments.Arguments.Count >= 1 ? BindToNaturalType(analyzedArguments.Arguments[0], diagnostics) : null;

            if (hasErrors)
            {
                // skip the rest of this binding
            }

            // There are four cases for a delegate creation expression (7.6.10.5):
            // 1. An anonymous function is treated as a conversion from the anonymous function to the delegate type.
            else if (argument is UnboundLambda unboundLambda)
            {
                // analyzedArguments.HasErrors could be true,
                // but here the argument is an unbound lambda, the error comes from inside
                // eg: new Action<int>(x => x.)
                // We should try to bind it anyway in order for intellisense to work.

                CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                var conversion = this.Conversions.ClassifyConversionFromExpression(unboundLambda, type, ref useSiteInfo);
                diagnostics.Add(node, useSiteInfo);
                // Attempting to make the conversion caches the diagnostics and the bound state inside
                // the unbound lambda. Fetch the result from the cache.
                Debug.Assert(!type.IsGenericOrNonGenericExpressionType(out _));
                BoundLambda boundLambda = unboundLambda.Bind(type, isExpressionTree: false);

                if (!conversion.IsImplicit || !conversion.IsValid)
                {
                    GenerateImplicitConversionError(diagnostics, unboundLambda.Syntax, conversion, unboundLambda, type);
                }
                else
                {
                    // We're not going to produce an error, but it is possible that the conversion from
                    // the lambda to the delegate type produced a warning, which we have not reported.
                    // Instead, we've cached it in the bound lambda. Report it now.
                    diagnostics.AddRange(boundLambda.Diagnostics);
                }

                // Just stuff the bound lambda into the delegate creation expression. When we lower the lambda to
                // its method form we will rewrite this expression to refer to the method.

                return new BoundDelegateCreationExpression(node, boundLambda, methodOpt: null, isExtensionMethod: false, type: type, hasErrors: !conversion.IsImplicit);
            }

            else if (analyzedArguments.HasErrors)
            {
                // There is no hope, skip.
            }

            // 2. A method group
            else if (argument.Kind == BoundKind.MethodGroup)
            {
                Conversion conversion;
                BoundMethodGroup methodGroup = (BoundMethodGroup)argument;
                hasErrors = MethodGroupConversionDoesNotExistOrHasErrors(methodGroup, type, node.Location, diagnostics, out conversion);
                methodGroup = FixMethodGroupWithTypeOrValue(methodGroup, conversion, diagnostics);
                return new BoundDelegateCreationExpression(node, methodGroup, conversion.Method, conversion.IsExtensionMethod, type, hasErrors);
            }

            else if ((object)argument.Type == null)
            {
                diagnostics.Add(ErrorCode.ERR_MethodNameExpected, argument.Syntax.Location);
            }

            // 3. A value of the compile-time type dynamic (which is dynamically case 4), or
            else if (argument.HasDynamicType())
            {
                return new BoundDelegateCreationExpression(node, argument, methodOpt: null, isExtensionMethod: false, type: type);
            }

            // 4. A delegate type.
            else if (argument.Type.TypeKind == TypeKind.Delegate)
            {
                var sourceDelegate = (NamedTypeSymbol)argument.Type;
                MethodGroup methodGroup = MethodGroup.GetInstance();
                try
                {
                    if (ReportDelegateInvokeUseSiteDiagnostic(diagnostics, argument.Type, node: node))
                    {
                        // We want failed "new" expression to use the constructors as their symbols.
                        return new BoundBadExpression(node, LookupResultKind.NotInvocable, StaticCast<Symbol>.From(type.InstanceConstructors), ImmutableArray.Create(argument), type);
                    }

                    methodGroup.PopulateWithSingleMethod(argument, sourceDelegate.DelegateInvokeMethod);
                    CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                    Conversion conv = Conversions.MethodGroupConversion(argument.Syntax, methodGroup, type, ref useSiteInfo);
                    diagnostics.Add(node, useSiteInfo);
                    if (!conv.Exists)
                    {
                        var boundMethodGroup = new BoundMethodGroup(
                            argument.Syntax, default, WellKnownMemberNames.DelegateInvokeName, ImmutableArray.Create(sourceDelegate.DelegateInvokeMethod),
                            sourceDelegate.DelegateInvokeMethod, null, BoundMethodGroupFlags.None, functionType: null, argument, LookupResultKind.Viable);
                        if (!Conversions.ReportDelegateOrFunctionPointerMethodGroupDiagnostics(this, boundMethodGroup, type, diagnostics))
                        {
                            // If we could not produce a more specialized diagnostic, we report
                            // No overload for '{0}' matches delegate '{1}'
                            diagnostics.Add(ErrorCode.ERR_MethDelegateMismatch, node.Location,
                                sourceDelegate.DelegateInvokeMethod,
                                type);
                        }
                    }
                    else
                    {
                        Debug.Assert(!conv.IsExtensionMethod);
                        Debug.Assert(conv.IsValid); // i.e. if it exists, then it is valid.

                        if (!this.MethodGroupConversionHasErrors(argument.Syntax, conv, argument, conv.IsExtensionMethod, isAddressOf: false, type, diagnostics))
                        {
                            // we do not place the "Invoke" method in the node, indicating that it did not appear in source.
                            return new BoundDelegateCreationExpression(node, argument, methodOpt: null, isExtensionMethod: false, type: type);
                        }
                    }
                }
                finally
                {
                    methodGroup.Free();
                }
            }

            // Not a valid delegate creation expression
            else
            {
                diagnostics.Add(ErrorCode.ERR_MethodNameExpected, argument.Syntax.Location);
            }

            // Note that we want failed "new" expression to use the constructors as their symbols.
            var childNodes = BuildArgumentsForErrorRecovery(analyzedArguments);
            return new BoundBadExpression(node, LookupResultKind.OverloadResolutionFailure, StaticCast<Symbol>.From(type.InstanceConstructors), childNodes, type);
        }

        private BoundExpression BindClassCreationExpression(ObjectCreationExpressionSyntax node, NamedTypeSymbol type, string typeName, BindingDiagnosticBag diagnostics, TypeSymbol initializerType = null)
        {
            // Get the bound arguments and the argument names.
            AnalyzedArguments analyzedArguments = AnalyzedArguments.GetInstance();
            try
            {
                // new C(__arglist()) is legal
                BindArgumentsAndNames(node.ArgumentList, diagnostics, analyzedArguments, allowArglist: true);

                // No point in performing overload resolution if the type is static or a tuple literal.  
                // Just return a bad expression containing the arguments.
                if (type.IsStatic)
                {
                    diagnostics.Add(ErrorCode.ERR_InstantiatingStaticClass, node.Location, type);
                    return MakeBadExpressionForObjectCreation(node, type, analyzedArguments, diagnostics);
                }
                else if (node.Type.Kind() == SyntaxKind.TupleType)
                {
                    diagnostics.Add(ErrorCode.ERR_NewWithTupleTypeSyntax, node.Type.GetLocation());
                    return MakeBadExpressionForObjectCreation(node, type, analyzedArguments, diagnostics);
                }

                return BindClassCreationExpression(node, typeName, node.Type, type, analyzedArguments, diagnostics, node.Initializer, initializerType);
            }
            finally
            {
                analyzedArguments.Free();
            }
        }

#nullable enable
        /// <summary>
        /// Helper method to create a synthesized constructor invocation.
        /// </summary>
        private BoundExpression MakeConstructorInvocation(
            NamedTypeSymbol type,
            ArrayBuilder<BoundExpression> arguments,
            ArrayBuilder<RefKind> refKinds,
            SyntaxNode node,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(type.TypeKind is TypeKind.Class or TypeKind.Struct);
            var analyzedArguments = AnalyzedArguments.GetInstance();

            try
            {
                analyzedArguments.Arguments.AddRange(arguments);
                analyzedArguments.RefKinds.AddRange(refKinds);

                if (type.IsStatic)
                {
                    diagnostics.Add(ErrorCode.ERR_InstantiatingStaticClass, node.Location, type);
                    return MakeBadExpressionForObjectCreation(node, type, analyzedArguments, initializerOpt: null, typeSyntax: null, diagnostics, wasCompilerGenerated: true);
                }

                var creation = BindClassCreationExpression(node, type.Name, node, type, analyzedArguments, diagnostics);
                creation.WasCompilerGenerated = true;
                return creation;
            }
            finally
            {
                analyzedArguments.Free();
            }
        }

        internal BoundExpression BindObjectCreationForErrorRecovery(BoundUnconvertedObjectCreationExpression node, BindingDiagnosticBag diagnostics)
        {
            var arguments = AnalyzedArguments.GetInstance(node.Arguments, node.ArgumentRefKindsOpt, node.ArgumentNamesOpt);
            var result = MakeBadExpressionForObjectCreation(node.Syntax, CreateErrorType(), arguments, node.InitializerOpt, typeSyntax: node.Syntax, diagnostics);
            arguments.Free();
            return result;
        }

        private BoundExpression MakeBadExpressionForObjectCreation(ObjectCreationExpressionSyntax node, TypeSymbol type, AnalyzedArguments analyzedArguments, BindingDiagnosticBag diagnostics, bool wasCompilerGenerated = false)
        {
            return MakeBadExpressionForObjectCreation(node, type, analyzedArguments, node.Initializer, node.Type, diagnostics, wasCompilerGenerated);
        }

        /// <param name="typeSyntax">Shouldn't be null if <paramref name="initializerOpt"/> is not null.</param>
        private BoundExpression MakeBadExpressionForObjectCreation(SyntaxNode node, TypeSymbol type, AnalyzedArguments analyzedArguments, InitializerExpressionSyntax? initializerOpt, SyntaxNode? typeSyntax, BindingDiagnosticBag diagnostics, bool wasCompilerGenerated = false)
        {
            var children = ArrayBuilder<BoundExpression>.GetInstance();
            children.AddRange(BuildArgumentsForErrorRecovery(analyzedArguments));
            if (initializerOpt != null)
            {
                Debug.Assert(typeSyntax is not null);
                var boundInitializer = BindInitializerExpression(syntax: initializerOpt,
                                                                 type: type,
                                                                 typeSyntax: typeSyntax,
                                                                 isForNewInstance: true,
                                                                 diagnostics: diagnostics);
                children.Add(boundInitializer);
            }

            return new BoundBadExpression(node, LookupResultKind.NotCreatable, ImmutableArray.Create<Symbol?>(type), children.ToImmutableAndFree(), type) { WasCompilerGenerated = wasCompilerGenerated };
        }
#nullable disable

        private BoundObjectInitializerExpressionBase BindInitializerExpression(
            InitializerExpressionSyntax syntax,
            TypeSymbol type,
            SyntaxNode typeSyntax,
            bool isForNewInstance,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(syntax != null);
            Debug.Assert((object)type != null);

            var implicitReceiver = new BoundObjectOrCollectionValuePlaceholder(typeSyntax, isForNewInstance, type) { WasCompilerGenerated = true };

            switch (syntax.Kind())
            {
                case SyntaxKind.ObjectInitializerExpression:
                    // Uses a special binder to produce customized diagnostics for the object initializer
                    return BindObjectInitializerExpression(
                        syntax, type, diagnostics, implicitReceiver, useObjectInitDiagnostics: true);

                case SyntaxKind.WithInitializerExpression:
                    return BindObjectInitializerExpression(
                        syntax, type, diagnostics, implicitReceiver, useObjectInitDiagnostics: false);

                case SyntaxKind.CollectionInitializerExpression:
                    return BindCollectionInitializerExpression(syntax, type, diagnostics, implicitReceiver);

                default:
                    throw ExceptionUtilities.Unreachable;
            }
        }

        private BoundExpression BindInitializerExpressionOrValue(
            ExpressionSyntax syntax,
            TypeSymbol type,
            SyntaxNode typeSyntax,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(syntax != null);
            Debug.Assert((object)type != null);

            switch (syntax.Kind())
            {
                case SyntaxKind.ObjectInitializerExpression:
                case SyntaxKind.CollectionInitializerExpression:
                    Debug.Assert(syntax.Parent.Parent.Kind() != SyntaxKind.WithInitializerExpression);
                    return BindInitializerExpression((InitializerExpressionSyntax)syntax, type, typeSyntax, isForNewInstance: false, diagnostics);
                default:
                    return BindValue(syntax, diagnostics, BindValueKind.RValue);
            }
        }

        private BoundObjectInitializerExpression BindObjectInitializerExpression(
            InitializerExpressionSyntax initializerSyntax,
            TypeSymbol initializerType,
            BindingDiagnosticBag diagnostics,
            BoundObjectOrCollectionValuePlaceholder implicitReceiver,
            bool useObjectInitDiagnostics)
        {
            // SPEC:    7.6.10.2 Object initializers
            //
            // SPEC:    An object initializer consists of a sequence of member initializers, enclosed by { and } tokens and separated by commas.
            // SPEC:    Each member initializer must name an accessible field or property of the object being initialized, followed by an equals sign and
            // SPEC:    an expression or an object initializer or collection initializer.

            Debug.Assert(initializerSyntax.Kind() == SyntaxKind.ObjectInitializerExpression ||
                         initializerSyntax.Kind() == SyntaxKind.WithInitializerExpression);
            Debug.Assert((object)initializerType != null);

            // We use a location specific binder for binding object initializer field/property access to generate object initializer specific diagnostics:
            //  1) CS1914 (ERR_StaticMemberInObjectInitializer)
            //  2) CS1917 (ERR_ReadonlyValueTypeInObjectInitializer)
            //  3) CS1918 (ERR_ValueTypePropertyInObjectInitializer)
            // Note that this is only used for the LHS of the assignment - these diagnostics do not apply on the RHS.
            // For this reason, we will actually need two binders: this and this.WithAdditionalFlags.
            var objectInitializerMemberBinder = useObjectInitDiagnostics
                ? this.WithAdditionalFlags(BinderFlags.ObjectInitializerMember)
                : this;

            var initializers = ArrayBuilder<BoundExpression>.GetInstance(initializerSyntax.Expressions.Count);

            // Member name map to report duplicate assignments to a field/property.
            var memberNameMap = PooledHashSet<string>.GetInstance();
            foreach (var memberInitializer in initializerSyntax.Expressions)
            {
                BoundExpression boundMemberInitializer = BindInitializerMemberAssignment(
                    memberInitializer, initializerType, objectInitializerMemberBinder, diagnostics, implicitReceiver);

                initializers.Add(boundMemberInitializer);

                ReportDuplicateObjectMemberInitializers(boundMemberInitializer, memberNameMap, diagnostics);
            }

            return new BoundObjectInitializerExpression(
                initializerSyntax,
                implicitReceiver,
                initializers.ToImmutableAndFree(),
                initializerType);
        }

        private BoundExpression BindInitializerMemberAssignment(
            ExpressionSyntax memberInitializer,
            TypeSymbol initializerType,
            Binder objectInitializerMemberBinder,
            BindingDiagnosticBag diagnostics,
            BoundObjectOrCollectionValuePlaceholder implicitReceiver)
        {
            // SPEC:    A member initializer that specifies an expression after the equals sign is processed in the same way as an assignment (spec 7.17.1) to the field or property.

            if (memberInitializer.Kind() == SyntaxKind.SimpleAssignmentExpression)
            {
                var initializer = (AssignmentExpressionSyntax)memberInitializer;

                // Bind member initializer identifier, i.e. left part of assignment
                BoundExpression boundLeft = null;
                var leftSyntax = initializer.Left;

                if (initializerType.IsDynamic() && leftSyntax.Kind() == SyntaxKind.IdentifierName)
                {
                    {
                        // D = { ..., <identifier> = <expr>, ... }, where D : dynamic
                        var memberName = ((IdentifierNameSyntax)leftSyntax).Identifier.Text;
                        boundLeft = new BoundDynamicObjectInitializerMember(leftSyntax, memberName, implicitReceiver.Type, initializerType, hasErrors: false);
                    }
                }
                else
                {
                    // We use a location specific binder for binding object initializer field/property access to generate object initializer specific diagnostics:
                    //  1) CS1914 (ERR_StaticMemberInObjectInitializer)
                    //  2) CS1917 (ERR_ReadonlyValueTypeInObjectInitializer)
                    //  3) CS1918 (ERR_ValueTypePropertyInObjectInitializer)
                    // See comments in BindObjectInitializerExpression for more details.

                    Debug.Assert(objectInitializerMemberBinder != null);

                    boundLeft = objectInitializerMemberBinder.BindObjectInitializerMember(initializer, implicitReceiver, diagnostics);
                }

                if (boundLeft != null)
                {
                    Debug.Assert((object)boundLeft.Type != null);

                    // Bind member initializer value, i.e. right part of assignment
                    BoundExpression boundRight = BindInitializerExpressionOrValue(
                        syntax: initializer.Right,
                        type: boundLeft.Type,
                        typeSyntax: boundLeft.Syntax,
                        diagnostics: diagnostics);

                    // Bind member initializer assignment expression
                    return BindAssignment(initializer, boundLeft, boundRight, isRef: false, diagnostics);
                }
            }

            var boundExpression = BindValue(memberInitializer, diagnostics, BindValueKind.RValue);
            Error(diagnostics, ErrorCode.ERR_InvalidInitializerElementInitializer, memberInitializer);
            return ToBadExpression(boundExpression, LookupResultKind.NotAValue);
        }

        // returns BadBoundExpression or BoundObjectInitializerMember
        private BoundExpression BindObjectInitializerMember(
            AssignmentExpressionSyntax namedAssignment,
            BoundObjectOrCollectionValuePlaceholder implicitReceiver,
            BindingDiagnosticBag diagnostics)
        {
            BoundExpression boundMember;
            LookupResultKind resultKind;
            bool hasErrors;

            if (namedAssignment.Left.Kind() == SyntaxKind.IdentifierName)
            {
                var memberName = (IdentifierNameSyntax)namedAssignment.Left;

                // SPEC:    Each member initializer must name an accessible field or property of the object being initialized, followed by an equals sign and
                // SPEC:    an expression or an object initializer or collection initializer.
                // SPEC:    A member initializer that specifies an expression after the equals sign is processed in the same way as an assignment (7.17.1) to the field or property.

                // SPEC VIOLATION:  Native compiler also allows initialization of field-like events in object initializers, so we allow it as well.

                boundMember = BindInstanceMemberAccess(
                    node: memberName,
                    right: memberName,
                    boundLeft: implicitReceiver,
                    rightName: memberName.Identifier.ValueText,
                    rightArity: 0,
                    typeArgumentsSyntax: default(SeparatedSyntaxList<TypeSyntax>),
                    typeArgumentsWithAnnotations: default(ImmutableArray<TypeWithAnnotations>),
                    invoked: false,
                    indexed: false,
                    diagnostics: diagnostics);

                resultKind = boundMember.ResultKind;
                hasErrors = boundMember.HasAnyErrors || implicitReceiver.HasAnyErrors;

                if (boundMember.Kind == BoundKind.PropertyGroup)
                {
                    boundMember = BindIndexedPropertyAccess((BoundPropertyGroup)boundMember, mustHaveAllOptionalParameters: true, diagnostics: diagnostics);
                    if (boundMember.HasAnyErrors)
                    {
                        hasErrors = true;
                    }
                }
            }
            else if (namedAssignment.Left.Kind() == SyntaxKind.ImplicitElementAccess)
            {
                var implicitIndexing = (ImplicitElementAccessSyntax)namedAssignment.Left;
                boundMember = BindElementAccess(implicitIndexing, implicitReceiver, implicitIndexing.ArgumentList, diagnostics);

                resultKind = boundMember.ResultKind;
                hasErrors = boundMember.HasAnyErrors || implicitReceiver.HasAnyErrors;
            }
            else
            {
                return null;
            }

            // SPEC:    A member initializer that specifies an object initializer after the equals sign is a nested object initializer,
            // SPEC:    i.e. an initialization of an embedded object. Instead of assigning a new value to the field or property,
            // SPEC:    the assignments in the nested object initializer are treated as assignments to members of the field or property.
            // SPEC:    Nested object initializers cannot be applied to properties with a value type, or to read-only fields with a value type.

            // NOTE:    The dev11 behavior does not match the spec that was current at the time (quoted above).  However, in the roslyn
            // NOTE:    timeframe, the spec will be updated to apply the same restriction to nested collection initializers.  Therefore,
            // NOTE:    roslyn will implement the dev11 behavior and it will be spec-compliant.

            // NOTE:    In the roslyn timeframe, an additional restriction will (likely) be added to the spec - it is not sufficient for the
            // NOTE:    type of the member to not be a value type - it must actually be a reference type (i.e. unconstrained type parameters
            // NOTE:    should be prohibited).  To avoid breaking existing code, roslyn will not implement this new spec clause.
            // TODO:    If/when we have a way to version warnings, we should add a warning for this.

            BoundKind boundMemberKind = boundMember.Kind;
            SyntaxKind rhsKind = namedAssignment.Right.Kind();
            bool isRhsNestedInitializer = rhsKind == SyntaxKind.ObjectInitializerExpression || rhsKind == SyntaxKind.CollectionInitializerExpression;
            BindValueKind valueKind = isRhsNestedInitializer ? BindValueKind.RValue : BindValueKind.Assignable;

            ImmutableArray<BoundExpression> arguments = ImmutableArray<BoundExpression>.Empty;
            ImmutableArray<string> argumentNamesOpt = default(ImmutableArray<string>);
            ImmutableArray<int> argsToParamsOpt = default(ImmutableArray<int>);
            ImmutableArray<RefKind> argumentRefKindsOpt = default(ImmutableArray<RefKind>);
            BitVector defaultArguments = default(BitVector);
            bool expanded = false;

            switch (boundMemberKind)
            {
                case BoundKind.FieldAccess:
                    {
                        var fieldSymbol = ((BoundFieldAccess)boundMember).FieldSymbol;
                        if (isRhsNestedInitializer && fieldSymbol.IsReadOnly && fieldSymbol.Type.IsValueType)
                        {
                            if (!hasErrors)
                            {
                                // TODO: distinct error code for collection initializers?  (Dev11 doesn't have one.)
                                Error(diagnostics, ErrorCode.ERR_ReadonlyValueTypeInObjectInitializer, namedAssignment.Left, fieldSymbol, fieldSymbol.Type);
                                hasErrors = true;
                            }

                            resultKind = LookupResultKind.NotAValue;
                        }
                        break;
                    }

                case BoundKind.EventAccess:
                    break;

                case BoundKind.PropertyAccess:
                    hasErrors |= isRhsNestedInitializer && !CheckNestedObjectInitializerPropertySymbol(((BoundPropertyAccess)boundMember).PropertySymbol, namedAssignment.Left, diagnostics, hasErrors, ref resultKind);
                    break;

                case BoundKind.IndexerAccess:
                    {
                        var indexer = BindIndexerDefaultArguments((BoundIndexerAccess)boundMember, valueKind, diagnostics);
                        boundMember = indexer;
                        hasErrors |= isRhsNestedInitializer && !CheckNestedObjectInitializerPropertySymbol(indexer.Indexer, namedAssignment.Left, diagnostics, hasErrors, ref resultKind);
                        arguments = indexer.Arguments;
                        argumentNamesOpt = indexer.ArgumentNamesOpt;
                        argsToParamsOpt = indexer.ArgsToParamsOpt;
                        argumentRefKindsOpt = indexer.ArgumentRefKindsOpt;
                        defaultArguments = indexer.DefaultArguments;
                        expanded = indexer.Expanded;

                        break;
                    }

                case BoundKind.DynamicIndexerAccess:
                    {
                        var indexer = (BoundDynamicIndexerAccess)boundMember;
                        arguments = indexer.Arguments;
                        argumentNamesOpt = indexer.ArgumentNamesOpt;
                        argumentRefKindsOpt = indexer.ArgumentRefKindsOpt;
                    }

                    break;

                case BoundKind.ArrayAccess:
                case BoundKind.PointerElementAccess:
                    return boundMember;

                default:
                    return BadObjectInitializerMemberAccess(boundMember, implicitReceiver, namedAssignment.Left, diagnostics, valueKind, hasErrors);
            }

            if (!hasErrors)
            {
                // CheckValueKind to generate possible diagnostics for invalid initializers non-viable member lookup result:
                //      1) CS0154 (ERR_PropertyLacksGet)
                //      2) CS0200 (ERR_AssgReadonlyProp)
                if (!CheckValueKind(boundMember.Syntax, boundMember, valueKind, checkingReceiver: false, diagnostics: diagnostics))
                {
                    hasErrors = true;
                    resultKind = isRhsNestedInitializer ? LookupResultKind.NotAValue : LookupResultKind.NotAVariable;
                }
            }

            return new BoundObjectInitializerMember(
                namedAssignment.Left,
                boundMember.ExpressionSymbol,
                arguments,
                argumentNamesOpt,
                argumentRefKindsOpt,
                expanded,
                argsToParamsOpt,
                defaultArguments,
                resultKind,
                implicitReceiver.Type,
                type: boundMember.Type,
                hasErrors: hasErrors);
        }

        private static bool CheckNestedObjectInitializerPropertySymbol(
            PropertySymbol propertySymbol,
            ExpressionSyntax memberNameSyntax,
            BindingDiagnosticBag diagnostics,
            bool suppressErrors,
            ref LookupResultKind resultKind)
        {
            bool hasErrors = false;
            if (propertySymbol.Type.IsValueType)
            {
                if (!suppressErrors)
                {
                    // TODO: distinct error code for collection initializers?  (Dev11 doesn't have one.)
                    Error(diagnostics, ErrorCode.ERR_ValueTypePropertyInObjectInitializer, memberNameSyntax, propertySymbol, propertySymbol.Type);
                    hasErrors = true;
                }

                resultKind = LookupResultKind.NotAValue;
            }

            return !hasErrors;
        }

        private BoundExpression BadObjectInitializerMemberAccess(
            BoundExpression boundMember,
            BoundObjectOrCollectionValuePlaceholder implicitReceiver,
            ExpressionSyntax memberNameSyntax,
            BindingDiagnosticBag diagnostics,
            BindValueKind valueKind,
            bool suppressErrors)
        {
            if (!suppressErrors)
            {
                string member;
                var identName = memberNameSyntax as IdentifierNameSyntax;
                if (identName != null)
                {
                    member = identName.Identifier.ValueText;
                }
                else
                {
                    member = memberNameSyntax.ToString();
                }

                switch (boundMember.ResultKind)
                {
                    case LookupResultKind.Empty:
                        Error(diagnostics, ErrorCode.ERR_NoSuchMember, memberNameSyntax, implicitReceiver.Type, member);
                        break;

                    case LookupResultKind.Inaccessible:
                        boundMember = CheckValue(boundMember, valueKind, diagnostics);
                        Debug.Assert(boundMember.HasAnyErrors);
                        break;

                    default:
                        Error(diagnostics, ErrorCode.ERR_MemberCannotBeInitialized, memberNameSyntax, member);
                        break;
                }
            }

            return ToBadExpression(boundMember, (valueKind == BindValueKind.RValue) ? LookupResultKind.NotAValue : LookupResultKind.NotAVariable);
        }

        private static void ReportDuplicateObjectMemberInitializers(BoundExpression boundMemberInitializer, HashSet<string> memberNameMap, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(memberNameMap != null);

            // SPEC:    It is an error for an object initializer to include more than one member initializer for the same field or property.

            if (!boundMemberInitializer.HasAnyErrors)
            {
                // SPEC:    A member initializer that specifies an expression after the equals sign is processed in the same way as an assignment (7.17.1) to the field or property.

                var memberInitializerSyntax = boundMemberInitializer.Syntax;

                Debug.Assert(memberInitializerSyntax.Kind() == SyntaxKind.SimpleAssignmentExpression);
                var namedAssignment = (AssignmentExpressionSyntax)memberInitializerSyntax;

                var memberNameSyntax = namedAssignment.Left as IdentifierNameSyntax;
                if (memberNameSyntax != null)
                {
                    var memberName = memberNameSyntax.Identifier.ValueText;

                    if (!memberNameMap.Add(memberName))
                    {
                        Error(diagnostics, ErrorCode.ERR_MemberAlreadyInitialized, memberNameSyntax, memberName);
                    }
                }
            }
        }

        private BoundCollectionInitializerExpression BindCollectionInitializerExpression(
            InitializerExpressionSyntax initializerSyntax,
            TypeSymbol initializerType,
            BindingDiagnosticBag diagnostics,
            BoundObjectOrCollectionValuePlaceholder implicitReceiver)
        {
            // SPEC:    7.6.10.3 Collection initializers
            //
            // SPEC:    A collection initializer consists of a sequence of element initializers, enclosed by { and } tokens and separated by commas.
            // SPEC:    The following is an example of an object creation expression that includes a collection initializer:
            // SPEC:        List<int> digits = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            // SPEC:    The collection object to which a collection initializer is applied must be of a type that implements System.Collections.IEnumerable or
            // SPEC:    a compile-time error occurs. For each specified element in order, the collection initializer invokes an Add method on the target object
            // SPEC:    with the expression list of the element initializer as argument list, applying normal overload resolution for each invocation.
            // SPEC:    Thus, the collection object must contain an applicable Add method for each element initializer.

            Debug.Assert(initializerSyntax.Kind() == SyntaxKind.CollectionInitializerExpression);
            Debug.Assert(initializerSyntax.Expressions.Any());
            Debug.Assert((object)initializerType != null);

            var initializerBuilder = ArrayBuilder<BoundExpression>.GetInstance();

            // SPEC:    The collection object to which a collection initializer is applied must be of a type that implements System.Collections.IEnumerable or
            // SPEC:    a compile-time error occurs.

            bool hasEnumerableInitializerType = CollectionInitializerTypeImplementsIEnumerable(initializerType, initializerSyntax, diagnostics);
            if (!hasEnumerableInitializerType && !initializerSyntax.HasErrors && !initializerType.IsErrorType())
            {
                Error(diagnostics, ErrorCode.ERR_CollectionInitRequiresIEnumerable, initializerSyntax, initializerType);
            }

            // We use a location specific binder for binding collection initializer Add method to generate specific overload resolution diagnostics:
            //  1) CS1921 (ERR_InitializerAddHasWrongSignature)
            //  2) CS1950 (ERR_BadArgTypesForCollectionAdd)
            //  3) CS1954 (ERR_InitializerAddHasParamModifiers)
            var collectionInitializerAddMethodBinder = this.WithAdditionalFlags(BinderFlags.CollectionInitializerAddMethod);

            foreach (var elementInitializer in initializerSyntax.Expressions)
            {
                // NOTE:    collectionInitializerAddMethodBinder is used only for binding the Add method invocation expression, but not the entire initializer.
                // NOTE:    Hence it is being passed as a parameter to BindCollectionInitializerElement().
                // NOTE:    Ideally we would want to avoid this and bind the entire initializer with the collectionInitializerAddMethodBinder.
                // NOTE:    However, this approach has few issues. These issues also occur when binding object initializer member assignment.
                // NOTE:    See comments for objectInitializerMemberBinder in BindObjectInitializerExpression method for details about the pitfalls of alternate approaches.

                BoundExpression boundElementInitializer = BindCollectionInitializerElement(elementInitializer, initializerType,
                    hasEnumerableInitializerType, collectionInitializerAddMethodBinder, diagnostics, implicitReceiver);

                initializerBuilder.Add(boundElementInitializer);
            }

            return new BoundCollectionInitializerExpression(initializerSyntax, implicitReceiver, initializerBuilder.ToImmutableAndFree(), initializerType);
        }

        private bool CollectionInitializerTypeImplementsIEnumerable(TypeSymbol initializerType, CSharpSyntaxNode node, BindingDiagnosticBag diagnostics)
        {
            // SPEC:    The collection object to which a collection initializer is applied must be of a type that implements System.Collections.IEnumerable or
            // SPEC:    a compile-time error occurs.

            if (initializerType.IsDynamic())
            {
                // We cannot determine at compile time if initializerType implements System.Collections.IEnumerable, we must assume that it does.
                return true;
            }
            else if (!initializerType.IsErrorType())
            {
                TypeSymbol collectionsIEnumerableType = this.GetSpecialType(SpecialType.System_Collections_IEnumerable, diagnostics, node);

                // NOTE:    Ideally, to check if the initializer type implements System.Collections.IEnumerable we can walk through
                // NOTE:    its implemented interfaces. However the native compiler checks to see if there is conversion from initializer
                // NOTE:    type to the predefined System.Collections.IEnumerable type, so we do the same.

                CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                var result = Conversions.ClassifyImplicitConversionFromType(initializerType, collectionsIEnumerableType, ref useSiteInfo).IsValid;
                diagnostics.Add(node, useSiteInfo);
                return result;
            }
            else
            {
                return false;
            }
        }

        private BoundExpression BindCollectionInitializerElement(
            ExpressionSyntax elementInitializer,
            TypeSymbol initializerType,
            bool hasEnumerableInitializerType,
            Binder collectionInitializerAddMethodBinder,
            BindingDiagnosticBag diagnostics,
            BoundObjectOrCollectionValuePlaceholder implicitReceiver)
        {
            // SPEC:    Each element initializer specifies an element to be added to the collection object being initialized, and consists of
            // SPEC:    a list of expressions enclosed by { and } tokens and separated by commas.
            // SPEC:    A single-expression element initializer can be written without braces, but cannot then be an assignment expression,
            // SPEC:    to avoid ambiguity with member initializers. The non-assignment-expression production is defined in 7.18.

            if (elementInitializer.Kind() == SyntaxKind.ComplexElementInitializerExpression)
            {
                return BindComplexElementInitializerExpression(
                    (InitializerExpressionSyntax)elementInitializer,
                    diagnostics,
                    hasEnumerableInitializerType,
                    collectionInitializerAddMethodBinder,
                    implicitReceiver);
            }
            else
            {
                // Must be a non-assignment expression.
                if (SyntaxFacts.IsAssignmentExpression(elementInitializer.Kind()))
                {
                    Error(diagnostics, ErrorCode.ERR_InvalidInitializerElementInitializer, elementInitializer);
                }

                var boundElementInitializer = BindInitializerExpressionOrValue(elementInitializer, initializerType, implicitReceiver.Syntax, diagnostics);

                BoundExpression result = BindCollectionInitializerElementAddMethod(
                    elementInitializer,
                    ImmutableArray.Create(boundElementInitializer),
                    hasEnumerableInitializerType,
                    collectionInitializerAddMethodBinder,
                    diagnostics,
                    implicitReceiver);

                result.WasCompilerGenerated = true;
                return result;
            }
        }

        private BoundExpression BindComplexElementInitializerExpression(
            InitializerExpressionSyntax elementInitializer,
            BindingDiagnosticBag diagnostics,
            bool hasEnumerableInitializerType,
            Binder collectionInitializerAddMethodBinder = null,
            BoundObjectOrCollectionValuePlaceholder implicitReceiver = null)
        {
            var elementInitializerExpressions = elementInitializer.Expressions;

            if (elementInitializerExpressions.Any())
            {
                var exprBuilder = ArrayBuilder<BoundExpression>.GetInstance();
                foreach (var childElementInitializer in elementInitializerExpressions)
                {
                    exprBuilder.Add(BindValue(childElementInitializer, diagnostics, BindValueKind.RValue));
                }

                return BindCollectionInitializerElementAddMethod(
                    elementInitializer,
                    exprBuilder.ToImmutableAndFree(),
                    hasEnumerableInitializerType,
                    collectionInitializerAddMethodBinder,
                    diagnostics,
                    implicitReceiver);
            }
            else
            {
                Error(diagnostics, ErrorCode.ERR_EmptyElementInitializer, elementInitializer);
                return BadExpression(elementInitializer, LookupResultKind.NotInvocable);
            }
        }

        private BoundExpression BindUnexpectedComplexElementInitializer(InitializerExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node.Kind() == SyntaxKind.ComplexElementInitializerExpression);

            return BindComplexElementInitializerExpression(node, diagnostics, hasEnumerableInitializerType: false);
        }

        private BoundExpression BindCollectionInitializerElementAddMethod(
            ExpressionSyntax elementInitializer,
            ImmutableArray<BoundExpression> boundElementInitializerExpressions,
            bool hasEnumerableInitializerType,
            Binder collectionInitializerAddMethodBinder,
            BindingDiagnosticBag diagnostics,
            BoundObjectOrCollectionValuePlaceholder implicitReceiver)
        {
            // SPEC:    For each specified element in order, the collection initializer invokes an Add method on the target object
            // SPEC:    with the expression list of the element initializer as argument list, applying normal overload resolution for each invocation.
            // SPEC:    Thus, the collection object must contain an applicable Add method for each element initializer.

            // We use a location specific binder for binding collection initializer Add method to generate specific overload resolution diagnostics.
            //  1) CS1921 (ERR_InitializerAddHasWrongSignature)
            //  2) CS1950 (ERR_BadArgTypesForCollectionAdd)
            //  3) CS1954 (ERR_InitializerAddHasParamModifiers)
            // See comments in BindCollectionInitializerExpression for more details.

            Debug.Assert(!boundElementInitializerExpressions.IsEmpty);

            if (!hasEnumerableInitializerType)
            {
                return BadExpression(elementInitializer, LookupResultKind.NotInvocable, ImmutableArray<Symbol>.Empty, boundElementInitializerExpressions);
            }

            Debug.Assert(collectionInitializerAddMethodBinder != null);
            Debug.Assert(collectionInitializerAddMethodBinder.Flags.Includes(BinderFlags.CollectionInitializerAddMethod));
            Debug.Assert(implicitReceiver != null);
            Debug.Assert((object)implicitReceiver.Type != null);

            if (implicitReceiver.Type.IsDynamic())
            {
                var hasErrors = ReportBadDynamicArguments(elementInitializer, boundElementInitializerExpressions, refKinds: default, diagnostics, queryClause: null);

                return new BoundDynamicCollectionElementInitializer(
                    elementInitializer,
                    applicableMethods: ImmutableArray<MethodSymbol>.Empty,
                    implicitReceiver,
                    arguments: boundElementInitializerExpressions.SelectAsArray(e => BindToNaturalType(e, diagnostics)),
                    type: GetSpecialType(SpecialType.System_Void, diagnostics, elementInitializer),
                    hasErrors: hasErrors);
            }

            // Receiver is early bound, find method Add and invoke it (may still be a dynamic invocation):

            var addMethodInvocation = collectionInitializerAddMethodBinder.MakeInvocationExpression(
                elementInitializer,
                implicitReceiver,
                methodName: WellKnownMemberNames.CollectionInitializerAddMethodName,
                args: boundElementInitializerExpressions,
                diagnostics: diagnostics);

            if (addMethodInvocation.Kind == BoundKind.DynamicInvocation)
            {
                var dynamicInvocation = (BoundDynamicInvocation)addMethodInvocation;
                return new BoundDynamicCollectionElementInitializer(
                    elementInitializer,
                    dynamicInvocation.ApplicableMethods,
                    implicitReceiver,
                    dynamicInvocation.Arguments,
                    dynamicInvocation.Type,
                    hasErrors: dynamicInvocation.HasAnyErrors);
            }
            else if (addMethodInvocation.Kind == BoundKind.Call)
            {
                var boundCall = (BoundCall)addMethodInvocation;

                // Either overload resolution succeeded for this call or it did not. If it
                // did not succeed then we've stashed the original method symbols from the
                // method group, and we should use those as the symbols displayed for the
                // call. If it did succeed then we did not stash any symbols.
                if (boundCall.HasErrors && !boundCall.OriginalMethodsOpt.IsDefault)
                {
                    return boundCall;
                }

                return new BoundCollectionElementInitializer(
                    elementInitializer,
                    boundCall.Method,
                    boundCall.Arguments,
                    boundCall.ReceiverOpt,
                    boundCall.Expanded,
                    boundCall.ArgsToParamsOpt,
                    boundCall.DefaultArguments,
                    boundCall.InvokedAsExtensionMethod,
                    boundCall.ResultKind,
                    boundCall.Type,
                    boundCall.HasAnyErrors)
                { WasCompilerGenerated = true };
            }
            else
            {
                Debug.Assert(addMethodInvocation.Kind == BoundKind.BadExpression);
                return addMethodInvocation;
            }
        }

        internal ImmutableArray<MethodSymbol> FilterInaccessibleConstructors(ImmutableArray<MethodSymbol> constructors, bool allowProtectedConstructorsOfBaseType, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            ArrayBuilder<MethodSymbol> builder = null;

            for (int i = 0; i < constructors.Length; i++)
            {
                MethodSymbol constructor = constructors[i];

                if (!IsConstructorAccessible(constructor, ref useSiteInfo, allowProtectedConstructorsOfBaseType))
                {
                    if (builder == null)
                    {
                        builder = ArrayBuilder<MethodSymbol>.GetInstance();
                        builder.AddRange(constructors, i);
                    }
                }
                else
                {
                    builder?.Add(constructor);
                }
            }

            return builder == null ? constructors : builder.ToImmutableAndFree();
        }

        private bool IsConstructorAccessible(MethodSymbol constructor, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo, bool allowProtectedConstructorsOfBaseType = false)
        {
            Debug.Assert((object)constructor != null);
            Debug.Assert(constructor.MethodKind == MethodKind.Constructor || constructor.MethodKind == MethodKind.StaticConstructor);

            NamedTypeSymbol containingType = this.ContainingType;
            if ((object)containingType != null)
            {
                // SPEC VIOLATION: The specification implies that when considering 
                // SPEC VIOLATION: instance methods or instance constructors, we first 
                // SPEC VIOLATION: do overload resolution on the accessible members, and 
                // SPEC VIOLATION: then if the best method chosen is protected and accessed 
                // SPEC VIOLATION: through the wrong type, then an error occurs. The native 
                // SPEC VIOLATION: compiler however does it in the opposite order. First it
                // SPEC VIOLATION: filters out the protected methods that cannot be called
                // SPEC VIOLATION: through the given type, and then it does overload resolution
                // SPEC VIOLATION: on the rest.
                // 
                // That said, it is somewhat odd that the same rule applies to constructors
                // as instance methods. A protected constructor is never going to be called
                // via an instance of a *more derived but different class* the way a 
                // virtual method might be. Nevertheless, that's what we do.
                //
                // A constructor is accessed through an instance of the type being constructed:
                return allowProtectedConstructorsOfBaseType ?
                    this.IsAccessible(constructor, ref useSiteInfo, null) :
                    this.IsSymbolAccessibleConditional(constructor, containingType, ref useSiteInfo, constructor.ContainingType);
            }
            else
            {
                Debug.Assert((object)this.Compilation.Assembly != null);
                return IsSymbolAccessibleConditional(constructor, this.Compilation.Assembly, ref useSiteInfo);
            }
        }

        protected BoundExpression BindClassCreationExpression(
            SyntaxNode node,
            string typeName,
            SyntaxNode typeNode,
            NamedTypeSymbol type,
            AnalyzedArguments analyzedArguments,
            BindingDiagnosticBag diagnostics,
            InitializerExpressionSyntax initializerSyntaxOpt = null,
            TypeSymbol initializerTypeOpt = null,
            bool wasTargetTyped = false)
        {

            BoundExpression result = null;
            bool hasErrors = type.IsErrorType();
            if (type.IsAbstract)
            {
                // Report error for new of abstract type.
                diagnostics.Add(ErrorCode.ERR_NoNewAbstract, node.Location, type);
                hasErrors = true;
            }

            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            BoundObjectInitializerExpressionBase boundInitializerOpt = null;

            // If we have a dynamic argument then do overload resolution to see if there are one or more
            // applicable candidates. If there are, then this is a dynamic object creation; we'll work out
            // which ctor to call at runtime. If we have a dynamic argument but no applicable candidates
            // then we do the analysis again for error reporting purposes.

            if (analyzedArguments.HasDynamicArgument)
            {
                OverloadResolutionResult<MethodSymbol> overloadResolutionResult = OverloadResolutionResult<MethodSymbol>.GetInstance();
                this.OverloadResolution.ObjectCreationOverloadResolution(GetAccessibleConstructorsForOverloadResolution(type, ref useSiteInfo), analyzedArguments, overloadResolutionResult, ref useSiteInfo);
                diagnostics.Add(node, useSiteInfo);
                useSiteInfo = new CompoundUseSiteInfo<AssemblySymbol>(useSiteInfo);

                if (overloadResolutionResult.HasAnyApplicableMember)
                {
                    var argArray = BuildArgumentsForDynamicInvocation(analyzedArguments, diagnostics);
                    var refKindsArray = analyzedArguments.RefKinds.ToImmutableOrNull();

                    hasErrors &= ReportBadDynamicArguments(node, argArray, refKindsArray, diagnostics, queryClause: null);

                    boundInitializerOpt = makeBoundInitializerOpt();
                    result = new BoundDynamicObjectCreationExpression(
                        node,
                        typeName,
                        argArray,
                        analyzedArguments.GetNames(),
                        refKindsArray,
                        boundInitializerOpt,
                        overloadResolutionResult.GetAllApplicableMembers(),
                        type,
                        hasErrors);
                }

                overloadResolutionResult.Free();
                if (result != null)
                {
                    return result;
                }
            }


            if (TryPerformConstructorOverloadResolution(
                    type,
                    analyzedArguments,
                    typeName,
                    typeNode.Location,
                    hasErrors, //don't cascade in these cases
                    diagnostics,
                    out MemberResolutionResult<MethodSymbol> memberResolutionResult,
                    out ImmutableArray<MethodSymbol> candidateConstructors,
                    allowProtectedConstructorsOfBaseType: false))
            {
                var method = memberResolutionResult.Member;

                bool hasError = false;

                // What if some of the arguments are implicit?  Dev10 reports unsafe errors
                // if the implied argument would have an unsafe type.  We need to check
                // the parameters explicitly, since there won't be bound nodes for the implied
                // arguments until lowering.
                if (method.HasUnsafeParameter())
                {
                    // Don't worry about double reporting (i.e. for both the argument and the parameter)
                    // because only one unsafe diagnostic is allowed per scope - the others are suppressed.
                    hasError = ReportUnsafeIfNotAllowed(node, diagnostics) || hasError;
                }

                ReportDiagnosticsIfObsolete(diagnostics, method, node, hasBaseReceiver: false);
                // NOTE: Use-site diagnostics were reported during overload resolution.

                ConstantValue constantValueOpt = (initializerSyntaxOpt == null && method.IsDefaultValueTypeConstructor(requireZeroInit: true)) ?
                    FoldParameterlessValueTypeConstructor(type) :
                    null;

                var expanded = memberResolutionResult.Result.Kind == MemberResolutionKind.ApplicableInExpandedForm;
                var argToParams = memberResolutionResult.Result.ArgsToParamsOpt;
                BindDefaultArguments(node, method.Parameters, analyzedArguments.Arguments, analyzedArguments.RefKinds, ref argToParams, out var defaultArguments, expanded, enableCallerInfo: true, diagnostics);

                var arguments = analyzedArguments.Arguments.ToImmutable();
                var refKinds = analyzedArguments.RefKinds.ToImmutableOrNull();

                if (!hasError)
                {
                    hasError = !CheckInvocationArgMixing(
                        node,
                        method,
                        null,
                        method.Parameters,
                        arguments,
                        argToParams,
                        this.LocalScopeDepth,
                        diagnostics);
                }

                boundInitializerOpt = makeBoundInitializerOpt();
                result = new BoundObjectCreationExpression(
                    node,
                    method,
                    candidateConstructors,
                    arguments,
                    analyzedArguments.GetNames(),
                    refKinds,
                    expanded,
                    argToParams,
                    defaultArguments,
                    constantValueOpt,
                    boundInitializerOpt,
                    wasTargetTyped,
                    type,
                    hasError);

                // CONSIDER: Add ResultKind field to BoundObjectCreationExpression to avoid wrapping result with BoundBadExpression.
                if (type.IsAbstract)
                {
                    result = BadExpression(node, LookupResultKind.NotCreatable, result);
                }

                return result;
            }

            LookupResultKind resultKind;

            if (type.IsAbstract)
            {
                resultKind = LookupResultKind.NotCreatable;
            }
            else if (memberResolutionResult.IsValid && !IsConstructorAccessible(memberResolutionResult.Member, ref useSiteInfo))
            {
                resultKind = LookupResultKind.Inaccessible;
            }
            else
            {
                resultKind = LookupResultKind.OverloadResolutionFailure;
            }

            diagnostics.Add(node, useSiteInfo);

            ArrayBuilder<Symbol> symbols = ArrayBuilder<Symbol>.GetInstance();
            symbols.AddRange(candidateConstructors);

            // NOTE: The use site diagnostics of the candidate constructors have already been reported (in PerformConstructorOverloadResolution).

            var childNodes = ArrayBuilder<BoundExpression>.GetInstance();
            childNodes.AddRange(BuildArgumentsForErrorRecovery(analyzedArguments, candidateConstructors));
            if (initializerSyntaxOpt != null)
            {
                childNodes.Add(boundInitializerOpt ?? makeBoundInitializerOpt());
            }

            return new BoundBadExpression(node, resultKind, symbols.ToImmutableAndFree(), childNodes.ToImmutableAndFree(), type);

            BoundObjectInitializerExpressionBase makeBoundInitializerOpt()
            {
                if (initializerSyntaxOpt != null)
                {
                    return BindInitializerExpression(syntax: initializerSyntaxOpt,
                                                     type: initializerTypeOpt ?? type,
                                                     typeSyntax: typeNode,
                                                     isForNewInstance: true,
                                                     diagnostics: diagnostics);
                }
                return null;
            }
        }

        private BoundExpression BindInterfaceCreationExpression(ObjectCreationExpressionSyntax node, NamedTypeSymbol type, BindingDiagnosticBag diagnostics)
        {
            AnalyzedArguments analyzedArguments = AnalyzedArguments.GetInstance();
            BindArgumentsAndNames(node.ArgumentList, diagnostics, analyzedArguments);
            var result = BindInterfaceCreationExpression(node, type, diagnostics, node.Type, analyzedArguments, node.Initializer, wasTargetTyped: false);
            analyzedArguments.Free();
            return result;
        }

        private BoundExpression BindInterfaceCreationExpression(SyntaxNode node, NamedTypeSymbol type, BindingDiagnosticBag diagnostics, SyntaxNode typeNode, AnalyzedArguments analyzedArguments, InitializerExpressionSyntax initializerOpt, bool wasTargetTyped)
        {
            Debug.Assert((object)type != null);

            // COM interfaces which have ComImportAttribute and CoClassAttribute can be instantiated with "new". 
            // CoClassAttribute contains the type information of the original CoClass for the interface.
            // We replace the interface creation with CoClass object creation for this case.

            // NOTE: We don't attempt binding interface creation to CoClass creation if we are within an attribute argument.
            // NOTE: This is done to prevent a cycle in an error scenario where we have a "new InterfaceType" expression in an attribute argument.
            // NOTE: Accessing IsComImport/ComImportCoClass properties on given type symbol would attempt ForceCompeteAttributes, which would again try binding all attributes on the symbol.
            // NOTE: causing infinite recursion. We avoid this cycle by checking if we are within in context of an Attribute argument.
            if (!this.InAttributeArgument && type.IsComImport)
            {
                NamedTypeSymbol coClassType = type.ComImportCoClass;
                if ((object)coClassType != null)
                {
                    return BindComImportCoClassCreationExpression(node, type, coClassType, diagnostics, typeNode, analyzedArguments, initializerOpt, wasTargetTyped);
                }
            }

            // interfaces can't be instantiated in C#
            diagnostics.Add(ErrorCode.ERR_NoNewAbstract, node.Location, type);
            return MakeBadExpressionForObjectCreation(node, type, analyzedArguments, initializerOpt, typeNode, diagnostics);
        }

        private BoundExpression BindComImportCoClassCreationExpression(SyntaxNode node, NamedTypeSymbol interfaceType, NamedTypeSymbol coClassType, BindingDiagnosticBag diagnostics, SyntaxNode typeNode, AnalyzedArguments analyzedArguments, InitializerExpressionSyntax initializerOpt, bool wasTargetTyped)
        {
            Debug.Assert((object)interfaceType != null);
            Debug.Assert(interfaceType.IsInterfaceType());
            Debug.Assert((object)coClassType != null);
            Debug.Assert(TypeSymbol.Equals(interfaceType.ComImportCoClass, coClassType, TypeCompareKind.ConsiderEverything2));
            Debug.Assert(coClassType.TypeKind == TypeKind.Class || coClassType.TypeKind == TypeKind.Error);

            if (coClassType.IsErrorType())
            {
                Error(diagnostics, ErrorCode.ERR_MissingCoClass, node, coClassType, interfaceType);
            }
            else if (coClassType.IsUnboundGenericType)
            {
                // BREAKING CHANGE:     Dev10 allows the following code to compile, even though the output assembly is not verifiable and generates a runtime exception:
                //
                //          [ComImport, Guid("00020810-0000-0000-C000-000000000046")]
                //          [CoClass(typeof(GenericClass<>))]
                //          public interface InterfaceType {}
                //          public class GenericClass<T>: InterfaceType {}
                // 
                //          public class Program
                //          {
                //              public static void Main() { var i = new InterfaceType(); }
                //          }
                //
                //  We disallow CoClass creation if coClassType is an unbound generic type and report a compile time error.

                Error(diagnostics, ErrorCode.ERR_BadCoClassSig, node, coClassType, interfaceType);
            }
            else
            {
                // NoPIA support
                if (interfaceType.ContainingAssembly.IsLinked)
                {
                    return BindNoPiaObjectCreationExpression(node, interfaceType, coClassType, diagnostics, typeNode, analyzedArguments, initializerOpt);
                }

                var classCreation = BindClassCreationExpression(
                    node,
                    coClassType.Name,
                    typeNode,
                    coClassType,
                    analyzedArguments,
                    diagnostics,
                    initializerOpt,
                    interfaceType,
                    wasTargetTyped);
                CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                Conversion conversion = this.Conversions.ClassifyConversionFromExpression(classCreation, interfaceType, ref useSiteInfo, forCast: true);
                diagnostics.Add(node, useSiteInfo);
                if (!conversion.IsValid)
                {
                    SymbolDistinguisher distinguisher = new SymbolDistinguisher(this.Compilation, coClassType, interfaceType);
                    Error(diagnostics, ErrorCode.ERR_NoExplicitConv, node, distinguisher.First, distinguisher.Second);
                }

                // Bind the conversion, but drop the conversion node.
                CreateConversion(classCreation, conversion, interfaceType, diagnostics);

                // Override result type to be the interface type.
                switch (classCreation.Kind)
                {
                    case BoundKind.ObjectCreationExpression:
                        var creation = (BoundObjectCreationExpression)classCreation;
                        return creation.Update(creation.Constructor, creation.ConstructorsGroup, creation.Arguments, creation.ArgumentNamesOpt,
                                               creation.ArgumentRefKindsOpt, creation.Expanded, creation.ArgsToParamsOpt, creation.DefaultArguments, creation.ConstantValueOpt,
                                               creation.InitializerExpressionOpt, interfaceType);

                    case BoundKind.BadExpression:
                        var bad = (BoundBadExpression)classCreation;
                        return bad.Update(bad.ResultKind, bad.Symbols, bad.ChildBoundNodes, interfaceType);

                    default:
                        throw ExceptionUtilities.UnexpectedValue(classCreation.Kind);
                }
            }

            return MakeBadExpressionForObjectCreation(node, interfaceType, analyzedArguments, initializerOpt, typeNode, diagnostics);
        }

        private BoundExpression BindNoPiaObjectCreationExpression(
            SyntaxNode node,
            NamedTypeSymbol interfaceType,
            NamedTypeSymbol coClassType,
            BindingDiagnosticBag diagnostics,
            SyntaxNode typeNode,
            AnalyzedArguments analyzedArguments,
            InitializerExpressionSyntax initializerOpt)
        {
            string guidString;
            if (!coClassType.GetGuidString(out guidString))
            {
                // At this point, VB reports ERRID_NoPIAAttributeMissing2 if guid isn't there.
                // C# doesn't complain and instead uses zero guid.
                guidString = System.Guid.Empty.ToString("D");
            }

            var boundInitializerOpt = initializerOpt == null ? null :
                BindInitializerExpression(syntax: initializerOpt,
                    type: interfaceType,
                    typeSyntax: typeNode,
                    isForNewInstance: true,
                    diagnostics: diagnostics);

            var creation = new BoundNoPiaObjectCreationExpression(node, guidString, boundInitializerOpt, interfaceType);

            if (analyzedArguments.Arguments.Count > 0)
            {
                diagnostics.Add(ErrorCode.ERR_BadCtorArgCount, typeNode.Location, interfaceType, analyzedArguments.Arguments.Count);

                var children = BuildArgumentsForErrorRecovery(analyzedArguments).Add(creation);
                return new BoundBadExpression(node, LookupResultKind.OverloadResolutionFailure, ImmutableArray<Symbol>.Empty, children, creation.Type);
            }

            return creation;
        }

        private BoundExpression BindTypeParameterCreationExpression(ObjectCreationExpressionSyntax node, TypeParameterSymbol typeParameter, BindingDiagnosticBag diagnostics)
        {
            AnalyzedArguments analyzedArguments = AnalyzedArguments.GetInstance();
            BindArgumentsAndNames(node.ArgumentList, diagnostics, analyzedArguments);
            var result = BindTypeParameterCreationExpression(node, typeParameter, analyzedArguments, node.Initializer, node.Type, diagnostics);
            analyzedArguments.Free();
            return result;
        }

        private BoundExpression BindTypeParameterCreationExpression(SyntaxNode node, TypeParameterSymbol typeParameter, AnalyzedArguments analyzedArguments, InitializerExpressionSyntax initializerOpt, SyntaxNode typeSyntax, BindingDiagnosticBag diagnostics)
        {
            if (!typeParameter.HasConstructorConstraint && !typeParameter.IsValueType)
            {
                diagnostics.Add(ErrorCode.ERR_NoNewTyvar, node.Location, typeParameter);
            }
            else if (analyzedArguments.Arguments.Count > 0)
            {
                diagnostics.Add(ErrorCode.ERR_NewTyvarWithArgs, node.Location, typeParameter);
            }
            else
            {
                var boundInitializerOpt = initializerOpt == null ?
                     null :
                     BindInitializerExpression(
                        syntax: initializerOpt,
                        type: typeParameter,
                        typeSyntax: typeSyntax,
                        isForNewInstance: true,
                        diagnostics: diagnostics);
                return new BoundNewT(node, boundInitializerOpt, typeParameter);
            }

            return MakeBadExpressionForObjectCreation(node, typeParameter, analyzedArguments, initializerOpt, typeSyntax, diagnostics);
        }

        /// <summary>
        /// Given the type containing constructors, gets the list of candidate instance constructors and uses overload resolution to determine which one should be called.
        /// </summary>
        /// <param name="typeContainingConstructors">The containing type of the constructors.</param>
        /// <param name="analyzedArguments">The already bound arguments to the constructor.</param>
        /// <param name="errorName">The name to use in diagnostics if overload resolution fails.</param>
        /// <param name="errorLocation">The location at which to report overload resolution result diagnostics.</param>
        /// <param name="suppressResultDiagnostics">True to suppress overload resolution result diagnostics (but not argument diagnostics).</param>
        /// <param name="diagnostics">Where diagnostics will be reported.</param>
        /// <param name="memberResolutionResult">If this method returns true, then it will contain a valid MethodResolutionResult.
        /// Otherwise, it may contain a MethodResolutionResult for an inaccessible constructor (in which case, it will incorrectly indicate success) or nothing at all.</param>
        /// <param name="candidateConstructors">Candidate instance constructors of type <paramref name="typeContainingConstructors"/> used for overload resolution.</param>
        /// <param name="allowProtectedConstructorsOfBaseType">It is always legal to access a protected base class constructor
        /// via a constructor initializer, but not from an object creation expression.</param>
        /// <returns>True if overload resolution successfully chose an accessible constructor.</returns>
        /// <remarks>
        /// The two-pass algorithm (accessible constructors, then all constructors) is the reason for the unusual signature
        /// of this method (i.e. not populating a pre-existing <see cref="OverloadResolutionResult{MethodSymbol}"/>).
        /// Presently, rationalizing this behavior is not worthwhile.
        /// </remarks>
        internal bool TryPerformConstructorOverloadResolution(
            NamedTypeSymbol typeContainingConstructors,
            AnalyzedArguments analyzedArguments,
            string errorName,
            Location errorLocation,
            bool suppressResultDiagnostics,
            BindingDiagnosticBag diagnostics,
            out MemberResolutionResult<MethodSymbol> memberResolutionResult,
            out ImmutableArray<MethodSymbol> candidateConstructors,
            bool allowProtectedConstructorsOfBaseType) // Last to make named arguments more convenient.
        {
            // Get accessible constructors for performing overload resolution.
            ImmutableArray<MethodSymbol> allInstanceConstructors;
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            candidateConstructors = GetAccessibleConstructorsForOverloadResolution(typeContainingConstructors, allowProtectedConstructorsOfBaseType, out allInstanceConstructors, ref useSiteInfo);

            OverloadResolutionResult<MethodSymbol> result = OverloadResolutionResult<MethodSymbol>.GetInstance();

            // Indicates whether overload resolution successfully chose an accessible constructor.
            bool succeededConsideringAccessibility = false;

            // Indicates whether overload resolution resulted in a single best match, even though it might be inaccessible.
            bool succeededIgnoringAccessibility = false;

            if (candidateConstructors.Any())
            {
                // We have at least one accessible candidate constructor, perform overload resolution with accessible candidateConstructors.
                this.OverloadResolution.ObjectCreationOverloadResolution(candidateConstructors, analyzedArguments, result, ref useSiteInfo);

                if (result.Succeeded)
                {
                    succeededConsideringAccessibility = true;
                    succeededIgnoringAccessibility = true;
                }
            }

            if (!succeededConsideringAccessibility && allInstanceConstructors.Length > candidateConstructors.Length)
            {
                // Overload resolution failed on the accessible candidateConstructors, but we have at least one inaccessible constructor.
                // We might have a best match constructor which is inaccessible.
                // Try overload resolution with all instance constructors to generate correct diagnostics and semantic info for this case.
                OverloadResolutionResult<MethodSymbol> inaccessibleResult = OverloadResolutionResult<MethodSymbol>.GetInstance();
                this.OverloadResolution.ObjectCreationOverloadResolution(allInstanceConstructors, analyzedArguments, inaccessibleResult, ref useSiteInfo);

                if (inaccessibleResult.Succeeded)
                {
                    succeededIgnoringAccessibility = true;
                    candidateConstructors = allInstanceConstructors;
                    result.Free();
                    result = inaccessibleResult;
                }
                else
                {
                    inaccessibleResult.Free();
                }
            }

            diagnostics.Add(errorLocation, useSiteInfo);

            if (succeededIgnoringAccessibility)
            {
                this.CoerceArguments<MethodSymbol>(result.ValidResult, analyzedArguments.Arguments, diagnostics, receiverType: null, receiverRefKind: null, receiverEscapeScope: Binder.ExternalScope);
            }

            // Fill in the out parameter with the result, if there was one; it might be inaccessible.
            memberResolutionResult = succeededIgnoringAccessibility ?
                result.ValidResult :
                default(MemberResolutionResult<MethodSymbol>); // Invalid results are not interesting - we have enough info in candidateConstructors.

            // If something failed and we are reporting errors, then report the right errors.
            // * If the failure was due to inaccessibility, just report that.
            // * If the failure was not due to inaccessibility then only report an error
            //   on the constructor if there were no errors on the arguments.
            if (!succeededConsideringAccessibility && !suppressResultDiagnostics)
            {
                if (succeededIgnoringAccessibility)
                {
                    // It is not legal to directly call a protected constructor on a base class unless
                    // the "this" of the call is known to be of the current type. That is, it is
                    // perfectly legal to say ": base()" to call a protected base class ctor, but
                    // it is not legal to say "new MyBase()" if the ctor is protected. 
                    //
                    // The native compiler produces the error CS1540:
                    //
                    //   Cannot access protected member 'MyBase.MyBase' via a qualifier of type 'MyBase'; 
                    //   the qualifier must be of type 'Derived' (or derived from it)
                    //
                    // Though technically correct, this is a very confusing error message for this scenario;
                    // one does not typically think of the constructor as being a method that is 
                    // called with an implicit "this" of a particular receiver type, even though of course
                    // that is exactly what it is.
                    //
                    // The better error message here is to simply say that the best possible ctor cannot
                    // be accessed because it is not accessible.
                    //
                    // CONSIDER: We might consider making up a new error message for this situation.

                    // 
                    // CS0122: 'MyBase.MyBase' is inaccessible due to its protection level
                    diagnostics.Add(ErrorCode.ERR_BadAccess, errorLocation, result.ValidResult.Member);
                }
                else
                {
                    result.ReportDiagnostics(
                        binder: this, location: errorLocation, nodeOpt: null, diagnostics,
                        name: errorName, receiver: null, invokedExpression: null, analyzedArguments,
                        memberGroup: candidateConstructors, typeContainingConstructors, delegateTypeBeingInvoked: null);
                }
            }

            result.Free();
            return succeededConsideringAccessibility;
        }

        private ImmutableArray<MethodSymbol> GetAccessibleConstructorsForOverloadResolution(NamedTypeSymbol type, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            ImmutableArray<MethodSymbol> allInstanceConstructors;
            return GetAccessibleConstructorsForOverloadResolution(type, false, out allInstanceConstructors, ref useSiteInfo);
        }

        private ImmutableArray<MethodSymbol> GetAccessibleConstructorsForOverloadResolution(NamedTypeSymbol type, bool allowProtectedConstructorsOfBaseType, out ImmutableArray<MethodSymbol> allInstanceConstructors, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            if (type.IsErrorType())
            {
                // For Caas, we want to supply the constructors even in error cases
                // We may end up supplying the constructors of an unconstructed symbol,
                // but that's better than nothing.
                type = type.GetNonErrorGuess() as NamedTypeSymbol ?? type;
            }

            allInstanceConstructors = type.InstanceConstructors;
            return FilterInaccessibleConstructors(allInstanceConstructors, allowProtectedConstructorsOfBaseType, ref useSiteInfo);
        }

        private static ConstantValue FoldParameterlessValueTypeConstructor(NamedTypeSymbol type)
        {
            // DELIBERATE SPEC VIOLATION:
            //
            // Object creation expressions like "new int()" are not considered constant expressions
            // by the specification but they are by the native compiler; we maintain compatibility
            // with this bug.
            // 
            // Additionally, it also treats "new X()", where X is an enum type, as a
            // constant expression with default value 0, we maintain compatibility with it.

            var specialType = type.SpecialType;

            if (type.TypeKind == TypeKind.Enum)
            {
                specialType = type.EnumUnderlyingType.SpecialType;
            }

            switch (specialType)
            {
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_Int32:
                case SpecialType.System_Int64:
                case SpecialType.System_Byte:
                case SpecialType.System_UInt16:
                case SpecialType.System_UInt32:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                case SpecialType.System_Boolean:
                case SpecialType.System_Char:
                    return ConstantValue.Default(specialType);
            }

            return null;
        }

        private BoundLiteral BindLiteralConstant(LiteralExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            // bug.Assert(node.Kind == SyntaxKind.LiteralExpression);

            var value = node.Token.Value;

            ConstantValue cv;
            TypeSymbol type = null;

            if (value == null)
            {
                cv = ConstantValue.Null;
            }
            else
            {
                Debug.Assert(!value.GetType().GetTypeInfo().IsEnum);

                var specialType = SpecialTypeExtensions.FromRuntimeTypeOfLiteralValue(value);

                // C# literals can't be of type byte, sbyte, short, ushort:
                Debug.Assert(
                    specialType != SpecialType.None &&
                    specialType != SpecialType.System_Byte &&
                    specialType != SpecialType.System_SByte &&
                    specialType != SpecialType.System_Int16 &&
                    specialType != SpecialType.System_UInt16);

                cv = ConstantValue.Create(value, specialType);
                type = GetSpecialType(specialType, diagnostics, node);
            }

            return new BoundLiteral(node, cv, type);
        }

        private BoundExpression BindCheckedExpression(CheckedExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            // the binder is not cached since we only cache statement level binders
            return this.WithCheckedOrUncheckedRegion(node.Kind() == SyntaxKind.CheckedExpression).
                BindParenthesizedExpression(node.Expression, diagnostics);
        }

        /// <summary>
        /// Binds a member access expression
        /// </summary>
        private BoundExpression BindMemberAccess(
            MemberAccessExpressionSyntax node,
            bool invoked,
            bool indexed,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node != null);

            BoundExpression boundLeft;

            ExpressionSyntax exprSyntax = node.Expression;
            if (node.Kind() == SyntaxKind.SimpleMemberAccessExpression)
            {
                // NOTE: CheckValue will be called explicitly in BindMemberAccessWithBoundLeft.
                boundLeft = BindLeftOfPotentialColorColorMemberAccess(exprSyntax, diagnostics);
            }
            else
            {
                Debug.Assert(node.Kind() == SyntaxKind.PointerMemberAccessExpression);
                boundLeft = BindRValueWithoutTargetType(exprSyntax, diagnostics); // Not Color Color issues with ->

                // CONSIDER: another approach would be to construct a BoundPointerMemberAccess (assuming such a type existed),
                // but that would be much more cumbersome because we'd be unable to build upon the BindMemberAccess infrastructure,
                // which expects a receiver.

                // Dereference before binding member;
                TypeSymbol pointedAtType;
                bool hasErrors;
                BindPointerIndirectionExpressionInternal(node, boundLeft, diagnostics, out pointedAtType, out hasErrors);

                // If there is no pointed-at type, fall back on the actual type (i.e. assume the user meant "." instead of "->").
                if (ReferenceEquals(pointedAtType, null))
                {
                    boundLeft = ToBadExpression(boundLeft);
                }
                else
                {
                    boundLeft = new BoundPointerIndirectionOperator(exprSyntax, boundLeft, pointedAtType, hasErrors)
                    {
                        WasCompilerGenerated = true, // don't interfere with the type info for exprSyntax.
                    };
                }
            }

            return BindMemberAccessWithBoundLeft(node, boundLeft, node.Name, node.OperatorToken, invoked, indexed, diagnostics);
        }

        /// <summary>
        /// Attempt to bind the LHS of a member access expression.  If this is a Color Color case (spec 7.6.4.1),
        /// then return a BoundExpression if we can easily disambiguate or a BoundTypeOrValueExpression if we
        /// cannot.  If this is not a Color Color case, then return null.
        /// </summary>
        private BoundExpression BindLeftOfPotentialColorColorMemberAccess(ExpressionSyntax left, BindingDiagnosticBag diagnostics)
        {
            if (left is IdentifierNameSyntax identifier)
            {
                return BindLeftIdentifierOfPotentialColorColorMemberAccess(identifier, diagnostics);
            }

            // NOTE: it is up to the caller to call CheckValue on the result.
            return BindExpression(left, diagnostics);
        }

        // Avoid inlining to minimize stack size in caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private BoundExpression BindLeftIdentifierOfPotentialColorColorMemberAccess(IdentifierNameSyntax left, BindingDiagnosticBag diagnostics)
        {
            // SPEC: 7.6.4.1 Identical simple names and type names
            // SPEC: In a member access of the form E.I, if E is a single identifier, and if the meaning of E as
            // SPEC: a simple-name (spec 7.6.2) is a constant, field, property, local variable, or parameter with the
            // SPEC: same type as the meaning of E as a type-name (spec 3.8), then both possible meanings of E are 
            // SPEC: permitted. The two possible meanings of E.I are never ambiguous, since I must necessarily be
            // SPEC: a member of the type E in both cases. In other words, the rule simply permits access to the 
            // SPEC: static members and nested types of E where a compile-time error would otherwise have occurred. 

            var valueDiagnostics = BindingDiagnosticBag.Create(diagnostics);
            var boundValue = BindIdentifier(left, invoked: false, indexed: false, diagnostics: valueDiagnostics);

            Symbol leftSymbol;
            if (boundValue.Kind == BoundKind.Conversion)
            {
                // BindFieldAccess may insert a conversion if binding occurs
                // within an enum member initializer.
                leftSymbol = ((BoundConversion)boundValue).Operand.ExpressionSymbol;
            }
            else
            {
                leftSymbol = boundValue.ExpressionSymbol;
            }

            if ((object)leftSymbol != null)
            {
                switch (leftSymbol.Kind)
                {
                    case SymbolKind.Field:
                    case SymbolKind.Local:
                    case SymbolKind.Parameter:
                    case SymbolKind.Property:
                    case SymbolKind.RangeVariable:
                        var leftType = boundValue.Type;
                        Debug.Assert((object)leftType != null);

                        var leftName = left.Identifier.ValueText;
                        if (leftType.Name == leftName || IsUsingAliasInScope(leftName))
                        {
                            var typeDiagnostics = BindingDiagnosticBag.Create(diagnostics);
                            var boundType = BindNamespaceOrType(left, typeDiagnostics);
                            if (TypeSymbol.Equals(boundType.Type, leftType, TypeCompareKind.ConsiderEverything2))
                            {
                                // NOTE: ReplaceTypeOrValueReceiver will call CheckValue explicitly.
                                boundValue = BindToNaturalType(boundValue, valueDiagnostics);
                                return new BoundTypeOrValueExpression(left,
                                    new BoundTypeOrValueData(leftSymbol, boundValue, valueDiagnostics, boundType, typeDiagnostics), leftType);
                            }
                        }
                        break;

                        // case SymbolKind.Event: //SPEC: 7.6.4.1 (a.k.a. Color Color) doesn't cover events
                }
            }

            // Not a Color Color case; return the bound member.
            // NOTE: it is up to the caller to call CheckValue on the result.
            diagnostics.AddRange(valueDiagnostics);
            return boundValue;
        }

        // returns true if name matches a using alias in scope
        // NOTE: when true is returned, the corresponding using is also marked as "used" 
        private bool IsUsingAliasInScope(string name)
        {
            var isSemanticModel = this.IsSemanticModelBinder;
            for (var chain = this.ImportChain; chain != null; chain = chain.ParentOpt)
            {
                if (IsUsingAlias(chain.Imports.UsingAliases, name, isSemanticModel))
                {
                    return true;
                }
            }

            return false;
        }

        private BoundExpression BindDynamicMemberAccess(
            ExpressionSyntax node,
            BoundExpression boundLeft,
            SimpleNameSyntax right,
            bool invoked,
            bool indexed,
            BindingDiagnosticBag diagnostics)
        {
            // We have an expression of the form "dynExpr.Name" or "dynExpr.Name<X>"

            SeparatedSyntaxList<TypeSyntax> typeArgumentsSyntax = right.Kind() == SyntaxKind.GenericName ?
                ((GenericNameSyntax)right).TypeArgumentList.Arguments :
                default(SeparatedSyntaxList<TypeSyntax>);
            bool rightHasTypeArguments = typeArgumentsSyntax.Count > 0;
            ImmutableArray<TypeWithAnnotations> typeArgumentsWithAnnotations = rightHasTypeArguments ?
                BindTypeArguments(typeArgumentsSyntax, diagnostics) :
                default(ImmutableArray<TypeWithAnnotations>);

            bool hasErrors = false;

            if (!invoked && rightHasTypeArguments)
            {
                // error CS0307: The property 'P' cannot be used with type arguments
                Error(diagnostics, ErrorCode.ERR_TypeArgsNotAllowed, right, right.Identifier.Text, SymbolKind.Property.Localize());
                hasErrors = true;
            }

            if (rightHasTypeArguments)
            {
                for (int i = 0; i < typeArgumentsWithAnnotations.Length; ++i)
                {
                    var typeArgument = typeArgumentsWithAnnotations[i];
                    if (typeArgument.Type.IsPointerOrFunctionPointer() || typeArgument.Type.IsRestrictedType())
                    {
                        // "The type '{0}' may not be used as a type argument"
                        Error(diagnostics, ErrorCode.ERR_BadTypeArgument, typeArgumentsSyntax[i], typeArgument.Type);
                        hasErrors = true;
                    }
                }
            }

            return new BoundDynamicMemberAccess(
                syntax: node,
                receiver: boundLeft,
                typeArgumentsOpt: typeArgumentsWithAnnotations,
                name: right.Identifier.ValueText,
                invoked: invoked,
                indexed: indexed,
                type: Compilation.DynamicType,
                hasErrors: hasErrors);
        }

        /// <summary>
        /// Bind the RHS of a member access expression, given the bound LHS.
        /// It is assumed that CheckValue has not been called on the LHS.
        /// </summary>
        /// <remarks>
        /// If new checks are added to this method, they will also need to be added to <see cref="MakeQueryInvocation(CSharpSyntaxNode, BoundExpression, string, TypeSyntax, TypeWithAnnotations, BindingDiagnosticBag)"/>.
        /// </remarks>
        private BoundExpression BindMemberAccessWithBoundLeft(
            ExpressionSyntax node,
            BoundExpression boundLeft,
            SimpleNameSyntax right,
            SyntaxToken operatorToken,
            bool invoked,
            bool indexed,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node != null);
            Debug.Assert(boundLeft != null);

            boundLeft = MakeMemberAccessValue(boundLeft, diagnostics);

            TypeSymbol leftType = boundLeft.Type;

            if ((object)leftType != null && leftType.IsDynamic())
            {
                // There are some sources of a `dynamic` typed value that can be known before runtime
                // to be invalid. For example, accessing a set-only property whose type is dynamic:
                //   dynamic Goo { set; }
                // If Goo itself is a dynamic thing (e.g. in `x.Goo.Bar`, `x` is dynamic, and we're
                // currently checking Bar), then CheckValue will do nothing.
                boundLeft = CheckValue(boundLeft, BindValueKind.RValue, diagnostics);
                return BindDynamicMemberAccess(node, boundLeft, right, invoked, indexed, diagnostics);
            }

            // No member accesses on void
            if ((object)leftType != null && leftType.IsVoidType())
            {
                diagnostics.Add(ErrorCode.ERR_BadUnaryOp, operatorToken.GetLocation(), SyntaxFacts.GetText(operatorToken.Kind()), leftType);
                return BadExpression(node, boundLeft);
            }

            // No member accesses on default
            if (boundLeft.IsLiteralDefault())
            {
                DiagnosticInfo diagnosticInfo = new CSDiagnosticInfo(ErrorCode.ERR_BadOpOnNullOrDefaultOrNew, SyntaxFacts.GetText(operatorToken.Kind()), boundLeft.Display);
                diagnostics.Add(new CSDiagnostic(diagnosticInfo, operatorToken.GetLocation()));
                return BadExpression(node, boundLeft);
            }

            if (boundLeft.Kind == BoundKind.UnboundLambda)
            {
                Debug.Assert((object)leftType == null);

                var msgId = ((UnboundLambda)boundLeft).MessageID;
                diagnostics.Add(ErrorCode.ERR_BadUnaryOp, node.Location, SyntaxFacts.GetText(operatorToken.Kind()), msgId.Localize());
                return BadExpression(node, boundLeft);
            }

            boundLeft = BindToNaturalType(boundLeft, diagnostics);
            leftType = boundLeft.Type;
            var lookupResult = LookupResult.GetInstance();
            try
            {
                LookupOptions options = LookupOptions.AllMethodsOnArityZero;
                if (invoked)
                {
                    options |= LookupOptions.MustBeInvocableIfMember;
                }

                var typeArgumentsSyntax = right.Kind() == SyntaxKind.GenericName ? ((GenericNameSyntax)right).TypeArgumentList.Arguments : default(SeparatedSyntaxList<TypeSyntax>);
                var typeArguments = typeArgumentsSyntax.Count > 0 ? BindTypeArguments(typeArgumentsSyntax, diagnostics) : default(ImmutableArray<TypeWithAnnotations>);

                // A member-access consists of a primary-expression, a predefined-type, or a 
                // qualified-alias-member, followed by a "." token, followed by an identifier, 
                // optionally followed by a type-argument-list.

                // A member-access is either of the form E.I or of the form E.I<A1, ..., AK>, where
                // E is a primary-expression, I is a single identifier and <A1, ..., AK> is an
                // optional type-argument-list. When no type-argument-list is specified, consider K
                // to be zero. 

                // UNDONE: A member-access with a primary-expression of type dynamic is dynamically bound. 
                // UNDONE: In this case the compiler classifies the member access as a property access of 
                // UNDONE: type dynamic. The rules below to determine the meaning of the member-access are 
                // UNDONE: then applied at run-time, using the run-time type instead of the compile-time 
                // UNDONE: type of the primary-expression. If this run-time classification leads to a method 
                // UNDONE: group, then the member access must be the primary-expression of an invocation-expression.

                // The member-access is evaluated and classified as follows:

                var rightName = right.Identifier.ValueText;
                var rightArity = right.Arity;
                BoundExpression result;

                switch (boundLeft.Kind)
                {
                    case BoundKind.NamespaceExpression:
                        {
                            result = tryBindMemberAccessWithBoundNamespaceLeft(((BoundNamespaceExpression)boundLeft).NamespaceSymbol, node, boundLeft, right, diagnostics, lookupResult, options, typeArgumentsSyntax, typeArguments, rightName, rightArity);
                            if (result is object)
                            {
                                return result;
                            }

                            break;
                        }
                    case BoundKind.TypeExpression:
                        {
                            result = tryBindMemberAccessWithBoundTypeLeft(node, boundLeft, right, invoked, indexed, diagnostics, leftType, lookupResult, options, typeArgumentsSyntax, typeArguments, rightName, rightArity);
                            if (result is object)
                            {
                                return result;
                            }

                            break;
                        }
                    case BoundKind.TypeOrValueExpression:
                        {
                            // CheckValue call will occur in ReplaceTypeOrValueReceiver.
                            // NOTE: This means that we won't get CheckValue diagnostics in error scenarios,
                            // but they would be cascading anyway.
                            return BindInstanceMemberAccess(node, right, boundLeft, rightName, rightArity, typeArgumentsSyntax, typeArguments, invoked, indexed, diagnostics);
                        }
                    default:
                        {
                            // Can't dot into the null literal
                            if (boundLeft.Kind == BoundKind.Literal && ((BoundLiteral)boundLeft).ConstantValueOpt == ConstantValue.Null)
                            {
                                if (!boundLeft.HasAnyErrors)
                                {
                                    Error(diagnostics, ErrorCode.ERR_BadUnaryOp, node, operatorToken.Text, boundLeft.Display);
                                }

                                return BadExpression(node, boundLeft);
                            }
                            else if ((object)leftType != null)
                            {
                                // NB: We don't know if we really only need RValue access, or if we are actually
                                // passing the receiver implicitly by ref (e.g. in a struct instance method invocation).
                                // These checks occur later.
                                boundLeft = CheckValue(boundLeft, BindValueKind.RValue, diagnostics);
                                boundLeft = BindToNaturalType(boundLeft, diagnostics);
                                return BindInstanceMemberAccess(node, right, boundLeft, rightName, rightArity, typeArgumentsSyntax, typeArguments, invoked, indexed, diagnostics);
                            }
                            break;
                        }
                }

                this.BindMemberAccessReportError(node, right, rightName, boundLeft, lookupResult.Error, diagnostics);
                return BindMemberAccessBadResult(node, rightName, boundLeft, lookupResult.Error, lookupResult.Symbols.ToImmutable(), lookupResult.Kind);
            }
            finally
            {
                lookupResult.Free();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            BoundExpression tryBindMemberAccessWithBoundNamespaceLeft(
                NamespaceSymbol ns,
                ExpressionSyntax node,
                BoundExpression boundLeft,
                SimpleNameSyntax right,
                BindingDiagnosticBag diagnostics,
                LookupResult lookupResult,
                LookupOptions options,
                SeparatedSyntaxList<TypeSyntax> typeArgumentsSyntax,
                ImmutableArray<TypeWithAnnotations> typeArguments,
                string rightName,
                int rightArity)
            {
                // If K is zero and E is a namespace and E contains a nested namespace with name I, 
                // then the result is that namespace.

                CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                this.LookupMembersWithFallback(lookupResult, ns, rightName, rightArity, ref useSiteInfo, options: options);
                diagnostics.Add(right, useSiteInfo);

                ArrayBuilder<Symbol> symbols = lookupResult.Symbols;

                if (lookupResult.IsMultiViable)
                {
                    bool wasError;
                    Symbol sym = ResultSymbol(lookupResult, rightName, rightArity, node, diagnostics, false, out wasError, ns, options);
                    if (wasError)
                    {
                        return new BoundBadExpression(node, LookupResultKind.Ambiguous, lookupResult.Symbols.AsImmutable(), ImmutableArray.Create(boundLeft), CreateErrorType(rightName), hasErrors: true);
                    }
                    else if (sym.Kind == SymbolKind.Namespace)
                    {
                        return new BoundNamespaceExpression(node, (NamespaceSymbol)sym);
                    }
                    else
                    {
                        Debug.Assert(sym.Kind == SymbolKind.NamedType);
                        var type = (NamedTypeSymbol)sym;

                        if (!typeArguments.IsDefault)
                        {
                            type = ConstructNamedTypeUnlessTypeArgumentOmitted(right, type, typeArgumentsSyntax, typeArguments, diagnostics);
                        }

                        ReportDiagnosticsIfObsolete(diagnostics, type, node, hasBaseReceiver: false);

                        return new BoundTypeExpression(node, null, type);
                    }
                }
                else if (lookupResult.Kind == LookupResultKind.WrongArity)
                {
                    Debug.Assert(symbols.Count > 0);
                    Debug.Assert(symbols[0].Kind == SymbolKind.NamedType);

                    Error(diagnostics, lookupResult.Error, right);

                    return new BoundTypeExpression(node, null,
                                new ExtendedErrorTypeSymbol(GetContainingNamespaceOrType(symbols[0]), symbols.ToImmutable(), lookupResult.Kind, lookupResult.Error, rightArity));
                }
                else if (lookupResult.Kind == LookupResultKind.Empty)
                {
                    Debug.Assert(lookupResult.IsClear, "If there's a legitimate reason for having candidates without a reason, then we should produce something intelligent in such cases.");
                    Debug.Assert(lookupResult.Error == null);
                    NotFound(node, rightName, rightArity, rightName, diagnostics, aliasOpt: null, qualifierOpt: ns, options: options);

                    return new BoundBadExpression(node, lookupResult.Kind, symbols.AsImmutable(), ImmutableArray.Create(boundLeft), CreateErrorType(rightName), hasErrors: true);
                }

                return null;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            BoundExpression tryBindMemberAccessWithBoundTypeLeft(
                ExpressionSyntax node,
                BoundExpression boundLeft,
                SimpleNameSyntax right,
                bool invoked,
                bool indexed,
                BindingDiagnosticBag diagnostics,
                TypeSymbol leftType,
                LookupResult lookupResult,
                LookupOptions options,
                SeparatedSyntaxList<TypeSyntax> typeArgumentsSyntax,
                ImmutableArray<TypeWithAnnotations> typeArguments,
                string rightName,
                int rightArity)
            {
                Debug.Assert((object)leftType != null);
                if (leftType.TypeKind == TypeKind.TypeParameter)
                {
                    CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                    this.LookupMembersWithFallback(lookupResult, leftType, rightName, rightArity, ref useSiteInfo, basesBeingResolved: null, options: options | LookupOptions.MustNotBeInstance | LookupOptions.MustBeAbstract);
                    diagnostics.Add(right, useSiteInfo);
                    if (lookupResult.IsMultiViable)
                    {
                        CheckFeatureAvailability(boundLeft.Syntax, MessageID.IDS_FeatureStaticAbstractMembersInInterfaces, diagnostics);
                        return BindMemberOfType(node, right, rightName, rightArity, indexed, boundLeft, typeArgumentsSyntax, typeArguments, lookupResult, BoundMethodGroupFlags.None, diagnostics: diagnostics);
                    }
                    else if (lookupResult.IsClear)
                    {
                        Error(diagnostics, ErrorCode.ERR_BadSKunknown, boundLeft.Syntax, leftType, MessageID.IDS_SK_TYVAR.Localize());
                        return BadExpression(node, LookupResultKind.NotAValue, boundLeft);
                    }
                }
                else if (this.EnclosingNameofArgument == node)
                {
                    // Support selecting an extension method from a type name in nameof(.)
                    return BindInstanceMemberAccess(node, right, boundLeft, rightName, rightArity, typeArgumentsSyntax, typeArguments, invoked, indexed, diagnostics);
                }
                else
                {
                    CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                    this.LookupMembersWithFallback(lookupResult, leftType, rightName, rightArity, ref useSiteInfo, basesBeingResolved: null, options: options);
                    diagnostics.Add(right, useSiteInfo);
                    if (lookupResult.IsMultiViable)
                    {
                        return BindMemberOfType(node, right, rightName, rightArity, indexed, boundLeft, typeArgumentsSyntax, typeArguments, lookupResult, BoundMethodGroupFlags.None, diagnostics: diagnostics);
                    }
                }

                return null;
            }
        }

        private void WarnOnAccessOfOffDefault(SyntaxNode node, BoundExpression boundLeft, BindingDiagnosticBag diagnostics)
        {
            if ((boundLeft is BoundDefaultLiteral || boundLeft is BoundDefaultExpression) && boundLeft.ConstantValue == ConstantValue.Null &&
                Compilation.LanguageVersion < MessageID.IDS_FeatureNullableReferenceTypes.RequiredVersion())
            {
                Error(diagnostics, ErrorCode.WRN_DotOnDefault, node, boundLeft.Type);
            }
        }

        /// <summary>
        /// Create a value from the expression that can be used as a left-hand-side
        /// of a member access. This method special-cases method and property
        /// groups only. All other expressions are returned as is.
        /// </summary>
        private BoundExpression MakeMemberAccessValue(BoundExpression expr, BindingDiagnosticBag diagnostics)
        {
            switch (expr.Kind)
            {
                case BoundKind.MethodGroup:
                    {
                        var methodGroup = (BoundMethodGroup)expr;
                        CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                        var resolution = this.ResolveMethodGroup(methodGroup, analyzedArguments: null, isMethodGroupConversion: false, useSiteInfo: ref useSiteInfo);
                        diagnostics.Add(expr.Syntax, useSiteInfo);
                        if (!expr.HasAnyErrors)
                        {
                            diagnostics.AddRange(resolution.Diagnostics);

                            if (resolution.MethodGroup != null && !resolution.HasAnyErrors)
                            {
                                Debug.Assert(!resolution.IsEmpty);
                                var method = resolution.MethodGroup.Methods[0];
                                Error(diagnostics, ErrorCode.ERR_BadSKunknown, methodGroup.NameSyntax, method, MessageID.IDS_SK_METHOD.Localize());
                            }
                        }
                        expr = this.BindMemberAccessBadResult(methodGroup);
                        resolution.Free();
                        return expr;
                    }

                case BoundKind.PropertyGroup:
                    return BindIndexedPropertyAccess((BoundPropertyGroup)expr, mustHaveAllOptionalParameters: false, diagnostics: diagnostics);

                default:
                    return BindToNaturalType(expr, diagnostics);
            }
        }

        private BoundExpression BindInstanceMemberAccess(
            SyntaxNode node,
            SyntaxNode right,
            BoundExpression boundLeft,
            string rightName,
            int rightArity,
            SeparatedSyntaxList<TypeSyntax> typeArgumentsSyntax,
            ImmutableArray<TypeWithAnnotations> typeArgumentsWithAnnotations,
            bool invoked,
            bool indexed,
            BindingDiagnosticBag diagnostics,
            bool searchExtensionMethodsIfNecessary = true)
        {
            Debug.Assert(rightArity == (typeArgumentsWithAnnotations.IsDefault ? 0 : typeArgumentsWithAnnotations.Length));
            var leftType = boundLeft.Type;
            LookupOptions options = LookupOptions.AllMethodsOnArityZero;
            if (invoked)
            {
                options |= LookupOptions.MustBeInvocableIfMember;
            }

            var lookupResult = LookupResult.GetInstance();
            try
            {
                // If E is a property access, indexer access, variable, or value, the type of
                // which is T, and a member lookup of I in T with K type arguments produces a
                // match, then E.I is evaluated and classified as follows:

                // UNDONE: Classify E as prop access, indexer access, variable or value

                bool leftIsBaseReference = boundLeft.Kind == BoundKind.BaseReference;
                if (leftIsBaseReference)
                {
                    options |= LookupOptions.UseBaseReferenceAccessibility;
                }

                CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                this.LookupMembersWithFallback(lookupResult, leftType, rightName, rightArity, ref useSiteInfo, basesBeingResolved: null, options: options);
                diagnostics.Add(right, useSiteInfo);

                // SPEC: Otherwise, an attempt is made to process E.I as an extension method invocation.
                // SPEC: If this fails, E.I is an invalid member reference, and a binding-time error occurs.
                searchExtensionMethodsIfNecessary = searchExtensionMethodsIfNecessary && !leftIsBaseReference;

                BoundMethodGroupFlags flags = 0;
                if (searchExtensionMethodsIfNecessary)
                {
                    flags |= BoundMethodGroupFlags.SearchExtensionMethods;
                }

                if (lookupResult.IsMultiViable)
                {
                    return BindMemberOfType(node, right, rightName, rightArity, indexed, boundLeft, typeArgumentsSyntax, typeArgumentsWithAnnotations, lookupResult, flags, diagnostics);
                }

                if (searchExtensionMethodsIfNecessary)
                {
                    var boundMethodGroup = new BoundMethodGroup(
                        node,
                        typeArgumentsWithAnnotations,
                        boundLeft,
                        rightName,
                        lookupResult.Symbols.All(s => s.Kind == SymbolKind.Method) ? lookupResult.Symbols.SelectAsArray(s_toMethodSymbolFunc) : ImmutableArray<MethodSymbol>.Empty,
                        lookupResult,
                        flags,
                        this);

                    if (!boundMethodGroup.HasErrors && typeArgumentsSyntax.Any(SyntaxKind.OmittedTypeArgument))
                    {
                        Error(diagnostics, ErrorCode.ERR_OmittedTypeArgument, node);
                    }

                    return boundMethodGroup;
                }

                this.BindMemberAccessReportError(node, right, rightName, boundLeft, lookupResult.Error, diagnostics);
                return BindMemberAccessBadResult(node, rightName, boundLeft, lookupResult.Error, lookupResult.Symbols.ToImmutable(), lookupResult.Kind);
            }
            finally
            {
                lookupResult.Free();
            }
        }

        private void BindMemberAccessReportError(BoundMethodGroup node, BindingDiagnosticBag diagnostics)
        {
            var nameSyntax = node.NameSyntax;
            var syntax = node.MemberAccessExpressionSyntax ?? nameSyntax;
            this.BindMemberAccessReportError(syntax, nameSyntax, node.Name, node.ReceiverOpt, node.LookupError, diagnostics);
        }

        /// <summary>
        /// Report the error from member access lookup. Or, if there
        /// was no explicit error from lookup, report "no such member".
        /// </summary>
        private void BindMemberAccessReportError(
            SyntaxNode node,
            SyntaxNode name,
            string plainName,
            BoundExpression boundLeft,
            DiagnosticInfo lookupError,
            BindingDiagnosticBag diagnostics)
        {
            if (boundLeft.HasAnyErrors && boundLeft.Kind != BoundKind.TypeOrValueExpression)
            {
                return;
            }

            if (lookupError != null)
            {
                // CONSIDER: there are some cases where Dev10 uses the span of "node",
                // rather than "right".
                diagnostics.Add(new CSDiagnostic(lookupError, name.Location));
            }
            else if (node.IsQuery())
            {
                ReportQueryLookupFailed(node, boundLeft, plainName, ImmutableArray<Symbol>.Empty, diagnostics);
            }
            else
            {

                if ((object)boundLeft.Type == null)
                {
                    Error(diagnostics, ErrorCode.ERR_NoSuchMember, name, boundLeft.Display, plainName);
                }
                else if (boundLeft.Kind == BoundKind.TypeExpression ||
                    boundLeft.Kind == BoundKind.BaseReference ||
                    node.Kind() == SyntaxKind.AwaitExpression && plainName == WellKnownMemberNames.GetResult)
                {
                    Error(diagnostics, ErrorCode.ERR_NoSuchMember, name, boundLeft.Type, plainName);
                }
                else if (WouldUsingSystemFindExtension(boundLeft.Type, plainName))
                {
                    Error(diagnostics, ErrorCode.ERR_NoSuchMemberOrExtensionNeedUsing, name, boundLeft.Type, plainName, "System");
                }
                else
                {
                    Error(diagnostics, ErrorCode.ERR_NoSuchMemberOrExtension, name, boundLeft.Type, plainName);
                }
            }
        }

        private bool WouldUsingSystemFindExtension(TypeSymbol receiver, string methodName)
        {
            // we have a special case to make the diagnostic for await expressions more clear for Windows:
            // if the receiver type is a windows RT async interface and the method name is GetAwaiter,
            // then we would suggest a using directive for "System".
            // TODO: we should check if such a using directive would actually help, or if there is already one in scope.
            return methodName == WellKnownMemberNames.GetAwaiter && ImplementsWinRTAsyncInterface(receiver);
        }

        /// <summary>
        /// Return true if the given type is or implements a WinRTAsyncInterface.
        /// </summary>
        private bool ImplementsWinRTAsyncInterface(TypeSymbol type)
        {
            return IsWinRTAsyncInterface(type) || type.AllInterfacesNoUseSiteDiagnostics.Any(i => IsWinRTAsyncInterface(i));
        }

        private bool IsWinRTAsyncInterface(TypeSymbol type)
        {
            if (!type.IsInterfaceType())
            {
                return false;
            }

            var namedType = ((NamedTypeSymbol)type).ConstructedFrom;
            return
                TypeSymbol.Equals(namedType, Compilation.GetWellKnownType(WellKnownType.Windows_Foundation_IAsyncAction), TypeCompareKind.ConsiderEverything2) ||
                TypeSymbol.Equals(namedType, Compilation.GetWellKnownType(WellKnownType.Windows_Foundation_IAsyncActionWithProgress_T), TypeCompareKind.ConsiderEverything2) ||
                TypeSymbol.Equals(namedType, Compilation.GetWellKnownType(WellKnownType.Windows_Foundation_IAsyncOperation_T), TypeCompareKind.ConsiderEverything2) ||
                TypeSymbol.Equals(namedType, Compilation.GetWellKnownType(WellKnownType.Windows_Foundation_IAsyncOperationWithProgress_T2), TypeCompareKind.ConsiderEverything2);
        }

        private BoundExpression BindMemberAccessBadResult(BoundMethodGroup node)
        {
            var nameSyntax = node.NameSyntax;
            var syntax = node.MemberAccessExpressionSyntax ?? nameSyntax;
            return this.BindMemberAccessBadResult(syntax, node.Name, node.ReceiverOpt, node.LookupError, StaticCast<Symbol>.From(node.Methods), node.ResultKind);
        }

        /// <summary>
        /// Return a BoundExpression representing the invalid member.
        /// </summary>
        private BoundExpression BindMemberAccessBadResult(
            SyntaxNode node,
            string nameString,
            BoundExpression boundLeft,
            DiagnosticInfo lookupError,
            ImmutableArray<Symbol> symbols,
            LookupResultKind lookupKind)
        {
            if (symbols.Length > 0 && symbols[0].Kind == SymbolKind.Method)
            {
                var builder = ArrayBuilder<MethodSymbol>.GetInstance();
                foreach (var s in symbols)
                {
                    var m = s as MethodSymbol;
                    if ((object)m != null) builder.Add(m);
                }
                var methods = builder.ToImmutableAndFree();

                // Expose the invalid methods as a BoundMethodGroup.
                // Since we do not want to perform further method
                // lookup, searchExtensionMethods is set to false.
                // Don't bother calling ConstructBoundMethodGroupAndReportOmittedTypeArguments -
                // we've reported other errors.
                return new BoundMethodGroup(
                    node,
                    default(ImmutableArray<TypeWithAnnotations>),
                    nameString,
                    methods,
                    methods.Length == 1 ? methods[0] : null,
                    lookupError,
                    flags: BoundMethodGroupFlags.None,
                    functionType: null,
                    receiverOpt: boundLeft,
                    resultKind: lookupKind,
                    hasErrors: true);
            }

            var symbolOpt = symbols.Length == 1 ? symbols[0] : null;
            return new BoundBadExpression(
                node,
                lookupKind,
                (object)symbolOpt == null ? ImmutableArray<Symbol>.Empty : ImmutableArray.Create(symbolOpt),
                boundLeft == null ? ImmutableArray<BoundExpression>.Empty : ImmutableArray.Create(BindToTypeForErrorRecovery(boundLeft)),
                GetNonMethodMemberType(symbolOpt));
        }

        private TypeSymbol GetNonMethodMemberType(Symbol symbolOpt)
        {
            TypeSymbol resultType = null;
            if ((object)symbolOpt != null)
            {
                switch (symbolOpt.Kind)
                {
                    case SymbolKind.Field:
                        resultType = ((FieldSymbol)symbolOpt).GetFieldType(this.FieldsBeingBound).Type;
                        break;
                    case SymbolKind.Property:
                        resultType = ((PropertySymbol)symbolOpt).Type;
                        break;
                    case SymbolKind.Event:
                        resultType = ((EventSymbol)symbolOpt).Type;
                        break;
                }
            }
            return resultType ?? CreateErrorType();
        }

        /// <summary>
        /// Combine the receiver and arguments of an extension method
        /// invocation into a single argument list to allow overload resolution
        /// to treat the invocation as a static method invocation with no receiver.
        /// </summary>
        private static void CombineExtensionMethodArguments(BoundExpression receiver, AnalyzedArguments originalArguments, AnalyzedArguments extensionMethodArguments)
        {
            Debug.Assert(receiver != null);
            Debug.Assert(extensionMethodArguments.Arguments.Count == 0);
            Debug.Assert(extensionMethodArguments.Names.Count == 0);
            Debug.Assert(extensionMethodArguments.RefKinds.Count == 0);

            extensionMethodArguments.IsExtensionMethodInvocation = true;
            extensionMethodArguments.Arguments.Add(receiver);
            extensionMethodArguments.Arguments.AddRange(originalArguments.Arguments);

            if (originalArguments.Names.Count > 0)
            {
                extensionMethodArguments.Names.Add(null);
                extensionMethodArguments.Names.AddRange(originalArguments.Names);
            }

            if (originalArguments.RefKinds.Count > 0)
            {
                extensionMethodArguments.RefKinds.Add(RefKind.None);
                extensionMethodArguments.RefKinds.AddRange(originalArguments.RefKinds);
            }
        }

        /// <summary>
        /// Binds a static or instance member access.
        /// </summary>
        private BoundExpression BindMemberOfType(
            SyntaxNode node,
            SyntaxNode right,
            string plainName,
            int arity,
            bool indexed,
            BoundExpression left,
            SeparatedSyntaxList<TypeSyntax> typeArgumentsSyntax,
            ImmutableArray<TypeWithAnnotations> typeArgumentsWithAnnotations,
            LookupResult lookupResult,
            BoundMethodGroupFlags methodGroupFlags,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node != null);
            Debug.Assert(left != null);
            Debug.Assert(lookupResult.IsMultiViable);
            Debug.Assert(lookupResult.Symbols.Any());

            var members = ArrayBuilder<Symbol>.GetInstance();
            BoundExpression result;
            bool wasError;
            Symbol symbol = GetSymbolOrMethodOrPropertyGroup(lookupResult, right, plainName, arity, members, diagnostics, out wasError,
                                                             qualifierOpt: left is BoundTypeExpression typeExpr ? typeExpr.Type : null);

            if ((object)symbol == null)
            {
                Debug.Assert(members.Count > 0);

                // If I identifies one or more methods, then the result is a method group with
                // no associated instance expression. If a type argument list was specified, it
                // is used in calling a generic method.

                // (Note that for static methods, we are stashing away the type expression in
                // the receiver of the method group, even though the spec notes that there is
                // no associated instance expression.)

                result = ConstructBoundMemberGroupAndReportOmittedTypeArguments(
                    node,
                    typeArgumentsSyntax,
                    typeArgumentsWithAnnotations,
                    left,
                    plainName,
                    members,
                    lookupResult,
                    methodGroupFlags,
                    wasError,
                    diagnostics);
            }
            else
            {
                // methods are special because of extension methods.
                Debug.Assert(symbol.Kind != SymbolKind.Method);
                left = ReplaceTypeOrValueReceiver(left, symbol.IsStatic || symbol.Kind == SymbolKind.NamedType, diagnostics);

                // Events are handled later as we don't know yet if we are binding to the event or it's backing field.
                if (symbol.Kind != SymbolKind.Event)
                {
                    ReportDiagnosticsIfObsolete(diagnostics, symbol, node, hasBaseReceiver: left.Kind == BoundKind.BaseReference);
                }

                switch (symbol.Kind)
                {
                    case SymbolKind.NamedType:
                    case SymbolKind.ErrorType:
                        if (IsInstanceReceiver(left) == true && !wasError)
                        {
                            // CS0572: 'B': cannot reference a type through an expression; try 'A.B' instead
                            Error(diagnostics, ErrorCode.ERR_BadTypeReference, right, plainName, symbol);
                            wasError = true;
                        }

                        // If I identifies a type, then the result is that type constructed with
                        // the given type arguments.
                        var type = (NamedTypeSymbol)symbol;
                        if (!typeArgumentsWithAnnotations.IsDefault)
                        {
                            type = ConstructNamedTypeUnlessTypeArgumentOmitted(right, type, typeArgumentsSyntax, typeArgumentsWithAnnotations, diagnostics);
                        }

                        result = new BoundTypeExpression(
                            syntax: node,
                            aliasOpt: null,
                            boundContainingTypeOpt: left as BoundTypeExpression,
                            boundDimensionsOpt: ImmutableArray<BoundExpression>.Empty,
                            typeWithAnnotations: TypeWithAnnotations.Create(type));
                        break;

                    case SymbolKind.Property:
                        // If I identifies a static property, then the result is a property
                        // access with no associated instance expression.
                        result = BindPropertyAccess(node, left, (PropertySymbol)symbol, diagnostics, lookupResult.Kind, hasErrors: wasError);
                        break;

                    case SymbolKind.Event:
                        // If I identifies a static event, then the result is an event
                        // access with no associated instance expression.
                        result = BindEventAccess(node, left, (EventSymbol)symbol, diagnostics, lookupResult.Kind, hasErrors: wasError);
                        break;

                    case SymbolKind.Field:
                        // If I identifies a static field:
                        // UNDONE: If the field is readonly and the reference occurs outside the static constructor of 
                        // UNDONE: the class or struct in which the field is declared, then the result is a value, namely
                        // UNDONE: the value of the static field I in E.
                        // UNDONE: Otherwise, the result is a variable, namely the static field I in E.
                        // UNDONE: Need a way to mark an expression node as "I am a variable, not a value".
                        result = BindFieldAccess(node, left, (FieldSymbol)symbol, diagnostics, lookupResult.Kind, indexed, hasErrors: wasError);
                        break;

                    default:
                        throw ExceptionUtilities.UnexpectedValue(symbol.Kind);
                }
            }

            members.Free();
            return result;
        }

        protected MethodGroupResolution BindExtensionMethod(
            SyntaxNode expression,
            string methodName,
            AnalyzedArguments analyzedArguments,
            BoundExpression left,
            ImmutableArray<TypeWithAnnotations> typeArgumentsWithAnnotations,
            bool isMethodGroupConversion,
            RefKind returnRefKind,
            TypeSymbol returnType,
            bool withDependencies)
        {
            var firstResult = new MethodGroupResolution();
            AnalyzedArguments actualArguments = null;

            foreach (var scope in new ExtensionMethodScopes(this))
            {
                var methodGroup = MethodGroup.GetInstance();
                var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies);

                this.PopulateExtensionMethodsFromSingleBinder(scope, methodGroup, expression, left, methodName, typeArgumentsWithAnnotations, diagnostics);

                // analyzedArguments will be null if the caller is resolving for error recovery to the first method group
                // that can accept that receiver, regardless of arguments, when the signature cannot be inferred.
                // (In the error case of nameof(o.M) or the error case of o.M = null; for instance.)
                if (analyzedArguments == null)
                {
                    if (expression == EnclosingNameofArgument)
                    {
                        for (int i = methodGroup.Methods.Count - 1; i >= 0; i--)
                        {
                            if ((object)methodGroup.Methods[i].ReduceExtensionMethod(left.Type, this.Compilation) == null)
                                methodGroup.Methods.RemoveAt(i);
                        }
                    }

                    if (methodGroup.Methods.Count != 0)
                    {
                        return new MethodGroupResolution(methodGroup, diagnostics.ToReadOnlyAndFree());
                    }
                }

                if (methodGroup.Methods.Count == 0)
                {
                    methodGroup.Free();
                    diagnostics.Free();
                    continue;
                }

                if (actualArguments == null)
                {
                    // Create a set of arguments for overload resolution of the
                    // extension methods that includes the "this" parameter.
                    actualArguments = AnalyzedArguments.GetInstance();
                    CombineExtensionMethodArguments(left, analyzedArguments, actualArguments);
                }

                var overloadResolutionResult = OverloadResolutionResult<MethodSymbol>.GetInstance();
                bool allowRefOmittedArguments = methodGroup.Receiver.IsExpressionOfComImportType();
                CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                OverloadResolution.MethodInvocationOverloadResolution(
                    methods: methodGroup.Methods,
                    typeArguments: methodGroup.TypeArguments,
                    receiver: methodGroup.Receiver,
                    arguments: actualArguments,
                    result: overloadResolutionResult,
                    useSiteInfo: ref useSiteInfo,
                    isMethodGroupConversion: isMethodGroupConversion,
                    allowRefOmittedArguments: allowRefOmittedArguments,
                    returnRefKind: returnRefKind,
                    returnType: returnType);
                diagnostics.Add(expression, useSiteInfo);
                var sealedDiagnostics = diagnostics.ToReadOnlyAndFree();

                // Note: the MethodGroupResolution instance is responsible for freeing its copy of actual arguments
                var result = new MethodGroupResolution(methodGroup, null, overloadResolutionResult, AnalyzedArguments.GetInstance(actualArguments), methodGroup.ResultKind, sealedDiagnostics);

                // If the search in the current scope resulted in any applicable method (regardless of whether a best
                // applicable method could be determined) then our search is complete. Otherwise, store aside the
                // first non-applicable result and continue searching for an applicable result.
                if (result.HasAnyApplicableMethod)
                {
                    if (!firstResult.IsEmpty)
                    {
                        firstResult.MethodGroup.Free();
                        firstResult.OverloadResolutionResult.Free();
                    }
                    return result;
                }
                else if (firstResult.IsEmpty)
                {
                    firstResult = result;
                }
                else
                {
                    // Neither the first result, nor applicable. No need to save result.
                    overloadResolutionResult.Free();
                    methodGroup.Free();
                }
            }

            Debug.Assert((actualArguments == null) || !firstResult.IsEmpty);
            actualArguments?.Free();
            return firstResult;
        }

        private void PopulateExtensionMethodsFromSingleBinder(
            ExtensionMethodScope scope,
            MethodGroup methodGroup,
            SyntaxNode node,
            BoundExpression left,
            string rightName,
            ImmutableArray<TypeWithAnnotations> typeArgumentsWithAnnotations,
            BindingDiagnosticBag diagnostics)
        {
            int arity;
            LookupOptions options;
            if (typeArgumentsWithAnnotations.IsDefault)
            {
                arity = 0;
                options = LookupOptions.AllMethodsOnArityZero;
            }
            else
            {
                arity = typeArgumentsWithAnnotations.Length;
                options = LookupOptions.Default;
            }

            var lookupResult = LookupResult.GetInstance();
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            this.LookupExtensionMethodsInSingleBinder(scope, lookupResult, rightName, arity, options, ref useSiteInfo);
            diagnostics.Add(node, useSiteInfo);

            if (lookupResult.IsMultiViable)
            {
                Debug.Assert(lookupResult.Symbols.Any());
                var members = ArrayBuilder<Symbol>.GetInstance();
                bool wasError;
                Symbol symbol = GetSymbolOrMethodOrPropertyGroup(lookupResult, node, rightName, arity, members, diagnostics, out wasError, qualifierOpt: null);
                Debug.Assert((object)symbol == null);
                Debug.Assert(members.Count > 0);
                methodGroup.PopulateWithExtensionMethods(left, members, typeArgumentsWithAnnotations, lookupResult.Kind);
                members.Free();
            }

            lookupResult.Free();
        }

        protected BoundExpression BindFieldAccess(
            SyntaxNode node,
            BoundExpression receiver,
            FieldSymbol fieldSymbol,
            BindingDiagnosticBag diagnostics,
            LookupResultKind resultKind,
            bool indexed,
            bool hasErrors)
        {
            bool hasError = false;
            NamedTypeSymbol type = fieldSymbol.ContainingType;
            var isEnumField = (fieldSymbol.IsStatic && type.IsEnumType());

            if (isEnumField && !type.IsValidEnumType())
            {
                Error(diagnostics, ErrorCode.ERR_BindToBogus, node, fieldSymbol);
                hasError = true;
            }

            if (!hasError)
            {
                hasError = this.CheckInstanceOrStatic(node, receiver, fieldSymbol, ref resultKind, diagnostics);
            }

            if (!hasError && fieldSymbol.IsFixedSizeBuffer && !IsInsideNameof)
            {
                // SPEC: In a member access of the form E.I, if E is of a struct type and a member lookup of I in
                // that struct type identifies a fixed size member, then E.I is evaluated and classified as follows:
                // * If the expression E.I does not occur in an unsafe context, a compile-time error occurs.
                // * If E is classified as a value, a compile-time error occurs.
                // * Otherwise, if E is a moveable variable and the expression E.I is not a fixed_pointer_initializer,
                //   a compile-time error occurs.
                // * Otherwise, E references a fixed variable and the result of the expression is a pointer to the
                //   first element of the fixed size buffer member I in E. The result is of type S*, where S is
                //   the element type of I, and is classified as a value.

                TypeSymbol receiverType = receiver.Type;

                // Reflect errors that have been reported elsewhere...
                hasError = (object)receiverType == null || !receiverType.IsValueType;

                if (!hasError)
                {
                    var isFixedStatementExpression = SyntaxFacts.IsFixedStatementExpression(node);

                    if (IsMoveableVariable(receiver, out Symbol accessedLocalOrParameterOpt) != isFixedStatementExpression)
                    {
                        if (indexed)
                        {
                            // SPEC C# 7.3: If the fixed size buffer access is the receiver of an element_access_expression,
                            // E may be either fixed or moveable
                            CheckFeatureAvailability(node, MessageID.IDS_FeatureIndexingMovableFixedBuffers, diagnostics);
                        }
                        else
                        {
                            Error(diagnostics, isFixedStatementExpression ? ErrorCode.ERR_FixedNotNeeded : ErrorCode.ERR_FixedBufferNotFixed, node);
                            hasErrors = hasError = true;
                        }
                    }
                }

                if (!hasError)
                {
                    hasError = !CheckValueKind(node, receiver, BindValueKind.FixedReceiver, checkingReceiver: false, diagnostics: diagnostics);
                }
            }

            ConstantValue constantValueOpt = null;

            if (fieldSymbol.IsConst && !IsInsideNameof)
            {
                constantValueOpt = fieldSymbol.GetConstantValue(this.ConstantFieldsInProgress, this.IsEarlyAttributeBinder);
                if (constantValueOpt == ConstantValue.Unset)
                {
                    // Evaluating constant expression before dependencies
                    // have been evaluated. Treat this as a Bad value.
                    constantValueOpt = ConstantValue.Bad;
                }
            }

            if (!fieldSymbol.IsStatic)
            {
                WarnOnAccessOfOffDefault(node, receiver, diagnostics);
            }

            if (!IsBadBaseAccess(node, receiver, fieldSymbol, diagnostics))
            {
                CheckReceiverAndRuntimeSupportForSymbolAccess(node, receiver, fieldSymbol, diagnostics);
            }

            TypeSymbol fieldType = fieldSymbol.GetFieldType(this.FieldsBeingBound).Type;
            BoundExpression expr = new BoundFieldAccess(node, receiver, fieldSymbol, constantValueOpt, resultKind, fieldType, hasErrors: (hasErrors || hasError));

            // Spec 14.3: "Within an enum member initializer, values of other enum members are
            // always treated as having the type of their underlying type"
            if (this.InEnumMemberInitializer())
            {
                NamedTypeSymbol enumType = null;
                if (isEnumField)
                {
                    // This is an obvious consequence of the spec.
                    // It is for cases like:
                    // enum E {
                    //     A,
                    //     B = A + 1, //A is implicitly converted to int (underlying type)
                    // }
                    enumType = type;
                }
                else if (constantValueOpt != null && fieldType.IsEnumType())
                {
                    // This seems like a borderline SPEC VIOLATION that we're preserving for back compat.
                    // It is for cases like:
                    // const E e = E.A;
                    // enum E {
                    //     A,
                    //     B = e + 1, //e is implicitly converted to int (underlying type)
                    // }
                    enumType = (NamedTypeSymbol)fieldType;
                }

                if ((object)enumType != null)
                {
                    NamedTypeSymbol underlyingType = enumType.EnumUnderlyingType;
                    Debug.Assert((object)underlyingType != null);
                    expr = new BoundConversion(
                        node,
                        expr,
                        Conversion.ImplicitNumeric,
                        @checked: true,
                        explicitCastInCode: false,
                        conversionGroupOpt: null,
                        constantValueOpt: expr.ConstantValue,
                        type: underlyingType);
                }
            }

            return expr;
        }

        private bool InEnumMemberInitializer()
        {
            var containingType = this.ContainingType;
            return this.InFieldInitializer && (object)containingType != null && containingType.IsEnumType();
        }

        private BoundExpression BindPropertyAccess(
            SyntaxNode node,
            BoundExpression receiver,
            PropertySymbol propertySymbol,
            BindingDiagnosticBag diagnostics,
            LookupResultKind lookupResult,
            bool hasErrors)
        {
            bool hasError = this.CheckInstanceOrStatic(node, receiver, propertySymbol, ref lookupResult, diagnostics);

            if (!propertySymbol.IsStatic)
            {
                WarnOnAccessOfOffDefault(node, receiver, diagnostics);
            }

            return new BoundPropertyAccess(node, receiver, propertySymbol, lookupResult, propertySymbol.Type, hasErrors: (hasErrors || hasError));
        }

        private void CheckReceiverAndRuntimeSupportForSymbolAccess(SyntaxNode node, BoundExpression receiverOpt, Symbol symbol, BindingDiagnosticBag diagnostics)
        {
            if (symbol.ContainingType?.IsInterface == true)
            {
                if (symbol.IsStatic && symbol.IsAbstract)
                {
                    Debug.Assert(symbol is not TypeSymbol);

                    if (receiverOpt is BoundQueryClause { Value: var value })
                    {
                        receiverOpt = value;
                    }

                    if (receiverOpt is not BoundTypeExpression { Type: { TypeKind: TypeKind.TypeParameter } })
                    {
                        Error(diagnostics, ErrorCode.ERR_BadAbstractStaticMemberAccess, node);
                        return;
                    }

                    if (!Compilation.Assembly.RuntimeSupportsStaticAbstractMembersInInterfaces && Compilation.SourceModule != symbol.ContainingModule)
                    {
                        Error(diagnostics, ErrorCode.ERR_RuntimeDoesNotSupportStaticAbstractMembersInInterfaces, node);
                        return;
                    }
                }

                if (!Compilation.Assembly.RuntimeSupportsDefaultInterfaceImplementation && Compilation.SourceModule != symbol.ContainingModule)
                {
                    if (!symbol.IsStatic && !(symbol is TypeSymbol) &&
                        !symbol.IsImplementableInterfaceMember())
                    {
                        Error(diagnostics, ErrorCode.ERR_RuntimeDoesNotSupportDefaultInterfaceImplementation, node);
                    }
                    else
                    {
                        switch (symbol.DeclaredAccessibility)
                        {
                            case Accessibility.Protected:
                            case Accessibility.ProtectedOrInternal:
                            case Accessibility.ProtectedAndInternal:

                                Error(diagnostics, ErrorCode.ERR_RuntimeDoesNotSupportProtectedAccessForInterfaceMember, node);
                                break;
                        }
                    }
                }
            }
        }

        private BoundExpression BindEventAccess(
            SyntaxNode node,
            BoundExpression receiver,
            EventSymbol eventSymbol,
            BindingDiagnosticBag diagnostics,
            LookupResultKind lookupResult,
            bool hasErrors)
        {
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            bool isUsableAsField = eventSymbol.HasAssociatedField && this.IsAccessible(eventSymbol.AssociatedField, ref useSiteInfo, (receiver != null) ? receiver.Type : null);
            diagnostics.Add(node, useSiteInfo);

            bool hasError = this.CheckInstanceOrStatic(node, receiver, eventSymbol, ref lookupResult, diagnostics);

            if (!eventSymbol.IsStatic)
            {
                WarnOnAccessOfOffDefault(node, receiver, diagnostics);
            }

            return new BoundEventAccess(node, receiver, eventSymbol, isUsableAsField, lookupResult, eventSymbol.Type, hasErrors: (hasErrors || hasError));
        }

        // Say if the receive is an instance or a type, or could be either (returns null).
        private static bool? IsInstanceReceiver(BoundExpression receiver)
        {
            if (receiver == null)
            {
                return false;
            }
            else
            {
                switch (receiver.Kind)
                {
                    case BoundKind.PreviousSubmissionReference:
                        // Could be either instance or static reference.
                        return null;
                    case BoundKind.TypeExpression:
                        return false;
                    case BoundKind.QueryClause:
                        return IsInstanceReceiver(((BoundQueryClause)receiver).Value);
                    default:
                        return true;
                }
            }
        }

        private bool CheckInstanceOrStatic(
            SyntaxNode node,
            BoundExpression receiver,
            Symbol symbol,
            ref LookupResultKind resultKind,
            BindingDiagnosticBag diagnostics)
        {
            bool? instanceReceiver = IsInstanceReceiver(receiver);

            if (!symbol.RequiresInstanceReceiver())
            {
                if (instanceReceiver == true)
                {
                    ErrorCode errorCode = this.Flags.Includes(BinderFlags.ObjectInitializerMember) ?
                        ErrorCode.ERR_StaticMemberInObjectInitializer :
                        ErrorCode.ERR_ObjectProhibited;
                    Error(diagnostics, errorCode, node, symbol);
                    resultKind = LookupResultKind.StaticInstanceMismatch;
                    return true;
                }
            }
            else
            {
                if (instanceReceiver == false && !IsInsideNameof)
                {
                    Error(diagnostics, ErrorCode.ERR_ObjectRequired, node, symbol);
                    resultKind = LookupResultKind.StaticInstanceMismatch;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Given a viable LookupResult, report any ambiguity errors and return either a single
        /// non-method symbol or a method or property group. If the result set represents a
        /// collection of methods or a collection of properties where at least one of the properties
        /// is an indexed property, then 'methodOrPropertyGroup' is populated with the method or
        /// property group and the method returns null. Otherwise, the method returns a single
        /// symbol and 'methodOrPropertyGroup' is empty. (Since the result set is viable, there
        /// must be at least one symbol.) If the result set is ambiguous - either containing multiple
        /// members of different member types, or multiple properties but no indexed properties -
        /// then a diagnostic is reported for the ambiguity and a single symbol is returned.
        /// </summary>
        private Symbol GetSymbolOrMethodOrPropertyGroup(LookupResult result, SyntaxNode node, string plainName, int arity, ArrayBuilder<Symbol> methodOrPropertyGroup, BindingDiagnosticBag diagnostics, out bool wasError, NamespaceOrTypeSymbol qualifierOpt)
        {
            Debug.Assert(!methodOrPropertyGroup.Any());

            node = GetNameSyntax(node) ?? node;
            wasError = false;

            Debug.Assert(result.Kind != LookupResultKind.Empty);
            Debug.Assert(!result.Symbols.Any(s => s.IsIndexer()));

            Symbol other = null; // different member type from 'methodOrPropertyGroup'

            // Populate 'methodOrPropertyGroup' with a set of methods if any,
            // or a set of properties if properties but no methods. If there are
            // other member types, 'other' will be set to one of those members.
            foreach (var symbol in result.Symbols)
            {
                var kind = symbol.Kind;
                if (methodOrPropertyGroup.Count > 0)
                {
                    var existingKind = methodOrPropertyGroup[0].Kind;
                    if (existingKind != kind)
                    {
                        // Mix of different member kinds. Prefer methods over
                        // properties and properties over other members.
                        if ((existingKind == SymbolKind.Method) ||
                            ((existingKind == SymbolKind.Property) && (kind != SymbolKind.Method)))
                        {
                            other = symbol;
                            continue;
                        }

                        other = methodOrPropertyGroup[0];
                        methodOrPropertyGroup.Clear();
                    }
                }

                if ((kind == SymbolKind.Method) || (kind == SymbolKind.Property))
                {
                    // SPEC VIOLATION: The spec states "Members that include an override modifier are excluded from the set"
                    // SPEC VIOLATION: However, we are not going to do that here; we will keep the overriding member
                    // SPEC VIOLATION: in the method group. The reason is because for features like "go to definition"
                    // SPEC VIOLATION: we wish to go to the overriding member, not to the member of the base class.
                    // SPEC VIOLATION: Or, for code generation of a call to Int32.ToString() we want to generate
                    // SPEC VIOLATION: code that directly calls the Int32.ToString method with an int on the stack,
                    // SPEC VIOLATION: rather than making a virtual call to ToString on a boxed int.
                    methodOrPropertyGroup.Add(symbol);
                }
                else
                {
                    other = symbol;
                }
            }

            Debug.Assert(methodOrPropertyGroup.Any() || ((object)other != null));

            if ((methodOrPropertyGroup.Count > 0) &&
                IsMethodOrPropertyGroup(methodOrPropertyGroup))
            {
                // Ambiguities between methods and non-methods are reported here,
                // but all other ambiguities, including those between properties and
                // non-methods, are reported in ResultSymbol.
                if ((methodOrPropertyGroup[0].Kind == SymbolKind.Method) || ((object)other == null))
                {
                    // Result will be treated as a method or property group. Any additional
                    // checks, such as use-site errors, must be handled by the caller when
                    // converting to method invocation or property access.

                    if (result.Error != null)
                    {
                        Error(diagnostics, result.Error, node);
                        wasError = (result.Error.Severity == DiagnosticSeverity.Error);
                    }

                    return null;
                }
            }

            methodOrPropertyGroup.Clear();
            return ResultSymbol(result, plainName, arity, node, diagnostics, false, out wasError, qualifierOpt);
        }

        private static bool IsMethodOrPropertyGroup(ArrayBuilder<Symbol> members)
        {
            Debug.Assert(members.Count > 0);

            var member = members[0];

            // Members should be a consistent type.
            Debug.Assert(members.All(m => m.Kind == member.Kind));

            switch (member.Kind)
            {
                case SymbolKind.Method:
                    return true;

                case SymbolKind.Property:
                    Debug.Assert(members.All(m => !m.IsIndexer()));

                    // Do not treat a set of non-indexed properties as a property group, to
                    // avoid the overhead of a BoundPropertyGroup node and overload
                    // resolution for the common property access case. If there are multiple
                    // non-indexed properties (two properties P that differ by custom attributes
                    // for instance), the expectation is that the caller will report an ambiguity
                    // and choose one for error recovery.
                    foreach (PropertySymbol property in members)
                    {
                        if (property.IsIndexedProperty)
                        {
                            return true;
                        }
                    }
                    return false;

                default:
                    throw ExceptionUtilities.UnexpectedValue(member.Kind);
            }
        }

        private BoundExpression BindElementAccess(ElementAccessExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            BoundExpression receiver = BindExpression(node.Expression, diagnostics: diagnostics, invoked: false, indexed: true);
            return BindElementAccess(node, receiver, node.ArgumentList, diagnostics);
        }

        private BoundExpression BindElementAccess(ExpressionSyntax node, BoundExpression receiver, BracketedArgumentListSyntax argumentList, BindingDiagnosticBag diagnostics)
        {
            AnalyzedArguments analyzedArguments = AnalyzedArguments.GetInstance();
            try
            {
                BindArgumentsAndNames(argumentList, diagnostics, analyzedArguments);

                if (receiver.Kind == BoundKind.PropertyGroup)
                {
                    var propertyGroup = (BoundPropertyGroup)receiver;
                    return BindIndexedPropertyAccess(node, propertyGroup.ReceiverOpt, propertyGroup.Properties, analyzedArguments, diagnostics);
                }

                receiver = CheckValue(receiver, BindValueKind.RValue, diagnostics);
                receiver = BindToNaturalType(receiver, diagnostics);

                return BindElementOrIndexerAccess(node, receiver, analyzedArguments, diagnostics);
            }
            finally
            {
                analyzedArguments.Free();
            }
        }

        private BoundExpression BindElementOrIndexerAccess(ExpressionSyntax node, BoundExpression expr, AnalyzedArguments analyzedArguments, BindingDiagnosticBag diagnostics)
        {
            if ((object)expr.Type == null)
            {
                return BadIndexerExpression(node, expr, analyzedArguments, null, diagnostics);
            }

            WarnOnAccessOfOffDefault(node, expr, diagnostics);

            // Did we have any errors?
            if (analyzedArguments.HasErrors || expr.HasAnyErrors)
            {
                // At this point we definitely have reported an error, but we still might be 
                // able to get more semantic analysis of the indexing operation. We do not
                // want to report cascading errors.

                BoundExpression result = BindElementAccessCore(node, expr, analyzedArguments, BindingDiagnosticBag.Discarded);
                return result;
            }

            return BindElementAccessCore(node, expr, analyzedArguments, diagnostics);
        }

        private BoundExpression BadIndexerExpression(ExpressionSyntax node, BoundExpression expr, AnalyzedArguments analyzedArguments, DiagnosticInfo errorOpt, BindingDiagnosticBag diagnostics)
        {
            if (!expr.HasAnyErrors)
            {
                diagnostics.Add(errorOpt ?? new CSDiagnosticInfo(ErrorCode.ERR_BadIndexLHS, expr.Display), node.Location);
            }

            var childBoundNodes = BuildArgumentsForErrorRecovery(analyzedArguments).Add(expr);
            return new BoundBadExpression(node, LookupResultKind.Empty, ImmutableArray<Symbol>.Empty, childBoundNodes, CreateErrorType(), hasErrors: true);
        }

        private BoundExpression BindElementAccessCore(
             ExpressionSyntax node,
             BoundExpression expr,
             AnalyzedArguments arguments,
             BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node != null);
            Debug.Assert(expr != null);
            Debug.Assert((object)expr.Type != null);
            Debug.Assert(arguments != null);

            var exprType = expr.Type;
            switch (exprType.TypeKind)
            {
                case TypeKind.Array:
                    return BindArrayAccess(node, expr, arguments, diagnostics);

                case TypeKind.Dynamic:
                    return BindDynamicIndexer(node, expr, arguments, ImmutableArray<PropertySymbol>.Empty, diagnostics);

                case TypeKind.Pointer:
                    return BindPointerElementAccess(node, expr, arguments, diagnostics);

                case TypeKind.Class:
                case TypeKind.Struct:
                case TypeKind.Interface:
                case TypeKind.TypeParameter:
                    return BindIndexerAccess(node, expr, arguments, diagnostics);

                case TypeKind.Submission: // script class is synthesized and should not be used as a type of an indexer expression:
                default:
                    return BadIndexerExpression(node, expr, arguments, null, diagnostics);
            }
        }

        private BoundExpression BindArrayAccess(ExpressionSyntax node, BoundExpression expr, AnalyzedArguments arguments, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node != null);
            Debug.Assert(expr != null);
            Debug.Assert(arguments != null);

            // For an array access, the primary-no-array-creation-expression of the element-access
            // must be a value of an array-type. Furthermore, the argument-list of an array access
            // is not allowed to contain named arguments.The number of expressions in the
            // argument-list must be the same as the rank of the array-type, and each expression
            // must be of type int, uint, long, ulong, or must be implicitly convertible to one or
            // more of these types.

            if (arguments.Names.Count > 0)
            {
                Error(diagnostics, ErrorCode.ERR_NamedArgumentForArray, node);
            }

            bool hasErrors = ReportRefOrOutArgument(arguments, diagnostics);
            var arrayType = (ArrayTypeSymbol)expr.Type;

            // Note that the spec says to determine which of {int, uint, long, ulong} *each* index
            // expression is convertible to. That is not what C# 1 through 4 did; the
            // implementations instead determined which of those four types *all* of the index
            // expressions converted to. 

            int rank = arrayType.Rank;

            if (arguments.Arguments.Count != rank)
            {
                Error(diagnostics, ErrorCode.ERR_BadIndexCount, node, rank);
                return new BoundArrayAccess(node, expr, BuildArgumentsForErrorRecovery(arguments), arrayType.ElementType, hasErrors: true);
            }

            // Convert all the arguments to the array index type.
            BoundExpression[] convertedArguments = new BoundExpression[arguments.Arguments.Count];
            for (int i = 0; i < arguments.Arguments.Count; ++i)
            {
                BoundExpression argument = arguments.Arguments[i];

                BoundExpression index = ConvertToArrayIndex(argument, diagnostics, allowIndexAndRange: rank == 1);
                convertedArguments[i] = index;

                // NOTE: Dev10 only warns if rank == 1
                // Question: Why do we limit this warning to one-dimensional arrays?
                // Answer: Because multidimensional arrays can have nonzero lower bounds in the CLR.
                if (rank == 1 && !index.HasAnyErrors)
                {
                    ConstantValue constant = index.ConstantValue;
                    if (constant != null && constant.IsNegativeNumeric)
                    {
                        Error(diagnostics, ErrorCode.WRN_NegativeArrayIndex, index.Syntax);
                    }
                }
            }

            TypeSymbol resultType = rank == 1 &&
                TypeSymbol.Equals(
                    convertedArguments[0].Type,
                    Compilation.GetWellKnownType(WellKnownType.System_Range),
                    TypeCompareKind.ConsiderEverything)
                ? arrayType
                : arrayType.ElementType;

            return hasErrors
                ? new BoundArrayAccess(node, BindToTypeForErrorRecovery(expr), convertedArguments.Select(e => BindToTypeForErrorRecovery(e)).AsImmutableOrNull(), resultType, hasErrors: true)
                : new BoundArrayAccess(node, expr, convertedArguments.AsImmutableOrNull(), resultType, hasErrors: false);
        }

        private BoundExpression ConvertToArrayIndex(BoundExpression index, BindingDiagnosticBag diagnostics, bool allowIndexAndRange)
        {
            Debug.Assert(index != null);

            if (index.Kind == BoundKind.OutVariablePendingInference)
            {
                return ((OutVariablePendingInference)index).FailInference(this, diagnostics);
            }
            else if (index.Kind == BoundKind.DiscardExpression && !index.HasExpressionType())
            {
                return ((BoundDiscardExpression)index).FailInference(this, diagnostics);
            }

            var node = index.Syntax;
            var result =
                TryImplicitConversionToArrayIndex(index, SpecialType.System_Int32, node, diagnostics) ??
                TryImplicitConversionToArrayIndex(index, SpecialType.System_UInt32, node, diagnostics) ??
                TryImplicitConversionToArrayIndex(index, SpecialType.System_Int64, node, diagnostics) ??
                TryImplicitConversionToArrayIndex(index, SpecialType.System_UInt64, node, diagnostics);

            if (result is null && allowIndexAndRange)
            {
                result = TryImplicitConversionToArrayIndex(index, WellKnownType.System_Index, node, diagnostics);

                if (result is null)
                {
                    result = TryImplicitConversionToArrayIndex(index, WellKnownType.System_Range, node, diagnostics);
                    if (result is object)
                    {
                        // This member is needed for lowering and should produce an error if not present
                        _ = GetWellKnownTypeMember(
                            WellKnownMember.System_Runtime_CompilerServices_RuntimeHelpers__GetSubArray_T,
                            diagnostics,
                            syntax: node);
                    }
                }
                else
                {
                    // This member is needed for lowering and should produce an error if not present
                    _ = GetWellKnownTypeMember(
                        WellKnownMember.System_Index__GetOffset,
                        diagnostics,
                        syntax: node);
                }
            }

            if (result is null)
            {
                // Give the error that would be given upon conversion to int32.
                NamedTypeSymbol int32 = GetSpecialType(SpecialType.System_Int32, diagnostics, node);
                CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                Conversion failedConversion = this.Conversions.ClassifyConversionFromExpression(index, int32, ref useSiteInfo);
                diagnostics.Add(node, useSiteInfo);
                GenerateImplicitConversionError(diagnostics, node, failedConversion, index, int32);

                // Suppress any additional diagnostics
                return CreateConversion(node, index, failedConversion, isCast: false, conversionGroupOpt: null, destination: int32, diagnostics: BindingDiagnosticBag.Discarded);
            }

            return result;
        }

        private BoundExpression TryImplicitConversionToArrayIndex(BoundExpression expr, WellKnownType wellKnownType, SyntaxNode node, BindingDiagnosticBag diagnostics)
        {
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            TypeSymbol type = GetWellKnownType(wellKnownType, ref useSiteInfo);

            if (type.IsErrorType())
            {
                return null;
            }

            var attemptDiagnostics = BindingDiagnosticBag.GetInstance(diagnostics);
            var result = TryImplicitConversionToArrayIndex(expr, type, node, attemptDiagnostics);
            if (result is object)
            {
                diagnostics.Add(node, useSiteInfo);
                diagnostics.AddRange(attemptDiagnostics);
            }
            attemptDiagnostics.Free();
            return result;
        }

        private BoundExpression TryImplicitConversionToArrayIndex(BoundExpression expr, SpecialType specialType, SyntaxNode node, BindingDiagnosticBag diagnostics)
        {
            var attemptDiagnostics = BindingDiagnosticBag.GetInstance(diagnostics);

            TypeSymbol type = GetSpecialType(specialType, attemptDiagnostics, node);

            var result = TryImplicitConversionToArrayIndex(expr, type, node, attemptDiagnostics);

            if (result is object)
            {
                diagnostics.AddRange(attemptDiagnostics);
            }

            attemptDiagnostics.Free();
            return result;
        }

        private BoundExpression TryImplicitConversionToArrayIndex(BoundExpression expr, TypeSymbol targetType, SyntaxNode node, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(expr != null);
            Debug.Assert((object)targetType != null);

            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            Conversion conversion = this.Conversions.ClassifyImplicitConversionFromExpression(expr, targetType, ref useSiteInfo);
            diagnostics.Add(node, useSiteInfo);
            if (!conversion.Exists)
            {
                return null;
            }

            if (conversion.IsDynamic)
            {
                conversion = conversion.SetArrayIndexConversionForDynamic();
            }

            BoundExpression result = CreateConversion(expr.Syntax, expr, conversion, isCast: false, conversionGroupOpt: null, destination: targetType, diagnostics); // UNDONE: was cast?
            Debug.Assert(result != null); // If this ever fails (it shouldn't), then put a null-check around the diagnostics update.

            return result;
        }

        private BoundExpression BindPointerElementAccess(ExpressionSyntax node, BoundExpression expr, AnalyzedArguments analyzedArguments, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node != null);
            Debug.Assert(expr != null);
            Debug.Assert(analyzedArguments != null);

            bool hasErrors = false;

            if (analyzedArguments.Names.Count > 0)
            {
                // CONSIDER: the error text for this error code mentions "arrays".  It might be nice if we had
                // a separate error code for pointer element access.
                Error(diagnostics, ErrorCode.ERR_NamedArgumentForArray, node);
                hasErrors = true;
            }

            hasErrors = hasErrors || ReportRefOrOutArgument(analyzedArguments, diagnostics);

            Debug.Assert(expr.Type.IsPointerType());
            PointerTypeSymbol pointerType = (PointerTypeSymbol)expr.Type;
            TypeSymbol pointedAtType = pointerType.PointedAtType;

            ArrayBuilder<BoundExpression> arguments = analyzedArguments.Arguments;
            if (arguments.Count != 1)
            {
                if (!hasErrors)
                {
                    Error(diagnostics, ErrorCode.ERR_PtrIndexSingle, node);
                }
                return new BoundPointerElementAccess(node, expr, BadExpression(node, BuildArgumentsForErrorRecovery(analyzedArguments)).MakeCompilerGenerated(),
                    CheckOverflowAtRuntime, pointedAtType, hasErrors: true);
            }

            if (pointedAtType.IsVoidType())
            {
                Error(diagnostics, ErrorCode.ERR_VoidError, expr.Syntax);
                hasErrors = true;
            }

            BoundExpression index = arguments[0];

            index = ConvertToArrayIndex(index, diagnostics, allowIndexAndRange: false);
            return new BoundPointerElementAccess(node, expr, index, CheckOverflowAtRuntime, pointedAtType, hasErrors);
        }

        private static bool ReportRefOrOutArgument(AnalyzedArguments analyzedArguments, BindingDiagnosticBag diagnostics)
        {
            int numArguments = analyzedArguments.Arguments.Count;
            for (int i = 0; i < numArguments; i++)
            {
                RefKind refKind = analyzedArguments.RefKind(i);
                if (refKind != RefKind.None)
                {
                    Error(diagnostics, ErrorCode.ERR_BadArgExtraRef, analyzedArguments.Argument(i).Syntax, i + 1, refKind.ToArgumentDisplayString());
                    return true;
                }
            }

            return false;
        }

        private BoundExpression BindIndexerAccess(ExpressionSyntax node, BoundExpression expr, AnalyzedArguments analyzedArguments, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node != null);
            Debug.Assert(expr != null);
            Debug.Assert((object)expr.Type != null);
            Debug.Assert(analyzedArguments != null);

            LookupResult lookupResult = LookupResult.GetInstance();
            LookupOptions lookupOptions = expr.Kind == BoundKind.BaseReference ? LookupOptions.UseBaseReferenceAccessibility : LookupOptions.Default;
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            this.LookupMembersWithFallback(lookupResult, expr.Type, WellKnownMemberNames.Indexer, arity: 0, useSiteInfo: ref useSiteInfo, options: lookupOptions);
            diagnostics.Add(node, useSiteInfo);

            // Store, rather than return, so that we can release resources.
            BoundExpression indexerAccessExpression;

            if (!lookupResult.IsMultiViable)
            {
                if (TryBindIndexOrRangeIndexer(
                    node,
                    expr,
                    analyzedArguments,
                    diagnostics,
                    out var patternIndexerAccess))
                {
                    indexerAccessExpression = patternIndexerAccess;
                }
                else
                {
                    indexerAccessExpression = BadIndexerExpression(node, expr, analyzedArguments, lookupResult.Error, diagnostics);
                }
            }
            else
            {
                ArrayBuilder<PropertySymbol> indexerGroup = ArrayBuilder<PropertySymbol>.GetInstance();
                foreach (Symbol symbol in lookupResult.Symbols)
                {
                    Debug.Assert(symbol.IsIndexer());
                    indexerGroup.Add((PropertySymbol)symbol);
                }

                indexerAccessExpression = BindIndexerOrIndexedPropertyAccess(node, expr, indexerGroup, analyzedArguments, diagnostics);
                indexerGroup.Free();
            }

            lookupResult.Free();
            return indexerAccessExpression;
        }

        private static readonly Func<PropertySymbol, bool> s_isIndexedPropertyWithNonOptionalArguments = property =>
            {
                if (property.IsIndexer || !property.IsIndexedProperty)
                {
                    return false;
                }

                Debug.Assert(property.ParameterCount > 0);
                var parameter = property.Parameters[0];
                return !parameter.IsOptional && !parameter.IsParams;
            };

        private static readonly SymbolDisplayFormat s_propertyGroupFormat =
            new SymbolDisplayFormat(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                memberOptions:
                    SymbolDisplayMemberOptions.IncludeContainingType,
                miscellaneousOptions:
                    SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                    SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        private BoundExpression BindIndexedPropertyAccess(BoundPropertyGroup propertyGroup, bool mustHaveAllOptionalParameters, BindingDiagnosticBag diagnostics)
        {
            var syntax = propertyGroup.Syntax;
            var receiverOpt = propertyGroup.ReceiverOpt;
            var properties = propertyGroup.Properties;

            if (properties.All(s_isIndexedPropertyWithNonOptionalArguments))
            {
                Error(diagnostics,
                    mustHaveAllOptionalParameters ? ErrorCode.ERR_IndexedPropertyMustHaveAllOptionalParams : ErrorCode.ERR_IndexedPropertyRequiresParams,
                    syntax,
                    properties[0].ToDisplayString(s_propertyGroupFormat));
                return BoundIndexerAccess.ErrorAccess(
                    syntax,
                    receiverOpt,
                    CreateErrorPropertySymbol(properties),
                    ImmutableArray<BoundExpression>.Empty,
                    default(ImmutableArray<string>),
                    default(ImmutableArray<RefKind>),
                    properties);
            }

            var arguments = AnalyzedArguments.GetInstance();
            var result = BindIndexedPropertyAccess(syntax, receiverOpt, properties, arguments, diagnostics);
            arguments.Free();
            return result;
        }

        private BoundExpression BindIndexedPropertyAccess(SyntaxNode syntax, BoundExpression receiverOpt, ImmutableArray<PropertySymbol> propertyGroup, AnalyzedArguments arguments, BindingDiagnosticBag diagnostics)
        {
            // TODO: We're creating an extra copy of the properties array in BindIndexerOrIndexedProperty
            // converting the ArrayBuilder to ImmutableArray. Avoid the extra copy.
            var properties = ArrayBuilder<PropertySymbol>.GetInstance();
            properties.AddRange(propertyGroup);
            var result = BindIndexerOrIndexedPropertyAccess(syntax, receiverOpt, properties, arguments, diagnostics);
            properties.Free();
            return result;
        }

        private BoundExpression BindDynamicIndexer(
             SyntaxNode syntax,
             BoundExpression receiver,
             AnalyzedArguments arguments,
             ImmutableArray<PropertySymbol> applicableProperties,
             BindingDiagnosticBag diagnostics)
        {
            bool hasErrors = false;

            BoundKind receiverKind = receiver.Kind;
            if (receiverKind == BoundKind.BaseReference)
            {
                Error(diagnostics, ErrorCode.ERR_NoDynamicPhantomOnBaseIndexer, syntax);
                hasErrors = true;
            }
            else if (receiverKind == BoundKind.TypeOrValueExpression)
            {
                var typeOrValue = (BoundTypeOrValueExpression)receiver;

                // Unfortunately, the runtime binder doesn't have APIs that would allow us to pass both "type or value".
                // Ideally the runtime binder would choose between type and value based on the result of the overload resolution.
                // We need to pick one or the other here. Dev11 compiler passes the type only if the value can't be accessed.
                bool inStaticContext;
                bool useType = IsInstance(typeOrValue.Data.ValueSymbol) && !HasThis(isExplicit: false, inStaticContext: out inStaticContext);

                receiver = ReplaceTypeOrValueReceiver(typeOrValue, useType, diagnostics);
            }

            var argArray = BuildArgumentsForDynamicInvocation(arguments, diagnostics);
            var refKindsArray = arguments.RefKinds.ToImmutableOrNull();

            hasErrors &= ReportBadDynamicArguments(syntax, argArray, refKindsArray, diagnostics, queryClause: null);

            return new BoundDynamicIndexerAccess(
                syntax,
                receiver,
                argArray,
                arguments.GetNames(),
                refKindsArray,
                applicableProperties,
                AssemblySymbol.DynamicType,
                hasErrors);
        }

        private BoundExpression BindIndexerOrIndexedPropertyAccess(
            SyntaxNode syntax,
            BoundExpression receiverOpt,
            ArrayBuilder<PropertySymbol> propertyGroup,
            AnalyzedArguments analyzedArguments,
            BindingDiagnosticBag diagnostics)
        {
            OverloadResolutionResult<PropertySymbol> overloadResolutionResult = OverloadResolutionResult<PropertySymbol>.GetInstance();
            bool allowRefOmittedArguments = receiverOpt.IsExpressionOfComImportType();
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            this.OverloadResolution.PropertyOverloadResolution(propertyGroup, receiverOpt, analyzedArguments, overloadResolutionResult, allowRefOmittedArguments, ref useSiteInfo);
            diagnostics.Add(syntax, useSiteInfo);
            BoundExpression propertyAccess;

            if (analyzedArguments.HasDynamicArgument && overloadResolutionResult.HasAnyApplicableMember)
            {
                // Note that the runtime binder may consider candidates that haven't passed compile-time final validation 
                // and an ambiguity error may be reported. Also additional checks are performed in runtime final validation 
                // that are not performed at compile-time.
                // Only if the set of final applicable candidates is empty we know for sure the call will fail at runtime.
                var finalApplicableCandidates = GetCandidatesPassingFinalValidation(syntax, overloadResolutionResult, receiverOpt, default(ImmutableArray<TypeWithAnnotations>), diagnostics);
                overloadResolutionResult.Free();
                return BindDynamicIndexer(syntax, receiverOpt, analyzedArguments, finalApplicableCandidates, diagnostics);
            }

            ImmutableArray<string> argumentNames = analyzedArguments.GetNames();
            ImmutableArray<RefKind> argumentRefKinds = analyzedArguments.RefKinds.ToImmutableOrNull();
            if (!overloadResolutionResult.Succeeded)
            {
                // If the arguments had an error reported about them then suppress further error
                // reporting for overload resolution. 

                ImmutableArray<PropertySymbol> candidates = propertyGroup.ToImmutable();

                if (!analyzedArguments.HasErrors)
                {
                    if (TryBindIndexOrRangeIndexer(
                        syntax,
                        receiverOpt,
                        analyzedArguments,
                        diagnostics,
                        out var patternIndexerAccess))
                    {
                        return patternIndexerAccess;
                    }
                    else
                    {
                        // Dev10 uses the "this" keyword as the method name for indexers.
                        var candidate = candidates[0];
                        var name = candidate.IsIndexer ? SyntaxFacts.GetText(SyntaxKind.ThisKeyword) : candidate.Name;

                        overloadResolutionResult.ReportDiagnostics(
                            binder: this,
                            location: syntax.Location,
                            nodeOpt: syntax,
                            diagnostics: diagnostics,
                            name: name,
                            receiver: null,
                            invokedExpression: null,
                            arguments: analyzedArguments,
                            memberGroup: candidates,
                            typeContainingConstructor: null,
                            delegateTypeBeingInvoked: null);
                    }
                }

                ImmutableArray<BoundExpression> arguments = BuildArgumentsForErrorRecovery(analyzedArguments, candidates);

                // A bad BoundIndexerAccess containing an ErrorPropertySymbol will produce better flow analysis results than
                // a BoundBadExpression containing the candidate indexers.
                PropertySymbol property = (candidates.Length == 1) ? candidates[0] : CreateErrorPropertySymbol(candidates);

                propertyAccess = BoundIndexerAccess.ErrorAccess(
                    syntax,
                    receiverOpt,
                    property,
                    arguments,
                    argumentNames,
                    argumentRefKinds,
                    candidates);
            }
            else
            {
                MemberResolutionResult<PropertySymbol> resolutionResult = overloadResolutionResult.ValidResult;
                PropertySymbol property = resolutionResult.Member;
                RefKind? receiverRefKind = receiverOpt?.GetRefKind();
                uint receiverEscapeScope = property.RequiresInstanceReceiver && receiverOpt != null
                    ? receiverRefKind?.IsWritableReference() == true ? GetRefEscape(receiverOpt, LocalScopeDepth) : GetValEscape(receiverOpt, LocalScopeDepth)
                    : Binder.ExternalScope;
                this.CoerceArguments<PropertySymbol>(resolutionResult, analyzedArguments.Arguments, diagnostics, receiverOpt?.Type, receiverRefKind, receiverEscapeScope);

                var isExpanded = resolutionResult.Result.Kind == MemberResolutionKind.ApplicableInExpandedForm;
                var argsToParams = resolutionResult.Result.ArgsToParamsOpt;

                ReportDiagnosticsIfObsolete(diagnostics, property, syntax, hasBaseReceiver: receiverOpt != null && receiverOpt.Kind == BoundKind.BaseReference);

                // Make sure that the result of overload resolution is valid.
                var gotError = MemberGroupFinalValidationAccessibilityChecks(receiverOpt, property, syntax, diagnostics, invokedAsExtensionMethod: false);

                var receiver = ReplaceTypeOrValueReceiver(receiverOpt, property.IsStatic, diagnostics);

                if (!gotError && receiver != null && receiver.Kind == BoundKind.ThisReference && receiver.WasCompilerGenerated)
                {
                    gotError = IsRefOrOutThisParameterCaptured(syntax, diagnostics);
                }

                var arguments = analyzedArguments.Arguments.ToImmutable();

                if (!gotError)
                {
                    gotError = !CheckInvocationArgMixing(
                        syntax,
                        property,
                        receiver,
                        property.Parameters,
                        arguments,
                        argsToParams,
                        this.LocalScopeDepth,
                        diagnostics);
                }

                // Note that we do not bind default arguments here, because at this point we do not know whether
                // the indexer is being used in a 'get', or 'set', or 'get+set' (compound assignment) context.
                propertyAccess = new BoundIndexerAccess(
                    syntax,
                    receiver,
                    property,
                    arguments,
                    argumentNames,
                    argumentRefKinds,
                    isExpanded,
                    argsToParams,
                    defaultArguments: default,
                    property.Type,
                    gotError);
            }

            overloadResolutionResult.Free();
            return propertyAccess;
        }

        private bool TryBindIndexOrRangeIndexer(
            SyntaxNode syntax,
            BoundExpression receiverOpt,
            AnalyzedArguments arguments,
            BindingDiagnosticBag diagnostics,
            out BoundIndexOrRangePatternIndexerAccess patternIndexerAccess)
        {
            patternIndexerAccess = null;

            // Verify a few things up-front, namely that we have a single argument
            // to this indexer that has an Index or Range type and that there is
            // a real receiver with a known type

            if (arguments.Arguments.Count != 1)
            {
                return false;
            }

            var argument = arguments.Arguments[0];

            var argType = argument.Type;
            bool argIsIndex = TypeSymbol.Equals(argType,
                Compilation.GetWellKnownType(WellKnownType.System_Index),
                TypeCompareKind.ConsiderEverything);
            bool argIsRange = !argIsIndex && TypeSymbol.Equals(argType,
                Compilation.GetWellKnownType(WellKnownType.System_Range),
                TypeCompareKind.ConsiderEverything);

            if ((!argIsIndex && !argIsRange) ||
                !(receiverOpt?.Type is TypeSymbol receiverType))
            {
                return false;
            }

            // SPEC:

            // An indexer invocation with a single argument of System.Index or System.Range will
            // succeed if the receiver type conforms to an appropriate pattern, namely

            // 1. The receiver type's original definition has an accessible property getter that returns
            //    an int and has the name Length or Count
            // 2. For Index: Has an accessible indexer with a single int parameter
            //    For Range: Has an accessible Slice method that takes two int parameters

            PropertySymbol lengthOrCountProperty;

            var lookupResult = LookupResult.GetInstance();
            var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;

            // Look for Length first

            if (!tryLookupLengthOrCount(WellKnownMemberNames.LengthPropertyName, out lengthOrCountProperty) &&
                !tryLookupLengthOrCount(WellKnownMemberNames.CountPropertyName, out lengthOrCountProperty))
            {
                return false;
            }

            Debug.Assert(lengthOrCountProperty is { });

            if (argIsIndex)
            {
                // Look for `T this[int i]` indexer

                LookupMembersInType(
                    lookupResult,
                    receiverType,
                    WellKnownMemberNames.Indexer,
                    arity: 0,
                    basesBeingResolved: null,
                    LookupOptions.Default,
                    originalBinder: this,
                    diagnose: false,
                    ref discardedUseSiteInfo);

                if (lookupResult.IsMultiViable)
                {
                    foreach (var candidate in lookupResult.Symbols)
                    {
                        if (!candidate.IsStatic &&
                            candidate is PropertySymbol property &&
                            IsAccessible(property, ref discardedUseSiteInfo) &&
                            property.OriginalDefinition is { ParameterCount: 1 } original &&
                            isIntNotByRef(original.Parameters[0]))
                        {
                            CheckImplicitThisCopyInReadOnlyMember(receiverOpt, lengthOrCountProperty.GetMethod, diagnostics);
                            ReportDiagnosticsIfObsolete(diagnostics, property, syntax, hasBaseReceiver: false);
                            ReportDiagnosticsIfObsolete(diagnostics, lengthOrCountProperty, syntax, hasBaseReceiver: false);
                            // note: implicit copy check on the indexer accessor happens in CheckPropertyValueKind
                            patternIndexerAccess = new BoundIndexOrRangePatternIndexerAccess(
                                syntax,
                                receiverOpt,
                                lengthOrCountProperty,
                                property,
                                BindToNaturalType(argument, diagnostics),
                                property.Type);
                            break;
                        }
                    }
                }
            }
            else if (receiverType.SpecialType == SpecialType.System_String)
            {
                Debug.Assert(argIsRange);
                // Look for Substring
                var substring = (MethodSymbol)Compilation.GetSpecialTypeMember(SpecialMember.System_String__Substring);
                if (substring is object)
                {
                    patternIndexerAccess = new BoundIndexOrRangePatternIndexerAccess(
                        syntax,
                        receiverOpt,
                        lengthOrCountProperty,
                        substring,
                        BindToNaturalType(argument, diagnostics),
                        substring.ReturnType);
                    checkWellKnown(WellKnownMember.System_Range__get_Start);
                    checkWellKnown(WellKnownMember.System_Range__get_End);
                }
            }
            else
            {
                Debug.Assert(argIsRange);
                // Look for `T Slice(int, int)` indexer

                LookupMembersInType(
                    lookupResult,
                    receiverType,
                    WellKnownMemberNames.SliceMethodName,
                    arity: 0,
                    basesBeingResolved: null,
                    LookupOptions.Default,
                    originalBinder: this,
                    diagnose: false,
                    ref discardedUseSiteInfo);

                if (lookupResult.IsMultiViable)
                {
                    foreach (var candidate in lookupResult.Symbols)
                    {
                        if (!candidate.IsStatic &&
                            IsAccessible(candidate, ref discardedUseSiteInfo) &&
                            candidate is MethodSymbol method &&
                            method.OriginalDefinition is var original &&
                            original.ParameterCount == 2 &&
                            isIntNotByRef(original.Parameters[0]) &&
                            isIntNotByRef(original.Parameters[1]))
                        {
                            CheckImplicitThisCopyInReadOnlyMember(receiverOpt, lengthOrCountProperty.GetMethod, diagnostics);
                            CheckImplicitThisCopyInReadOnlyMember(receiverOpt, method, diagnostics);
                            ReportDiagnosticsIfObsolete(diagnostics, method, syntax, hasBaseReceiver: false);
                            ReportDiagnosticsIfObsolete(diagnostics, lengthOrCountProperty, syntax, hasBaseReceiver: false);
                            patternIndexerAccess = new BoundIndexOrRangePatternIndexerAccess(
                                syntax,
                                receiverOpt,
                                lengthOrCountProperty,
                                method,
                                BindToNaturalType(argument, diagnostics),
                                method.ReturnType);
                            checkWellKnown(WellKnownMember.System_Range__get_Start);
                            checkWellKnown(WellKnownMember.System_Range__get_End);
                            break;
                        }
                    }
                }
            }

            cleanup(lookupResult);
            if (patternIndexerAccess is null)
            {
                return false;
            }

            _ = MessageID.IDS_FeatureIndexOperator.CheckFeatureAvailability(diagnostics, syntax);
            checkWellKnown(WellKnownMember.System_Index__GetOffset);
            if (arguments.Names.Count > 0)
            {
                diagnostics.Add(
                    argIsRange
                        ? ErrorCode.ERR_ImplicitRangeIndexerWithName
                        : ErrorCode.ERR_ImplicitIndexIndexerWithName,
                    arguments.Names[0].GetValueOrDefault().Location);
            }
            return true;

            static void cleanup(LookupResult lookupResult)
            {
                lookupResult.Free();
            }

            static bool isIntNotByRef(ParameterSymbol param)
                => param.Type.SpecialType == SpecialType.System_Int32 &&
                   param.RefKind == RefKind.None;

            void checkWellKnown(WellKnownMember member)
            {
                // Check required well-known member. They may not be needed
                // during lowering, but it's simpler to always require them to prevent
                // the user from getting surprising errors when optimizations fail
                _ = GetWellKnownTypeMember(member, diagnostics, syntax: syntax);
            }

            bool tryLookupLengthOrCount(string propertyName, out PropertySymbol valid)
            {
                LookupMembersInType(
                    lookupResult,
                    receiverType,
                    propertyName,
                    arity: 0,
                    basesBeingResolved: null,
                    LookupOptions.Default,
                    originalBinder: this,
                    diagnose: false,
                    useSiteInfo: ref discardedUseSiteInfo);

                if (lookupResult.IsSingleViable &&
                    lookupResult.Symbols[0] is PropertySymbol property &&
                    property.GetOwnOrInheritedGetMethod()?.OriginalDefinition is MethodSymbol getMethod &&
                    getMethod.ReturnType.SpecialType == SpecialType.System_Int32 &&
                    getMethod.RefKind == RefKind.None &&
                    !getMethod.IsStatic &&
                    IsAccessible(getMethod, ref discardedUseSiteInfo))
                {
                    lookupResult.Clear();
                    valid = property;
                    return true;
                }
                lookupResult.Clear();
                valid = null;
                return false;
            }
        }

        private ErrorPropertySymbol CreateErrorPropertySymbol(ImmutableArray<PropertySymbol> propertyGroup)
        {
            TypeSymbol propertyType = GetCommonTypeOrReturnType(propertyGroup) ?? CreateErrorType();
            var candidate = propertyGroup[0];
            return new ErrorPropertySymbol(candidate.ContainingType, propertyType, candidate.Name, candidate.IsIndexer, candidate.IsIndexedProperty);
        }

        /// <summary>
        /// Perform lookup and overload resolution on methods defined directly on the class and any
        /// extension methods in scope. Lookup will occur for extension methods in all nested scopes
        /// as necessary until an appropriate method is found. If analyzedArguments is null, the first
        /// method group is returned, without overload resolution being performed. That method group
        /// will either be the methods defined on the receiver class directly (no extension methods)
        /// or the first set of extension methods.
        /// </summary>
        /// <param name="node">The node associated with the method group</param>
        /// <param name="analyzedArguments">The arguments of the invocation (or the delegate type, if a method group conversion)</param>
        /// <param name="isMethodGroupConversion">True if it is a method group conversion</param>
        /// <param name="useSiteInfo"></param>
        /// <param name="inferWithDynamic"></param>
        /// <param name="returnRefKind">If a method group conversion, the desired ref kind of the delegate</param>
        /// <param name="returnType">If a method group conversion, the desired return type of the delegate.
        /// May be null during inference if the return type of the delegate needs to be computed.</param>
        internal MethodGroupResolution ResolveMethodGroup(
            BoundMethodGroup node,
            AnalyzedArguments analyzedArguments,
            bool isMethodGroupConversion,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo,
            bool inferWithDynamic = false,
            RefKind returnRefKind = default,
            TypeSymbol returnType = null,
            bool isFunctionPointerResolution = false,
            in CallingConventionInfo callingConventionInfo = default)
        {
            return ResolveMethodGroup(
                node, node.Syntax, node.Name, analyzedArguments, isMethodGroupConversion, ref useSiteInfo,
                inferWithDynamic: inferWithDynamic, returnRefKind: returnRefKind, returnType: returnType,
                isFunctionPointerResolution: isFunctionPointerResolution, callingConventionInfo: callingConventionInfo);
        }

        internal MethodGroupResolution ResolveMethodGroup(
            BoundMethodGroup node,
            SyntaxNode expression,
            string methodName,
            AnalyzedArguments analyzedArguments,
            bool isMethodGroupConversion,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo,
            bool inferWithDynamic = false,
            bool allowUnexpandedForm = true,
            RefKind returnRefKind = default,
            TypeSymbol returnType = null,
            bool isFunctionPointerResolution = false,
            in CallingConventionInfo callingConventionInfo = default)
        {
            var methodResolution = ResolveMethodGroupInternal(
                node, expression, methodName, analyzedArguments, isMethodGroupConversion, ref useSiteInfo,
                inferWithDynamic: inferWithDynamic, allowUnexpandedForm: allowUnexpandedForm,
                returnRefKind: returnRefKind, returnType: returnType,
                isFunctionPointerResolution: isFunctionPointerResolution, callingConvention: callingConventionInfo);
            if (methodResolution.IsEmpty && !methodResolution.HasAnyErrors)
            {
                Debug.Assert(node.LookupError == null);

                var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, useSiteInfo.AccumulatesDependencies);
                diagnostics.AddRange(methodResolution.Diagnostics); // Could still have use site warnings.
                BindMemberAccessReportError(node, diagnostics);

                // Note: no need to free `methodResolution`, we're transferring the pooled objects it owned
                return new MethodGroupResolution(methodResolution.MethodGroup, methodResolution.OtherSymbol, methodResolution.OverloadResolutionResult, methodResolution.AnalyzedArguments, methodResolution.ResultKind, diagnostics.ToReadOnlyAndFree());
            }
            return methodResolution;
        }

        internal MethodGroupResolution ResolveMethodGroupForFunctionPointer(
            BoundMethodGroup methodGroup,
            AnalyzedArguments analyzedArguments,
            TypeSymbol returnType,
            RefKind returnRefKind,
            in CallingConventionInfo callingConventionInfo,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            return ResolveDefaultMethodGroup(
                methodGroup,
                analyzedArguments,
                isMethodGroupConversion: true,
                ref useSiteInfo,
                inferWithDynamic: false,
                allowUnexpandedForm: true,
                returnRefKind,
                returnType,
                isFunctionPointerResolution: true,
                callingConventionInfo);
        }

        private MethodGroupResolution ResolveMethodGroupInternal(
            BoundMethodGroup methodGroup,
            SyntaxNode expression,
            string methodName,
            AnalyzedArguments analyzedArguments,
            bool isMethodGroupConversion,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo,
            bool inferWithDynamic = false,
            bool allowUnexpandedForm = true,
            RefKind returnRefKind = default,
            TypeSymbol returnType = null,
            bool isFunctionPointerResolution = false,
            in CallingConventionInfo callingConvention = default)
        {
            var methodResolution = ResolveDefaultMethodGroup(
                methodGroup, analyzedArguments, isMethodGroupConversion, ref useSiteInfo,
                inferWithDynamic, allowUnexpandedForm,
                returnRefKind, returnType, isFunctionPointerResolution, callingConvention);

            // If the method group's receiver is dynamic then there is no point in looking for extension methods; 
            // it's going to be a dynamic invocation.
            if (!methodGroup.SearchExtensionMethods || methodResolution.HasAnyApplicableMethod || methodGroup.MethodGroupReceiverIsDynamic())
            {
                return methodResolution;
            }

            var extensionMethodResolution = BindExtensionMethod(
                expression, methodName, analyzedArguments, methodGroup.ReceiverOpt, methodGroup.TypeArgumentsOpt, isMethodGroupConversion,
                returnRefKind: returnRefKind, returnType: returnType, withDependencies: useSiteInfo.AccumulatesDependencies);
            bool preferExtensionMethodResolution = false;

            if (extensionMethodResolution.HasAnyApplicableMethod)
            {
                preferExtensionMethodResolution = true;
            }
            else if (extensionMethodResolution.IsEmpty)
            {
                preferExtensionMethodResolution = false;
            }
            else if (methodResolution.IsEmpty)
            {
                preferExtensionMethodResolution = true;
            }
            else
            {
                // At this point, both method group resolutions are non-empty but neither contains any applicable method.
                // Choose the MethodGroupResolution with the better (i.e. less worse) result kind.

                Debug.Assert(!methodResolution.HasAnyApplicableMethod);
                Debug.Assert(!extensionMethodResolution.HasAnyApplicableMethod);
                Debug.Assert(!methodResolution.IsEmpty);
                Debug.Assert(!extensionMethodResolution.IsEmpty);

                LookupResultKind methodResultKind = methodResolution.ResultKind;
                LookupResultKind extensionMethodResultKind = extensionMethodResolution.ResultKind;
                if (methodResultKind != extensionMethodResultKind &&
                    methodResultKind == extensionMethodResultKind.WorseResultKind(methodResultKind))
                {
                    preferExtensionMethodResolution = true;
                }
            }

            if (preferExtensionMethodResolution)
            {
                methodResolution.Free();
                Debug.Assert(!extensionMethodResolution.IsEmpty);
                return extensionMethodResolution;  //NOTE: the first argument of this MethodGroupResolution could be a BoundTypeOrValueExpression
            }

            extensionMethodResolution.Free();

            return methodResolution;
        }

        private MethodGroupResolution ResolveDefaultMethodGroup(
            BoundMethodGroup node,
            AnalyzedArguments analyzedArguments,
            bool isMethodGroupConversion,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo,
            bool inferWithDynamic = false,
            bool allowUnexpandedForm = true,
            RefKind returnRefKind = default,
            TypeSymbol returnType = null,
            bool isFunctionPointerResolution = false,
            in CallingConventionInfo callingConvention = default)
        {
            var methods = node.Methods;
            if (methods.Length == 0)
            {
                var method = node.LookupSymbolOpt as MethodSymbol;
                if ((object)method != null)
                {
                    methods = ImmutableArray.Create(method);
                }
            }

            var sealedDiagnostics = ImmutableBindingDiagnostic<AssemblySymbol>.Empty;
            if (node.LookupError != null)
            {
                var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: false);
                Error(diagnostics, node.LookupError, node.NameSyntax);
                sealedDiagnostics = diagnostics.ToReadOnlyAndFree();
            }

            if (methods.Length == 0)
            {
                return new MethodGroupResolution(node.LookupSymbolOpt, node.ResultKind, sealedDiagnostics);
            }

            var methodGroup = MethodGroup.GetInstance();
            // NOTE: node.ReceiverOpt could be a BoundTypeOrValueExpression - users need to check.
            methodGroup.PopulateWithNonExtensionMethods(node.ReceiverOpt, methods, node.TypeArgumentsOpt, node.ResultKind, node.LookupError);

            if (node.LookupError != null)
            {
                return new MethodGroupResolution(methodGroup, sealedDiagnostics);
            }

            // Arguments will be null if the caller is resolving to the first available
            // method group, regardless of arguments, when the signature cannot
            // be inferred. (In the error case of o.M = null; for instance.)
            if (analyzedArguments == null)
            {
                return new MethodGroupResolution(methodGroup, sealedDiagnostics);
            }
            else
            {
                var result = OverloadResolutionResult<MethodSymbol>.GetInstance();
                bool allowRefOmittedArguments = methodGroup.Receiver.IsExpressionOfComImportType();
                OverloadResolution.MethodInvocationOverloadResolution(
                    methodGroup.Methods,
                    methodGroup.TypeArguments,
                    methodGroup.Receiver,
                    analyzedArguments,
                    result,
                    ref useSiteInfo,
                    isMethodGroupConversion,
                    allowRefOmittedArguments,
                    inferWithDynamic,
                    allowUnexpandedForm,
                    returnRefKind,
                    returnType,
                    isFunctionPointerResolution,
                    callingConvention);

                // Note: the MethodGroupResolution instance is responsible for freeing its copy of analyzed arguments
                return new MethodGroupResolution(methodGroup, null, result, AnalyzedArguments.GetInstance(analyzedArguments), methodGroup.ResultKind, sealedDiagnostics);
            }
        }

#nullable enable
        internal NamedTypeSymbol? GetMethodGroupDelegateType(BoundMethodGroup node)
        {
            var method = GetUniqueSignatureFromMethodGroup(node);
            if (method is { } &&
                GetMethodGroupOrLambdaDelegateType(method.RefKind, method.ReturnsVoid ? default : method.ReturnTypeWithAnnotations, method.ParameterRefKinds, method.ParameterTypesWithAnnotations) is { } delegateType)
            {
                return delegateType;
            }
            return null;
        }

        /// <summary>
        /// Returns one of the methods from the method group if all methods in the method group
        /// have the same signature, ignoring parameter names and custom modifiers. The particular
        /// method returned is not important since the caller is interested in the signature only.
        /// </summary>
        private MethodSymbol? GetUniqueSignatureFromMethodGroup(BoundMethodGroup node)
        {
            MethodSymbol? method = null;
            foreach (var m in node.Methods)
            {
                switch (node.ReceiverOpt)
                {
                    case BoundTypeExpression:
                        if (!m.IsStatic) continue;
                        break;
                    case BoundThisReference { WasCompilerGenerated: true }:
                        break;
                    default:
                        if (m.IsStatic) continue;
                        break;
                }
                if (!isCandidateUnique(ref method, m))
                {
                    return null;
                }
            }
            if (node.SearchExtensionMethods)
            {
                var receiver = node.ReceiverOpt!;
                foreach (var scope in new ExtensionMethodScopes(this))
                {
                    var methodGroup = MethodGroup.GetInstance();
                    PopulateExtensionMethodsFromSingleBinder(scope, methodGroup, node.Syntax, receiver, node.Name, node.TypeArgumentsOpt, BindingDiagnosticBag.Discarded);
                    foreach (var m in methodGroup.Methods)
                    {
                        if (m.ReduceExtensionMethod(receiver.Type, Compilation) is { } reduced &&
                            !isCandidateUnique(ref method, reduced))
                        {
                            methodGroup.Free();
                            return null;
                        }
                    }
                    methodGroup.Free();
                }
            }
            if (method is null)
            {
                return null;
            }
            int n = node.TypeArgumentsOpt.IsDefaultOrEmpty ? 0 : node.TypeArgumentsOpt.Length;
            if (method.Arity != n)
            {
                return null;
            }
            else if (n > 0)
            {
                method = method.ConstructedFrom.Construct(node.TypeArgumentsOpt);
            }
            return method;

            static bool isCandidateUnique(ref MethodSymbol? method, MethodSymbol candidate)
            {
                if (method is null)
                {
                    method = candidate;
                    return true;
                }
                if (MemberSignatureComparer.MethodGroupSignatureComparer.Equals(method, candidate))
                {
                    return true;
                }
                method = null;
                return false;
            }
        }

        // This method was adapted from LoweredDynamicOperationFactory.GetDelegateType().
        internal NamedTypeSymbol? GetMethodGroupOrLambdaDelegateType(
            RefKind returnRefKind,
            TypeWithAnnotations returnTypeOpt,
            ImmutableArray<RefKind> parameterRefKinds,
            ImmutableArray<TypeWithAnnotations> parameterTypes)
        {
            Debug.Assert(parameterRefKinds.IsDefault || parameterRefKinds.Length == parameterTypes.Length);
            Debug.Assert(returnTypeOpt.Type?.IsVoidType() != true); // expecting !returnTypeOpt.HasType rather than System.Void

            bool returnsVoid = !returnTypeOpt.HasType;
            var typeArguments = returnsVoid ? parameterTypes : parameterTypes.Add(returnTypeOpt);

            if (returnsVoid && returnRefKind != RefKind.None)
            {
                // Invalid return type.
                return null;
            }

            if (!typeArguments.All(t => isValidTypeArgument(t.Type)))
            {
                // https://github.com/dotnet/roslyn/issues/55217: Support parameter
                // and return types that are not valid generic type arguments.
                return null;
            }

            bool hasByRefParameters = !parameterRefKinds.IsDefault && parameterRefKinds.Any(refKind => refKind != RefKind.None);

            if (returnRefKind == RefKind.None && !hasByRefParameters)
            {
                var wkDelegateType = returnsVoid ?
                    WellKnownTypes.GetWellKnownActionDelegate(invokeArgumentCount: parameterTypes.Length) :
                    WellKnownTypes.GetWellKnownFunctionDelegate(invokeArgumentCount: parameterTypes.Length);

                if (wkDelegateType != WellKnownType.Unknown)
                {
                    // The caller of GetMethodGroupOrLambdaDelegateType() is responsible for
                    // checking and reporting use-site diagnostics for the returned delegate type.
                    var delegateType = Compilation.GetWellKnownType(wkDelegateType);
                    if (typeArguments.Length == 0)
                    {
                        return delegateType;
                    }
                    if (checkConstraints(Compilation, Conversions, delegateType, typeArguments))
                    {
                        return delegateType.Construct(typeArguments);
                    }
                }
            }

            var refKinds = (hasByRefParameters || returnRefKind != RefKind.None) ? RefKindVector.Create(parameterTypes.Length + (returnsVoid ? 0 : 1)) : default;
            Debug.Assert(Enumerable.Range(0, refKinds.Capacity).All(i => refKinds[i] == RefKind.None));

            if (hasByRefParameters)
            {
                for (int i = 0; i < parameterRefKinds.Length; i++)
                {
                    refKinds[i] = parameterRefKinds[i];
                }
            }
            if (returnRefKind != RefKind.None)
            {
                refKinds[parameterTypes.Length] = returnRefKind;
            }

            var synthesizedType = Compilation.AnonymousTypeManager.SynthesizeDelegate(parameterCount: parameterTypes.Length, refKinds, returnsVoid, generation: 0);
            return synthesizedType.Construct(typeArguments);

            static bool isValidTypeArgument(TypeSymbol? type)
            {
                return type is { } &&
                    !type.IsPointerOrFunctionPointer() &&
                    !type.IsRestrictedType();
            }

            static bool checkConstraints(CSharpCompilation compilation, ConversionsBase conversions, NamedTypeSymbol delegateType, ImmutableArray<TypeWithAnnotations> typeArguments)
            {
                var diagnosticsBuilder = ArrayBuilder<TypeParameterDiagnosticInfo>.GetInstance();
                var typeParameters = delegateType.TypeParameters;
                var substitution = new TypeMap(typeParameters, typeArguments);
                ArrayBuilder<TypeParameterDiagnosticInfo>? useSiteDiagnosticsBuilder = null;
                var result = delegateType.CheckConstraints(
                    new ConstraintsHelper.CheckConstraintsArgs(compilation, conversions, includeNullability: false, NoLocation.Singleton, diagnostics: null, template: CompoundUseSiteInfo<AssemblySymbol>.Discarded),
                    substitution,
                    typeParameters,
                    typeArguments,
                    diagnosticsBuilder,
                    nullabilityDiagnosticsBuilderOpt: null,
                    ref useSiteDiagnosticsBuilder);
                diagnosticsBuilder.Free();
                return result;
            }
        }
#nullable disable

        internal static bool ReportDelegateInvokeUseSiteDiagnostic(BindingDiagnosticBag diagnostics, TypeSymbol possibleDelegateType,
            Location location = null, SyntaxNode node = null)
        {
            Debug.Assert((location == null) ^ (node == null));

            if (!possibleDelegateType.IsDelegateType())
            {
                return false;
            }

            MethodSymbol invoke = possibleDelegateType.DelegateInvokeMethod();
            if ((object)invoke == null)
            {
                diagnostics.Add(new CSDiagnosticInfo(ErrorCode.ERR_InvalidDelegateType, possibleDelegateType), location ?? node.Location);
                return true;
            }

            UseSiteInfo<AssemblySymbol> info = invoke.GetUseSiteInfo();
            diagnostics.AddDependencies(info);

            DiagnosticInfo diagnosticInfo = info.DiagnosticInfo;
            if (diagnosticInfo == null)
            {
                return false;
            }

            if (location == null)
            {
                location = node.Location;
            }

            if (diagnosticInfo.Code == (int)ErrorCode.ERR_InvalidDelegateType)
            {
                diagnostics.Add(new CSDiagnostic(new CSDiagnosticInfo(ErrorCode.ERR_InvalidDelegateType, possibleDelegateType), location));
                return true;
            }

            return Symbol.ReportUseSiteDiagnostic(diagnosticInfo, diagnostics, location);
        }

        private BoundConditionalAccess BindConditionalAccessExpression(ConditionalAccessExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            BoundExpression receiver = BindConditionalAccessReceiver(node, diagnostics);

            var conditionalAccessBinder = new BinderWithConditionalReceiver(this, receiver);
            var access = conditionalAccessBinder.BindValue(node.WhenNotNull, diagnostics, BindValueKind.RValue);

            if (receiver.HasAnyErrors || access.HasAnyErrors)
            {
                return new BoundConditionalAccess(node, receiver, access, CreateErrorType(), hasErrors: true);
            }

            var receiverType = receiver.Type;
            Debug.Assert((object)receiverType != null);

            // access cannot be a method group
            if (access.Kind == BoundKind.MethodGroup)
            {
                return GenerateBadConditionalAccessNodeError(node, receiver, access, diagnostics);
            }

            var accessType = access.Type;

            // access cannot have no type
            if ((object)accessType == null)
            {
                return GenerateBadConditionalAccessNodeError(node, receiver, access, diagnostics);
            }

            // The resulting type must be either a reference type T or Nullable<T>
            // Therefore we must reject cases resulting in types that are not reference types and cannot be lifted into nullable.
            // - access cannot have unconstrained generic type
            // - access cannot be a pointer
            // - access cannot be a restricted type
            if ((!accessType.IsReferenceType && !accessType.IsValueType) || accessType.IsPointerOrFunctionPointer() || accessType.IsRestrictedType())
            {
                // Result type of the access is void when result value cannot be made nullable.
                // For improved diagnostics we detect the cases where the value will be used and produce a
                // more specific (though not technically correct) diagnostic here:
                // "Error CS0023: Operator '?' cannot be applied to operand of type 'T'"
                bool resultIsUsed = true;
                CSharpSyntaxNode parent = node.Parent;

                if (parent != null)
                {
                    switch (parent.Kind())
                    {
                        case SyntaxKind.ExpressionStatement:
                            resultIsUsed = ((ExpressionStatementSyntax)parent).Expression != node;
                            break;

                        case SyntaxKind.SimpleLambdaExpression:
                            resultIsUsed = (((SimpleLambdaExpressionSyntax)parent).Body != node) || MethodOrLambdaRequiresValue(ContainingMemberOrLambda, Compilation);
                            break;

                        case SyntaxKind.ParenthesizedLambdaExpression:
                            resultIsUsed = (((ParenthesizedLambdaExpressionSyntax)parent).Body != node) || MethodOrLambdaRequiresValue(ContainingMemberOrLambda, Compilation);
                            break;

                        case SyntaxKind.ArrowExpressionClause:
                            resultIsUsed = (((ArrowExpressionClauseSyntax)parent).Expression != node) || MethodOrLambdaRequiresValue(ContainingMemberOrLambda, Compilation);
                            break;

                        case SyntaxKind.ForStatement:
                            // Incrementors and Initializers doesn't have to produce a value
                            var loop = (ForStatementSyntax)parent;
                            resultIsUsed = !loop.Incrementors.Contains(node) && !loop.Initializers.Contains(node);
                            break;
                    }
                }

                if (resultIsUsed)
                {
                    return GenerateBadConditionalAccessNodeError(node, receiver, access, diagnostics);
                }

                accessType = GetSpecialType(SpecialType.System_Void, diagnostics, node);
            }

            // if access has value type, the type of the conditional access is nullable of that
            // https://github.com/dotnet/roslyn/issues/35075: The test `accessType.IsValueType && !accessType.IsNullableType()`
            // should probably be `accessType.IsNonNullableValueType()`
            if (accessType.IsValueType && !accessType.IsNullableType() && !accessType.IsVoidType())
            {
                accessType = GetSpecialType(SpecialType.System_Nullable_T, diagnostics, node).Construct(accessType);
            }

            return new BoundConditionalAccess(node, receiver, access, accessType);
        }

        internal static bool MethodOrLambdaRequiresValue(Symbol symbol, CSharpCompilation compilation)
        {
            return symbol is MethodSymbol method &&
                !method.ReturnsVoid &&
                !method.IsAsyncEffectivelyReturningTask(compilation);
        }

        private BoundConditionalAccess GenerateBadConditionalAccessNodeError(ConditionalAccessExpressionSyntax node, BoundExpression receiver, BoundExpression access, BindingDiagnosticBag diagnostics)
        {
            var operatorToken = node.OperatorToken;
            // TODO: need a special ERR for this.
            //       conditional access is not really a binary operator.
            DiagnosticInfo diagnosticInfo = new CSDiagnosticInfo(ErrorCode.ERR_BadUnaryOp, SyntaxFacts.GetText(operatorToken.Kind()), access.Display);
            diagnostics.Add(new CSDiagnostic(diagnosticInfo, operatorToken.GetLocation()));
            receiver = BadExpression(receiver.Syntax, receiver);

            return new BoundConditionalAccess(node, receiver, access, CreateErrorType(), hasErrors: true);
        }

        private BoundExpression BindMemberBindingExpression(MemberBindingExpressionSyntax node, bool invoked, bool indexed, BindingDiagnosticBag diagnostics)
        {
            BoundExpression receiver = GetReceiverForConditionalBinding(node, diagnostics);

            var memberAccess = BindMemberAccessWithBoundLeft(node, receiver, node.Name, node.OperatorToken, invoked, indexed, diagnostics);
            return memberAccess;
        }

        private BoundExpression BindElementBindingExpression(ElementBindingExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            BoundExpression receiver = GetReceiverForConditionalBinding(node, diagnostics);

            var memberAccess = BindElementAccess(node, receiver, node.ArgumentList, diagnostics);
            return memberAccess;
        }

        private static CSharpSyntaxNode GetConditionalReceiverSyntax(ConditionalAccessExpressionSyntax node)
        {
            Debug.Assert(node != null);
            Debug.Assert(node.Expression != null);

            var receiver = node.Expression;
            while (receiver.IsKind(SyntaxKind.ParenthesizedExpression))
            {
                receiver = ((ParenthesizedExpressionSyntax)receiver).Expression;
                Debug.Assert(receiver != null);
            }

            return receiver;
        }

        private BoundExpression GetReceiverForConditionalBinding(ExpressionSyntax binding, BindingDiagnosticBag diagnostics)
        {
            var conditionalAccessNode = SyntaxFactory.FindConditionalAccessNodeForBinding(binding);
            Debug.Assert(conditionalAccessNode != null);

            BoundExpression receiver = this.ConditionalReceiverExpression;
            if (receiver?.Syntax != GetConditionalReceiverSyntax(conditionalAccessNode))
            {
                // this can happen when semantic model binds parts of a Call or a broken access expression. 
                // We may not have receiver available in such cases.
                // Not a problem - we only need receiver to get its type and we can bind it here.
                receiver = BindConditionalAccessReceiver(conditionalAccessNode, diagnostics);
            }

            // create surrogate receiver
            var receiverType = receiver.Type;
            if (receiverType?.IsNullableType() == true)
            {
                receiverType = receiverType.GetNullableUnderlyingType();
            }

            receiver = new BoundConditionalReceiver(receiver.Syntax, 0, receiverType ?? CreateErrorType(), hasErrors: receiver.HasErrors) { WasCompilerGenerated = true };
            return receiver;
        }

        private BoundExpression BindConditionalAccessReceiver(ConditionalAccessExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            var receiverSyntax = node.Expression;
            var receiver = BindRValueWithoutTargetType(receiverSyntax, diagnostics);
            receiver = MakeMemberAccessValue(receiver, diagnostics);

            if (receiver.HasAnyErrors)
            {
                return receiver;
            }

            var operatorToken = node.OperatorToken;

            if (receiver.Kind == BoundKind.UnboundLambda)
            {
                var msgId = ((UnboundLambda)receiver).MessageID;
                DiagnosticInfo diagnosticInfo = new CSDiagnosticInfo(ErrorCode.ERR_BadUnaryOp, SyntaxFacts.GetText(operatorToken.Kind()), msgId.Localize());
                diagnostics.Add(new CSDiagnostic(diagnosticInfo, node.Location));
                return BadExpression(receiverSyntax, receiver);
            }

            var receiverType = receiver.Type;

            // Can't dot into the null literal or anything that has no type
            if ((object)receiverType == null)
            {
                Error(diagnostics, ErrorCode.ERR_BadUnaryOp, operatorToken.GetLocation(), operatorToken.Text, receiver.Display);
                return BadExpression(receiverSyntax, receiver);
            }

            // No member accesses on void
            if (receiverType.IsVoidType())
            {
                Error(diagnostics, ErrorCode.ERR_BadUnaryOp, operatorToken.GetLocation(), operatorToken.Text, receiverType);
                return BadExpression(receiverSyntax, receiver);
            }

            if (receiverType.IsValueType && !receiverType.IsNullableType())
            {
                // must be nullable or reference type
                Error(diagnostics, ErrorCode.ERR_BadUnaryOp, operatorToken.GetLocation(), operatorToken.Text, receiverType);
                return BadExpression(receiverSyntax, receiver);
            }

            return receiver;
        }
    }
}
