﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class Binder
    {
        #region Bind All Attributes

        // Method to bind attributes types early for all attributes to enable early decoding of some well-known attributes used within the binder.
        // Note: attributesToBind contains merged attributes from all the different syntax locations (e.g. for named types, partial methods, etc.).
        // Note: Additionally, the attributes with non-matching target specifier for the given owner symbol have been filtered out, i.e. Binder.MatchAttributeTarget method returned true.
        // For example, if were binding attributes on delegate type symbol for below code snippet:
        //      [A1]
        //      [return: A2]
        //      public delegate void Goo();
        // attributesToBind will only contain first attribute syntax.
        internal static void BindAttributeTypes(ImmutableArray<Binder> binders, ImmutableArray<AttributeSyntax> attributesToBind, Symbol ownerSymbol, NamedTypeSymbol[] boundAttributeTypes, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(binders.Any());
            Debug.Assert(attributesToBind.Any());
            Debug.Assert((object)ownerSymbol != null);
            Debug.Assert(binders.Length == attributesToBind.Length);
            RoslynDebug.Assert(boundAttributeTypes != null);

            for (int i = 0; i < attributesToBind.Length; i++)
            {
                // Some types may have been bound by an earlier stage.
                if (boundAttributeTypes[i] is null)
                {
                    var binder = binders[i];

                    // BindType for AttributeSyntax's name is handled specially during lookup, see Binder.LookupAttributeType.
                    // When looking up a name in attribute type context, we generate a diagnostic + error type if it is not an attribute type, i.e. named type deriving from System.Attribute.
                    // Hence we can assume here that BindType returns a NamedTypeSymbol.
                    boundAttributeTypes[i] = (NamedTypeSymbol)binder.BindType(attributesToBind[i].Name, diagnostics).Type;
                }
            }
        }

        // Method to bind all attributes (attribute arguments and constructor)
        internal static void GetAttributes(
            ImmutableArray<Binder> binders,
            ImmutableArray<AttributeSyntax> attributesToBind,
            ImmutableArray<NamedTypeSymbol> boundAttributeTypes,
            CSharpAttributeData?[] attributesBuilder,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(binders.Any());
            Debug.Assert(attributesToBind.Any());
            Debug.Assert(boundAttributeTypes.Any());
            Debug.Assert(binders.Length == attributesToBind.Length);
            Debug.Assert(boundAttributeTypes.Length == attributesToBind.Length);
            RoslynDebug.Assert(attributesBuilder != null);

            for (int i = 0; i < attributesToBind.Length; i++)
            {
                AttributeSyntax attributeSyntax = attributesToBind[i];
                NamedTypeSymbol boundAttributeType = boundAttributeTypes[i];
                Binder binder = binders[i];

                var attribute = (SourceAttributeData?)attributesBuilder[i];
                if (attribute == null)
                {
                    attributesBuilder[i] = binder.GetAttribute(attributeSyntax, boundAttributeType, diagnostics);
                }
                else
                {
                    // attributesBuilder might contain some early bound well-known attributes, which had no errors.
                    // We don't rebind the early bound attributes, but need to compute isConditionallyOmitted.
                    // Note that AttributeData.IsConditionallyOmitted is required only during emit, but must be computed here as
                    // its value depends on the values of conditional symbols, which in turn depends on the source file where the attribute is applied.

                    Debug.Assert(!attribute.HasErrors);
                    Debug.Assert(attribute.AttributeClass is object);
                    CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = binder.GetNewCompoundUseSiteInfo(diagnostics);
                    bool isConditionallyOmitted = binder.IsAttributeConditionallyOmitted(attribute.AttributeClass, attributeSyntax.SyntaxTree, ref useSiteInfo);
                    diagnostics.Add(attributeSyntax, useSiteInfo);
                    attributesBuilder[i] = attribute.WithOmittedCondition(isConditionallyOmitted);
                }
            }
        }

        #endregion

        #region Bind Single Attribute

        internal CSharpAttributeData GetAttribute(AttributeSyntax node, NamedTypeSymbol boundAttributeType, BindingDiagnosticBag diagnostics)
        {
            var boundAttribute = new ExecutableCodeBinder(node, this.ContainingMemberOrLambda, this).BindAttribute(node, boundAttributeType, diagnostics);

            return GetAttribute(boundAttribute, diagnostics);
        }

        internal BoundAttribute BindAttribute(AttributeSyntax node, NamedTypeSymbol attributeType, BindingDiagnosticBag diagnostics)
        {
            return this.GetRequiredBinder(node).BindAttributeCore(node, attributeType, diagnostics);
        }

        private Binder SkipSemanticModelBinder()
        {
            Binder result = this;

            while (result.IsSemanticModelBinder)
            {
                result = result.Next!;
            }

            return result;
        }

        private BoundAttribute BindAttributeCore(AttributeSyntax node, NamedTypeSymbol attributeType, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(this.SkipSemanticModelBinder() == this.GetRequiredBinder(node).SkipSemanticModelBinder());

            // If attribute name bound to an error type with a single named type
            // candidate symbol, we want to bind the attribute constructor
            // and arguments with that named type to generate better semantic info.

            // CONSIDER:    Do we need separate code paths for IDE and 
            // CONSIDER:    batch compilation scenarios? Above mentioned scenario
            // CONSIDER:    is not useful for batch compilation.

            NamedTypeSymbol attributeTypeForBinding = attributeType;
            LookupResultKind resultKind = LookupResultKind.Viable;
            if (attributeTypeForBinding.IsErrorType())
            {
                var errorType = (ErrorTypeSymbol)attributeTypeForBinding;
                resultKind = errorType.ResultKind;
                if (errorType.CandidateSymbols.Length == 1 && errorType.CandidateSymbols[0] is NamedTypeSymbol)
                {
                    attributeTypeForBinding = (NamedTypeSymbol)errorType.CandidateSymbols[0];
                }
            }

            // Bind constructor and named attribute arguments using the attribute binder
            var argumentListOpt = node.ArgumentList;
            Binder attributeArgumentBinder = this.WithAdditionalFlags(BinderFlags.AttributeArgument);
            AnalyzedAttributeArguments analyzedArguments = attributeArgumentBinder.BindAttributeArguments(argumentListOpt, attributeTypeForBinding, diagnostics);

            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            ImmutableArray<int> argsToParamsOpt = default;
            bool expanded = false;
            MethodSymbol? attributeConstructor = null;

            // Bind attributeType's constructor based on the bound constructor arguments
            ImmutableArray<BoundExpression> boundConstructorArguments;
            if (!attributeTypeForBinding.IsErrorType())
            {
                attributeConstructor = BindAttributeConstructor(node,
                                                                attributeTypeForBinding,
                                                                analyzedArguments.ConstructorArguments,
                                                                diagnostics,
                                                                ref resultKind,
                                                                suppressErrors: attributeType.IsErrorType(),
                                                                ref argsToParamsOpt,
                                                                ref expanded,
                                                                ref useSiteInfo,
                                                                out boundConstructorArguments);
            }
            else
            {
                boundConstructorArguments = analyzedArguments.ConstructorArguments.Arguments.SelectAsArray(
                    static (arg, attributeArgumentBinder) => attributeArgumentBinder.BindToTypeForErrorRecovery(arg),
                    attributeArgumentBinder);
            }
            Debug.Assert(boundConstructorArguments.All(a => !a.NeedsToBeConverted()));
            diagnostics.Add(node, useSiteInfo);

            if (attributeConstructor is object)
            {
                ReportDiagnosticsIfObsolete(diagnostics, attributeConstructor, node, hasBaseReceiver: false);

                if (attributeConstructor.Parameters.Any(p => p.RefKind == RefKind.In))
                {
                    Error(diagnostics, ErrorCode.ERR_AttributeCtorInParameter, node, attributeConstructor.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
                }
            }

            ImmutableArray<string?> boundConstructorArgumentNamesOpt = analyzedArguments.ConstructorArguments.GetNames();
            ImmutableArray<BoundAssignmentOperator> boundNamedArguments = analyzedArguments.NamedArguments?.ToImmutableAndFree() ?? ImmutableArray<BoundAssignmentOperator>.Empty;
            Debug.Assert(boundNamedArguments.All(arg => !arg.Right.NeedsToBeConverted()));

            analyzedArguments.ConstructorArguments.Free();

            return new BoundAttribute(node, attributeConstructor, boundConstructorArguments, boundConstructorArgumentNamesOpt, argsToParamsOpt, expanded,
                boundNamedArguments, resultKind, attributeType, hasErrors: resultKind != LookupResultKind.Viable);
        }

        private CSharpAttributeData GetAttribute(BoundAttribute boundAttribute, BindingDiagnosticBag diagnostics)
        {
            var attributeType = (NamedTypeSymbol)boundAttribute.Type;
            var attributeConstructor = boundAttribute.Constructor;

            RoslynDebug.Assert((object)attributeType != null);
            Debug.Assert(boundAttribute.Syntax.Kind() == SyntaxKind.Attribute);

            if (diagnostics.DiagnosticBag is object)
            {
                NullableWalker.AnalyzeIfNeeded(this, boundAttribute, boundAttribute.Syntax, diagnostics.DiagnosticBag);
            }

            bool hasErrors = boundAttribute.HasAnyErrors;

            if (attributeType.IsErrorType() || attributeType.IsAbstract || attributeConstructor is null)
            {
                // prevent cascading diagnostics
                Debug.Assert(hasErrors);
                return new SourceAttributeData(boundAttribute.Syntax.GetReference(), attributeType, attributeConstructor, hasErrors);
            }

            // Validate attribute constructor parameters have valid attribute parameter type
            ValidateTypeForAttributeParameters(attributeConstructor.Parameters, ((AttributeSyntax)boundAttribute.Syntax).Name, diagnostics, ref hasErrors);

            // Validate the attribute arguments and generate TypedConstant for argument's BoundExpression.
            var visitor = new AttributeExpressionVisitor(this);
            var arguments = boundAttribute.ConstructorArguments;
            var constructorArgsArray = visitor.VisitArguments(arguments, diagnostics, ref hasErrors);
            var namedArguments = visitor.VisitNamedArguments(boundAttribute.NamedArguments, diagnostics, ref hasErrors);

            Debug.Assert(!constructorArgsArray.IsDefault, "Property of VisitArguments");

            ImmutableArray<int> constructorArgumentsSourceIndices;
            ImmutableArray<TypedConstant> constructorArguments;
            if (hasErrors || attributeConstructor.ParameterCount == 0)
            {
                constructorArgumentsSourceIndices = default(ImmutableArray<int>);
                constructorArguments = constructorArgsArray;
            }
            else
            {
                constructorArguments = GetRewrittenAttributeConstructorArguments(out constructorArgumentsSourceIndices, attributeConstructor,
                    constructorArgsArray, boundAttribute.ConstructorArgumentNamesOpt, (AttributeSyntax)boundAttribute.Syntax, boundAttribute.ConstructorArgumentsToParamsOpt, diagnostics, ref hasErrors);
            }

            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            bool isConditionallyOmitted = IsAttributeConditionallyOmitted(attributeType, boundAttribute.SyntaxTree, ref useSiteInfo);
            diagnostics.Add(boundAttribute.Syntax, useSiteInfo);

            return new SourceAttributeData(boundAttribute.Syntax.GetReference(), attributeType, attributeConstructor, constructorArguments, constructorArgumentsSourceIndices, namedArguments, hasErrors, isConditionallyOmitted);
        }

        private void ValidateTypeForAttributeParameters(ImmutableArray<ParameterSymbol> parameters, CSharpSyntaxNode syntax, BindingDiagnosticBag diagnostics, ref bool hasErrors)
        {
            foreach (var parameter in parameters)
            {
                var paramType = parameter.TypeWithAnnotations;
                Debug.Assert(paramType.HasType);

                if (!paramType.Type.IsValidAttributeParameterType(Compilation))
                {
                    Error(diagnostics, ErrorCode.ERR_BadAttributeParamType, syntax, parameter.Name, paramType.Type);
                    hasErrors = true;
                }
            }
        }

        protected bool IsAttributeConditionallyOmitted(NamedTypeSymbol attributeType, SyntaxTree? syntaxTree, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // When early binding attributes, we don't want to determine if the attribute type is conditional and if so, must be emitted or not.
            // Invoking IsConditional property on attributeType can lead to a cycle, hence we delay this computation until after early binding.
            if (IsEarlyAttributeBinder)
            {
                return false;
            }

            Debug.Assert((object)attributeType != null);
            Debug.Assert(!attributeType.IsErrorType());

            if (attributeType.IsConditional)
            {
                ImmutableArray<string> conditionalSymbols = attributeType.GetAppliedConditionalSymbols();
                Debug.Assert(conditionalSymbols != null);
                if (syntaxTree.IsAnyPreprocessorSymbolDefined(conditionalSymbols))
                {
                    return false;
                }

                var baseType = attributeType.BaseTypeWithDefinitionUseSiteDiagnostics(ref useSiteInfo);
                if ((object)baseType != null && baseType.IsConditional)
                {
                    return IsAttributeConditionallyOmitted(baseType, syntaxTree, ref useSiteInfo);
                }

                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// The caller is responsible for freeing <see cref="AnalyzedAttributeArguments.ConstructorArguments"/> and <see cref="AnalyzedAttributeArguments.NamedArguments"/>.
        /// </summary>
        private AnalyzedAttributeArguments BindAttributeArguments(
            AttributeArgumentListSyntax? attributeArgumentList,
            NamedTypeSymbol attributeType,
            BindingDiagnosticBag diagnostics)
        {
            var boundConstructorArguments = AnalyzedArguments.GetInstance();
            ArrayBuilder<BoundAssignmentOperator>? boundNamedArgumentsBuilder = null;

            if (attributeArgumentList != null)
            {
                HashSet<string>? boundNamedArgumentsSet = null;

                // Only report the first "non-trailing named args required C# 7.2" error,
                // so as to avoid "cascading" errors.
                bool hadLangVersionError = false;

                var shouldHaveName = false;

                foreach (var argument in attributeArgumentList.Arguments)
                {
                    if (argument.NameEquals == null)
                    {
                        if (shouldHaveName)
                        {
                            diagnostics.Add(ErrorCode.ERR_NamedArgumentExpected, argument.Expression.GetLocation());
                        }

                        // Constructor argument
                        this.BindArgumentAndName(
                            boundConstructorArguments,
                            diagnostics,
                            ref hadLangVersionError,
                            argument,
                            BindArgumentExpression(diagnostics, argument.Expression, RefKind.None, allowArglist: false),
                            argument.NameColon,
                            refKind: RefKind.None);
                    }
                    else
                    {
                        shouldHaveName = true;

                        // Named argument
                        // TODO: use fully qualified identifier name for boundNamedArgumentsSet
                        string argumentName = argument.NameEquals.Name.Identifier.ValueText!;
                        if (boundNamedArgumentsBuilder == null)
                        {
                            boundNamedArgumentsBuilder = ArrayBuilder<BoundAssignmentOperator>.GetInstance();
                            boundNamedArgumentsSet = new HashSet<string>();
                        }
                        else if (boundNamedArgumentsSet!.Contains(argumentName))
                        {
                            // Duplicate named argument
                            Error(diagnostics, ErrorCode.ERR_DuplicateNamedAttributeArgument, argument, argumentName);
                        }

                        BoundAssignmentOperator boundNamedArgument = BindNamedAttributeArgument(argument, attributeType, diagnostics);
                        boundNamedArgumentsBuilder.Add(boundNamedArgument);
                        boundNamedArgumentsSet.Add(argumentName);
                    }
                }
            }

            return new AnalyzedAttributeArguments(boundConstructorArguments, boundNamedArgumentsBuilder);
        }

        private BoundAssignmentOperator BindNamedAttributeArgument(AttributeArgumentSyntax namedArgument, NamedTypeSymbol attributeType, BindingDiagnosticBag diagnostics)
        {
            bool wasError;
            LookupResultKind resultKind;
            Symbol namedArgumentNameSymbol = BindNamedAttributeArgumentName(namedArgument, attributeType, diagnostics, out wasError, out resultKind);

            ReportDiagnosticsIfObsolete(diagnostics, namedArgumentNameSymbol, namedArgument, hasBaseReceiver: false);

            if (namedArgumentNameSymbol.Kind == SymbolKind.Property)
            {
                var propertySymbol = (PropertySymbol)namedArgumentNameSymbol;
                var setMethod = propertySymbol.GetOwnOrInheritedSetMethod();
                if (setMethod != null)
                {
                    ReportDiagnosticsIfObsolete(diagnostics, setMethod, namedArgument, hasBaseReceiver: false);

                    if (setMethod.IsInitOnly && setMethod.DeclaringCompilation != this.Compilation)
                    {
                        // an error would have already been reported on declaring an init-only setter
                        CheckFeatureAvailability(namedArgument, MessageID.IDS_FeatureInitOnlySetters, diagnostics);
                    }
                }
            }

            Debug.Assert(resultKind == LookupResultKind.Viable || wasError);

            TypeSymbol namedArgumentType;
            if (wasError)
            {
                namedArgumentType = CreateErrorType();  // don't generate cascaded errors.
            }
            else
            {
                namedArgumentType = BindNamedAttributeArgumentType(namedArgument, namedArgumentNameSymbol, attributeType, diagnostics);
            }

            // BindRValue just binds the expression without doing any validation (if its a valid expression for attribute argument).
            // Validation is done later by AttributeExpressionVisitor
            BoundExpression namedArgumentValue = this.BindValue(namedArgument.Expression, diagnostics, BindValueKind.RValue);
            namedArgumentValue = GenerateConversionForAssignment(namedArgumentType, namedArgumentValue, diagnostics);

            // TODO: should we create an entry even if there are binding errors?
            var fieldSymbol = namedArgumentNameSymbol as FieldSymbol;
            RoslynDebug.Assert(namedArgument.NameEquals is object);
            IdentifierNameSyntax nameSyntax = namedArgument.NameEquals.Name;
            BoundExpression lvalue;
            if (fieldSymbol is object)
            {
                var containingAssembly = fieldSymbol.ContainingAssembly as SourceAssemblySymbol;

                // We do not want to generate any unassigned field or unreferenced field diagnostics.
                containingAssembly?.NoteFieldAccess(fieldSymbol, read: true, write: true);

                lvalue = new BoundFieldAccess(nameSyntax, null, fieldSymbol, ConstantValue.NotAvailable, resultKind, fieldSymbol.Type);
            }
            else
            {
                var propertySymbol = namedArgumentNameSymbol as PropertySymbol;
                if (propertySymbol is object)
                {
                    lvalue = new BoundPropertyAccess(nameSyntax, null, propertySymbol, resultKind, namedArgumentType);
                }
                else
                {
                    lvalue = BadExpression(nameSyntax, resultKind);
                }
            }

            return new BoundAssignmentOperator(namedArgument, lvalue, namedArgumentValue, namedArgumentType);
        }

        private Symbol BindNamedAttributeArgumentName(AttributeArgumentSyntax namedArgument, NamedTypeSymbol attributeType, BindingDiagnosticBag diagnostics, out bool wasError, out LookupResultKind resultKind)
        {
            RoslynDebug.Assert(namedArgument.NameEquals is object);
            var identifierName = namedArgument.NameEquals.Name;
            var name = identifierName.Identifier.ValueText;
            LookupResult result = LookupResult.GetInstance();
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            this.LookupMembersWithFallback(result, attributeType, name, 0, ref useSiteInfo);
            diagnostics.Add(identifierName, useSiteInfo);
            Symbol resultSymbol = this.ResultSymbol(result, name, 0, identifierName, diagnostics, false, out wasError, qualifierOpt: null);
            resultKind = result.Kind;
            result.Free();
            return resultSymbol;
        }

        private TypeSymbol BindNamedAttributeArgumentType(AttributeArgumentSyntax namedArgument, Symbol namedArgumentNameSymbol, NamedTypeSymbol attributeType, BindingDiagnosticBag diagnostics)
        {
            if (namedArgumentNameSymbol.Kind == SymbolKind.ErrorType)
            {
                return (TypeSymbol)namedArgumentNameSymbol;
            }

            // SPEC:    For each named-argument Arg in named-argument-list N:
            // SPEC:        Let Name be the identifier of the named-argument Arg.
            // SPEC:        Name must identify a non-static read-write public field or property on 
            // SPEC:            attribute class T. If T has no such field or property, then a compile-time error occurs.

            bool invalidNamedArgument = false;
            TypeSymbol? namedArgumentType = null;
            invalidNamedArgument |= (namedArgumentNameSymbol.DeclaredAccessibility != Accessibility.Public);
            invalidNamedArgument |= namedArgumentNameSymbol.IsStatic;

            if (!invalidNamedArgument)
            {
                switch (namedArgumentNameSymbol.Kind)
                {
                    case SymbolKind.Field:
                        var fieldSymbol = (FieldSymbol)namedArgumentNameSymbol;
                        namedArgumentType = fieldSymbol.Type;
                        invalidNamedArgument |= fieldSymbol.IsReadOnly;
                        invalidNamedArgument |= fieldSymbol.IsConst;
                        break;

                    case SymbolKind.Property:
                        var propertySymbol = ((PropertySymbol)namedArgumentNameSymbol).GetLeastOverriddenProperty(this.ContainingType);
                        namedArgumentType = propertySymbol.Type;
                        invalidNamedArgument |= propertySymbol.IsReadOnly;
                        var getMethod = propertySymbol.GetMethod;
                        var setMethod = propertySymbol.SetMethod;
                        invalidNamedArgument = invalidNamedArgument || (object)getMethod == null || (object)setMethod == null;
                        if (!invalidNamedArgument)
                        {
                            invalidNamedArgument =
                                getMethod!.DeclaredAccessibility != Accessibility.Public ||
                                setMethod!.DeclaredAccessibility != Accessibility.Public;
                        }
                        break;

                    default:
                        invalidNamedArgument = true;
                        break;
                }
            }

            if (invalidNamedArgument)
            {
                RoslynDebug.Assert(namedArgument.NameEquals is object);
                return new ExtendedErrorTypeSymbol(attributeType,
                    namedArgumentNameSymbol,
                    LookupResultKind.NotAVariable,
                    diagnostics.Add(ErrorCode.ERR_BadNamedAttributeArgument,
                        namedArgument.NameEquals.Name.Location,
                        namedArgumentNameSymbol.Name));
            }

            RoslynDebug.Assert(namedArgumentType is object);

            if (!namedArgumentType.IsValidAttributeParameterType(Compilation))
            {
                RoslynDebug.Assert(namedArgument.NameEquals is object);
                return new ExtendedErrorTypeSymbol(attributeType,
                    namedArgumentNameSymbol,
                    LookupResultKind.NotAVariable,
                    diagnostics.Add(ErrorCode.ERR_BadNamedAttributeArgumentType,
                        namedArgument.NameEquals.Name.Location,
                        namedArgumentNameSymbol.Name));
            }

            return namedArgumentType;
        }

        protected MethodSymbol BindAttributeConstructor(
            AttributeSyntax node,
            NamedTypeSymbol attributeType,
            AnalyzedArguments boundConstructorArguments,
            BindingDiagnosticBag diagnostics,
            ref LookupResultKind resultKind,
            bool suppressErrors,
            ref ImmutableArray<int> argsToParamsOpt,
            ref bool expanded,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo,
            out ImmutableArray<BoundExpression> constructorArguments)
        {
            MemberResolutionResult<MethodSymbol> memberResolutionResult;
            ImmutableArray<MethodSymbol> candidateConstructors;
            if (!TryPerformConstructorOverloadResolution(
                attributeType,
                boundConstructorArguments,
                attributeType.Name,
                node.Location,
                suppressErrors, //don't cascade in these cases
                diagnostics,
                out memberResolutionResult,
                out candidateConstructors,
                allowProtectedConstructorsOfBaseType: true))
            {
                resultKind = resultKind.WorseResultKind(
                    memberResolutionResult.IsValid && !IsConstructorAccessible(memberResolutionResult.Member, ref useSiteInfo) ?
                        LookupResultKind.Inaccessible :
                        LookupResultKind.OverloadResolutionFailure);
                constructorArguments = BuildArgumentsForErrorRecovery(boundConstructorArguments, candidateConstructors);
            }
            else
            {
                constructorArguments = boundConstructorArguments.Arguments.ToImmutable();
            }
            argsToParamsOpt = memberResolutionResult.Result.ArgsToParamsOpt;
            expanded = memberResolutionResult.Result.Kind == MemberResolutionKind.ApplicableInExpandedForm;
            return memberResolutionResult.Member;
        }

        /// <summary>
        /// Gets the rewritten attribute constructor arguments, i.e. the arguments
        /// are in the order of parameters, which may differ from the source
        /// if named constructor arguments are used.
        /// 
        /// For example:
        ///     void Goo(int x, int y, int z, int w = 3);
        /// 
        ///     Goo(0, z: 2, y: 1);
        ///     
        ///     Arguments returned: 0, 1, 2, 3
        /// </summary>
        /// <returns>Rewritten attribute constructor arguments</returns>
        /// <remarks>
        /// CONSIDER: Can we share some code will call rewriting in the local rewriter?
        /// </remarks>
        private ImmutableArray<TypedConstant> GetRewrittenAttributeConstructorArguments(
            out ImmutableArray<int> constructorArgumentsSourceIndices,
            MethodSymbol attributeConstructor,
            ImmutableArray<TypedConstant> constructorArgsArray,
            ImmutableArray<string?> constructorArgumentNamesOpt,
            AttributeSyntax syntax,
            ImmutableArray<int> argumentsToParams,
            BindingDiagnosticBag diagnostics,
            ref bool hasErrors)
        {
            RoslynDebug.Assert((object)attributeConstructor != null);
            Debug.Assert(!constructorArgsArray.IsDefault);
            Debug.Assert(!hasErrors);

            int argumentsCount = constructorArgsArray.Length;

            // argsConsumedCount keeps track of the number of constructor arguments
            // consumed from this.ConstructorArguments array
            int argsConsumedCount = 0;

            bool hasNamedCtorArguments = !constructorArgumentNamesOpt.IsDefault;
            Debug.Assert(!hasNamedCtorArguments ||
                constructorArgumentNamesOpt.Length == argumentsCount);

            ImmutableArray<ParameterSymbol> parameters = attributeConstructor.Parameters;
            int parameterCount = parameters.Length;

            var reorderedArguments = new TypedConstant[parameterCount];
            int[]? sourceIndices = null;

            for (int i = 0; i < parameterCount; i++)
            {
                Debug.Assert(argsConsumedCount <= argumentsCount);

                ParameterSymbol parameter = parameters[i];
                TypedConstant reorderedArgument;

                if (parameter.IsParams && parameter.Type.IsSZArray() && i + 1 == parameterCount)
                {
                    reorderedArgument = GetParamArrayArgument(parameter, constructorArgsArray, constructorArgumentNamesOpt, argumentsCount,
                        argsConsumedCount, this.Conversions, out bool foundNamed);
                    if (!foundNamed)
                    {
                        sourceIndices = sourceIndices ?? CreateSourceIndicesArray(i, parameterCount);
                    }
                }
                else if (argsConsumedCount < argumentsCount)
                {
                    if (!hasNamedCtorArguments ||
                        constructorArgumentNamesOpt[argsConsumedCount] == null)
                    {
                        // positional constructor argument
                        reorderedArgument = constructorArgsArray[argsConsumedCount];
                        if (sourceIndices != null)
                        {
                            sourceIndices[i] = argsConsumedCount;
                        }
                        argsConsumedCount++;
                    }
                    else
                    {
                        // Current parameter must either have a matching named argument or a default value
                        // For the former case, argsConsumedCount must be incremented to note that we have
                        // consumed a named argument. For the latter case, argsConsumedCount stays same.
                        int matchingArgumentIndex;
                        reorderedArgument = getMatchingNamedOrOptionalConstructorArgument(out matchingArgumentIndex, constructorArgsArray,
                            parameter, argumentsCount, ref argsConsumedCount, syntax, argumentsToParams, diagnostics);

                        sourceIndices = sourceIndices ?? CreateSourceIndicesArray(i, parameterCount);
                        sourceIndices[i] = matchingArgumentIndex;
                    }
                }
                else
                {
                    reorderedArgument = GetDefaultValueArgument(parameter, syntax, argumentsToParams, argumentsCount, diagnostics);
                    sourceIndices = sourceIndices ?? CreateSourceIndicesArray(i, parameterCount);
                }

                if (!hasErrors)
                {
                    if (reorderedArgument.Kind == TypedConstantKind.Error)
                    {
                        hasErrors = true;
                    }
                    else if (reorderedArgument.Kind == TypedConstantKind.Array &&
                        parameter.Type.TypeKind == TypeKind.Array &&
                        !((TypeSymbol)reorderedArgument.TypeInternal!).Equals(parameter.Type, TypeCompareKind.AllIgnoreOptions))
                    {
                        // NOTE: As in dev11, we don't allow array covariance conversions (presumably, we don't have a way to
                        // represent the conversion in metadata).
                        diagnostics.Add(ErrorCode.ERR_BadAttributeArgument, syntax.Location);
                        hasErrors = true;
                    }
                }

                reorderedArguments[i] = reorderedArgument;
            }

            constructorArgumentsSourceIndices = sourceIndices != null ? sourceIndices.AsImmutableOrNull() : default(ImmutableArray<int>);
            return reorderedArguments.AsImmutableOrNull();

            // Local functions
            TypedConstant getMatchingNamedOrOptionalConstructorArgument(
                out int matchingArgumentIndex,
                ImmutableArray<TypedConstant> constructorArgsArray,
                ParameterSymbol parameter,
                int argumentsCount,
                ref int argsConsumedCount,
                AttributeSyntax syntax,
                ImmutableArray<int> argumentsToParams,
                BindingDiagnosticBag diagnostics)
            {
                matchingArgumentIndex = argumentsToParams.IsDefaultOrEmpty
                    ? parameter.Ordinal
                    : argumentsToParams.IndexOf(parameter.Ordinal);

                if (matchingArgumentIndex > -1)
                {
                    // found a matching named argument
                    Debug.Assert(matchingArgumentIndex < argumentsCount);

                    // increment argsConsumedCount
                    argsConsumedCount++;
                    return constructorArgsArray[matchingArgumentIndex];
                }
                else
                {
                    return GetDefaultValueArgument(parameter, syntax, argumentsToParams, argumentsCount, diagnostics);
                }
            }
        }

        private static int[] CreateSourceIndicesArray(int paramIndex, int parameterCount)
        {
            Debug.Assert(paramIndex >= 0);
            Debug.Assert(paramIndex < parameterCount);

            var sourceIndices = new int[parameterCount];
            for (int i = 0; i < paramIndex; i++)
            {
                sourceIndices[i] = i;
            }

            for (int i = paramIndex; i < parameterCount; i++)
            {
                sourceIndices[i] = -1;
            }

            return sourceIndices;
        }

        private static int GetMatchingNamedConstructorArgumentIndex(string parameterName, ImmutableArray<string?> argumentNamesOpt, int startIndex, int argumentsCount)
        {
            RoslynDebug.Assert(parameterName != null);
            Debug.Assert(startIndex >= 0 && startIndex < argumentsCount);

            if (parameterName.IsEmpty() || !argumentNamesOpt.Any())
            {
                return argumentsCount;
            }

            // get the matching named (constructor) argument
            int argIndex = startIndex;
            while (argIndex < argumentsCount)
            {
                var name = argumentNamesOpt[argIndex];

                if (string.Equals(name, parameterName, StringComparison.Ordinal))
                {
                    break;
                }

                argIndex++;
            }

            return argIndex;
        }

        private TypedConstant GetDefaultValueArgument(ParameterSymbol parameter, AttributeSyntax syntax, ImmutableArray<int> argumentsToParams, int argumentsCount, BindingDiagnosticBag diagnostics)
        {
            var parameterType = parameter.Type;
            ConstantValue? defaultConstantValue = parameter.IsOptional ? parameter.ExplicitDefaultConstantValue : ConstantValue.NotAvailable;

            TypedConstantKind kind;
            object? defaultValue = null;

            if (!IsEarlyAttributeBinder && parameter.IsCallerLineNumber)
            {
                int line = syntax.SyntaxTree.GetDisplayLineNumber(syntax.Name.Span);
                kind = TypedConstantKind.Primitive;

                CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
                var conversion = Conversions.GetCallerLineNumberConversion(parameterType, ref useSiteInfo);
                diagnostics.Add(syntax, useSiteInfo);

                if (conversion.IsNumeric || conversion.IsConstantExpression)
                {
                    // DoUncheckedConversion() keeps "single" floats as doubles internally to maintain higher
                    // precision, so make sure they get cast to floats here.
                    defaultValue = (parameterType.SpecialType == SpecialType.System_Single)
                        ? (float)line
                        : Binder.DoUncheckedConversion(parameterType.SpecialType, ConstantValue.Create(line));
                }
                else
                {
                    // Boxing or identity conversion:
                    parameterType = GetSpecialType(SpecialType.System_Int32, diagnostics, syntax);
                    defaultValue = line;
                }
            }
            else if (!IsEarlyAttributeBinder && parameter.IsCallerFilePath)
            {
                parameterType = GetSpecialType(SpecialType.System_String, diagnostics, syntax);
                kind = TypedConstantKind.Primitive;
                defaultValue = syntax.SyntaxTree.GetDisplayPath(syntax.Name.Span, Compilation.Options.SourceReferenceResolver);
            }
            else if (!IsEarlyAttributeBinder && parameter.IsCallerMemberName && (object)((ContextualAttributeBinder)this).AttributedMember != null)
            {
                parameterType = GetSpecialType(SpecialType.System_String, diagnostics, syntax);
                kind = TypedConstantKind.Primitive;
                defaultValue = ((ContextualAttributeBinder)this).AttributedMember.GetMemberCallerName();
            }
            // We check IsCallerMemberName here since the above if can be reached with AttributedMember == null, in which case,
            // We shouldn't be checking CallerArgumentExpression
            else if (!IsEarlyAttributeBinder && syntax.ArgumentList is not null && !parameter.IsCallerMemberName &&
                getCallerArgumentArgumentIndex(parameter, argumentsToParams) is int argumentIndex && argumentIndex > -1 && argumentIndex < argumentsCount)
            {
                Debug.Assert(argumentsCount <= syntax.ArgumentList.Arguments.Count);
                parameterType = GetSpecialType(SpecialType.System_String, diagnostics, syntax);
                kind = TypedConstantKind.Primitive;
                defaultValue = syntax.ArgumentList.Arguments[argumentIndex].Expression.ToString();
            }
            else if (defaultConstantValue == ConstantValue.NotAvailable)
            {
                // There is no constant value given for the parameter in source/metadata.
                // For example, the attribute constructor with signature: M([Optional] int x), has no default value from syntax or attributes.
                // Default value for these cases is "default(parameterType)".

                // Optional parameter of System.Object type is treated specially though.
                // Native compiler treats "M([Optional] object x)" equivalent to "M(object x)" for attributes if parameter type is System.Object.
                // We generate a better diagnostic for this case by treating "x" in the above case as optional, but generating CS7067 instead.
                if (parameterType.SpecialType == SpecialType.System_Object)
                {
                    // CS7067: Attribute constructor parameter '{0}' is optional, but no default parameter value was specified.
                    diagnostics.Add(ErrorCode.ERR_BadAttributeParamDefaultArgument, syntax.Name.Location, parameter.Name);
                    kind = TypedConstantKind.Error;
                }
                else
                {
                    kind = TypedConstant.GetTypedConstantKind(parameterType, this.Compilation);
                    Debug.Assert(kind != TypedConstantKind.Error);

                    defaultConstantValue = parameterType.GetDefaultValue();
                    if (defaultConstantValue != null)
                    {
                        defaultValue = defaultConstantValue.Value;
                    }
                }
            }
            else if (defaultConstantValue.IsBad)
            {
                // Constant value through syntax had errors, don't generate cascading diagnostics.
                kind = TypedConstantKind.Error;
            }
            else if (parameterType.SpecialType == SpecialType.System_Object && !defaultConstantValue.IsNull)
            {
                // error CS1763: '{0}' is of type '{1}'. A default parameter value of a reference type other than string can only be initialized with null
                diagnostics.Add(ErrorCode.ERR_NotNullRefDefaultParameter, syntax.Location, parameter.Name, parameterType);
                kind = TypedConstantKind.Error;
            }
            else
            {
                kind = TypedConstant.GetTypedConstantKind(parameterType, this.Compilation);
                Debug.Assert(kind != TypedConstantKind.Error);

                defaultValue = defaultConstantValue.Value;
            }

            if (kind == TypedConstantKind.Array)
            {
                Debug.Assert(defaultValue == null);
                return new TypedConstant(parameterType, default(ImmutableArray<TypedConstant>));
            }
            else
            {
                return new TypedConstant(parameterType, kind, defaultValue);
            }

            static int getCallerArgumentArgumentIndex(ParameterSymbol parameter, ImmutableArray<int> argumentsToParams)
            {
                return argumentsToParams.IsDefault || parameter.CallerArgumentExpressionParameterIndex == -1
                        ? parameter.CallerArgumentExpressionParameterIndex
                        : argumentsToParams.IndexOf(parameter.CallerArgumentExpressionParameterIndex);
            }
        }

        private static TypedConstant GetParamArrayArgument(ParameterSymbol parameter, ImmutableArray<TypedConstant> constructorArgsArray,
            ImmutableArray<string?> constructorArgumentNamesOpt, int argumentsCount, int argsConsumedCount, Conversions conversions, out bool foundNamed)
        {
            Debug.Assert(argsConsumedCount <= argumentsCount);

            // If there's a named argument, we'll use that
            if (!constructorArgumentNamesOpt.IsDefault)
            {
                int argIndex = constructorArgumentNamesOpt.IndexOf(parameter.Name);
                if (argIndex >= 0)
                {
                    foundNamed = true;
                    if (TryGetNormalParamValue(parameter, constructorArgsArray, argIndex, conversions, out var namedValue))
                    {
                        return namedValue;
                    }

                    // A named argument for a params parameter is necessarily the only one for that parameter
                    return new TypedConstant(parameter.Type, ImmutableArray.Create(constructorArgsArray[argIndex]));
                }
            }

            int paramArrayArgCount = argumentsCount - argsConsumedCount;
            foundNamed = false;

            // If there are zero arguments left
            if (paramArrayArgCount == 0)
            {
                return new TypedConstant(parameter.Type, ImmutableArray<TypedConstant>.Empty);
            }

            // If there's exactly one argument left, we'll try to use it in normal form
            if (paramArrayArgCount == 1 &&
                TryGetNormalParamValue(parameter, constructorArgsArray, argsConsumedCount, conversions, out var lastValue))
            {
                return lastValue;
            }

            Debug.Assert(!constructorArgsArray.IsDefault);
            Debug.Assert(argsConsumedCount <= constructorArgsArray.Length);

            // Take the trailing arguments as an array for expanded form
            var values = new TypedConstant[paramArrayArgCount];

            for (int i = 0; i < paramArrayArgCount; i++)
            {
                values[i] = constructorArgsArray[argsConsumedCount++];
            }

            return new TypedConstant(parameter.Type, values.AsImmutableOrNull());
        }

        private static bool TryGetNormalParamValue(ParameterSymbol parameter, ImmutableArray<TypedConstant> constructorArgsArray,
            int argIndex, Conversions conversions, out TypedConstant result)
        {
            TypedConstant argument = constructorArgsArray[argIndex];
            if (argument.Kind != TypedConstantKind.Array)
            {
                result = default;
                return false;
            }

            Debug.Assert(argument.TypeInternal is object);
            var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded; // ignoring, since already bound argument and parameter
            Conversion conversion = conversions.ClassifyBuiltInConversion((TypeSymbol)argument.TypeInternal, parameter.Type, ref discardedUseSiteInfo);

            // NOTE: Won't always succeed, even though we've performed overload resolution.
            // For example, passing int[] to params object[] actually treats the int[] as an element of the object[].
            if (conversion.IsValid && (conversion.Kind == ConversionKind.ImplicitReference || conversion.Kind == ConversionKind.Identity))
            {
                result = argument;
                return true;
            }

            result = default;
            return false;
        }

        #endregion

        #region AttributeExpressionVisitor

        /// <summary>
        /// Walk a custom attribute argument bound node and return a TypedConstant.  Verify that the expression is a constant expression.
        /// </summary>
        private struct AttributeExpressionVisitor
        {
            private readonly Binder _binder;

            public AttributeExpressionVisitor(Binder binder)
            {
                _binder = binder;
            }

            public ImmutableArray<TypedConstant> VisitArguments(ImmutableArray<BoundExpression> arguments, BindingDiagnosticBag diagnostics, ref bool attrHasErrors, bool parentHasErrors = false)
            {
                var validatedArguments = ImmutableArray<TypedConstant>.Empty;

                int numArguments = arguments.Length;
                if (numArguments > 0)
                {
                    var builder = ArrayBuilder<TypedConstant>.GetInstance(numArguments);
                    foreach (var argument in arguments)
                    {
                        // current argument has errors if parent had errors OR argument.HasErrors.
                        bool curArgumentHasErrors = parentHasErrors || argument.HasAnyErrors;

                        builder.Add(VisitExpression(argument, diagnostics, ref attrHasErrors, curArgumentHasErrors));
                    }
                    validatedArguments = builder.ToImmutableAndFree();
                }

                return validatedArguments;
            }

            public ImmutableArray<KeyValuePair<string, TypedConstant>> VisitNamedArguments(ImmutableArray<BoundAssignmentOperator> arguments, BindingDiagnosticBag diagnostics, ref bool attrHasErrors)
            {
                ArrayBuilder<KeyValuePair<string, TypedConstant>>? builder = null;
                foreach (var argument in arguments)
                {
                    var kv = VisitNamedArgument(argument, diagnostics, ref attrHasErrors);

                    if (kv.HasValue)
                    {
                        if (builder == null)
                        {
                            builder = ArrayBuilder<KeyValuePair<string, TypedConstant>>.GetInstance();
                        }

                        builder.Add(kv.Value);
                    }
                }

                if (builder == null)
                {
                    return ImmutableArray<KeyValuePair<string, TypedConstant>>.Empty;
                }

                return builder.ToImmutableAndFree();
            }

            private KeyValuePair<String, TypedConstant>? VisitNamedArgument(BoundAssignmentOperator assignment, BindingDiagnosticBag diagnostics, ref bool attrHasErrors)
            {
                KeyValuePair<String, TypedConstant>? visitedArgument = null;

                switch (assignment.Left.Kind)
                {
                    case BoundKind.FieldAccess:
                        var fa = (BoundFieldAccess)assignment.Left;
                        visitedArgument = new KeyValuePair<String, TypedConstant>(fa.FieldSymbol.Name, VisitExpression(assignment.Right, diagnostics, ref attrHasErrors, assignment.HasAnyErrors));
                        break;

                    case BoundKind.PropertyAccess:
                        var pa = (BoundPropertyAccess)assignment.Left;
                        visitedArgument = new KeyValuePair<String, TypedConstant>(pa.PropertySymbol.Name, VisitExpression(assignment.Right, diagnostics, ref attrHasErrors, assignment.HasAnyErrors));
                        break;
                }

                return visitedArgument;
            }

            // SPEC:    An expression E is an attribute-argument-expression if all of the following statements are true:
            // SPEC:    1) The type of E is an attribute parameter type (§17.1.3).
            // SPEC:    2) At compile-time, the value of Expression can be resolved to one of the following:
            // SPEC:        a) A constant value.
            // SPEC:        b) A System.Type object.
            // SPEC:        c) A one-dimensional array of attribute-argument-expressions

            private TypedConstant VisitExpression(BoundExpression node, BindingDiagnosticBag diagnostics, ref bool attrHasErrors, bool curArgumentHasErrors)
            {
                // Validate Statement 1) of the spec comment above.

                RoslynDebug.Assert(node.Type is object);
                var typedConstantKind = node.Type.GetAttributeParameterTypedConstantKind(_binder.Compilation);

                return VisitExpression(node, typedConstantKind, diagnostics, ref attrHasErrors, curArgumentHasErrors || typedConstantKind == TypedConstantKind.Error);
            }

            private TypedConstant VisitExpression(BoundExpression node, TypedConstantKind typedConstantKind, BindingDiagnosticBag diagnostics, ref bool attrHasErrors, bool curArgumentHasErrors)
            {
                // Validate Statement 2) of the spec comment above.

                ConstantValue? constantValue = node.ConstantValue;
                if (constantValue != null)
                {
                    if (constantValue.IsBad)
                    {
                        typedConstantKind = TypedConstantKind.Error;
                    }

                    ConstantValueUtils.CheckLangVersionForConstantValue(node, diagnostics);

                    return CreateTypedConstant(node, typedConstantKind, diagnostics, ref attrHasErrors, curArgumentHasErrors, simpleValue: constantValue.Value);
                }

                switch (node.Kind)
                {
                    case BoundKind.Conversion:
                        return VisitConversion((BoundConversion)node, diagnostics, ref attrHasErrors, curArgumentHasErrors);
                    case BoundKind.TypeOfOperator:
                        return VisitTypeOfExpression((BoundTypeOfOperator)node, diagnostics, ref attrHasErrors, curArgumentHasErrors);
                    case BoundKind.ArrayCreation:
                        return VisitArrayCreation((BoundArrayCreation)node, diagnostics, ref attrHasErrors, curArgumentHasErrors);
                    default:
                        return CreateTypedConstant(node, TypedConstantKind.Error, diagnostics, ref attrHasErrors, curArgumentHasErrors);
                }
            }

            private TypedConstant VisitConversion(BoundConversion node, BindingDiagnosticBag diagnostics, ref bool attrHasErrors, bool curArgumentHasErrors)
            {
                Debug.Assert(node.ConstantValue == null);

                // We have a bound conversion with a non-constant value.
                // According to statement 2) of the spec comment, this is not a valid attribute argument.
                // However, native compiler allows conversions to object type if the conversion operand is a valid attribute argument.
                // See method AttributeHelper::VerifyAttrArg(EXPR *arg).

                // We will match native compiler's behavior here.
                // Devdiv Bug #8763: Additionally we allow conversions from array type to object[], provided a conversion exists and each array element is a valid attribute argument.

                var type = node.Type;
                var operand = node.Operand;
                var operandType = operand.Type;

                if ((object)type != null && operandType is object)
                {
                    if (type.SpecialType == SpecialType.System_Object ||
                        operandType.IsArray() && type.IsArray() &&
                        ((ArrayTypeSymbol)type).ElementType.SpecialType == SpecialType.System_Object)
                    {
                        var typedConstantKind = operandType.GetAttributeParameterTypedConstantKind(_binder.Compilation);
                        return VisitExpression(operand, typedConstantKind, diagnostics, ref attrHasErrors, curArgumentHasErrors);
                    }
                }

                return CreateTypedConstant(node, TypedConstantKind.Error, diagnostics, ref attrHasErrors, curArgumentHasErrors);
            }

            private static TypedConstant VisitTypeOfExpression(BoundTypeOfOperator node, BindingDiagnosticBag diagnostics, ref bool attrHasErrors, bool curArgumentHasErrors)
            {
                var typeOfArgument = (TypeSymbol?)node.SourceType.Type;

                // typeof argument is allowed to be:
                //  (a) an unbound type
                //  (b) closed constructed type
                // typeof argument cannot be an open type

                if (typeOfArgument is object) // skip this if the argument was an alias symbol
                {
                    var isValidArgument = true;
                    switch (typeOfArgument.Kind)
                    {
                        case SymbolKind.TypeParameter:
                            // type parameter represents an open type
                            isValidArgument = false;
                            break;

                        default:
                            isValidArgument = typeOfArgument.IsUnboundGenericType() || !typeOfArgument.ContainsTypeParameter();
                            break;
                    }

                    if (!isValidArgument && !curArgumentHasErrors)
                    {
                        // attribute argument type cannot be an open type
                        Binder.Error(diagnostics, ErrorCode.ERR_AttrArgWithTypeVars, node.Syntax, typeOfArgument.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
                        curArgumentHasErrors = true;
                        attrHasErrors = true;
                    }
                }

                return CreateTypedConstant(node, TypedConstantKind.Type, diagnostics, ref attrHasErrors, curArgumentHasErrors, simpleValue: node.SourceType.Type);
            }

            private TypedConstant VisitArrayCreation(BoundArrayCreation node, BindingDiagnosticBag diagnostics, ref bool attrHasErrors, bool curArgumentHasErrors)
            {
                ImmutableArray<BoundExpression> bounds = node.Bounds;
                int boundsCount = bounds.Length;

                if (boundsCount > 1)
                {
                    return CreateTypedConstant(node, TypedConstantKind.Error, diagnostics, ref attrHasErrors, curArgumentHasErrors);
                }

                var type = (ArrayTypeSymbol)node.Type;
                var typedConstantKind = type.GetAttributeParameterTypedConstantKind(_binder.Compilation);

                ImmutableArray<TypedConstant> initializer;
                if (node.InitializerOpt == null)
                {
                    if (boundsCount == 0)
                    {
                        initializer = ImmutableArray<TypedConstant>.Empty;
                    }
                    else
                    {
                        if (bounds[0].IsDefaultValue())
                        {
                            initializer = ImmutableArray<TypedConstant>.Empty;
                        }
                        else
                        {
                            // error: non-constant array creation
                            initializer = ImmutableArray.Create(CreateTypedConstant(node, TypedConstantKind.Error, diagnostics, ref attrHasErrors, curArgumentHasErrors));
                        }
                    }
                }
                else
                {
                    initializer = VisitArguments(node.InitializerOpt.Initializers, diagnostics, ref attrHasErrors, curArgumentHasErrors);
                }

                return CreateTypedConstant(node, typedConstantKind, diagnostics, ref attrHasErrors, curArgumentHasErrors, arrayValue: initializer);
            }

            private static TypedConstant CreateTypedConstant(BoundExpression node, TypedConstantKind typedConstantKind, BindingDiagnosticBag diagnostics, ref bool attrHasErrors, bool curArgumentHasErrors,
                object? simpleValue = null, ImmutableArray<TypedConstant> arrayValue = default(ImmutableArray<TypedConstant>))
            {
                var type = node.Type;
                RoslynDebug.Assert(type is object);

                if (typedConstantKind != TypedConstantKind.Error && type.ContainsTypeParameter())
                {
                    // Devdiv Bug #12636: Constant values of open types should not be allowed in attributes

                    // SPEC ERROR:  C# language specification does not explicitly disallow constant values of open types. For e.g.

                    //  public class C<T>
                    //  {
                    //      public enum E { V }
                    //  }
                    //
                    //  [SomeAttr(C<T>.E.V)]        // case (a): Constant value of open type.
                    //  [SomeAttr(C<int>.E.V)]      // case (b): Constant value of constructed type.

                    // Both expressions 'C<T>.E.V' and 'C<int>.E.V' satisfy the requirements for a valid attribute-argument-expression:
                    //  (a) Its type is a valid attribute parameter type as per section 17.1.3 of the specification.
                    //  (b) It has a compile time constant value.

                    // However, native compiler disallows both the above cases.
                    // We disallow case (a) as it cannot be serialized correctly, but allow case (b) to compile.

                    typedConstantKind = TypedConstantKind.Error;
                }

                if (typedConstantKind == TypedConstantKind.Error)
                {
                    if (!curArgumentHasErrors)
                    {
                        Binder.Error(diagnostics, ErrorCode.ERR_BadAttributeArgument, node.Syntax);
                        attrHasErrors = true;
                    }

                    return new TypedConstant(type, TypedConstantKind.Error, null);
                }
                else if (typedConstantKind == TypedConstantKind.Array)
                {
                    return new TypedConstant(type, arrayValue);
                }
                else
                {
                    return new TypedConstant(type, typedConstantKind, simpleValue);
                }
            }
        }

        #endregion

        #region AnalyzedAttributeArguments

        private struct AnalyzedAttributeArguments
        {
            internal readonly AnalyzedArguments ConstructorArguments;
            internal readonly ArrayBuilder<BoundAssignmentOperator>? NamedArguments;

            internal AnalyzedAttributeArguments(AnalyzedArguments constructorArguments, ArrayBuilder<BoundAssignmentOperator>? namedArguments)
            {
                this.ConstructorArguments = constructorArguments;
                this.NamedArguments = namedArguments;
            }
        }

        #endregion
    }
}
